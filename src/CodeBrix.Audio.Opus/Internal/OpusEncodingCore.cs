using System;
using System.Collections.Generic;
using CodeBrix.Audio.Opus.Codec;
using CodeBrix.Audio.Opus.Codec.Structs;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus.Internal;

/// <summary>
/// The one Opus encoding core in this package: input resampling, 20 ms frame accumulation, the
/// encoder's lookahead (the pre-skip), the identification header, and the end-of-stream padding
/// rule. It turns interleaved float samples into a queue of Opus packets and knows nothing about
/// any container.
/// </summary>
/// <remarks>
/// <para>
/// Every encoding face sits on this class. <see cref="OggOpusWriter" /> lays the packets into Ogg
/// pages (and through it <see cref="OpusFileWriter" /> and the engine's Opus sound encoder);
/// <see cref="OpusPacketEncoder" /> hands them to the caller for another container. Keeping a
/// single core is what guarantees that the packets a container receives are, byte for byte, the
/// packets an .opus file of the same input would hold.
/// </para>
/// <para>
/// Each packet is queued with its first sample on the STREAM timeline at 48 kHz: the first packet
/// starts at zero with the pre-skip still inside it, exactly as Ogg's granule positions count.
/// </para>
/// <para>
/// Input that is not at 48 kHz goes through a resampler, which delays everything it outputs by a
/// fixed amount. That delay is added to the pre-skip (so a decoder discards it along with the
/// encoder's warm-up), and <see cref="Finish" /> pushes silence through the resampler to drain the
/// last of the caller's audio out of it. The decoded stream then starts where the input started
/// and is as long as the input, at 48 kHz.
/// </para>
/// </remarks>
internal sealed class OpusEncodingCore
{
    /// <summary>Opus encodes at this rate, always.</summary>
    public const int EncodeSampleRate = 48000;

    /// <summary>20 ms at 48 kHz - the frame size opusenc and ffmpeg both default to.</summary>
    public const int FrameSamplesPerChannel = 960;

    /// <summary>Generous ceiling for one encoded packet.</summary>
    private const int MaxPacketBytes = 4000;

    private readonly int channels;
    private readonly int inputSampleRate;
    private readonly OpusEncoder encoder;
    private readonly IResampler resampler;
    private readonly float[] frame;
    private readonly byte[] packetBuffer = new byte[MaxPacketBytes];
    private readonly int preSkip;
    private readonly int resamplerDelay;
    private readonly byte[] identificationHeader;
    private readonly Queue<(byte[] Data, long FirstSample)> packets = new Queue<(byte[] Data, long FirstSample)>();

    private float[] resampleBuffer = Array.Empty<float>();
    private int frameFill;
    private long samplesEncoded;
    private long interleavedInputSamples;
    private long interleavedSamplesAtEncodeRate;
    private bool finished;

    /// <summary>Creates the encoder, fixing the pre-skip and the identification header.</summary>
    /// <param name="channels">Channels in the input, 1 or 2.</param>
    /// <param name="inputSampleRate">The rate of the samples that will be written.</param>
    /// <param name="settings">Encoder settings, or null for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="channels" /> is not 1 or 2, or <paramref name="inputSampleRate" /> is not positive.
    /// </exception>
    public OpusEncodingCore(int channels, int inputSampleRate, OggOpusWriterSettings settings)
    {
        CheckFormat(channels, inputSampleRate);

        this.channels = channels;
        this.inputSampleRate = inputSampleRate;

        settings ??= new OggOpusWriterSettings();

        encoder = new OpusEncoder(EncodeSampleRate, channels, settings.Application)
        {
            Bitrate = settings.Bitrate,
            UseVBR = settings.UseVariableBitrate,
            Complexity = settings.Complexity,
            SignalType = settings.SignalType
        };

        if (inputSampleRate != EncodeSampleRate)
        {
            resampler = ResamplerFactory.CreateResampler(
                channels, inputSampleRate, EncodeSampleRate, settings.ResamplerQuality);

            // The resampler's output lags its input by this many 48 kHz samples. It has to be known
            // now, not at Finish, because it goes into the identification header written first.
            resamplerDelay = resampler.OutputLatencySamples;
        }

        // The pre-skip is everything at the head of the stream that is not the caller's audio: the
        // encoder's lookahead (warm-up the decoder discards) plus, for resampled input, the
        // resampler's delay ahead of the first input sample.
        preSkip = encoder.Lookahead + resamplerDelay;

        frame = new float[FrameSamplesPerChannel * channels];

        // The declared input rate is recorded, because RFC 7845 keeps it as a record of what the
        // encoder was given - it is not, and must never become, a decoding parameter.
        identificationHeader = new OpusHead
        {
            ChannelCount = channels,
            PreSkip = preSkip,
            InputSampleRate = inputSampleRate,
            OutputGainQ78 = 0,
            ChannelMappingFamily = 0
        }.ToBytes();
    }

