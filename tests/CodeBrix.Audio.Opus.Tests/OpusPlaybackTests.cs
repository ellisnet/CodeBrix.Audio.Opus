using System;
using System.IO;
using System.Threading;
using CodeBrix.Audio.Playback;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// End-to-end playback of .opus through the real CodeBrix.Audio playback path and a real audio
/// device - the claim this package actually makes.
/// </summary>
/// <remarks>
/// <para>
/// Every other test here is device-less: it decodes into a buffer and compares numbers. Those
/// prove the codec. They do NOT prove that registering the package makes an .opus file play, which
/// is the thing a consuming application cares about, so these tests go all the way to the speakers
/// through AudioFilePlayer and SoundEffectClip - exactly the types the CodeBrix.Platform
/// AudioPlayer add-in uses.
/// </para>
/// <para>
/// Opt in with <c>CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1</c>, the same switch CodeBrix.Audio's own
/// audible tests use, and share their cross-process mutex so the two suites never sound at once.
/// </para>
/// </remarks>
[Collection("Registration")]
public class OpusPlaybackTests
{
    private const string PlaybackEnvVar = "CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS";

    private static bool PlaybackTestsEnabled =>
        Environment.GetEnvironmentVariable(PlaybackEnvVar) == "1";

    private static string TempOpusPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".opus");

    /// <summary>Encodes the motif to a .opus file and returns the path.</summary>
    private static string WriteMotifAsOpus(int sampleRate = 48000, int channels = 1)
    {
        var path = TempOpusPath();
        var samples = TestAudio.BuildCloseEncountersSamples(sampleRate, channels);

        using (var writer = new OpusFileWriter(path, sampleRate, channels))
        {
            writer.Write(samples);
        }

        return path;
    }

    [Fact]
    public void The_motif_encoded_to_opus_plays_through_AudioFilePlayer()
    {
        Assert.SkipUnless(PlaybackTestsEnabled,
            $"Set {PlaybackEnvVar}=1 to run the tests that open a real audio device and make sound.");

        //Arrange
        // The whole package in one go: encode with this library, register it, and let
        // CodeBrix.Audio's ordinary media player open the result by file name. If any link in
        // that chain is wrong, this is silent or throws.
        using var audible = new AudibleTestScope();
        CodeBrixAudioOpus.Register();

        // MONO on purpose, and the duration assertion below is the point of it. A mono file
        // played on a stereo device is the case where CodeBrix.Audio used to report exactly half
        // the true duration - the provider counted the file's channels while the transport
        // divided by the device's - so a voice note showed as half its length in any scrubber
        // bound to it. Fixed in CodeBrix.Audio 1.0.212.816; this is that fix seen from the
        // consumer's side, and it fails against any earlier package.
        var path = WriteMotifAsOpus(channels: 1);

        try
        {
            using var media = new AudioFilePlayer();

            //Act
            media.Load(path);
            var duration = media.Duration;
            media.Play();

            var deadline = DateTime.UtcNow + duration + TimeSpan.FromSeconds(2);
            while (media.Position < duration && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }

            //Assert
            duration.TotalSeconds.Should().BeApproximately(
                TestAudio.CloseEncountersDuration.TotalSeconds, 0.05);
            media.Position.TotalSeconds.Should().BeGreaterThan(duration.TotalSeconds * 0.75);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_fixture_opus_file_plays_as_a_sound_effect()
    {
        Assert.SkipUnless(PlaybackTestsEnabled,
            $"Set {PlaybackEnvVar}=1 to run the tests that open a real audio device and make sound.");

        //Arrange
        // SoundEffectClip is the other half of what the AudioPlayer add-in uses, and it decodes
        // the whole clip up front through the engine rather than streaming it.
        using var audible = new AudibleTestScope();
        CodeBrixAudioOpus.Register();

        var path = WriteMotifAsOpus(sampleRate: 48000, channels: 2);

        try
        {
            //Act
            using var clip = SoundEffectClip.Load(path);
            clip.Play(0.6f);

            Thread.Sleep(TestAudio.CloseEncountersDuration + TimeSpan.FromMilliseconds(300));

            //Assert
            clip.Duration.TotalSeconds.Should().BeApproximately(
                TestAudio.CloseEncountersDuration.TotalSeconds, 0.05);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_voice_note_shaped_file_plays_at_the_right_pitch_and_speed()
    {
        Assert.SkipUnless(PlaybackTestsEnabled,
            $"Set {PlaybackEnvVar}=1 to run the tests that open a real audio device and make sound.");

        //Arrange
        // The 48 kHz rule, by ear. This file is encoded from 16 kHz, so its header declares 16000
        // while the stream decodes at 48000. A decoder that trusted the declared rate would play
        // the motif three times too slow and more than an octave and a half down - unmistakable.
        using var audible = new AudibleTestScope();
        CodeBrixAudioOpus.Register();

        var path = WriteMotifAsOpus(sampleRate: 16000, channels: 1);

        try
        {
            using var reader = new OpusFileReader(path);

            //Act
            var declared = reader.EncoderInputSampleRate;
            var duration = reader.TotalTime;

            using var media = new AudioFilePlayer();
            media.Load(path);
            media.Play();
            Thread.Sleep(TestAudio.CloseEncountersDuration + TimeSpan.FromMilliseconds(300));

            //Assert
            declared.Should().Be(16000);
            reader.WaveFormat.SampleRate.Should().Be(48000);
            duration.TotalSeconds.Should().BeApproximately(
                TestAudio.CloseEncountersDuration.TotalSeconds, 0.05);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Seeking_during_playback_moves_the_transport()
    {
        Assert.SkipUnless(PlaybackTestsEnabled,
            $"Set {PlaybackEnvVar}=1 to run the tests that open a real audio device and make sound.");

        //Arrange
        // Plays the motif from the third note on, so a working seek is audible as a SHORTER tune
        // that starts part-way in.
        using var audible = new AudibleTestScope();
        CodeBrixAudioOpus.Register();

        var path = WriteMotifAsOpus();

        try
        {
            using var media = new AudioFilePlayer();
            media.Load(path);

            //Act
            media.Seek(TimeSpan.FromSeconds(0.72));   // start of the third tone
            media.Play();

            Thread.Sleep(TestAudio.CloseEncountersDuration);

            //Assert
            media.Position.TotalSeconds.Should().BeGreaterThan(0.7);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
