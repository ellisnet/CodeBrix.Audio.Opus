using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Opus.Codecs;
using CodeBrix.Audio.Opus.Ogg;
using SilverAssertions;
using SilverAssertions.Numeric;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for the identification header's OUTPUT GAIN, which RFC 7845 section 5.1 requires a
/// decoder to apply, and which both decode paths in this package now do.
/// </summary>
/// <remarks>
/// <para>
/// The field is a signed Q7.8 value in decibels - 256 units to the decibel - so a stored -1541
/// means -6.02 dB, a factor of one half. No encoder in ordinary use writes anything but 0, which
/// is why the committed fixtures all carry 0 and why these tests re-serialise a fixture's header
/// with a gain in it rather than shipping another binary asset.
/// </para>
/// <para>
/// The gain is applied through the codec's own gain control, in the one place inside the decoder
/// where the reference implementation applies it, so the stream path and the packet path cannot
/// drift apart - a claim one of these tests makes directly.
/// </para>
/// </remarks>
public class OpusOutputGainTests
{
    /// <summary>-6.02 dB in Q7.8: a factor of one half.</summary>
    private const short HalfGainQ78 = -1541;

    /// <summary>+6.02 dB in Q7.8: a factor of two, which these fixtures cannot hold without clipping.</summary>
    private const short DoubleGainQ78 = 1541;

    /// <summary>The linear factor a Q7.8 decibel value stands for.</summary>
    private static double LinearFactor(short gainQ78) => Math.Pow(10.0, gainQ78 / 256.0 / 20.0);

    /// <summary>Decodes a whole Ogg Opus file through the STREAM path.</summary>
    private static float[] StreamDecode(byte[] file)
    {
        using var stream = new MemoryStream(file);
        using var reader = new OggOpusReader(stream, leaveOpen: true);

        var all = new List<float>(reader.Channels * 96000);
        var block = new float[reader.Channels * 4800];
        int read;

        while ((read = reader.Read(block)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                all.Add(block[i]);
            }
        }

        return all.ToArray();
    }

    /// <summary>Decodes packets through the PACKET path, with the pre-skip dropped.</summary>
    private static float[] PacketDecode(byte[] codecPrivate, IReadOnlyList<byte[]> packets)
    {
        using var decoder = new OpusPacketCodecFactory().CreateDecoder("opus", codecPrivate, null);

        var output = new float[decoder.MaxSamplesPerPacket];
        var all = new List<float>(packets.Count * 1920);

        foreach (var packet in packets)
        {
            var written = decoder.DecodePacket(packet, output);

            for (var i = 0; i < written; i++)
            {
                all.Add(output[i]);
            }
        }

        var priming = decoder.PreSkipSamples * decoder.Channels;

        return all.GetRange(priming, all.Count - priming).ToArray();
    }

    /// <summary>The reference decode scaled by a factor, to compare a gained decode against.</summary>
    private static float[] Scaled(float[] samples, double factor)
    {
        var scaled = new float[samples.Length];

        for (var i = 0; i < samples.Length; i++)
        {
            scaled[i] = (float)(samples[i] * factor);
        }

        return scaled;
    }

    [Theory]
    [InlineData(HalfGainQ78)]      // -6.02 dB
    [InlineData((short)-512)]      // -2.00 dB
    [InlineData((short)770)]       // +3.01 dB, the largest boost these fixtures take without clipping
    public void The_stream_path_scales_by_the_factor_the_header_states(short gainQ78)
    {
        //Arrange
        var untouched = StreamDecode(File.ReadAllBytes(TestAssets.Path(TestAssets.OpusSweepStereo)));

        //Act
        var gained = StreamDecode(PacketFixtures.WithOutputGain(TestAssets.OpusSweepStereo, gainQ78));

        //Assert
        // Measured worst case across the fixtures and these three gains: 0.00023. The codec applies
        // the gain to its 16-bit intermediate, so the result is the ideal scaling to within a step
        // of that - not to the last float bit.
        gained.Length.Should().Be(untouched.Length);
        AudioAssertions.RelativeRmsError(gained, Scaled(untouched, LinearFactor(gainQ78)))
            .Should().BeLessThan(0.001);
    }

    [Theory]
    [InlineData(HalfGainQ78)]
    [InlineData((short)-512)]
    [InlineData((short)770)]
    public void The_packet_path_scales_by_the_factor_the_header_states(short gainQ78)
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        var untouched = PacketDecode(split.CodecPrivate, split.Packets);

        //Act
        var gained = PacketDecode(
            PacketFixtures.CodecPrivateWithOutputGain(TestAssets.OpusSweepStereo, gainQ78),
            split.Packets);

