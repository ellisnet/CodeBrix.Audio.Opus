using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Opus.Codecs;
using CodeBrix.Audio.Playback;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Packet loss carried all the way through PacketAudioPlayer to a real audio device: a source that
/// LOSES two packets and says so with <c>AudioPacket.Loss</c> must play for exactly as long as one
/// that loses nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is the only loss test that needs a device. The player's own adapter is internal to
/// CodeBrix.Audio and visible only to that package's tests, so a gap cannot be pushed through it
/// device-free from here; the timeline claim is therefore made against
/// <c>PacketAudioPlayer.Position</c>, which counts the frames the player actually handed over.
/// Opt in with <c>CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1</c>, the same switch the rest of the family
/// uses, and share its cross-process mutex so two suites never sound at once.
/// </para>
/// <para>
/// Everything else about concealment is measured device-free in
/// <see cref="OpusPacketLossTests" />.
/// </para>
/// </remarks>
[Collection("Registration")]
public class OpusPacketLossPlaybackTests
{
    private const string PlaybackEnvVar = "CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS";

    /// <summary>The packet in the motif that goes missing, and the one after it.</summary>
    private const int FirstLostPacket = 20;

    /// <summary>How many consecutive packets go missing.</summary>
    private const int LostPackets = 2;

    private static bool PlaybackTestsEnabled =>
        Environment.GetEnvironmentVariable(PlaybackEnvVar) == "1";

    /// <summary>
    /// Hands out packets the way a demultiplexer that KNOWS it dropped some hands them out: the
    /// packets it still has, and a loss packet where the missing ones were.
    /// </summary>
    private sealed class LossyPacketSource : IAudioPacketSource
    {
        private readonly IReadOnlyList<AudioPacket> packets;
        private int next;

        public LossyPacketSource(IReadOnlyList<AudioPacket> packets)
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

            packet = packets[next++];
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
    public void A_stream_that_loses_two_packets_plays_for_exactly_as_long_as_one_that_does_not()
    {
        Assert.SkipUnless(PlaybackTestsEnabled,
            $"Set {PlaybackEnvVar}=1 to run the tests that open a real audio device and make sound.");

        //Arrange
        if (!SharedAudioOutput.IsRunning)
        {
            // Nothing has opened the device yet, so start it at the rate Opus decodes at and the
            // motif reaches the speakers with no resampler in the way. When another test in the
            // same process got there first the rate is whatever that test chose - and the frame
            // count below is unaffected either way, because Position counts the frames read from
            // the PACKETS, before any channel or rate conversion. Reconfiguring a running output
            // throws, which is why this is asked rather than assumed.
            SharedAudioOutput.Configure(48000);
        }

        using var audible = new AudibleTestScope();
        CodeBrixAudioOpus.Register();

        var split = WriteAndSplitMotif();

        // What an unbroken decode of the same packets produces, measured device-free through the
        // same decoder - both the total, and how long the two packets that go missing were.
        var sizes = new List<int>();
        int uninterruptedFrames;
        int gapFrames;

        using (var reference = new OpusPacketCodecFactory().CreateDecoder("opus", split.CodecPrivate, null))
        {
            var scratch = new float[reference.MaxSamplesPerPacket];
            uninterruptedFrames = 0;

            foreach (var packet in split.Packets)
            {
                var produced = reference.DecodePacket(packet, scratch) / reference.Channels;
                sizes.Add(produced);
                uninterruptedFrames += produced;
            }

            gapFrames = 0;
            for (var i = 0; i < LostPackets; i++) gapFrames += sizes[FirstLostPacket + i];
        }

        split.Packets.Count.Should().BeGreaterThan(FirstLostPacket + LostPackets);
        gapFrames.Should().BeGreaterThan(0);

        // The same packets with two of them missing, and one loss packet in their place.
        var withGap = new List<AudioPacket>(split.Packets.Count);

        for (var i = 0; i < split.Packets.Count; i++)
        {
            if (i == FirstLostPacket)
            {
                withGap.Add(AudioPacket.Loss(gapFrames));
                continue;
            }

            if (i > FirstLostPacket && i < FirstLostPacket + LostPackets) continue;

            withGap.Add(new AudioPacket(split.Packets[i]));
        }

        using var player = new PacketAudioPlayer();
        using var finished = new ManualResetEventSlim(false);
        player.PlaybackEnded += (sender, args) => finished.Set();

        //Act
        // A listener hears the motif with a concealed gap in it, which should be all but
        // imperceptible - the point of concealment rather than silence.
        player.Open("opus", split.CodecPrivate, new LossyPacketSource(withGap));
        player.Volume = 0.6f;
        player.Play();

        var ended = finished.Wait(
            TestAudio.CloseEncountersDuration + TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        //Assert
        ended.Should().BeTrue();
        player.SampleRate.Should().Be(48000);
        player.Channels.Should().Be(1);

        // The claim: what was lost was REPLACED, not skipped, so the stream is exactly as long as
        // it would have been. Position counts the frames the player handed over, so a gap that came
        // out short or long would show up here to the frame.
        var deliveredFrames = (long)Math.Round(player.Position.TotalSeconds * 48000);
        deliveredFrames.Should().Be(uninterruptedFrames);
    }
}