    /// <summary>Channels in the input and in the packets.</summary>
    public int Channels => channels;

    /// <summary>The rate of the samples handed to <see cref="Write" />.</summary>
    public int InputSampleRate => inputSampleRate;

    /// <summary>
    /// What a decoder discards at the start, in 48 kHz samples: the encoder's lookahead plus, for
    /// input not at 48 kHz, the input resampler's delay.
    /// </summary>
    public int PreSkip => preSkip;

    /// <summary>The input resampler's delay in 48 kHz samples, part of <see cref="PreSkip" />; 0 at 48 kHz.</summary>
    public int ResamplerDelay => resamplerDelay;

    /// <summary>
    /// The 19-byte family-0 OpusHead for this stream. The array is owned by this core; callers
    /// that hand it out must copy it or wrap it read-only.
    /// </summary>
    public byte[] IdentificationHeader => identificationHeader;

    /// <summary>Input frames (one sample per channel) accepted so far, at the input rate.</summary>
    public long InputFrames => interleavedInputSamples / channels;

    /// <summary>48 kHz samples (per channel) emitted in packets so far.</summary>
    public long SamplesEncoded => samplesEncoded;

    /// <summary>
    /// Where the audible stream ends on the 48 kHz timeline: the pre-skip plus the input's length
    /// at the encode rate (the input frames scaled to 48 kHz and rounded to the nearest sample).
    /// This is the final granule position of an Ogg Opus stream.
    /// </summary>
    public long TargetSampleCount => preSkip + AtEncodeRate(InputFrames);

    /// <summary>Whether <see cref="Finish" /> has run.</summary>
    public bool IsFinished => finished;

    /// <summary>Packets queued and not yet taken.</summary>
    public int PendingPackets => packets.Count;