        //Assert
        gained.Length.Should().Be(untouched.Length);
        AudioAssertions.RelativeRmsError(gained, Scaled(untouched, LinearFactor(gainQ78)))
            .Should().BeLessThan(0.001);
    }

    [Fact]
    public void A_boost_past_full_scale_saturates_rather_than_wrapping()
    {
        //Arrange
        // These fixtures peak near 0.52, so +6.02 dB asks for 1.04 and cannot have it. The
        // reference implementation clamps at full scale, and so does this port - the alternative,
        // wrapping, would turn a loud passage into noise.
        var untouched = StreamDecode(File.ReadAllBytes(TestAssets.Path(TestAssets.OpusSweepStereo)));

        //Act
        var gained = StreamDecode(
            PacketFixtures.WithOutputGain(TestAssets.OpusSweepStereo, DoubleGainQ78));

        //Assert
        var peak = 0f;

        for (var i = 0; i < gained.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(gained[i]));
        }

        peak.Should().BeLessThanOrEqualTo(1f);
        peak.Should().BeGreaterThan(0.99f);

        // Clipping is why the tolerance here is an order of magnitude looser than the one above:
        // measured 0.0026 against the ideal doubling, versus 0.00023 when nothing clips.
        AudioAssertions.RelativeRmsError(gained, Scaled(untouched, LinearFactor(DoubleGainQ78)))
            .Should().BeLessThan(0.005);
    }

    [Theory]
    [InlineData(HalfGainQ78)]
    [InlineData((short)770)]
    public void Both_paths_stay_sample_identical_at_a_non_zero_gain(short gainQ78)
    {
        //Arrange
        // The reason the gain goes through the codec's own control rather than a multiply in
        // managed code on each path: one implementation, one rounding, no drift.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);

        //Act
        var fromStream = StreamDecode(
            PacketFixtures.WithOutputGain(TestAssets.OpusSweepStereo, gainQ78));
        var fromPackets = PacketDecode(
            PacketFixtures.CodecPrivateWithOutputGain(TestAssets.OpusSweepStereo, gainQ78),
            split.Packets);

        //Assert
        fromPackets.Length.Should().BeGreaterThanOrEqualTo(fromStream.Length);

        for (var i = 0; i < fromStream.Length; i++)
        {
            if (fromStream[i] != fromPackets[i])
            {
                Assert.Fail($"The two paths differ at sample {i} with gain {gainQ78}: " +
                            $"{fromStream[i]} vs {fromPackets[i]}.");
            }
        }
    }

    [Fact]
    public void A_zero_gain_header_decodes_exactly_as_the_untouched_fixture_does()
    {
        //Arrange
        // Every committed fixture stores 0, and every other test in this suite depends on gain 0
        // being a no-op. This also proves the header-rewriting the tests above rely on changes
        // nothing but the gain.
        var untouched = StreamDecode(File.ReadAllBytes(TestAssets.Path(TestAssets.OpusSweepStereo)));

        //Act
        var rewritten = StreamDecode(PacketFixtures.WithOutputGain(TestAssets.OpusSweepStereo, 0));

        //Assert
        rewritten.Length.Should().Be(untouched.Length);

        for (var i = 0; i < untouched.Length; i++)
        {
            if (rewritten[i] != untouched[i])
            {
                Assert.Fail($"A zero-gain header changed sample {i}.");
            }
        }
    }

    [Fact]
    public void The_gain_survives_a_reset_on_the_packet_path()
    {
        //Arrange
        // The codec's gain sits ABOVE its reset marker, so ResetState() leaves it alone - which is
        // what stops a seek from silently restoring full volume half way through a file. Nothing
        // in this repository re-applies it, so this test is the guard.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        var factory = new OpusPacketCodecFactory();

        using var gained = factory.CreateDecoder(
            "opus", PacketFixtures.CodecPrivateWithOutputGain(TestAssets.OpusSweepStereo, HalfGainQ78), null);
        using var plain = factory.CreateDecoder("opus", split.CodecPrivate, null);

        var gainedOutput = new float[gained.MaxSamplesPerPacket];
        var plainOutput = new float[plain.MaxSamplesPerPacket];

        for (var i = 0; i < 20; i++)
        {
            gained.DecodePacket(split.Packets[i], gainedOutput);
        }

        gained.Reset();

        //Act
        // Feed both from the same place - four packets of pre-roll, then the packet under test.
        var written = 0;

        for (var i = 20; i <= 24; i++)
        {
            written = gained.DecodePacket(split.Packets[i], gainedOutput);
            plain.DecodePacket(split.Packets[i], plainOutput);
        }

        //Assert
        var expected = LinearFactor(HalfGainQ78);

        for (var i = 0; i < written; i++)
        {
            ((double)gainedOutput[i]).Should().BeApproximately(plainOutput[i] * expected, 0.002);
        }
    }

    [Fact]
    public void The_gain_survives_a_seek_on_the_stream_path()
    {
        //Arrange
        // The same guard for the reader, whose Seek() calls the same ResetState().
        using var gainedStream =
            new MemoryStream(PacketFixtures.WithOutputGain(TestAssets.OpusSweepStereo, HalfGainQ78));
        using var plainStream = TestAssets.Open(TestAssets.OpusSweepStereo);

        using var gained = new OggOpusReader(gainedStream, leaveOpen: true);
        using var plain = new OggOpusReader(plainStream, leaveOpen: true);

        var gainedBlock = new float[gained.Channels * 4800];
        var plainBlock = new float[plain.Channels * 4800];

        //Act
        gained.Seek(48000).Should().BeTrue();
        plain.Seek(48000).Should().BeTrue();

        var read = gained.Read(gainedBlock);
        plain.Read(plainBlock).Should().Be(read);

        //Assert
        var expected = LinearFactor(HalfGainQ78);

        for (var i = 0; i < read; i++)
        {
            ((double)gainedBlock[i]).Should().BeApproximately(plainBlock[i] * expected, 0.002);
        }
    }
}
