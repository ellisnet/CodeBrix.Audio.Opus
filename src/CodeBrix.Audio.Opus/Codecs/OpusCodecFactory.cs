using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Codecs;
using CodeBrix.Audio.Engine.Enums;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Engine.Structs;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus.Codecs;

/// <summary>
/// Supplies the CodeBrix.Audio engine with a fully managed Ogg Opus decoder and encoder.
/// </summary>
/// <remarks>
/// <para>
/// Register it once at start-up - <see cref="CodeBrixAudioOpus.Register()" /> is the friendly way
/// - after which .opus plays through AudioFilePlayer, SoundEffectClip, the CodeBrix.Platform
/// AudioPlayer add-in and the GameEngine, with no other change to the consuming application.
/// </para>
/// <para>
/// THE OGG FORMAT-ID SHARING RULE. CodeBrix.Audio's metadata layer stamps EVERY Ogg stream with
/// the format identifier "ogg", whatever codec is inside, so this factory is offered Vorbis and
/// Ogg FLAC streams too - and the Vorbis factory is offered Opus streams. Both check what they
/// were actually handed and return null for anything else, which is what lets the two coexist.
/// </para>
/// </remarks>
public sealed class OpusCodecFactory : ICodecFactory
{
    /// <summary>The format identifier an Opus ENCODER is requested by.</summary>
    /// <remarks>
    /// Encoding is selected by format id rather than sniffed, and "opus" says which codec is
    /// meant where the shared "ogg" would not. Nothing competes for it: the engine's built-in
    /// native factory declines every encode except "wav".
    /// </remarks>
    public const string OpusFormatId = "opus";

    /// <summary>The identifier for Ogg streams, shared by every codec an Ogg container can hold.</summary>
    public const string OggFormatId = "ogg";

    /// <inheritdoc />
    public string FactoryId => "CodeBrix.Audio.Opus.ManagedOpus";

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedFormatIds { get; } =
        new[] { OggFormatId, OpusFormatId };

    /// <inheritdoc />
    /// <remarks>
    /// Below the engine's built-in native factory at 0, matching the managed Vorbis and FLAC
    /// factories. The native library cannot decode Opus, so it fails on these streams and the
    /// engine moves on to this factory.
    /// </remarks>
    public int Priority => -10;

    /// <inheritdoc />
    public ISoundDecoder CreateDecoder(Stream stream, string formatId, AudioFormat format)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        if (!string.Equals(formatId, OggFormatId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(formatId, OpusFormatId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // An earlier factory may have read from the stream before declining it, and the engine
        // does not rewind between factories on the format-id path.
        if (stream.CanSeek) stream.Position = 0;
        if (!IsOpusStream(stream)) return null;

        var channels = format.Channels > 0 ? format.Channels : 2;
        var sampleRate = format.SampleRate > 0 ? format.SampleRate : OggOpusReader.DecodeSampleRate;

        return new OpusSoundDecoder(stream, channels, sampleRate);
    }

    /// <inheritdoc />
    public ISoundDecoder TryCreateDecoder(Stream stream, out AudioFormat detectedFormat,
        AudioFormat? hintFormat = null)
    {
        detectedFormat = hintFormat ?? default;

        if (stream == null || !stream.CanSeek) return null;

        stream.Position = 0;
        if (!IsOpusStream(stream)) return null;

        // Probing has no target format to honour, so decode at the stream's own layout - 48 kHz,
        // always - and report that back for the caller to adapt to.
        var decoder = new OpusSoundDecoder(stream, 0, 0);

        detectedFormat = new AudioFormat
        {
            Format = SampleFormat.F32,
            Channels = decoder.Channels,
            Layout = AudioFormat.GetLayoutFromChannels(decoder.Channels),
            SampleRate = decoder.SampleRate
        };

        return decoder;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reached by the engine's Recorder, so <c>new Recorder(captureDevice, stream, "opus")</c>
    /// records straight to Ogg Opus once this factory is registered.
    /// </remarks>
    public ISoundEncoder CreateEncoder(Stream stream, string formatId, AudioFormat format)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        // Only the explicit "opus" id: an encoder cannot sniff what it has not written yet, and
        // claiming the shared "ogg" id would mean guessing that Ogg meant Opus rather than Vorbis.
        if (!string.Equals(formatId, OpusFormatId, StringComparison.OrdinalIgnoreCase)) return null;

        var channels = format.Channels > 0 ? format.Channels : 2;
        if (channels is not (1 or 2)) return null;

        var sampleRate = format.SampleRate > 0 ? format.SampleRate : OggOpusWriter.EncodeSampleRate;

        return new OpusSoundEncoder(stream, channels, sampleRate, new OggOpusWriterSettings());
    }

    /// <summary>Checks that the stream is an Ogg container carrying Opus specifically.</summary>
    private static bool IsOpusStream(Stream stream)
    {
        return OggCodecSniffer.Identify(stream) == OggCodec.Opus;
    }
}
