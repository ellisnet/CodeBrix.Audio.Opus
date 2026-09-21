using System;
using System.IO;
using CodeBrix.Audio.Abc;
using CodeBrix.Audio.Midi;
using CodeBrix.Audio.Synth;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests that Register() wires up the WRITER seam as well as the reader and the two codec seams,
/// so an application already calling it can write .opus by file name with no change of its own.
/// </summary>
/// <remarks>
/// <para>
/// Registration is process-wide and permanent, so - like the other registration tests - these
/// assert what is true afterwards and register first themselves.
/// </para>
/// <para>
/// The two end-to-end tests render MIDI and abc straight to .opus through CodeBrix.Audio's
/// SoundFontRenderer, which is the point of the whole seam: nothing in CodeBrix.Audio names Opus,
/// and nothing here names WAV. They synthesize with ToneSynthesizer so the test needs no SoundFont
/// and reaches nothing outside this repository.
/// </para>
/// </remarks>
[Collection("Registration")]
public class OpusWriterRegistrationTests
{
    private const int RenderSampleRate = 44100;

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".opus");

    /// <summary>Four rising quarter notes, built as MIDI events rather than read from a file.</summary>
    private static MidiSequence BuildMidiSequence()
    {
        const int quarter = 120;

        var collection = new MidiEventCollection(1, quarter);

        var note = 60;
        for (var beat = 0; beat < 4; beat++, note += 2)
        {
            var start = beat * quarter;
            collection.AddEvent(new NoteOnEvent(start, 1, note, 100, quarter), 1);
            collection.AddEvent(new NoteEvent(start + quarter, 1, MidiCommandCode.NoteOff, note, 0), 1);
        }

        collection.PrepareForExport();

        return MidiSequence.FromEvents(collection);
    }

    /// <summary>The same four notes, written as an abc tune.</summary>
    private static MidiSequence BuildAbcSequence()
    {
        var book = AbcReader.Parse("X:1\nT:Seam test\nM:4/4\nL:1/4\nK:C\nCDEF|\n");

        return MidiSequence.FromEvents(AbcToMidi.Convert(book.Tunes[0]));
    }

    // ----- registration -----

    [Fact]
    public void Register_makes_opus_writable_by_file_name()
    {
        //Arrange
        CodeBrixAudioOpus.Register();

        //Act
        var supported = AudioFileWriterRegistry.Supports("tune.opus");

        //Assert
        supported.Should().BeTrue();
        AudioFileWriterRegistry.Supports(".opus").Should().BeTrue();
    }

    [Fact]
    public void The_registered_writer_factory_is_this_packages_one_and_stays_the_same_instance()
    {
        //Arrange
        CodeBrixAudioOpus.Register();
        var first = AudioFileWriterRegistry.Resolve(".opus");

        //Act
        CodeBrixAudioOpus.Register();
        CodeBrixAudioOpus.Register();
        var second = AudioFileWriterRegistry.Resolve("music.opus");

        //Assert
        (first is OpusAudioFileWriterFactory).Should().BeTrue();
        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void Registering_the_writer_leaves_the_reader_registration_alone()
    {
        //Arrange
        // The write side is an ADDITION: the seam an existing caller relies on has to still be
        // there afterwards.
        CodeBrixAudioOpus.Register();

        //Act / Assert
        AudioFileReaderRegistry.Supports(".opus").Should().BeTrue();
        AudioFileWriterRegistry.Supports(".opus").Should().BeTrue();
    }

    [Fact]
    public void The_registry_writes_a_file_by_name_after_registering()
    {
        //Arrange
        CodeBrixAudioOpus.Register();

        var frames = 48000 / 4;
        var source = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
            source[i * 2] = value;
            source[(i * 2) + 1] = value;
        }

        using var output = new MemoryStream();

        //Act
        using (var writer = AudioFileWriterRegistry.Create("tune.opus", output, 48000, 2))
        {
            writer.Write(source, 0, source.Length);
            writer.Finish();
        }

        //Assert
        output.Position = 0;
        using var reader = new OpusFileReader(output);
        var decoded = AudioAssertions.ReadAll(reader);

        reader.WaveFormat.Channels.Should().Be(2);
        (decoded.Length / 2).Should().Be(frames);
        AudioAssertions.Rms(decoded).Should().BeGreaterThan(0.1);
    }

    // ----- end to end, through CodeBrix.Audio's renderer -----

    [Fact]
    public void A_midi_sequence_renders_straight_to_an_opus_file()
    {
        //Arrange
        CodeBrixAudioOpus.Register();

        var sequence = BuildMidiSequence();
        var synthesizer = new ToneSynthesizer(RenderSampleRate);
        var path = TempFile();

        //Act
        // Nothing in CodeBrix.Audio names Opus: the extension is the whole of the decision.
        SoundFontRenderer.RenderToFile(synthesizer, sequence, path);

        //Assert
        try
        {
            using var reader = new OpusFileReader(path);
            var decoded = AudioAssertions.ReadAll(reader);

            reader.WaveFormat.Channels.Should().Be(2);
            reader.WaveFormat.SampleRate.Should().Be(48000);
            reader.EncoderInputSampleRate.Should().Be(RenderSampleRate);
            reader.TotalTime.TotalSeconds.Should().BeApproximately(sequence.Length.TotalSeconds, 0.05);
            AudioAssertions.Rms(decoded).Should().BeGreaterThan(0.01);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_render_asked_for_as_pcm_still_writes_an_opus_file()
    {
        //Arrange
        // An application that renders "to whatever extension the user picked" hands the renderer
        // ONE WaveFormat. For .wav that 16-bit format decides the stored sample format; for .opus
        // only its rate and channel count mean anything, and the rest is ignored rather than
        // refused - so the same call works for both.
        CodeBrixAudioOpus.Register();

        var sequence = BuildMidiSequence();
        var synthesizer = new ToneSynthesizer(RenderSampleRate);
        var path = TempFile();

        //Act
        SoundFontRenderer.RenderToFile(
            synthesizer, sequence, path, new WaveFormat(RenderSampleRate, 16, 2));

        //Assert
        try
        {
            using var reader = new OpusFileReader(path);
            var decoded = AudioAssertions.ReadAll(reader);

            reader.WaveFormat.Channels.Should().Be(2);
            reader.EncoderInputSampleRate.Should().Be(RenderSampleRate);
            reader.TotalTime.TotalSeconds.Should().BeApproximately(sequence.Length.TotalSeconds, 0.05);
            AudioAssertions.Rms(decoded).Should().BeGreaterThan(0.01);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_abc_tune_renders_straight_to_an_opus_file()
    {
        //Arrange
        CodeBrixAudioOpus.Register();

        var sequence = BuildAbcSequence();
        var synthesizer = new ToneSynthesizer(RenderSampleRate);
        var path = TempFile();

        //Act
        SoundFontRenderer.RenderToFile(synthesizer, sequence, path);

        //Assert
        try
        {
            using var reader = new OpusFileReader(path);
            var decoded = AudioAssertions.ReadAll(reader);

            reader.WaveFormat.Channels.Should().Be(2);
            reader.TotalTime.TotalSeconds.Should().BeApproximately(sequence.Length.TotalSeconds, 0.05);
            AudioAssertions.Rms(decoded).Should().BeGreaterThan(0.01);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rendering_to_a_stream_by_extension_writes_opus_and_leaves_the_stream_open()
    {
        //Arrange
        CodeBrixAudioOpus.Register();

        var sequence = BuildMidiSequence();
        var synthesizer = new ToneSynthesizer(RenderSampleRate);

        using var output = new MemoryStream();

        //Act
        SoundFontRenderer.RenderToStream(synthesizer, sequence, output, ".opus");

        //Assert
        output.CanWrite.Should().BeTrue();
        output.Length.Should().BeGreaterThan(0);

        output.Position = 0;
        using var reader = new OpusFileReader(output);

        reader.TotalTime.TotalSeconds.Should().BeApproximately(sequence.Length.TotalSeconds, 0.05);
    }
}
