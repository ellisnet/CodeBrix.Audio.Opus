using System;
using CodeBrix.Audio.Opus.Internal;

namespace CodeBrix.Audio.Opus;

/// <summary>
/// Encodes samples into separate Opus packets plus the Opus identification header ("OpusHead"),
/// for containers that carry Opus as a sequence of packets with the header as codec-private data
/// - Matroska / WebM, MP4, or a network stream.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>Encode</c> call queues every 20 ms packet it completes; take them in order with
/// <see cref="TryReadPacket" />. <see cref="Finish" /> pads and encodes the last partial frame,
/// adds whatever silent packets the stream needs to hold the pre-skip plus every input sample, and
/// fixes <see cref="TrailingTrimSamples" />. Nothing is written anywhere - the header and the
/// packets are the whole output.
/// </para>
/// <para>
/// THE TIMELINE. Packets are stamped on the stream's own 48 kHz timeline: the first starts at 0
/// with <see cref="PreSkip" /> samples of encoder warm-up still inside it. A container states the
/// pre-skip separately (Matroska's CodecDelay, the OpusHead pre-skip field) and states
/// <see cref="TrailingTrimSamples" /> as the padding to drop at the very end (Matroska's
/// DiscardPadding on the last block).
/// </para>
/// <para>
/// For the same input and tuning, the packets are byte for byte the audio packets of the .opus
/// file <see cref="OpusFileWriter" /> writes, and <see cref="IdentificationHeader" /> is byte for
/// byte that file's identification header.
/// </para>
/// <para>
/// An instance is not thread-safe; use it from one thread at a time. Separate instances are
/// independent.
/// </para>
/// </remarks>
public sealed class OpusPacketEncoder : IDisposable
{
    private readonly OpusEncodingCore core;
    private readonly ReadOnlyMemory<byte> identificationHeader;

    private int trailingTrimSamples;

    /// <summary>
    /// Creates a packet encoder. <see cref="PreSkip" /> and <see cref="IdentificationHeader" /> are
    /// fixed and available at once.
    /// </summary>
    /// <param name="channels">Channels in the input and the packets: 1 or 2 (channel mapping family 0).</param>
    /// <param name="inputSampleRate">
    /// The rate of the samples that will be encoded. Any positive rate is accepted; anything but
    /// 48000 is resampled on the way in, exactly as <see cref="OpusFileWriter" /> does.
    /// </param>
    /// <param name="options">Encoder options, or null for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="channels" /> is not 1 or 2, <paramref name="inputSampleRate" /> is not
    /// positive, or an option is out of range.
    /// </exception>
    public OpusPacketEncoder(int channels, int inputSampleRate, OpusEncoderOptions options = null)
    {
        OpusEncodingCore.CheckFormat(channels, inputSampleRate);

        options ??= new OpusEncoderOptions();
        options.Validate();

        core = new OpusEncodingCore(channels, inputSampleRate, OpusEncoderSettingsMapper.ToSettings(options));
        identificationHeader = (byte[])core.IdentificationHeader.Clone();
    }

    /// <summary>The number of interleaved channels, in the input and in the packets.</summary>
    public int Channels => core.Channels;

    /// <summary>The rate, in hertz, of the samples handed to the <c>Encode</c> methods.</summary>
    public int InputSampleRate => core.InputSampleRate;

    /// <summary>The rate of the packets and of their timeline: always 48000, whatever the input rate.</summary>
    public int SampleRate => OpusEncodingCore.EncodeSampleRate;

    /// <summary>
    /// What a decoder discards at the start of the first packet(s), in 48 kHz samples: the
    /// encoder's lookahead (warm-up) plus, when <see cref="InputSampleRate" /> is not 48000, the
    /// input resampler's delay, so that the decoded audio starts exactly where the input started.
    /// Fixed at construction; it is the pre-skip field of
    /// <see cref="IdentificationHeader" /> and the value a Matroska muxer states as CodecDelay
    /// (converted to nanoseconds).
    /// </summary>
    public int PreSkip => core.PreSkip;

    /// <summary>
    /// The 19-byte Opus identification header ("OpusHead", RFC 7845 section 5.1, channel mapping
    /// family 0, the input rate recorded, output gain 0) - the bytes a container stores as the
    /// track's codec-private data. Valid from construction (nothing in it changes) and still
    /// readable after <see cref="Dispose" />.
    /// </summary>
    public ReadOnlyMemory<byte> IdentificationHeader => identificationHeader;

