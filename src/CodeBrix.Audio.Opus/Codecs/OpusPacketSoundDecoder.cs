using System;
using System.IO;
using CodeBrix.Audio.Engine.Enums;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Opus.Codec;
using CodeBrix.Audio.Opus.Codec.Structs;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus.Codecs;

/// <summary>
/// Decodes Opus audio one container packet at a time, using the fully managed codec in
/// <c>CodeBrix.Audio.Opus.Codec</c>.
/// </summary>
/// <remarks>
/// <para>
/// The identification header arrives in the container's codec-private data rather than as the
/// first Ogg page, so it is parsed once by <see cref="OpusPacketCodecFactory" /> and handed here;
/// from then on each packet the demultiplexer produces is decoded on its own.
/// </para>
/// <para>
/// It DOES apply the header's output gain, through the codec's own gain control, exactly as the
/// Ogg reader does.
/// </para>
/// <para>
/// TWO THINGS THIS DELIBERATELY DOES NOT DO. It does not discard the pre-skip - it REPORTS it, as
/// <see cref="PreSkipSamples" />, because the caller is the one that knows whether it is at the
/// start of the stream or in the middle of a seek. And it does not trim the tail of the stream:
/// the container states where the audio really stops (a discard-padding field, a total-sample
/// count), so the caller applies that to what comes back.
/// </para>
/// </remarks>
internal sealed class OpusPacketSoundDecoder : IPacketSoundDecoder
{
    /// <summary>Largest frame Opus can produce, per channel: 120 ms at 48 kHz.</summary>
    private const int MaxFrameSamples = 5760;

    /// <summary>
    /// The span of audio a lost packet is concealed over when nothing better is known: 20 ms, the
    /// frame size almost every Opus stream in a media container is encoded at.
    /// </summary>
    private const int DefaultConcealmentSamples = 960;

    private readonly object syncLock = new object();
    private readonly int channels;
    private readonly int preSkip;

    private IOpusDecoder decoder;
    private bool disposed;

    /// <summary>Creates a decoder from a parsed Opus identification header.</summary>
    /// <param name="head">
    /// The identification header the container carried, already checked for a supported channel
    /// mapping.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="head" /> is null.</exception>
    public OpusPacketSoundDecoder(OpusHead head)
    {
        if (head == null) throw new ArgumentNullException(nameof(head));

        channels = head.ChannelCount;
        preSkip = head.PreSkip;

        // 48000 UNCONDITIONALLY. The header's input sample rate records the rate the ENCODER was
        // fed - 16000 for a typical voice note, and permitted to be 0 - and RFC 7845 marks it
        // informational. Building the decoder from it would make a voice note play three times too
        // slow, which is the same rule OpusSoundDecoder states for the stream path.
        decoder = new OpusDecoder(OggOpusReader.DecodeSampleRate, channels);

        // The header's output gain, applied exactly as the Ogg reader applies it - through the
        // codec's own Q7.8 dB gain control, which is the same fixed-point format the header stores
        // it in. Doing it here rather than in managed code afterwards is what keeps the two seams
        // sample-identical, and it survives Reset().
        decoder.Gain = head.OutputGainQ78;
    }

    /// <inheritdoc />
    public int Channels => channels;

    /// <inheritdoc />
    /// <remarks>Always 48000: the only rate Opus decodes at.</remarks>
    public int SampleRate => OggOpusReader.DecodeSampleRate;

    /// <inheritdoc />
    public SampleFormat SampleFormat => SampleFormat.F32;

    /// <inheritdoc />
    /// <remarks>
    /// A worst case from the codec's own limits, not from any particular packet: 5760 samples per
    /// channel is a 120 ms packet at 48 kHz, the longest Opus defines.
    /// </remarks>
    public int MaxSamplesPerPacket => MaxFrameSamples * channels;

    /// <inheritdoc />
    /// <remarks>
    /// The encoder's priming, taken from the identification header, counted PER CHANNEL on the
    /// 48 kHz clock - the same unit the header itself uses. Discard this many frames at the start
    /// of the stream, on top of any start trim the container asks for separately.
    /// </remarks>
    public int PreSkipSamples => preSkip;

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">
    /// The packet is corrupt, truncated, or not an Opus packet.
    /// </exception>
    /// <remarks>
    /// AN EMPTY PACKET MEANS A LOST ONE, and is concealed rather than refused: the codec is asked
    /// to invent a plausible continuation for the audio that went missing (packet loss
    /// concealment), which is what keeps a live stream from clicking at every dropped packet. The
    /// gap is taken to be as long as the last packet decoded, or 20 ms when nothing has been
    /// decoded yet. Feed a zero-length packet for every packet the source knows it lost, and
    /// nothing at all for a gap it does not know about.
    /// </remarks>
    public int DecodePacket(ReadOnlySpan<byte> packet, Span<float> output)
    {
        lock (syncLock)
        {
            if (disposed) return 0;

            var samplesPerChannel = packet.IsEmpty
                ? ConcealmentFrames()
                : OpusPacketInfo.GetNumSamples(packet, OggOpusReader.DecodeSampleRate);

            // A packet whose length cannot be worked out is a corrupt one. Size the check by the
            // worst case so the buffer is still proved big enough, and let the codec report what is
            // actually wrong with the bytes.
            var required = (samplesPerChannel > 0 ? samplesPerChannel : MaxFrameSamples) * channels;

            if (output.Length < required)
            {
                throw new ArgumentException(
                    $"The output buffer holds {output.Length} samples, and this packet decodes to " +
                    $"{required}. Size the buffer to MaxSamplesPerPacket ({MaxSamplesPerPacket} " +
                    "samples for this stream) and no packet can ever be too big for it.",
                    nameof(output));
            }

            int decoded;

            try
            {
                decoded = decoder.Decode(
                    packet,
                    output,
                    samplesPerChannel > 0 ? samplesPerChannel : MaxFrameSamples,
                    decode_fec: false);
            }
            catch (Exception ex)
            {
                // The codec's own exception type is internal to this assembly, so letting it out
                // would hand the caller something it cannot name in a catch clause. This is the
                // same failure the stream reader reports the same way.
                throw new InvalidDataException(
                    "This Opus packet could not be decoded; it is corrupt, truncated, or not an " +
                    "Opus packet at all.", ex);
            }

            return decoded > 0 ? decoded * channels : 0;
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (syncLock)
        {
            if (disposed) return;

            decoder.ResetState();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (syncLock)
        {
            if (disposed) return;
            disposed = true;

            decoder.Dispose();
            decoder = null;
        }
    }

    /// <summary>How long a gap to conceal for a lost packet, in samples per channel.</summary>
    /// <remarks>
    /// The last packet's duration is the best guess available - a stream's packets are almost
    /// always all the same length - and 20 ms is the fallback before any packet has been decoded.
    /// </remarks>
    private int ConcealmentFrames()
    {
        var last = decoder.LastPacketDuration;

        return last > 0 && last <= MaxFrameSamples ? last : DefaultConcealmentSamples;
    }
}
