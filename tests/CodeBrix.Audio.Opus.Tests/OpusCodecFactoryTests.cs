using System;
using System.IO;
using CodeBrix.Audio.Engine.Enums;
using CodeBrix.Audio.Engine.Structs;
using CodeBrix.Audio.Opus.Codecs;
using SilverAssertions;
using SilverAssertions.Collections;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for the engine-facing codec factory.
/// </summary>
/// <remarks>
/// The behaviour that matters most here is DECLINING. CodeBrix.Audio's metadata layer stamps
/// every Ogg stream with the format id "ogg" whatever codec is inside, so this factory is offered
/// Vorbis streams and the Vorbis factory is offered Opus streams. Returning null for what you
/// cannot decode is the whole basis of the two coexisting.
/// </remarks>
public class OpusCodecFactoryTests
{
    private static AudioFormat StereoFormat => new AudioFormat
    {
        Format = SampleFormat.F32,
        Channels = 2,
        Layout = AudioFormat.GetLayoutFromChannels(2),
        SampleRate = 48000
    };

    [Fact]
    public void Identity_matches_the_family_convention()
    {
        //Arrange / Act
        var factory = new OpusCodecFactory();

        //Assert
        factory.FactoryId.Should().Be("CodeBrix.Audio.Opus.ManagedOpus");
        factory.Priority.Should().Be(-10);
        factory.SupportedFormatIds.Should().Contain("ogg");
        factory.SupportedFormatIds.Should().Contain("opus");
    }

    [Fact]
    public void The_factory_declines_a_vorbis_stream_instead_of_failing_on_it()
    {
        //Arrange
        // This is the coexistence rule. An Ogg Vorbis file arrives under the same "ogg" format id
        // an Opus file does; accepting it and then failing would stop the engine trying the
        // Vorbis codec that CAN decode it.
        var factory = new OpusCodecFactory();
        using var vorbis = TestAssets.Open(TestAssets.VorbisToneStereo);

        //Act
        var decoder = factory.CreateDecoder(vorbis, "ogg", StereoFormat);

        //Assert
        decoder.Should().BeNull();
    }

    [Fact]
    public void The_factory_accepts_an_opus_stream_offered_as_ogg()
    {
        //Arrange
        var factory = new OpusCodecFactory();
        using var opus = TestAssets.Open(TestAssets.OpusToneStereo);

        //Act
        using var decoder = factory.CreateDecoder(opus, "ogg", StereoFormat);

        //Assert
        decoder.Should().NotBeNull();
        decoder.Channels.Should().Be(2);
        decoder.SampleRate.Should().Be(48000);
    }

    [Fact]
    public void The_factory_accepts_an_opus_stream_offered_as_opus()
    {
        //Arrange
        var factory = new OpusCodecFactory();
        using var opus = TestAssets.Open(TestAssets.OpusToneStereo);

        //Act
        using var decoder = factory.CreateDecoder(opus, "opus", StereoFormat);

        //Assert
        decoder.Should().NotBeNull();
    }

    [Fact]
    public void The_factory_declines_a_format_id_it_knows_nothing_about()
    {
        //Arrange
        var factory = new OpusCodecFactory();
        using var opus = TestAssets.Open(TestAssets.OpusToneStereo);

        //Act
        var decoder = factory.CreateDecoder(opus, "wav", StereoFormat);

        //Assert
        decoder.Should().BeNull();
    }

    [Fact]
    public void The_factory_rewinds_a_stream_an_earlier_factory_left_mid_way()
    {
        //Arrange
        // The engine does not rewind between factories on the format-id path, so a factory that
        // read a few bytes and declined leaves the stream where it stopped.
        var factory = new OpusCodecFactory();
        using var opus = TestAssets.Open(TestAssets.OpusToneStereo);
        opus.Position = 1234;

        //Act
        using var decoder = factory.CreateDecoder(opus, "ogg", StereoFormat);

        //Assert
        decoder.Should().NotBeNull();
    }

    [Fact]
    public void Probing_detects_the_format_without_being_told_it()
    {
        //Arrange
        var factory = new OpusCodecFactory();
        using var opus = TestAssets.Open(TestAssets.OpusToneStereo);

        //Act
        using var decoder = factory.TryCreateDecoder(opus, out var detected);

        //Assert
        decoder.Should().NotBeNull();
        detected.Channels.Should().Be(2);
        detected.SampleRate.Should().Be(48000);
        detected.Format.Should().Be(SampleFormat.F32);
    }

    [Fact]
    public void Probing_declines_a_vorbis_stream()
    {
        //Arrange
        var factory = new OpusCodecFactory();
        using var vorbis = TestAssets.Open(TestAssets.VorbisToneStereo);

        //Act
        var decoder = factory.TryCreateDecoder(vorbis, out _);

        //Assert
        decoder.Should().BeNull();
    }

    [Fact]
    public void An_encoder_is_created_for_the_opus_format_id()
    {
        //Arrange
        // This is what makes `new Recorder(captureDevice, stream, "opus")` record to .opus.
        var factory = new OpusCodecFactory();
        using var output = new MemoryStream();

        //Act
        using var encoder = factory.CreateEncoder(output, "opus", StereoFormat);

        //Assert
        encoder.Should().NotBeNull();
    }

    [Fact]
    public void No_encoder_is_created_for_the_shared_ogg_format_id()
    {
        //Arrange
        // An encoder cannot sniff what has not been written yet, and "ogg" does not say whether
        // Vorbis or Opus was meant - so claiming it would be guessing.
        var factory = new OpusCodecFactory();
        using var output = new MemoryStream();

        //Act
        var encoder = factory.CreateEncoder(output, "ogg", StereoFormat);

        //Assert
        encoder.Should().BeNull();
    }

    [Fact]
    public void A_decoder_created_by_the_factory_decodes_real_audio()
    {
        //Arrange
        var factory = new OpusCodecFactory();
        using var opus = TestAssets.Open(TestAssets.OpusToneStereo);
        using var decoder = factory.CreateDecoder(opus, "ogg", StereoFormat);

        var buffer = new float[4800 * 2];

        //Act
        var read = decoder.Decode(buffer);

        //Assert
        read.Should().BeGreaterThan(0);
        AudioAssertions.Rms(buffer.AsSpan(0, read)).Should().BeGreaterThan(0.05);
    }
}
