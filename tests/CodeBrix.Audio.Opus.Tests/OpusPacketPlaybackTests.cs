using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodeBrix.Audio.Playback;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// End-to-end playback of Opus PACKETS through PacketAudioPlayer and a real audio device - the
/// claim the packet seam actually makes to an application demultiplexing a media container.
/// </summary>
/// <remarks>
/// <para>
/// Every other packet test is device-less and compares numbers. These take the same packets all the
/// way to the speakers, so they need a device: opt in with
/// <c>CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1</c>, the same switch the rest of the family uses, and
/// they share its cross-process mutex so two suites never sound at once.
/// </para>
/// <para>
/// The audio is the motif this repository's other audible tests play, encoded to Opus by this
/// library and then pulled apart into the packets a container would carry - so a listener can tell
/// at once whether the packet path produces music or mush.
/// </para>
/// </remarks>
[Collection("Registration")]
public class OpusPacketPlaybackTests
{
    private const string PlaybackEnvVar = "CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS";

    private static bool PlaybackTestsEnabled =>
        Environment.GetEnvironmentVariable(PlaybackEnvVar) == "1";

    /// <summary>
    /// Hands out packets from a list the way a demultiplexer hands them out of its read-ahead
    /// queue: immediately, never blocking, and reporting the end only once they have all gone.
    /// </summary>
    private sealed class ListPacketSource : IAudioPacketSource
    {
        private readonly IReadOnlyList<byte[]> packets;
        private int next;

        public ListPacketSource(IReadOnlyList<byte[]> packets)
        {
            this.packets = packets;
        }

        public bool EndOfStream => next >= packets.Count;

        public bool TryReadPacket(out AudioPacket packet)
        {
            if (next >= packets.Count)
            {
                packet = default;
                return false;
            }

            packet = new AudioPacket(packets[next++]);
            return true;
        }
    }

    /// <summary>Encodes the motif to a .opus file and splits it into container-shaped packets.</summary>
    private static PacketFixtures.SplitStream WriteAndSplitMotif()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".opus");

        try
        {
            using (var writer = new OpusFileWriter(path, 48000, 1))
            {
                writer.Write(TestAudio.BuildCloseEncountersSamples(48000, 1));
            }

            return PacketFixtures.SplitFile(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_motif_plays_through_PacketAudioPlayer_as_loose_packets()
    {
        Assert.SkipUnless(PlaybackTestsEnabled,
            $"Set {PlaybackEnvVar}=1 to run the tests that open a real audio device and make sound.");

        //Arrange
        // The whole seam in one go: register, hand the player an identification header and a source
        // of bare packets, and let it reach the speakers. If any link is wrong this is silent, or
        // ends immediately, or never ends at all.
        using var audible = new AudibleTestScope();
        CodeBrixAudioOpus.Register();

        var split = WriteAndSplitMotif();
        var finished = new ManualResetEventSlim(false);

        using var player = new PacketAudioPlayer();
        player.PlaybackEnded += (sender, args) => finished.Set();

        //Act
        player.Open("opus", split.CodecPrivate, new ListPacketSource(split.Packets));
        player.Play();

        var ended = finished.Wait(
            TestAudio.CloseEncountersDuration + TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        //Assert
        ended.Should().BeTrue();
        player.SampleRate.Should().Be(48000);
        player.Channels.Should().Be(1);
        player.Position.TotalSeconds.Should().BeGreaterThan(
            TestAudio.CloseEncountersDuration.TotalSeconds * 0.75);
    }

    [Fact]
    public void The_shared_output_builds_an_opus_packet_decoder_after_registering()
    {
        Assert.SkipUnless(PlaybackTestsEnabled,
            $"Set {PlaybackEnvVar}=1 to run the tests that open a real audio device and make sound.");

        //Arrange
        // Gated with the audible tests although it makes no sound: the packet registry belongs to
        // the RUNNING engine, so asking the shared output for a decoder starts the audio device.
        CodeBrixAudioOpus.Register();
        var split = PacketFixtures.Split(TestAssets.OpusToneStereo);

        //Act
        using var decoder = SharedAudioOutput.CreatePacketDecoder("opus", split.CodecPrivate);

        //Assert
        decoder.Should().NotBeNull();
        decoder.SampleRate.Should().Be(48000);
        decoder.Channels.Should().Be(2);
    }
}
