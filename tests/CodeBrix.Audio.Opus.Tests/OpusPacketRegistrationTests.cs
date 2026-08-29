using System;
using System.Linq;
using CodeBrix.Audio.Engine.Backends.MiniAudio;
using CodeBrix.Audio.Opus.Codecs;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Collections;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests that Register() wires up the PACKET seam as well as the stream one, so an application
/// already calling it picks up container audio with no change of its own.
/// </summary>
/// <remarks>
/// Registration is process-wide and permanent, so - like the other registration tests - these
/// assert what is true afterwards and register first themselves. They open no audio device:
/// MiniAudioEngine's constructor initialises a miniaudio context and nothing more, and the shared
/// output's registration list is static state that exists before anything starts.
/// </remarks>
[Collection("Registration")]
public class OpusPacketRegistrationTests
{
    [Fact]
    public void Register_adds_the_packet_factory_to_the_shared_output()
    {
        //Arrange / Act
        CodeBrixAudioOpus.Register();

        //Assert
        SharedAudioOutput.RegisteredPacketCodecFactories
            .Should().Contain(factory => factory is OpusPacketCodecFactory);
    }

    [Fact]
    public void Repeated_registration_leaves_exactly_one_packet_factory()
    {
        //Arrange / Act
        // ONE static instance is the whole reason this holds: the shared output de-duplicates on
        // the instance, so a factory constructed per call would stack up.
        CodeBrixAudioOpus.Register();
        CodeBrixAudioOpus.Register();
        CodeBrixAudioOpus.Register();

        //Assert
        SharedAudioOutput.RegisteredPacketCodecFactories
            .Count(factory => factory is OpusPacketCodecFactory)
            .Should().Be(1);
    }

    [Fact]
    public void Register_with_an_engine_adds_the_packet_factory_to_that_engine()
    {
        //Arrange
        using var engine = new MiniAudioEngine();

        //Act
        CodeBrixAudioOpus.Register(engine);

        //Assert
        engine.GetRegisteredPacketCodecs("opus")
            .Should().Contain(factory => factory is OpusPacketCodecFactory);
    }

    [Fact]
    public void Register_with_an_engine_still_registers_the_stream_factory()
    {
        //Arrange
        // The packet registration is an ADDITION: the seam an existing caller relies on has to
        // still be there afterwards.
        using var engine = new MiniAudioEngine();

        //Act
        CodeBrixAudioOpus.Register(engine);

        //Assert
        engine.GetRegisteredCodecs("opus")
            .Should().Contain(factory => factory is OpusCodecFactory);
    }
}
