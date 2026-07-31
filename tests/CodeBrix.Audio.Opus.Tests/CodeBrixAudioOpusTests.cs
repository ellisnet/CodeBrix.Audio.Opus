using System;
using System.IO;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for the Register() entry point - the one call a consuming application makes.
/// </summary>
/// <remarks>
/// Registration is deliberately process-wide and permanent, which is right for the library and
/// awkward for tests: nothing here can assert the UNregistered state without depending on test
/// ordering. So these tests assert what is true after registering, and register first themselves.
/// </remarks>
[Collection("Registration")]
public class CodeBrixAudioOpusTests
{
    [Fact]
    public void Register_makes_opus_openable_by_file_name()
    {
        //Arrange
        CodeBrixAudioOpus.Register();

        //Act
        var supported = AudioFileReaderRegistry.Supports("voice-note.opus");

        //Assert
        supported.Should().BeTrue();
        CodeBrixAudioOpus.IsRegistered.Should().BeTrue();
    }

    [Fact]
    public void Register_is_idempotent()
    {
        //Arrange / Act
        CodeBrixAudioOpus.Register();
        CodeBrixAudioOpus.Register();
        CodeBrixAudioOpus.Register();

        //Assert
        CodeBrixAudioOpus.IsRegistered.Should().BeTrue();
        AudioFileReaderRegistry.Supports(".opus").Should().BeTrue();
    }

    [Fact]
    public void The_registry_opens_a_real_opus_file_after_registering()
    {
        //Arrange
        CodeBrixAudioOpus.Register();

        //Act
        using var stream = AudioFileReaderRegistry.OpenFile(TestAssets.Path(TestAssets.OpusToneStereo));

        //Assert
        stream.WaveFormat.SampleRate.Should().Be(48000);
        stream.WaveFormat.Channels.Should().Be(2);
        stream.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AudioFileReader_reads_an_opus_file_by_name_after_registering()
    {
        //Arrange
        // This is the path a consuming application actually takes.
        CodeBrixAudioOpus.Register();

        //Act
        using var reader = new AudioFileReader(TestAssets.Path(TestAssets.OpusToneStereo));
        var samples = new float[4800 * 2];
        var read = reader.Read(samples);

        //Assert
        reader.WaveFormat.SampleRate.Should().Be(48000);
        read.Should().BeGreaterThan(0);
        AudioAssertions.Rms(samples.AsSpan(0, read)).Should().BeGreaterThan(0.05);
    }

    [Fact]
    public void Registering_with_a_null_engine_is_rejected()
    {
        //Arrange / Act
        var register = () => CodeBrixAudioOpus.Register(null);

        //Assert
        register.Should().Throw<ArgumentNullException>();
    }
}

/// <summary>
/// Keeps the registration tests out of parallel with anything else that inspects global
/// registry state.
/// </summary>
[CollectionDefinition("Registration", DisableParallelization = true)]
public class RegistrationCollection
{
}
