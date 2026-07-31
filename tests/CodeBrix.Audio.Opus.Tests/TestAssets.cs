using System;
using System.IO;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Locates the generated audio fixtures that are copied next to the test assembly.
/// </summary>
/// <remarks>
/// The files come from tools/make_test_fixtures/make_fixtures.sh and are described in
/// tests/Assets/audio/AUDIO-FIXTURES.txt. They are synthesized locally, not third-party audio.
/// </remarks>
internal static class TestAssets
{
    /// <summary>0.25 s stereo Ogg Opus encoded from 48 kHz - the everyday case.</summary>
    public const string OpusToneStereo = "opus-tone-stereo-48000.opus";

    /// <summary>
    /// 0.25 s mono Ogg Opus encoded from a 16 kHz source, so its header declares 16000 while the
    /// stream decodes at 48 kHz. The messenger voice-note shape.
    /// </summary>
    public const string OpusToneMonoFrom16000 = "opus-tone-mono-from-16000.opus";

    /// <summary>
    /// 2 s stereo sweep for the seek tests. Its instantaneous frequency is 200 + 1800*t Hz -
    /// 200 Hz at the start, 2000 Hz one second in, 3800 Hz at the end - so the frequency at any
    /// point identifies the position, and a seek can be checked against the audio itself.
    /// </summary>
    public const string OpusSweepStereo = "opus-sweep-stereo-48000.opus";

    /// <summary>An Opus stream cut off mid-page.</summary>
    public const string OpusTruncated = "opus-truncated.opus";

    /// <summary>An Ogg VORBIS stream - not Opus, and the codec factory must decline it.</summary>
    public const string VorbisToneStereo = "vorbis-tone-stereo-44100.ogg";

    /// <summary>Opus decodes at this rate, whatever a file's header declares.</summary>
    public const int DecodeSampleRate = 48000;

    /// <summary>Pre-skip carried by every fixture, in 48 kHz samples.</summary>
    public const int FixturePreSkip = 312;

    /// <summary>Audible samples per channel in the 0.25 s fixtures: 12312 granule - 312 pre-skip.</summary>
    public const int ShortFixtureSamples = 12000;

    /// <summary>Audible samples per channel in the 2 s sweep: 96312 granule - 312 pre-skip.</summary>
    public const int SweepFixtureSamples = 96000;

    /// <summary>ffmpeg's own decode of a fixture, the reference this library is measured against.</summary>
    public static string FfmpegReferenceFor(string opusFileName) =>
        Path(System.IO.Path.GetFileNameWithoutExtension(opusFileName) + ".ffmpeg.wav");

    /// <summary>Full path to a fixture beside the test assembly.</summary>
    public static string Path(string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "audio", fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Audio fixture '{fileName}' was not copied to the test output. Regenerate the " +
                "fixtures with tools/make_test_fixtures/make_fixtures.sh.", path);
        }

        return path;
    }

    /// <summary>Opens a fixture as a seekable in-memory stream.</summary>
    public static MemoryStream Open(string fileName) => new(File.ReadAllBytes(Path(fileName)));
}
