using System;
using System.Collections.Generic;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Opus.Codecs;
using SilverAssertions;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// What the Opus packet decoder does about PACKET LOSS: unlike a codec with no concealment of its
/// own, it SYNTHESISES a plausible continuation of the audio that went missing, for exactly as
/// long as the container says was lost.
/// </summary>
/// <remarks>
/// <para>
/// The claim that separates real concealment from silence is measurable, so it is measured: the
/// audio a gap comes back as has a level of its own, close to the level of the packet before it,
/// where a decoder without concealment would hand back zeros. Every number in these tests was
/// measured on the fixture named in the test and is quoted in the comment beside the tolerance it
/// is held to.
/// </para>
/// <para>
/// None of these needs an audio device. The one test that takes the same audio through
/// PacketAudioPlayer and a real device lives in <see cref="OpusPacketLossPlaybackTests" />.
/// </para>
/// </remarks>
public class OpusPacketLossTests
{
    /// <summary>The smallest stretch Opus conceals in, per channel: 2.5 ms at 48 kHz.</summary>
    private const int Step = 120;

    /// <summary>Packets in every fixture are 20 ms long, which is 960 frames at 48 kHz.</summary>
    private const int PacketFrames = 960;

    /// <summary>How many real packets to decode before asking for concealment.</summary>
    private const int PacketsBeforeTheGap = 8;

    private static OpusPacketCodecFactory Factory => new OpusPacketCodecFactory();

