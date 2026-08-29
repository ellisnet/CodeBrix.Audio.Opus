using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Engine.Enums;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Opus.Codecs;
using CodeBrix.Audio.Opus.Ogg;
using SilverAssertions;
using SilverAssertions.Collections;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using SilverAssertions.Specialized;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for the PACKET seam - the factory and decoder a demultiplexer feeds, as opposed to the
/// stream seam that opens a whole Ogg file.
/// </summary>
/// <remarks>
/// <para>
/// The claim these have to prove is that the two seams decode the SAME audio. Both run through the
/// same internal codec on the same packets, so the bar is sample-exactness rather than a tolerance,
/// and the round-trip test holds them to it.
/// </para>
/// <para>
/// The other half is the answers this factory gives when it cannot serve a request. NULL means
/// "not mine" and lets the engine try the next factory; a THROW means "mine, and undecodable",
/// which is the multichannel case and is worth a specific message.
/// </para>
/// </remarks>
public class OpusPacketCodecFactoryTests
{
    /// <summary>An 80 ms pre-roll, in 20 ms packets - what RFC 7845 asks for after a seek.</summary>
    private const int PreRollPackets = 4;

    private static OpusPacketCodecFactory Factory => new OpusPacketCodecFactory();

    /// <summary>Decodes every packet of a split fixture, concatenated, exactly as fed.</summary>
    private static float[] DecodeAllPackets(IPacketSoundDecoder decoder, IReadOnlyList<byte[]> packets)
    {
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

        return all.ToArray();
    }

    [Fact]
    public void Identity_matches_the_family_convention()
    {
        //Arrange / Act
        var factory = Factory;

        //Assert
        factory.FactoryId.Should().Be("CodeBrix.Audio.Opus.ManagedOpus.Packets");
        factory.Priority.Should().Be(0);
        factory.SupportedCodecIds.Should().Contain("opus");
        factory.SupportedCodecIds.Count.Should().Be(1);
    }

    [Fact]
    public void The_factory_declines_another_codecs_packets()
    {
        //Arrange
        // Returning null rather than throwing is what lets several packet factories coexist: the
        // engine offers the request to the next one.
        var split = PacketFixtures.Split(TestAssets.OpusToneStereo);

        //Act
        var decoder = Factory.CreateDecoder("vorbis", split.CodecPrivate, null);

        //Assert
        decoder.Should().BeNull();
    }

    [Fact]
    public void The_factory_declines_codec_private_data_that_is_not_an_opus_header()
    {
        //Arrange
        var garbage = new byte[] { 2, 30, 30, 1, 2, 3, 4, 5 };

        //Act
        var decoder = Factory.CreateDecoder("opus", garbage, null);

        //Assert
        decoder.Should().BeNull();
    }

    [Fact]
    public void The_factory_declines_codec_private_data_that_is_too_short_to_be_a_header()
    {
        //Arrange / Act
        var decoder = Factory.CreateDecoder("opus", Array.Empty<byte>(), null);

        //Assert
        decoder.Should().BeNull();
    }

    [Fact]
    public void A_multichannel_header_is_refused_with_a_message_that_says_why()
    {
        //Arrange
        // Family 1 is the surround mapping. The multistream codec is vendored but unused, so this
        // is a real limit rather than a missing case - and a stream that hits it deserves better
        // than the engine's generic "no registered packet decoder".
        var surround = new OpusHead
        {
            ChannelCount = 6,
            ChannelMappingFamily = 1,
            PreSkip = 312
        }.ToBytes();

        //Act
        var create = () => Factory.CreateDecoder("opus", surround, null);

        //Assert
        create.Should().Throw<NotSupportedException>()
            .WithMessage("*channel mapping family 1 (surround, 6 channels)*")
            .WithMessage("*mapping family 0 (mono/stereo)*");
    }

    [Fact]
    public void A_family_zero_header_with_too_many_channels_is_refused_too()
    {
        //Arrange
        // Family 0 is DEFINED for mono and stereo only, so this header is malformed rather than
        // merely unsupported - but the caller still gets told which part of it was wrong.
        var malformed = new OpusHead
        {
            ChannelCount = 3,
            ChannelMappingFamily = 0,
            PreSkip = 312
        }.ToBytes();

        //Act
        var create = () => Factory.CreateDecoder("opus", malformed, null);

        //Assert
        create.Should().Throw<NotSupportedException>().WithMessage("*mono and stereo only*");
    }

    [Theory]
    [InlineData(TestAssets.OpusToneStereo, 2)]
    [InlineData(TestAssets.OpusToneMonoFrom16000, 1)]
    public void A_decoder_reports_48_kHz_and_the_headers_channel_count(string fixture, int channels)
    {
        //Arrange
        // The 16 kHz-sourced fixture is the one that matters: its header declares 16000, and a
        // decoder that believed it would play three times too slow.
        var split = PacketFixtures.Split(fixture);

        //Act
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);