    /// <summary>Throws when a channel count or input rate is not one this encoder accepts.</summary>
    /// <param name="channels">Channels in the input.</param>
    /// <param name="inputSampleRate">The rate of the input.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either value is out of range.</exception>
    public static void CheckFormat(int channels, int inputSampleRate)
    {
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels),
                "Opus encoding here supports mono and stereo (channel mapping family 0).");
        }
        if (inputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRate));
    }

    /// <summary>Throws when an interleaved sample count is not a whole number of frames.</summary>
    /// <param name="sampleCount">The interleaved sample count offered.</param>
    /// <param name="paramName">The caller's parameter name, for the exception.</param>
    /// <exception cref="ArgumentException"><paramref name="sampleCount" /> is not a multiple of <see cref="Channels" />.</exception>
    public void CheckWholeFrames(int sampleCount, string paramName)
    {
        if (sampleCount % channels != 0)
        {
            throw new ArgumentException(
                $"The sample count ({sampleCount}) must be a whole number of {channels}-channel frames.",
                paramName);
        }
    }

    /// <summary>Takes the oldest queued packet.</summary>
    /// <param name="data">The packet bytes, an array owned by the caller from now on.</param>
    /// <param name="firstSample">The packet's first sample on the 48 kHz stream timeline.</param>
    /// <returns>True when a packet was taken.</returns>
    public bool TryDequeue(out byte[] data, out long firstSample)
    {
        if (packets.TryDequeue(out var packet))
        {
            data = packet.Data;
            firstSample = packet.FirstSample;
            return true;
        }

        data = null;
        firstSample = 0;
        return false;
    }

    /// <summary>Drops every queued packet.</summary>
    public void ClearQueue() => packets.Clear();

    /// <summary>Encodes interleaved samples at the input rate, queueing every packet that completes.</summary>
    /// <param name="samples">Interleaved samples in [-1, 1]: a whole number of frames.</param>
    /// <exception cref="InvalidOperationException"><see cref="Finish" /> has already run.</exception>
    /// <exception cref="ArgumentException">The length is not a multiple of <see cref="Channels" />.</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (finished) throw new InvalidOperationException("This encoder has already been finished.");

        // A partial frame is refused rather than buffered: at 48 kHz the stray sample would shift
        // the interleave for the rest of the stream (left becoming right), and on the resampled
        // path it would be silently dropped.
        CheckWholeFrames(samples.Length, nameof(samples));
        if (samples.Length == 0) return;

        interleavedInputSamples += samples.Length;

        Append(resampler == null ? samples : Resample(samples));
    }

    /// <summary>Feeds 48 kHz interleaved samples into the frame buffer, encoding each frame that fills.</summary>
    private void Append(ReadOnlySpan<float> atEncodeRate)
    {
        // Accumulate INTERLEAVED samples and divide once, at the end. A resampler call need not
        // hand back a whole number of 20 ms frames, so the frame buffer simply carries whatever
        // is left over into the next call.
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
    /// Ends the stream: drains the input resampler (if there is one) by pushing silence through
    /// it, pads the last partial frame with silence and encodes it, then encodes silent frames
    /// until the packets hold the pre-skip plus every input sample. Idempotent.
    /// </summary>
    /// <remarks>
    /// After this, <see cref="SamplesEncoded" /> minus <see cref="TargetSampleCount" /> is the
    /// padding at the end that a decoder must discard - what an Ogg stream states through its
    /// final granule position and a Matroska stream through DiscardPadding.
    /// </remarks>
    public void Finish()
    {
        if (finished) return;
        finished = true;

        var targetSamples = TargetSampleCount;

        if (resampler != null) DrainResampler();

        if (frameFill > 0)
        {
            Array.Clear(frame, frameFill, frame.Length - frameFill);
            frameFill = frame.Length;
            EncodeFrame();
        }

        // Keep emitting silent frames until the stream is long enough to contain every sample the
        // caller supplied plus the priming the decoder will discard.
        while (samplesEncoded < targetSamples)
        {
            Array.Clear(frame, 0, frame.Length);
            frameFill = frame.Length;
            EncodeFrame();
        }
    }

    private void EncodeFrame()
    {
        var bytes = encoder.Encode(frame, FrameSamplesPerChannel, packetBuffer, packetBuffer.Length);

        if (bytes <= 0)
        {
            throw new InvalidOperationException(
                $"The Opus encoder returned {bytes} for a frame of {FrameSamplesPerChannel} samples.");
        }

        var packet = new byte[bytes];
        Array.Copy(packetBuffer, packet, bytes);
        packets.Enqueue((packet, samplesEncoded));

        samplesEncoded += FrameSamplesPerChannel;
        frameFill = 0;
    }

    /// <summary>The 48 kHz length of a run of input frames, rounded to the nearest sample.</summary>
    private long AtEncodeRate(long inputFrames) =>
        resampler == null
            ? inputFrames
            : ((inputFrames * EncodeSampleRate) + (inputSampleRate / 2)) / inputSampleRate;

    /// <summary>
    /// Pushes silence through the resampler until everything the caller wrote has come out of it:
    /// the resampler's delay plus the input's length at 48 kHz. Anything beyond that is the
    /// filter's ringing past the end of the input, which the end trim would discard anyway, so it
    /// is not appended.
    /// </summary>
    private void DrainResampler()
    {
        var produced = interleavedSamplesAtEncodeRate / channels;
        var remaining = resamplerDelay + AtEncodeRate(InputFrames) - produced;

        for (var attempts = 0; remaining > 0; attempts++)
        {
            if (attempts > 64)
            {
                throw new InvalidOperationException("The input resampler did not drain.");
            }

            // Enough silent input to cover what is still owed, plus one frame of slack for the
            // resampler's fractional position.
            var silentFrames = (int)((remaining * inputSampleRate / EncodeSampleRate) + 1);
            var output = Resample(new float[silentFrames * channels]);

            var take = (int)Math.Min(remaining, output.Length / channels);
            Append(output[..(take * channels)]);
            remaining -= take;
        }
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