    /// <summary>Input frames (one sample per channel) accepted so far, at <see cref="InputSampleRate" />.</summary>
    public long FramesWritten => core.InputFrames;

    /// <summary>
    /// 48 kHz samples (per channel) emitted in packets so far: 960 for every packet made, whether
    /// or not it has been taken yet.
    /// </summary>
    public long SamplesEncoded => core.SamplesEncoded;

    /// <summary>The duration of the audio accepted so far: <see cref="FramesWritten" /> at <see cref="InputSampleRate" />.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)core.InputFrames / core.InputSampleRate);

    /// <summary>True once <see cref="Finish" /> has ended the stream.</summary>
    public bool IsFinished => core.IsFinished;

    /// <summary>True once <see cref="Dispose" /> has run.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>The number of encoded packets waiting to be taken with <see cref="TryReadPacket" />.</summary>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public int PendingPackets
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return core.PendingPackets;
        }
    }

    /// <summary>
    /// The 48 kHz samples (per channel) at the END of the stream that a decoder must discard: the
    /// silence that filled out the last packet, beyond <see cref="PreSkip" /> plus every input
    /// sample. It is what a Matroska muxer writes as DiscardPadding on the last block, and what an
    /// Ogg writer expresses through the final granule position.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Finish" /> has not run - an unfinished stream has no tail yet.
    /// </exception>
    public int TrailingTrimSamples
    {
        get
        {
            if (!core.IsFinished)
            {
                throw new InvalidOperationException(
                    "The stream has not been ended by Finish, so it has no trailing trim yet.");
            }

            return trailingTrimSamples;
        }
    }

    /// <summary>
    /// Encodes interleaved samples in [-1, 1] at <see cref="InputSampleRate" /> and queues every
    /// 20 ms packet that completes. A partial frame is kept for the next call.
    /// </summary>
    /// <param name="interleaved">The samples: a whole number of frames (a multiple of <see cref="Channels" />).</param>
    /// <exception cref="ArgumentException">The length is not a multiple of <see cref="Channels" />.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish" /> has already ended the stream.</exception>
    public void Encode(ReadOnlySpan<float> interleaved)
    {
        CheckWritable();

        // The check the encoding core makes itself, made here first so the exception names this
        // method's parameter.
        core.CheckWholeFrames(interleaved.Length, nameof(interleaved));

        core.Write(interleaved);
    }

    /// <summary>
    /// Encodes interleaved samples in [-1, 1] at <see cref="InputSampleRate" /> and queues every
    /// 20 ms packet that completes.
    /// </summary>
    /// <param name="samples">The buffer holding the samples.</param>
    /// <param name="offset">Index of the first sample to encode.</param>
    /// <param name="count">How many samples to encode: a multiple of <see cref="Channels" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="samples" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the buffer.</exception>
    /// <exception cref="ArgumentException"><paramref name="count" /> is not a multiple of <see cref="Channels" />.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish" /> has already ended the stream.</exception>
    public void Encode(float[] samples, int offset, int count)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));

        Encode(samples.AsSpan(offset, count));
    }

    /// <summary>Takes the oldest queued packet, if there is one.</summary>
    /// <param name="packet">The packet, or the default value when none is queued.</param>
    /// <returns>True when a packet was taken.</returns>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public bool TryReadPacket(out OpusPacket packet)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (core.TryDequeue(out var data, out var firstSample))
        {
            packet = new OpusPacket(data, firstSample, OpusEncodingCore.FrameSamplesPerChannel);
            return true;
        }

        packet = default;
        return false;
    }

    /// <summary>
    /// Ends the stream: pads the last partial frame with silence and encodes it, then encodes
    /// further silent packets until the stream holds <see cref="PreSkip" /> plus every input sample
    /// (at 48 kHz), and fixes <see cref="TrailingTrimSamples" />. Queued packets can still be taken
    /// afterwards; nothing more can be encoded. Calling it again does nothing.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (core.IsFinished) return;

        core.Finish();
        trailingTrimSamples = (int)(core.SamplesEncoded - core.TargetSampleCount);
    }

    /// <summary>
    /// Releases the encoder, DISCARDING any packets not yet taken. It does NOT finish the stream:
    /// call <see cref="Finish" /> and take the remaining packets first. Calling it more than once
    /// does nothing.
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        core.ClearQueue();
    }

    private void CheckWritable()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (core.IsFinished)
        {
            throw new InvalidOperationException("The stream has been ended by Finish; nothing more can be encoded.");
        }
    }
}