        //Assert
        decoder.Should().NotBeNull();
        decoder.SampleRate.Should().Be(48000);
        decoder.Channels.Should().Be(channels);
        decoder.SampleFormat.Should().Be(SampleFormat.F32);
        decoder.MaxSamplesPerPacket.Should().Be(5760 * channels);
        decoder.PreSkipSamples.Should().Be(TestAssets.FixturePreSkip);
    }

    [Fact]
    public void An_output_buffer_that_is_too_small_is_refused_by_name()
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusToneStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var tooSmall = new float[8];

        //Act
        var decode = () => decoder.DecodePacket(split.Packets[0], tooSmall);

        //Assert
        // The message names the property that makes the problem impossible, because that is the
        // fix - not the number this particular packet happened to need.
        decode.Should().Throw<ArgumentException>().WithMessage("*MaxSamplesPerPacket*");
    }

    [Theory]
    [InlineData(TestAssets.OpusToneStereo)]
    [InlineData(TestAssets.OpusToneMonoFrom16000)]
    [InlineData(TestAssets.OpusSweepStereo)]
    public void Packets_decode_to_exactly_what_the_stream_reader_produces(string fixture)
    {
        //Arrange
        // The whole point of the seam: audio lifted out of a container must be the same audio the
        // file path produces. Both run the same codec over the same packets, so anything less than
        // sample-exactness would mean one of the two paths is doing something extra.
        var split = PacketFixtures.Split(fixture);

        using var reference = TestAssets.Open(fixture);
        using var streamReader = new OggOpusReader(reference, leaveOpen: true);
        var expected = new List<float>(streamReader.Channels * 96000);
        var block = new float[streamReader.Channels * 4800];
        int read;

        while ((read = streamReader.Read(block)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                expected.Add(block[i]);
            }
        }

        //Act
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var decoded = DecodeAllPackets(decoder, split.Packets);

        // The packet path emits everything the packets contain; the stream path stops at the final
        // granule position. The difference is the padding the encoder added to fill its last frame,
        // and trimming it is the CONTAINER's job - so drop the pre-skip here and compare the prefix.
        var audible = decoded.AsSpan(decoder.PreSkipSamples * decoder.Channels);

        //Assert
        audible.Length.Should().BeGreaterThanOrEqualTo(expected.Count);

        // Measured: 168 frames on the 0.25 s fixtures, 648 on the 2 s sweep - the encoder's
        // padding inside its last 20 ms frame. The bound is the codec-general one (a maximum
        // packet) rather than those figures, because a differently encoded file would pad
        // differently and still be correct.
        var extraFrames = (audible.Length - expected.Count) / decoder.Channels;
        extraFrames.Should().BeLessThan(5760);

        for (var i = 0; i < expected.Count; i++)
        {
            if (audible[i] != expected[i])
            {
                Assert.Fail(
                    $"Packet and stream decodes differ at sample {i}: {audible[i]} vs {expected[i]}.");
            }
        }
    }

    /// <summary>
    /// Decodes one packet of a fixture twice: straight through from the start, and again after a
    /// Reset() with <paramref name="preRollPackets" /> packets of pre-roll fed in first.
    /// </summary>
    /// <returns>The uninterrupted decode, then the decode after the reset.</returns>
    private static (float[] Straight, float[] AfterReset) DecodeWithAndWithoutAReset(
        string fixture, int preRollPackets)
    {
        var split = PacketFixtures.Split(fixture);
        var target = split.Packets.Count / 2;

        using var straight = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var expected = new float[straight.MaxSamplesPerPacket];
        var expectedLength = 0;

        for (var i = 0; i <= target; i++)
        {
            expectedLength = straight.DecodePacket(split.Packets[i], expected);
        }

        using var seeking = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var scratch = new float[seeking.MaxSamplesPerPacket];

        // Decode from the start, then pretend the source jumped: reset, rewind by the pre-roll and
        // feed forward again, exactly as a player honouring the seek contract would.
        for (var i = 0; i < target; i++)
        {
            seeking.DecodePacket(split.Packets[i], scratch);
        }

        seeking.Reset();

        for (var i = target - preRollPackets; i < target; i++)
        {
            seeking.DecodePacket(split.Packets[i], scratch);
        }

        var actualLength = seeking.DecodePacket(split.Packets[target], scratch);
        actualLength.Should().Be(expectedLength);

        return (expected.AsSpan(0, expectedLength).ToArray(), scratch.AsSpan(0, actualLength).ToArray());
    }

    [Fact]
    public void Reset_and_an_eighty_millisecond_pre_roll_rejoin_the_uninterrupted_decode()
    {
        //Arrange / Act
        // A codec that carries state between packets cannot decode correctly at a jump, so a seek
        // starts a little early and throws the pre-roll away. 80 ms is what RFC 7845 section 4.2
        // asks for and what Matroska records as an Opus track's SeekPreRoll - four of the 20 ms
        // packets these fixtures are encoded in.
        var (straight, afterReset) = DecodeWithAndWithoutAReset(TestAssets.OpusSweepStereo, PreRollPackets);

        //Assert
        // 80 ms gets CLOSE, not identical. Measured on this fixture: 0.074 relative RMS, largest
        // single-sample difference 0.085 - which is what the standard pre-roll is worth on a pure
        // sweep, the hardest case there is for a codec whose post-filter is a tonal comb. Anything
        // near 1.0 would mean the pre-roll had not worked at all.
        AudioAssertions.RelativeRmsError(afterReset, straight).Should().BeLessThan(0.12);
    }

    [Fact]
    public void A_longer_pre_roll_converges_on_the_uninterrupted_decode()
    {
        //Arrange / Act
        // The other end of the same measurement, and the number an application that wants a seek
        // to be inaudible should use: 240 ms of pre-roll on this fixture leaves a largest
        // difference of 0.00003 - one step of the codec's 16-bit intermediate.
        var (straight, afterReset) =
            DecodeWithAndWithoutAReset(TestAssets.OpusSweepStereo, PreRollPackets * 3);

        //Assert
        var largestDifference = 0f;

        for (var i = 0; i < straight.Length; i++)
        {
            largestDifference = Math.Max(largestDifference, Math.Abs(afterReset[i] - straight[i]));
        }

        largestDifference.Should().BeLessThan(0.0001f);
    }

    [Fact]
    public void Reset_leaves_the_decoder_exactly_as_a_newly_built_one()
    {
        //Arrange
        // This is what says the residual difference above belongs to the CODEC rather than to
        // Reset(): a reset decoder and a brand-new one, fed the same packets, agree sample for
        // sample. A partial reset would show up here and nowhere else.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        var target = split.Packets.Count / 2;

        using var reused = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var reusedOutput = new float[reused.MaxSamplesPerPacket];

        for (var i = 0; i < target; i++)
        {
            reused.DecodePacket(split.Packets[i], reusedOutput);
        }

        reused.Reset();

        using var fresh = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var freshOutput = new float[fresh.MaxSamplesPerPacket];

        //Act
        var reusedLength = 0;
        var freshLength = 0;

        for (var i = target - PreRollPackets; i <= target; i++)
        {
            reusedLength = reused.DecodePacket(split.Packets[i], reusedOutput);
            freshLength = fresh.DecodePacket(split.Packets[i], freshOutput);
        }

        //Assert
        reusedLength.Should().Be(freshLength);

        for (var i = 0; i < reusedLength; i++)
        {
            if (reusedOutput[i] != freshOutput[i])
            {
                Assert.Fail($"A reset decoder differs from a new one at sample {i}.");
            }
        }
    }

    [Fact]
    public void An_empty_packet_is_concealed_rather_than_refused()
    {
        //Arrange
        // A demultiplexer that knows it lost a packet says so with an empty one, and the codec
        // invents a plausible continuation. Throwing here would end playback over one dropped
        // packet.
        var split = PacketFixtures.Split(TestAssets.OpusSweepStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];

        for (var i = 0; i < 8; i++)
        {
            decoder.DecodePacket(split.Packets[i], output);
        }

        //Act
        var concealed = decoder.DecodePacket(ReadOnlySpan<byte>.Empty, output);

        //Assert
        concealed.Should().BeGreaterThan(0);
        concealed.Should().BeLessThanOrEqualTo(decoder.MaxSamplesPerPacket);

        // The next real packet still decodes, so the concealment left the decoder usable.
        var afterwards = decoder.DecodePacket(split.Packets[8], output);
        afterwards.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_corrupt_packet_fails_with_an_exception_the_caller_can_catch()
    {
        //Arrange
        // The vendored codec's own exception type is INTERNAL to this assembly, so letting it out
        // would hand the caller something it cannot name in a catch clause. A corrupt packet is
        // reported the same way a corrupt stream is.
        var split = PacketFixtures.Split(TestAssets.OpusToneStereo);
        using var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];

        // A table-of-contents byte announcing an arbitrary frame count, and then nothing to say how
        // many: not a decodable packet under any reading.
        var corrupt = new byte[] { 0x03 };

        //Act
        var decode = () => decoder.DecodePacket(corrupt, output);

        //Assert
        decode.Should().Throw<InvalidDataException>().WithMessage("*Opus packet*");
    }

    [Fact]
    public void A_disposed_decoder_decodes_nothing_instead_of_throwing()
    {
        //Arrange
        var split = PacketFixtures.Split(TestAssets.OpusToneStereo);
        var decoder = Factory.CreateDecoder("opus", split.CodecPrivate, null);
        var output = new float[decoder.MaxSamplesPerPacket];

        //Act
        decoder.Dispose();
        var written = decoder.DecodePacket(split.Packets[0], output);

        //Assert
        // The audio thread may be part-way through a packet when the player is torn down, so a
        // race there has to be harmless rather than fatal.
        written.Should().Be(0);
    }
}
