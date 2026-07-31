using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Opus.Codec;
using CodeBrix.Audio.Opus.Codec.Enums;
using CodeBrix.Audio.Opus.Codec.Structs;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// Encodes interleaved float samples to an Ogg Opus stream.
/// </summary>
/// <remarks>
/// Opus encodes at 48 kHz, so input at any other rate is resampled on the way in. The declared
/// input rate is still written into the identification header, because RFC 7845 keeps it as a
/// record of what the encoder was given - it is not, and must never become, a decoding parameter.
/// </remarks>
internal sealed class OggOpusWriter : IDisposable
{
    /// <summary>Opus encodes at this rate, always.</summary>
    public const int EncodeSampleRate = 48000;

    /// <summary>20 ms at 48 kHz - the frame size opusenc and ffmpeg both default to.</summary>
    private const int FrameSamplesPerChannel = 960;

    /// <summary>Generous ceiling for one encoded packet.</summary>
    private const int MaxPacketBytes = 4000;

    private readonly Stream destination;
    private readonly bool leaveOpen;
    private readonly int channels;
    private readonly int inputSampleRate;
    private readonly OpusEncoder encoder;
    private readonly OggPageWriter pages;
    private readonly IResampler resampler;
    private readonly float[] frame;
    private readonly byte[] packetBuffer = new byte[MaxPacketBytes];
    private readonly int preSkip;

    private float[] resampleBuffer = Array.Empty<float>();
    private int frameFill;
    private long granuleWritten;
    private long interleavedSamplesAtEncodeRate;
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
        this.channels = channels;
        this.inputSampleRate = inputSampleRate;

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels),
                "Opus encoding here supports mono and stereo (channel mapping family 0).");
        }
        if (inputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRate));

        settings ??= new OggOpusWriterSettings();

        encoder = new OpusEncoder(EncodeSampleRate, channels, settings.Application)
        {
            Bitrate = settings.Bitrate,
            UseVBR = settings.UseVariableBitrate,
            Complexity = settings.Complexity,
            SignalType = settings.SignalType
        };

        // The encoder's lookahead IS the pre-skip: those leading output samples are warm-up, not
        // audio, and the decoder discards exactly this many.
        preSkip = encoder.Lookahead;

        if (inputSampleRate != EncodeSampleRate)
        {
            resampler = ResamplerFactory.CreateResampler(
                channels, inputSampleRate, EncodeSampleRate, settings.ResamplerQuality);
        }

        frame = new float[FrameSamplesPerChannel * channels];
        pages = new OggPageWriter(destination, serialNumber, leaveOpen: true);

        WriteHeaders(inputSampleRate, settings);
    }

    /// <summary>The pre-skip written into the identification header.</summary>
    public int PreSkip => preSkip;

    /// <summary>Encodes and writes interleaved samples at the declared input rate.</summary>
    /// <param name="samples">Interleaved samples in [-1, 1].</param>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (finished) throw new InvalidOperationException("This writer has already been finished.");
        if (samples.Length == 0) return;

        var atEncodeRate = resampler == null ? samples : Resample(samples);

        // Accumulate INTERLEAVED samples and divide once, at the end. Dividing per call would
        // truncate whenever a caller writes a chunk that is not a whole number of frames, and
        // those half-frames add up: writing 0.5 s of stereo in 777-sample pieces loses 31 frames.
        interleavedSamplesAtEncodeRate += atEncodeRate.Length;

        var consumed = 0;
        while (consumed < atEncodeRate.Length)
        {
            var take = Math.Min(frame.Length - frameFill, atEncodeRate.Length - consumed);
            atEncodeRate.Slice(consumed, take).CopyTo(frame.AsSpan(frameFill, take));

            frameFill += take;
            consumed += take;

            if (frameFill == frame.Length) EncodeFrame();
        }
    }

    /// <summary>
    /// Flushes the last partial frame and closes the logical bitstream.
    /// </summary>
    /// <remarks>
    /// Two things have to be right here or the file plays but misreports itself. The tail is
    /// padded with silence so the encoder gets a whole frame, and then the final page's granule
    /// position is set to the TRUE sample count - pre-skip plus the audio actually supplied - so
    /// a decoder trims that padding instead of playing it.
    /// </remarks>
    public void Finish()
    {
        if (finished) return;
        finished = true;

        var targetGranule = preSkip + (interleavedSamplesAtEncodeRate / channels);

        if (frameFill > 0)
        {
            Array.Clear(frame, frameFill, frame.Length - frameFill);
            frameFill = frame.Length;
            EncodeFrame();
        }

        // Keep emitting silent frames until the stream is long enough to contain every sample the
        // caller supplied plus the priming the decoder will discard.
        while (granuleWritten < targetGranule)
        {
            Array.Clear(frame, 0, frame.Length);
            frameFill = frame.Length;
            EncodeFrame();
        }

        pages.SetPendingGranule(targetGranule);
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

    private void WriteHeaders(int inputSampleRate, OggOpusWriterSettings settings)
    {
        var head = new OpusHead
        {
            ChannelCount = channels,
            PreSkip = preSkip,
            InputSampleRate = inputSampleRate,
            OutputGainQ78 = 0,
            ChannelMappingFamily = 0
        };

        // RFC 7845 requires the identification header to be alone on the first page, and the
        // comment header to start on the page after it.
        pages.WritePacket(head.ToBytes(), 0);
        pages.FlushPage(endOfStream: false);

        var tags = new OpusTags { Vendor = settings.Vendor };
        foreach (var pair in settings.Tags)
        {
            tags.Tags[pair.Key] = new List<string>(pair.Value);
        }

        pages.WritePacket(tags.ToBytes(), 0);
        pages.FlushPage(endOfStream: false);
    }

    private void EncodeFrame()
    {
        var bytes = encoder.Encode(frame, FrameSamplesPerChannel, packetBuffer, packetBuffer.Length);

        if (bytes <= 0)
        {
            throw new InvalidOperationException(
                $"The Opus encoder returned {bytes} for a frame of {FrameSamplesPerChannel} samples.");
        }

        granuleWritten += FrameSamplesPerChannel;

        var packet = new byte[bytes];
        Array.Copy(packetBuffer, packet, bytes);
        pages.WritePacket(packet, granuleWritten);

        frameFill = 0;
    }

    private ReadOnlySpan<float> Resample(ReadOnlySpan<float> samples)
    {
        // ProcessInterleaved counts in FRAMES PER CHANNEL, not interleaved samples, even though
        // the buffers themselves are interleaved - it walks each channel with a stride. Handing
        // it interleaved totals makes it read a whole channel's worth past the end of the input.
        var inputFrames = samples.Length / channels;

        // Size for the ratio, plus a whole frame of slack: the resampler carries state between
        // calls, so a given call can emit slightly more than the ratio alone predicts. It reports
        // what it actually produced, which is what gets returned.
        var estimateFrames = (int)(((long)inputFrames * EncodeSampleRate) / inputSampleRate)
                             + FrameSamplesPerChannel;

        if (resampleBuffer.Length < estimateFrames * channels)
        {
            resampleBuffer = new float[estimateFrames * channels];
        }

        var input = new float[inputFrames * channels];
        samples[..(inputFrames * channels)].CopyTo(input);

        var consumedFrames = inputFrames;
        var producedFrames = resampleBuffer.Length / channels;

        resampler.ProcessInterleaved(input, ref consumedFrames, resampleBuffer, ref producedFrames);

        return resampleBuffer.AsSpan(0, producedFrames * channels);
    }
}
