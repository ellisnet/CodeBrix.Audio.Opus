using System;
using System.Collections.Generic;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Engine.Structs;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus.Codecs;

/// <summary>
/// Supplies the CodeBrix.Audio engine with a fully managed Opus decoder for audio that arrives as
/// CONTAINER PACKETS - the shape a demultiplexer produces - rather than as an Ogg stream.
/// </summary>
/// <remarks>
/// <para>
/// This is the packet-level peer of <see cref="OpusCodecFactory" />. That one is handed a
/// <see cref="System.IO.Stream" /> with Ogg framing around it; this one is handed the bare Opus
/// packets a media container carried, one at a time, with no framing of their own.
/// </para>
/// <para>
/// Register it once at start-up - <see cref="CodeBrixAudioOpus.Register()" /> is the friendly way
/// and does both seams in the one call - or by hand:
/// </para>
/// <code>
/// SharedAudioOutput.RegisterPacketCodecFactory(new OpusPacketCodecFactory());
/// </code>
/// <para>
/// Hold ONE instance if you register it yourself: the shared output de-duplicates on the instance,
/// so a freshly constructed factory per call would register the codec repeatedly.
/// </para>
/// <para>
/// <see cref="Priority" /> is 0 - the built-in level - because there is no native packet decoder
/// for it to sit below. The engine's bundled native library decodes Ogg STREAMS, not loose
/// packets, so nothing competes for "opus" here and the -10 the stream factory uses would say
/// something that is not true.
/// </para>
/// </remarks>
public sealed class OpusPacketCodecFactory : IPacketCodecFactory
{
    /// <summary>The codec identifier this factory decodes packets for.</summary>
    /// <remarks>
    /// A CODEC identifier, not a container one: the packet registry is keyed by what is inside the
    /// packets, so "opus" rather than the "ogg" the stream seam shares with every other codec an
    /// Ogg container can hold.
    /// </remarks>
    public const string OpusCodecId = "opus";

    /// <inheritdoc />
    public string FactoryId => "CodeBrix.Audio.Opus.ManagedOpus.Packets";

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedCodecIds { get; } = new[] { OpusCodecId };

    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <paramref name="codecPrivate" /> is the Opus identification header - the "OpusHead" bytes of
    /// RFC 7845 section 5.1 - exactly as the container stored them. A Matroska or WebM track stores
    /// that header verbatim in its CodecPrivate element, so there is no unwrapping to do and no
    /// second header parser; a container of your own storing the same bytes works the same way.
    /// </para>
    /// <para>
    /// NULL MEANS "NOT MINE", and nothing else: another codec's identifier, or bytes that are not a
    /// well-formed identification header. The engine then offers the request to the next factory,
    /// which is how factories coexist.
    /// </para>
    /// <para>
    /// A header this package genuinely cannot decode is a different answer, and it THROWS rather
    /// than returning null - returning null would let the engine report the generic "no registered
    /// packet decoder" when the real answer is "that stream is multichannel". The engine logs a
    /// factory exception and moves on to the next factory, so the specific reason reaches the
    /// engine log rather than being lost. See the channel-mapping remarks below.
    /// </para>
    /// <para>
    /// <paramref name="hint" /> is IGNORED. An Opus stream always decodes at 48 kHz and at the
    /// channel count its header declares, and this decoder converts neither; the caller reads
    /// <see cref="IPacketSoundDecoder.SampleRate" /> and
    /// <see cref="IPacketSoundDecoder.Channels" /> for what it is actually going to get. The
    /// engine's own mixing path converts from there.
    /// </para>
    /// <para>
    /// MONO AND STEREO ONLY - channel mapping family 0, the same limit the Ogg reader has. A
    /// family-1 (multichannel) header is refused with a message naming the family and the channel
    /// count rather than mis-mapped into a stereo pair.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The header is well formed but describes audio this package does not decode: a channel
    /// mapping family other than 0, or a channel count other than 1 or 2.
    /// </exception>
    public IPacketSoundDecoder CreateDecoder(string codecId, ReadOnlyMemory<byte> codecPrivate, AudioFormat? hint)
    {
        if (!string.Equals(codecId, OpusCodecId, StringComparison.OrdinalIgnoreCase)) return null;

        // The container's codec-private data IS the identification header, so TryParse is the whole
        // header path. Bytes that are not one belong to some other codec - decline, do not throw.
        if (!OpusHead.TryParse(codecPrivate.Span, out var head)) return null;

        if (!head.IsSupportedMapping)
        {
            throw new NotSupportedException(
                head.ChannelMappingFamily == 0
                    ? $"Opus channel mapping family 0 with {head.ChannelCount} channels is not " +
                      "supported by this decoder; mapping family 0 is defined for mono and " +
                      "stereo only."
                    : $"Opus channel mapping family {head.ChannelMappingFamily} (surround, " +
                      $"{head.ChannelCount} channels) is not supported by this decoder; only " +
                      "mapping family 0 (mono/stereo) is supported.");
        }

        return new OpusPacketSoundDecoder(head);
    }
}
