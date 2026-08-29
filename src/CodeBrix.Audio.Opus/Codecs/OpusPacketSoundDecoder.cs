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

    /// <summary>
    /// The smallest stretch Opus will conceal in one go, per channel: 2.5 ms at 48 kHz. Every
    /// concealment length the codec accepts is a whole number of these.
    /// </summary>
    private const int ConcealmentStepSamples = 120;

    private readonly object syncLock = new object();
    private readonly int channels;
    private readonly int preSkip;

    private IOpusDecoder decoder;
    private bool disposed;

    // How long the last REAL packet was, per channel - the guess an empty packet is concealed for.
    // The codec's own LastPacketDuration cannot be used for it, because concealment updates that
    // too: after ConcealLoss(5760) it reads 5760, and an empty packet arriving next would then be
    // taken to mean 120 ms of loss rather than one 20 ms packet. This is only ever set by a packet
    // that carried bytes, and Reset clears it exactly as the codec clears its own.
    private int lastRealPacketFrames;

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
    /// <para>
    /// AN EMPTY PACKET MEANS A LOST ONE, and is concealed rather than refused: the codec is asked
    /// to invent a plausible continuation for the audio that went missing (packet loss
    /// concealment), which is what keeps a live stream from clicking at every dropped packet. The
    /// gap is taken to be as long as the last real packet decoded, or 20 ms when nothing has been
    /// decoded yet. Feed a zero-length packet for every packet the source knows it lost, and
    /// nothing at all for a gap it does not know about.
    /// </para>
    /// <para>
    /// A CALLER THAT KNOWS HOW LONG THE GAP WAS should call <see cref="ConcealLoss" /> instead,
    /// which conceals the length the container actually lost rather than assuming one packet.
    /// </para>
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

            if (decoded <= 0) return 0;

            if (!packet.IsEmpty)
            {
                lastRealPacketFrames = decoded;
            }

            return decoded * channels;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// True: packet-loss concealment is part of the Opus specification, so a gap here becomes
    /// SYNTHESISED AUDIO that continues the pitch and the spectral shape of what came before it,
    /// not the silence a codec without concealment has to fall back on.
    /// </remarks>
    public bool SupportsLossConcealment => true;

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">
    /// The codec refused to conceal. It cannot happen for a buffer sized to
    /// <see cref="MaxSamplesPerPacket" />, because every length this asks for is one the codec
    /// accepts; it is reported rather than swallowed so a defect here cannot be mistaken for a
    /// codec that simply has no concealment.
    /// </exception>
    /// <remarks>
    /// <para>
    /// THE CHUNKING RULE. Opus conceals in whole steps of 2.5 ms - 120 frames at 48 kHz - and never
    /// more than 120 ms (5760 frames) in one call, so a gap is covered over several calls and this
    /// returns what ONE call covered:
    /// </para>
    /// <list type="number">
    /// <item>
    /// the room available is <c>output.Length / Channels</c>, capped at 5760 frames;
    /// </item>
    /// <item>
    /// a buffer with room for less than one 2.5 ms step conceals nothing and returns 0, so the
    /// caller fills the gap itself - it is not an error, and it cannot happen for a buffer sized to
    /// <see cref="MaxSamplesPerPacket" />;
    /// </item>
    /// <item>
    /// otherwise the chunk is the smaller of <paramref name="lostFrames" /> and the room, ROUNDED
    /// DOWN to a whole number of 2.5 ms steps - so a gap that is a multiple of 120 frames is
    /// covered exactly, with nothing left over;
    /// </item>
    /// <item>
    /// THE REMAINDER. When what is left of the gap is shorter than 2.5 ms, rounding down would
    /// reach zero and the loop would never finish, so the chunk is ROUNDED UP to one 2.5 ms step.
    /// The codec's state therefore advances by up to 2.5 ms more than was lost - there is no
    /// shorter concealment to ask it for - but the RETURN VALUE is still capped at
    /// <paramref name="lostFrames" />, so the caller is told exactly the length it asked about and
    /// the timeline keeps its shape to the sample. The surplus frames are written into the buffer
    /// past the returned count and are meant to be ignored.
    /// </item>
    /// </list>
    /// <para>
    /// AFTER <see cref="Reset" />, AND BEFORE THE FIRST PACKET, THIS PRODUCES SILENCE. Concealment
    /// continues the audio the decoder last saw, and a decoder that has been reset has not seen
    /// any: the codec answers with zeros of exactly the length asked for rather than refusing. It
    /// is safe to call, in other words, but it is not worth calling until a real packet has been
    /// decoded - which is also why a player seeking into a stream feeds its pre-roll first.
    /// </para>
    /// <para>
    /// <see cref="DecodePacket" /> with an EMPTY packet is unchanged and still conceals one
    /// packet's worth - the last packet's duration, or 20 ms before anything has been decoded.
    /// That is the lengthless convention; this method is the one that knows how long the gap was.
    /// </para>
    /// </remarks>
    public int ConcealLoss(int lostFrames, Span<float> output)
    {
        lock (syncLock)
        {
            if (disposed || lostFrames <= 0) return 0;

            var room = Math.Min(output.Length / channels, MaxFrameSamples);

            if (room < ConcealmentStepSamples)
            {
                // Not even one 2.5 ms step fits. Nothing to do but let the caller fill the gap.
                return 0;
            }

            var wanted = Math.Min(lostFrames, room);
            var chunk = wanted / ConcealmentStepSamples * ConcealmentStepSamples;

            if (chunk == 0)
            {
                // The remainder: less than one step is still asked for, and the codec has no
                // shorter concealment than a step. Round up, and report only what was asked for.
                chunk = ConcealmentStepSamples;
            }

            int concealed;

            try
            {
                concealed = decoder.Decode(
                    ReadOnlySpan<byte>.Empty, output, chunk, decode_fec: false);
            }
            catch (Exception ex)
            {
                // The codec's own exception type is internal to this assembly, so letting it out
                // would hand the caller something it cannot name in a catch clause.
                throw new InvalidDataException(
                    $"This Opus decoder could not conceal a gap of {chunk} frames per channel.", ex);
            }

            if (concealed <= 0) return 0;

            // Never claim more than the gap: the rounded-up surplus is in the buffer, but it is not
            // part of the loss and counting it would push everything after the gap out of place.
            return Math.Min(concealed, lostFrames) * channels;
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (syncLock)
        {
            if (disposed) return;

            lastRealPacketFrames = 0;
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
    /// The last REAL packet's duration is the best guess available - a stream's packets are almost
    /// always all the same length - and 20 ms is the fallback before any packet has been decoded.
    /// Concealment already produced does not count towards it: a gap covered by
    /// <see cref="ConcealLoss" /> would otherwise redefine what "one packet" means for the empty
    /// packet after it.
    /// </remarks>
    private int ConcealmentFrames()
    {
        var last = lastRealPacketFrames;

        return last > 0 && last <= MaxFrameSamples ? last : DefaultConcealmentSamples;
    }
}
