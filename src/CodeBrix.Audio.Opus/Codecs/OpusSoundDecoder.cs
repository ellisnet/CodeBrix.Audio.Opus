using System;
using System.IO;
using CodeBrix.Audio.Codecs;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus.Codecs;

/// <summary>
/// Feeds decoded Opus audio to the CodeBrix.Audio engine, converting to whatever channel count
/// and sample rate the engine asked for.
/// </summary>
internal sealed class OpusSoundDecoder : ManagedSoundDecoder
{
    private readonly OggOpusReader reader;

    /// <summary>Creates a decoder over an Ogg Opus stream.</summary>
    /// <param name="stream">The stream to decode, which this decoder does not own.</param>
    /// <param name="channels">Channel count the engine wants, or 0 to adopt the file's.</param>
    /// <param name="sampleRate">Sample rate the engine wants, or 0 to adopt the file's.</param>
    public OpusSoundDecoder(Stream stream, int channels, int sampleRate)
        : base(channels, sampleRate)
    {
        reader = new OggOpusReader(stream, leaveOpen: true);

        // The SOURCE rate is 48000 - always, whatever the file's header declares. That header
        // field records the rate the ENCODER was fed (16 kHz for a typical voice note) and RFC
        // 7845 marks it informational. Passing it here would tell the base class to convert FROM
        // a rate this decoder does not actually produce, and a 16 kHz voice note would play three
        // times too slow.
        Initialize(
            reader.Channels,
            OggOpusReader.DecodeSampleRate,
            reader.TotalSamples < 0 ? 0 : reader.TotalSamples);
    }

    /// <inheritdoc />
    protected override int ReadSourceSamples(Span<float> destination) => reader.Read(destination);

    /// <inheritdoc />
    protected override bool SeekSource(long frameIndex) => reader.Seek(frameIndex);

    /// <inheritdoc />
    protected override void DisposeCore() => reader.Dispose();
}