    [Fact]
    public void Opus_conceals_packet_loss_itself()
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);

        //Act
        var supported = decoder.SupportsLossConcealment;

        //Assert
        // Concealment is part of the Opus specification, and an application is entitled to know
        // whether it is getting synthesised audio or a player's silence.
        supported.Should().BeTrue();
    }

    [Theory]
    [InlineData(960, 1)]      // 20 ms  - one packet, one call
    [InlineData(1200, 1)]     // 25 ms  - ten 2.5 ms steps, still one call
    [InlineData(2880, 1)]     // 60 ms
    [InlineData(5760, 1)]     // 120 ms - the longest Opus conceals in one go
    [InlineData(9600, 2)]     // 200 ms - 5760 + 3840, because a call cannot exceed 120 ms
    public void A_gap_is_concealed_for_exactly_as_long_as_it_lasted(int gapFrames, int expectedCalls)
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);

        //Act
        var (covered, calls) = ConcealWholeGap(decoder, gapFrames, output);

        //Assert
        // Not one frame more and not one frame less, however many calls it took - that is what
        // keeps everything after the gap where it belongs.
        covered.Should().Be(gapFrames);
        calls.Should().Be(expectedCalls);
    }

    [Fact]
    public void A_gap_that_is_not_a_whole_number_of_steps_is_still_covered_exactly()
    {
        //Arrange
        // 1000 frames is 20.83 ms: eight whole 2.5 ms steps and 40 frames left over. The codec has
        // no concealment shorter than a step, so the last call runs a whole step and REPORTS ONLY
        // THE 40 FRAMES THAT WERE ASKED FOR. The surplus is written past the returned count and is
        // meant to be ignored; the decoder's own state advances by the full step.
        const int gapFrames = 1000;
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);

        //Act
        var returned = new List<int>();
        var covered = 0;

        while (covered < gapFrames)
        {
            var produced = decoder.ConcealLoss(gapFrames - covered, output);
            produced.Should().BeGreaterThan(0);
            returned.Add(produced / decoder.Channels);
            covered += produced / decoder.Channels;
        }

        //Assert
        covered.Should().Be(gapFrames);
        returned.Count.Should().Be(2);
        returned[0].Should().Be(960);   // eight whole steps, all that fits below 1000
        returned[1].Should().Be(40);    // the remainder, concealed over a whole step and clipped
    }

    [Fact]
    public void A_gap_shorter_than_one_step_is_still_covered_in_one_call()
    {
        //Arrange
        // The smallest interesting remainder: a single frame. Rounding DOWN would give nothing and
        // the caller would loop for ever, so it rounds UP to one step and reports the one frame.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);

        //Act
        var produced = decoder.ConcealLoss(1, output);

        //Assert
        produced.Should().Be(decoder.Channels);
    }

    [Theory]
    [InlineData(960, 0.75)]    // 20 ms  - measured 0.350 against a preceding packet of 0.355
    [InlineData(2880, 0.55)]   // 60 ms  - measured 0.289; the codec fades as the gap runs on
    [InlineData(9600, 0.25)]   // 200 ms - measured 0.175, still an eighth of a second of audio
    public void Concealment_is_synthesised_audio_rather_than_silence(int gapFrames, double leastRatio)
    {
        //Arrange
        // THE TEST THAT SEPARATES REAL CONCEALMENT FROM A CODEC THAT HAS NONE. A decoder without
        // concealment answers a gap with zeros, so its RMS is exactly 0; Opus answers with audio
        // that carries on the pitch and the level of what came before, and only fades as the gap
        // gets long.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        var lastRealPacket = DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);
        var lastRealRms = AudioAssertions.Rms(new ReadOnlySpan<float>(output, 0, lastRealPacket));

        //Act
        var concealed = new List<float>(gapFrames * decoder.Channels);
        var covered = 0;

        while (covered < gapFrames)
        {
            var produced = decoder.ConcealLoss(gapFrames - covered, output);
            for (var i = 0; i < produced; i++) concealed.Add(output[i]);
            covered += produced / decoder.Channels;
        }

        //Assert
        var concealedRms = AudioAssertions.Rms(concealed.ToArray());

        // Measured: the preceding packet is at 0.355 RMS on this fixture.
        lastRealRms.Should().BeGreaterThan(0.3);

        // Absolute floor first, so the test says plainly that this is NOT silence...
        concealedRms.Should().BeGreaterThan(0.05);

        // ...then relative to the audio it is standing in for, which is the honest measure.
        concealedRms.Should().BeGreaterThan(lastRealRms * leastRatio);
    }

    [Fact]
    public void The_audio_after_a_gap_stays_where_it_was_and_re_converges()
    {
        //Arrange
        // Two packets are dropped in the middle of the sweep and concealed. Three claims: the
        // stream comes out the same LENGTH, everything before the gap is untouched, and the audio
        // after it converges back on what an uninterrupted decode produced.
        const int lostAt = 20;
        const int lostCount = 2;

        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        var sizes = new List<int>();
        int channels;
        float[] reference;

        using (var referenceDecoder = Factory.CreateDecoder("opus", split.CodecPrivate, null))
        {
            channels = referenceDecoder.Channels;
            reference = DecodeEveryPacket(referenceDecoder, split.Packets, sizes);
        }

        var gapFrames = 0;
        for (var i = 0; i < lostCount; i++) gapFrames += sizes[lostAt + i];

        var framesBeforeGap = 0;
        for (var i = 0; i < lostAt; i++) framesBeforeGap += sizes[i];

        //Act
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        var delivered = new List<float>(reference.Length);

        for (var i = 0; i < split.Packets.Count; i++)
        {
            if (i == lostAt)
            {
                var covered = 0;

                while (covered < gapFrames)
                {
                    var produced = decoder.ConcealLoss(gapFrames - covered, output);
                    for (var k = 0; k < produced; k++) delivered.Add(output[k]);
                    covered += produced / decoder.Channels;
                }

                continue;
            }

            if (i > lostAt && i < lostAt + lostCount) continue;

            var decoded = decoder.DecodePacket(split.Packets[i], output);
            for (var k = 0; k < decoded; k++) delivered.Add(output[k]);
        }

        //Assert
        var actual = delivered.ToArray();

        // 1. The timeline keeps its length: what was lost was replaced, not skipped. Measured on
        //    this fixture: 1920 frames lost at frame 19200, of 96960 in all.
        gapFrames.Should().Be(PacketFrames * lostCount);
        actual.Length.Should().Be(reference.Length);

        // 2. Everything before the gap is the audio it always was, sample for sample.
        for (var i = 0; i < framesBeforeGap * channels; i++)
        {
            if (actual[i] != reference[i])
            {
                Assert.Fail($"The audio before the gap changed at sample {i}.");
            }
        }

        var afterTheGap = (framesBeforeGap + gapFrames) * channels;

        // 3. RE-CONVERGENCE, measured the way the seek pre-roll is measured. The first packet after
        //    a gap is decoded against a state the concealment left behind, so it is WRONG - 0.466
        //    relative RMS over the first 20 ms on this fixture - and the codec then pulls back:
        //      from  80 ms after the gap onwards   0.0070
        //      from 240 ms after the gap onwards   0.000035
        //    Anything near 1.0 would mean the concealment had left the decoder unusable.
        var first20Ms = RelativeRmsFrom(actual, reference, afterTheGap, 0, 20 * 48, channels);
        first20Ms.Should().BeGreaterThan(0.1);

        RelativeRmsFrom(actual, reference, afterTheGap, 80 * 48, int.MaxValue, channels)
            .Should().BeLessThan(0.02);

        RelativeRmsFrom(actual, reference, afterTheGap, 240 * 48, int.MaxValue, channels)
            .Should().BeLessThan(0.0005);
    }

    [Fact]
    public void The_next_real_packet_after_a_gap_still_decodes()
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);
        ConcealWholeGap(decoder, PacketFrames * 2, output);

        //Act
        var decoded = decoder.DecodePacket(split.Packets[PacketsBeforeTheGap + 2], output);

        //Assert
        // Concealment advances the codec's state like a decoded packet does, so the stream carries
        // on rather than having to be reset.
        decoded.Should().Be(PacketFrames * decoder.Channels);
    }

    [Fact]
    public void Concealing_straight_after_a_reset_gives_silence_instead_of_throwing()
    {
        //Arrange
        // Concealment continues the audio the decoder last saw, and a decoder that has just been
        // reset has not seen any. The codec answers with zeros of exactly the length asked for
        // rather than refusing, so a player that conceals before its seek pre-roll is fed gets a
        // gap of the right length instead of an exception.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);
        decoder.Reset();

        //Act
        var produced = decoder.ConcealLoss(PacketFrames, output);

        //Assert
        produced.Should().Be(PacketFrames * decoder.Channels);

        for (var i = 0; i < produced; i++)
        {
            if (output[i] != 0f)
            {
                Assert.Fail($"Concealment after a reset should be silent, but sample {i} was {output[i]}.");
            }
        }
    }

    [Fact]
    public void Concealing_before_any_packet_has_been_decoded_gives_silence_too()
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];

        //Act
        var produced = decoder.ConcealLoss(PacketFrames, output);

        //Assert
        // The same answer as after a reset, and for the same reason - a brand-new decoder has no
        // audio to continue.
        produced.Should().Be(PacketFrames * decoder.Channels);
        AudioAssertions.Rms(new ReadOnlySpan<float>(output, 0, produced)).Should().Be(0.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Concealing_nothing_produces_nothing(int lostFrames)
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);

        //Act
        var produced = decoder.ConcealLoss(lostFrames, output);

        //Assert
        // A gap of no length costs nothing and cannot make a caller's loop spin.
        produced.Should().Be(0);
    }

    [Fact]
    public void A_buffer_too_small_for_one_step_conceals_nothing()
    {
        //Arrange
        // Nothing shorter than a 2.5 ms step can be asked of the codec, so a buffer that cannot
        // hold one gets 0 back - the interface's "fill it yourself" answer - rather than an
        // exception. A buffer sized to MaxSamplesPerPacket can never land here.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var scratch = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, scratch);

        var tooSmall = new float[(Step * decoder.Channels) - 1];
        var exactlyOneStep = new float[Step * decoder.Channels];

        //Act
        var nothing = decoder.ConcealLoss(PacketFrames, tooSmall);
        var oneStep = decoder.ConcealLoss(PacketFrames, exactlyOneStep);

        //Assert
        nothing.Should().Be(0);
        oneStep.Should().Be(Step * decoder.Channels);
    }

    [Fact]
    public void A_disposed_decoder_conceals_nothing_instead_of_throwing()
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];

        //Act
        decoder.Dispose();
        var produced = decoder.ConcealLoss(PacketFrames, output);

        //Assert
        // The audio thread may be part-way through a gap when the player is torn down, so a race
        // there has to be harmless rather than fatal - the same rule DecodePacket follows.
        produced.Should().Be(0);
    }

    [Fact]
    public void An_empty_packet_still_conceals_one_packet()
    {
        //Arrange
        // The lengthless convention is unchanged: a caller that only knows a packet went missing,
        // and not how long it was, still gets a packet's worth of concealment.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);

        //Act
        var produced = decoder.DecodePacket(ReadOnlySpan<byte>.Empty, output);

        //Assert
        produced.Should().Be(PacketFrames * decoder.Channels);
    }

    [Fact]
    public void An_empty_packet_before_anything_is_decoded_conceals_twenty_milliseconds()
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];

        //Act
        var produced = decoder.DecodePacket(ReadOnlySpan<byte>.Empty, output);

        //Assert
        // 20 ms is the fallback when no real packet has set the length yet - the frame size almost
        // every Opus stream in a container is encoded at.
        produced.Should().Be(PacketFrames * decoder.Channels);
    }

    [Fact]
    public void Concealment_does_not_redefine_what_one_lost_packet_means()
    {
        //Arrange
        // REGRESSION. The codec's own LastPacketDuration is updated by concealment as well as by
        // decoding, so reading the empty-packet length off it made a 120 ms ConcealLoss turn the
        // NEXT empty packet into 120 ms of loss instead of one 20 ms packet. The decoder remembers
        // the last REAL packet's duration itself for exactly that reason.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);

        //Act
        decoder.ConcealLoss(5760, output);
        var produced = decoder.DecodePacket(ReadOnlySpan<byte>.Empty, output);

        //Assert
        produced.Should().Be(PacketFrames * decoder.Channels);
    }

    [Fact]
    public void Concealment_never_produces_more_than_a_packets_worth_at_a_time()
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);

        //Act
        // A minute of loss, asked for in one go: the buffer contract still holds, call after call.
        var produced = decoder.ConcealLoss(48000 * 60, output);

        //Assert
        produced.Should().Be(decoder.MaxSamplesPerPacket);
    }

    [Theory]
    [InlineData(TestAssets.OpusToneStereo)]
    [InlineData(TestAssets.OpusToneMonoFrom16000)]
    [InlineData(TestAssets.OpusSweepStereo)]
    public void Every_fixture_conceals_every_step_size_without_complaint(string fixture)
    {
        //Arrange
        // The chunk sizes the rule can pick are multiples of 2.5 ms, and the codec decomposes each
        // of them by the duration of the last packet it saw. The mono fixture matters here: it was
        // encoded from 16 kHz, so it exercises the codec's speech path rather than its music path.
        var split = PacketFixtures.Split(fixture);

        foreach (var gapFrames in new[] { Step, 240, 480, 960, 1200, 1920, 2880, 3000, 3840, 5760 })
        {
            using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
            var output = new float[decoder.MaxSamplesPerPacket];
            DecodeSome(decoder, split.Packets, PacketsBeforeTheGap, output);

            //Act
            var produced = decoder.ConcealLoss(gapFrames, output);

            //Assert
            produced.Should().Be(gapFrames * decoder.Channels);
        }
    }

    // ----- helpers -----

    /// <summary>
    /// Feeds the first <paramref name="count" /> packets in, and returns what the last one made.
    /// </summary>
    private static int DecodeSome(
        IPacketSoundDecoder decoder, IReadOnlyList<byte[]> packets, int count, float[] output)
    {
        var produced = 0;

        for (var i = 0; i < count && i < packets.Count; i++)
        {
            produced = decoder.DecodePacket(packets[i], output);
        }

        return produced;
    }

    /// <summary>
    /// Asks for concealment until the gap is covered, the way the interface says to.
    /// </summary>
    private static (int Covered, int Calls) ConcealWholeGap(
        IPacketSoundDecoder decoder, int gapFrames, float[] output)
    {
        var covered = 0;
        var calls = 0;

        while (covered < gapFrames)
        {
            var produced = decoder.ConcealLoss(gapFrames - covered, output);
            produced.Should().BeGreaterThan(0);
            produced.Should().BeLessThanOrEqualTo(decoder.MaxSamplesPerPacket);
            covered += produced / decoder.Channels;
            calls++;
        }

        return (covered, calls);
    }

    /// <summary>Decodes every packet one at a time, recording what each one made.</summary>
    private static float[] DecodeEveryPacket(
        IPacketSoundDecoder decoder, IReadOnlyList<byte[]> packets, List<int> sizes)
    {
        var output = new float[decoder.MaxSamplesPerPacket];
        var all = new List<float>(packets.Count * decoder.MaxSamplesPerPacket);

        foreach (var packet in packets)
        {
            var produced = decoder.DecodePacket(packet, output);
            sizes.Add(produced / decoder.Channels);
            for (var i = 0; i < produced; i++) all.Add(output[i]);
        }

        return all.ToArray();
    }

    /// <summary>
    /// Relative RMS error over a window that starts <paramref name="skipFrames" /> frames after
    /// <paramref name="from" /> and runs for <paramref name="lengthFrames" /> frames, or to the end.
    /// </summary>
    private static double RelativeRmsFrom(
        float[] actual, float[] reference, int from, int skipFrames, int lengthFrames, int channels)
    {
        var start = from + (skipFrames * channels);
        var available = Math.Min(actual.Length, reference.Length) - start;

        if (available <= 0) return 0;

        var take = lengthFrames == int.MaxValue
            ? available
            : Math.Min(available, lengthFrames * channels);

        return AudioAssertions.RelativeRmsError(
            new ReadOnlySpan<float>(actual, start, take),
            new ReadOnlySpan<float>(reference, start, take));
    }
}
