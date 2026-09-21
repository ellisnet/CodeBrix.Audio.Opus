using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Opus.Internal;
using CodeBrix.Audio.Wave;

namespace CodeBrix.Audio.Opus;

/// <summary>
/// Writes <c>.opus</c> files through CodeBrix.Audio's <see cref="AudioFileWriterRegistry" /> - the
/// write-side peer of the ".opus" reader that <see cref="CodeBrixAudioOpus.Register()" /> installs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CodeBrixAudioOpus.Register()" /> registers one of these, so an application already
/// making that call can write .opus by file name with no further change - including through
/// CodeBrix.Audio's <c>SoundFontRenderer.RenderToFile</c>, which picks its writer from the path's
/// extension:
/// </para>
/// <code>
/// CodeBrixAudioOpus.Register();
/// SoundFontRenderer.RenderToFile(synthesizer, sequence, "tune.opus");
/// </code>
/// <para>
/// OGG IS WRITTEN STRICTLY FORWARDS, so <see cref="RequiresSeekableStream" /> is false and a .opus
/// file can be written to a pipe or a network stream. Nothing is patched at the end: the closing
/// page carries the true sample count, and it is written last rather than written back.
/// </para>
/// <para>
/// ONLY THE SAMPLE RATE AND CHANNEL COUNT OF A <see cref="WaveFormat" /> ARE USED. An Opus file
/// stores a compressed payload rather than PCM or float samples, so the format's encoding and bit
/// depth mean nothing here and are ignored rather than refused: <c>new WaveFormat(44100, 16, 2)</c>
/// writes exactly the same file as <see cref="DefaultFormat" /> does. An application that renders
/// "to whatever extension the user picked" with one fixed format therefore gets .opus behaving like
/// .wav and .aiff instead of failing.
/// </para>
/// <para>
/// ENCODER SETTINGS BELONG TO THE FACTORY, not to the format. A <see cref="WaveFormat" /> has no way
/// to say 64 kbps in the Voice profile - so the bitrate, profile, variable bitrate, complexity and
/// tags come from the <see cref="OpusFileWriterOptions" /> this factory was built with, and every
/// writer it makes uses them. An application that wants its own settings registers a factory that
/// carries them:
/// </para>
/// <code>
/// var options = new OpusFileWriterOptions { Bitrate = 160_000 };
/// AudioFileWriterRegistry.Register(new OpusAudioFileWriterFactory(options));
/// </code>
/// </remarks>
public sealed class OpusAudioFileWriterFactory : IAudioFileWriterFactory
{
    private static readonly string[] OpusExtensions = [".opus"];

    private readonly OpusFileWriterOptions options;

    /// <summary>Creates the factory with the encoder defaults - 96 kbps, Music, variable bitrate.</summary>
    public OpusAudioFileWriterFactory() : this(null)
    {
    }

    /// <summary>Creates the factory with encoder settings of your own.</summary>
    /// <param name="options">
    /// The settings every writer this factory makes is built with, or null for the defaults.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An option is outside its permitted range. The check happens here rather than at the first
    /// write, so a bad setting is reported where it was made.
    /// </exception>
    public OpusAudioFileWriterFactory(OpusFileWriterOptions options)
    {
        this.options = options ?? new OpusFileWriterOptions();
        this.options.Validate();
    }

    /// <summary>The encoder settings every writer this factory makes is built with.</summary>
    public OpusFileWriterOptions Options => options;

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions => OpusExtensions;

    /// <inheritdoc />
    /// <remarks>
    /// False. An Ogg stream is written from beginning to end and nothing in it is revisited, so a
    /// .opus file can be written to a stream that cannot seek.
    /// </remarks>
    public bool RequiresSeekableStream => false;

    /// <inheritdoc />
    /// <remarks>
    /// 32-bit IEEE float at the rate the caller asks for. The encoder takes float samples, and it
    /// resamples anything that is not 48 kHz itself - so there is no reason to make a caller
    /// rendering at 44.1 kHz convert first, and the declared rate is recorded in the file header as
    /// the rate the encoder was given.
    /// </remarks>
    public WaveFormat DefaultFormat(int sampleRate, int channels)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), sampleRate, "The sample rate must be positive.");
        }

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(channels),
                channels,
                "An Opus file holds mono or stereo audio - channel mapping family 0 - so 1 or 2 " +
                "channels.");
        }

        return WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// ONLY <see cref="WaveFormat.SampleRate" /> AND <see cref="WaveFormat.Channels" /> ARE READ
    /// from <paramref name="format" />. Its encoding and bit depth are ignored rather than refused,
    /// because an Opus file stores a compressed payload and <c>IAudioFileWriter.Write</c> always
    /// takes float samples - so a 16-bit PCM format writes the same file a float one does.
    /// </para>
    /// <para>
    /// The stream is NOT closed by the writer; the caller opened it and the caller closes it. It
    /// does not need to be seekable.
    /// </para>
    /// </remarks>
    public IAudioFileWriter Create(Stream stream, WaveFormat format)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (format == null)
        {
            throw new ArgumentNullException(nameof(format));
        }

        if (!stream.CanWrite)
        {
            throw new ArgumentException(
                "A .opus file cannot be written to a stream that is not writable.", nameof(stream));
        }

        if (format.SampleRate <= 0)
        {
            throw new ArgumentException(
                $"The sample rate must be positive, and this format asks for {format.SampleRate}.",
                nameof(format));
        }

        if (format.Channels is not (1 or 2))
        {
            throw new ArgumentException(
                $"An Opus file holds mono or stereo audio - channel mapping family 0 - and this " +
                $"format asks for {format.Channels} channels.",
                nameof(format));
        }

        // format.Encoding and format.BitsPerSample are deliberately NOT looked at. An Opus file
        // stores a compressed payload, so a bit depth cannot be honoured - but the samples arrive
        // as floats through IAudioFileWriter.Write whatever the format says, so ignoring it costs
        // nothing and refusing it would break a caller writing several formats with one format.
        return new OpusStreamAudioFileWriter(stream, format, options);
    }
}
