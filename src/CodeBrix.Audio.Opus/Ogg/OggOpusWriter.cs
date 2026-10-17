using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Opus.Internal;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// Encodes interleaved float samples to an Ogg Opus stream.
/// </summary>
/// <remarks>
/// <para>
/// A thin Ogg layer over <see cref="OpusEncodingCore" />, which owns resampling, framing, the
/// pre-skip and the end-of-stream padding rule. This class only lays the core's packets into
/// pages: the identification header alone on the first page, the comment header on the second,
/// each audio packet with the granule position at its end, and a final page whose granule states
/// the true sample count.
/// </para>
/// <para>
/// Opus encodes at 48 kHz, so input at any other rate is resampled on the way in. The declared
/// input rate is still written into the identification header, because RFC 7845 keeps it as a
/// record of what the encoder was given - it is not, and must never become, a decoding parameter.
/// </para>
/// </remarks>
internal sealed class OggOpusWriter : IDisposable
{
    /// <summary>Opus encodes at this rate, always.</summary>
    public const int EncodeSampleRate = OpusEncodingCore.EncodeSampleRate;

    private readonly Stream destination;
    private readonly bool leaveOpen;
    private readonly OpusEncodingCore core;
    private readonly OggPageWriter pages;

    private bool finished;
    private bool disposed;

    /// <summary>Creates a writer and emits the two header pages.</summary>
    /// <param name="destination">The stream to write to.</param>
    /// <param name="channels">Channels in the input, 1 or 2.</param>
    /// <param name="inputSampleRate">The rate of the samples that will be written.</param>
    /// <param name="settings">Encoder settings.</param>
    /// <param name="serialNumber">The logical bitstream serial number.</param>
    /// <param name="leaveOpen">When false the stream is disposed along with this writer.</param>
    public OggOpusWriter(Stream destination, int channels, int inputSampleRate,
        OggOpusWriterSettings settings, uint serialNumber, bool leaveOpen = true)
    {
        this.destination = destination ?? throw new ArgumentNullException(nameof(destination));
        this.leaveOpen = leaveOpen;

        settings ??= new OggOpusWriterSettings();

        core = new OpusEncodingCore(channels, inputSampleRate, settings);
        pages = new OggPageWriter(destination, serialNumber, leaveOpen: true);

        WriteHeaders(settings);
    }

    /// <summary>
    /// The pre-skip written into the identification header: the encoder's lookahead plus, for
    /// input not at 48 kHz, the input resampler's delay.
    /// </summary>
    public int PreSkip => core.PreSkip;

    /// <summary>Encodes and writes interleaved samples at the declared input rate.</summary>
    /// <param name="samples">Interleaved samples in [-1, 1]: a whole number of frames.</param>
    /// <exception cref="ArgumentException">The length is not a multiple of the channel count.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish" /> has already run.</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (finished) throw new InvalidOperationException("This writer has already been finished.");
        core.CheckWholeFrames(samples.Length, nameof(samples));
        if (samples.Length == 0) return;

        core.Write(samples);
        WriteQueuedPackets();
    }

    /// <summary>
    /// Flushes the last partial frame and closes the logical bitstream.
    /// </summary>
    /// <remarks>
    /// Two things have to be right here or the file plays but misreports itself. The tail is
    /// padded with silence so the encoder gets a whole frame (the core does that), and then the
    /// final page's granule position is set to the TRUE sample count - pre-skip plus the audio
    /// actually supplied - so a decoder trims that padding instead of playing it.
    /// </remarks>
    public void Finish()
    {
        if (finished) return;
        finished = true;

        core.Finish();
        WriteQueuedPackets();

        pages.SetPendingGranule(core.TargetSampleCount);
        pages.FlushPage(endOfStream: true);
        destination.Flush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        try
        {
            Finish();
        }
        finally
        {
            pages.Dispose();
            if (!leaveOpen) destination.Dispose();
        }
    }

    private void WriteHeaders(OggOpusWriterSettings settings)
    {
        // RFC 7845 requires the identification header to be alone on the first page, and the
        // comment header to start on the page after it.
        pages.WritePacket((byte[])core.IdentificationHeader.Clone(), 0);
        pages.FlushPage(endOfStream: false);

        var tags = new OpusTags { Vendor = settings.Vendor };
        foreach (var pair in settings.Tags)
        {
            tags.Tags[pair.Key] = new List<string>(pair.Value);
        }

        pages.WritePacket(tags.ToBytes(), 0);
        pages.FlushPage(endOfStream: false);
    }

    private void WriteQueuedPackets()
    {
        // An Ogg granule position counts to the END of the packet it belongs to.
        while (core.TryDequeue(out var packet, out var firstSample))
        {
            pages.WritePacket(packet, firstSample + OpusEncodingCore.FrameSamplesPerChannel);
        }
    }
}
