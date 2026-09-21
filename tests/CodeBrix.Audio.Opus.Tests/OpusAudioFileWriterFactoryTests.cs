using System;
using System.IO;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using SilverAssertions.Specialized;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for the IAudioFileWriter seam over the Opus encoder - the write-side peer of the ".opus"
/// reader, and what lets anything in CodeBrix.Audio that writes audio by file name write .opus.
/// </summary>
/// <remarks>
/// These drive the factory DIRECTLY rather than through AudioFileWriterRegistry, so they say
/// nothing about registration and are safe to run in parallel with anything else; the registry
/// side is OpusWriterRegistrationTests. Opus is lossy, so a round trip is compared by level and
/// by relative error, never sample for sample.
/// </remarks>
public class OpusAudioFileWriterFactoryTests
{
    private const int SampleRate = 48000;

    /// <summary>Generates a tone: a different frequency per channel, so a swap cannot hide.</summary>
    private static float[] Tone(int sampleRate, int channels, double seconds, double frequency = 440)
    {
        var frames = (int)(sampleRate * seconds);
        var samples = new float[frames * channels];

        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                var f = frequency * (c == 1 ? 1.5 : 1.0);
                samples[(i * channels) + c] = (float)(0.5 * Math.Sin(2 * Math.PI * f * i / sampleRate));
            }
        }

        return samples;
    }

    /// <summary>Writes one buffer through the seam, start to finish, and hands back the bytes.</summary>
    private static byte[] WriteThroughTheSeam(
        IAudioFileWriterFactory factory, float[] samples, int sampleRate, int channels) =>
        WriteThroughTheSeam(factory, samples, factory.DefaultFormat(sampleRate, channels));

    /// <summary>The same, at a format of the caller's own choosing.</summary>
    private static byte[] WriteThroughTheSeam(
        IAudioFileWriterFactory factory, float[] samples, WaveFormat format)
    {
        using var output = new MemoryStream();

        using (var writer = factory.Create(output, format))
        {
            writer.Write(samples, 0, samples.Length);
            writer.Finish();
        }

        return output.ToArray();
    }

    // ----- what the factory says about itself -----

    [Fact]
    public void The_factory_declares_the_opus_extension()
    {
        //Arrange / Act
        var factory = new OpusAudioFileWriterFactory();

        //Assert
        factory.Extensions.Count.Should().Be(1);
        factory.Extensions[0].Should().Be(".opus");
    }

    [Fact]
    public void The_factory_does_not_need_a_stream_that_can_seek() =>
        new OpusAudioFileWriterFactory().RequiresSeekableStream.Should().BeFalse();

    [Fact]
    public void The_default_format_is_float_at_the_rate_it_was_given()
    {
        //Arrange
        // 44.1 kHz is what an offline render ordinarily produces. The writer resamples to the
        // 48 kHz Opus encodes at, so there is no reason to make the caller convert first.
        var factory = new OpusAudioFileWriterFactory();

        //Act
        var format = factory.DefaultFormat(44100, 2);

        //Assert
        format.SampleRate.Should().Be(44100);
        format.Channels.Should().Be(2);
        format.BitsPerSample.Should().Be(32);
        format.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
    }

    [Fact]
    public void The_default_format_refuses_a_rate_that_is_not_positive()
    {
        //Arrange / Act
        var act = () => new OpusAudioFileWriterFactory().DefaultFormat(0, 2);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_default_format_refuses_multichannel()
    {
        //Arrange / Act
        // Channel mapping family 0 is the limit everywhere else in this package too.
        var act = () => new OpusAudioFileWriterFactory().DefaultFormat(48000, 6);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ----- writing -----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_file_written_through_the_seam_reads_back_as_the_audio_that_went_in(int channels)
    {
        //Arrange
        var factory = new OpusAudioFileWriterFactory();
        var source = Tone(SampleRate, channels, 0.5);

        //Act
        var bytes = WriteThroughTheSeam(factory, source, SampleRate, channels);

        //Assert
        using var stream = new MemoryStream(bytes);
        using var reader = new OpusFileReader(stream);
        var decoded = AudioAssertions.ReadAll(reader);

        reader.WaveFormat.Channels.Should().Be(channels);
        (decoded.Length / channels).Should().Be(source.Length / channels);

        // Lossy, so a tolerance - but a tone survives encoding well.
        AudioAssertions.RelativeRmsError(decoded, source).Should().BeLessThan(0.25);
        AudioAssertions.Rms(decoded).Should().BeApproximately(AudioAssertions.Rms(source), 0.05);
    }

    [Fact]
    public void A_stream_that_cannot_seek_is_enough_to_write_a_whole_file()
    {
        //Arrange
        // The claim RequiresSeekableStream makes, proved rather than asserted: this stream throws
        // from Seek, Position and Length, so a writer that patched a header would fail here.
        var factory = new OpusAudioFileWriterFactory();
        var source = Tone(SampleRate, 2, 0.25);

        using var forwardOnly = new ForwardOnlyStream();

        //Act
        using (var writer = factory.Create(forwardOnly, factory.DefaultFormat(SampleRate, 2)))
        {
            writer.Write(source.AsSpan());
            writer.Finish();
        }

        //Assert
        using var stream = new MemoryStream(forwardOnly.ToArray());
        using var reader = new OpusFileReader(stream);
        var decoded = AudioAssertions.ReadAll(reader);

        reader.WaveFormat.Channels.Should().Be(2);
        (decoded.Length / 2).Should().Be(source.Length / 2);
        AudioAssertions.Rms(decoded).Should().BeGreaterThan(0.1);
    }

    [Fact]
    public void The_writer_reports_every_sample_it_was_given()
    {
        //Arrange
        var factory = new OpusAudioFileWriterFactory();
        var source = Tone(SampleRate, 2, 0.1);

        using var output = new MemoryStream();

        //Act
        using var writer = factory.Create(output, factory.DefaultFormat(SampleRate, 2));
        writer.Write(source, 0, source.Length);
        writer.Write(source.AsSpan());

        //Assert
        // Counting every channel, as the seam says: two buffers of interleaved stereo.
        writer.SamplesWritten.Should().Be(source.Length * 2L);
        writer.WaveFormat.SampleRate.Should().Be(SampleRate);
        writer.WaveFormat.Channels.Should().Be(2);
    }

    [Fact]
    public void The_writer_leaves_the_stream_it_was_handed_open()
    {
        //Arrange
        var factory = new OpusAudioFileWriterFactory();
        var output = new MemoryStream();

        //Act
        using (var writer = factory.Create(output, factory.DefaultFormat(SampleRate, 1)))
        {
            writer.Write(Tone(SampleRate, 1, 0.1), 0, SampleRate / 10);
        }

        //Assert
        output.CanWrite.Should().BeTrue();
        output.Length.Should().BeGreaterThan(0);
        output.Dispose();
    }

    [Fact]
    public void Finishing_twice_is_harmless_and_writing_afterwards_is_not_allowed()
    {
        //Arrange
        var factory = new OpusAudioFileWriterFactory();

        using var output = new MemoryStream();
        var writer = factory.Create(output, factory.DefaultFormat(SampleRate, 1));
        writer.Write(Tone(SampleRate, 1, 0.05), 0, SampleRate / 20);

        //Act
        writer.Finish();
        writer.Finish();
        writer.Dispose();

        var act = () => writer.Write(new float[10], 0, 10);

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_write_that_runs_past_the_end_of_the_buffer_is_refused()
    {
        //Arrange
        var factory = new OpusAudioFileWriterFactory();

        using var output = new MemoryStream();
        using var writer = factory.Create(output, factory.DefaultFormat(SampleRate, 1));

        //Act
        var nullBuffer = () => writer.Write(null, 0, 1);
        var pastTheEnd = () => writer.Write(new float[10], 4, 8);

        //Assert
        nullBuffer.Should().Throw<ArgumentNullException>();
        pastTheEnd.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ----- the options -----

    [Fact]
    public void The_bitrate_the_factory_was_given_is_the_bitrate_the_file_is_written_at()
    {
        //Arrange
        // Constant bitrate on both sides, so the setting is the whole difference: at 160 kbps the
        // file has to be several times the size of the same audio at 32 kbps.
        var thin = new OpusAudioFileWriterFactory(new OpusFileWriterOptions
        {
            Bitrate = 32_000,
            UseVariableBitrate = false
        });

        var fat = new OpusAudioFileWriterFactory(new OpusFileWriterOptions
        {
            Bitrate = 160_000,
            UseVariableBitrate = false
        });

        var source = Tone(SampleRate, 2, 0.5);

        //Act
        var thinBytes = WriteThroughTheSeam(thin, source, SampleRate, 2);
        var fatBytes = WriteThroughTheSeam(fat, source, SampleRate, 2);

        //Assert
        thinBytes.Length.Should().BeGreaterThan(0);
        fatBytes.Length.Should().BeGreaterThan(thinBytes.Length * 2);
    }

    [Fact]
    public void The_tags_the_factory_was_given_reach_the_file()
    {
        //Arrange
        var options = new OpusFileWriterOptions { Profile = OpusEncodingProfile.Voice };
        options.Tags["TITLE"] = "Written through the seam";

        var factory = new OpusAudioFileWriterFactory(options);

        //Act
        var bytes = WriteThroughTheSeam(factory, Tone(SampleRate, 1, 0.1), SampleRate, 1);

        //Assert
        using var stream = new MemoryStream(bytes);
        using var reader = new OpusFileReader(stream);

        reader.Tags["TITLE"][0].Should().Be("Written through the seam");
    }

    [Fact]
    public void The_factory_holds_the_options_it_was_given_and_defaults_when_it_was_given_none()
    {
        //Arrange
        var options = new OpusFileWriterOptions { Bitrate = 128_000 };

        //Act
        var chosen = new OpusAudioFileWriterFactory(options);
        var standard = new OpusAudioFileWriterFactory();

        //Assert
        chosen.Options.Bitrate.Should().Be(128_000);
        standard.Options.Bitrate.Should().Be(96_000);
        standard.Options.Profile.Should().Be(OpusEncodingProfile.Music);
        standard.Options.UseVariableBitrate.Should().BeTrue();
        standard.Options.Complexity.Should().Be(10);
    }

    [Fact]
    public void Options_out_of_range_are_refused_where_they_are_given()
    {
        //Arrange / Act
        // At construction rather than at the first write: a bad setting is reported where the
        // mistake was made, not at the file that happens to be written first.
        var act = () => new OpusAudioFileWriterFactory(new OpusFileWriterOptions { Bitrate = 10 });

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ----- only the rate and the channel count of a format are used -----

    [Fact]
    public void A_pcm_format_writes_the_same_opus_audio_that_a_float_format_does()
    {
        //Arrange
        // An Opus file stores a compressed payload, so a bit depth means nothing to it: the
        // encoding is IGNORED rather than refused. That is what lets an application render "to
        // whatever extension the user picked" with one fixed WaveFormat and not fail on .opus.
        var factory = new OpusAudioFileWriterFactory();
        var source = Tone(SampleRate, 2, 0.25);

        //Act
        var fromPcm = WriteThroughTheSeam(factory, source, new WaveFormat(SampleRate, 16, 2));
        var fromFloat = WriteThroughTheSeam(
            factory, source, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2));

        //Assert
        using var pcmStream = new MemoryStream(fromPcm);
        using var pcmReader = new OpusFileReader(pcmStream);
        var pcmDecoded = AudioAssertions.ReadAll(pcmReader);

        using var floatStream = new MemoryStream(fromFloat);
        using var floatReader = new OpusFileReader(floatStream);
        var floatDecoded = AudioAssertions.ReadAll(floatReader);

        pcmReader.WaveFormat.Channels.Should().Be(2);
        pcmReader.EncoderInputSampleRate.Should().Be(SampleRate);
        pcmReader.TotalTime.TotalSeconds.Should()
            .BeApproximately(floatReader.TotalTime.TotalSeconds, 0.001);

        pcmDecoded.Length.Should().Be(floatDecoded.Length);
        (pcmDecoded.Length / 2).Should().Be(source.Length / 2);
        AudioAssertions.RelativeRmsError(pcmDecoded, source).Should().BeLessThan(0.25);
    }

    [Fact]
    public void The_writer_reports_the_format_it_was_given_even_when_that_format_is_pcm()
    {
        //Arrange
        // The seam documents this property as the format the writer was created with, so that is
        // exactly what comes back. Its rate and channel count are what the encoder is fed; its bit
        // depth is not what the file stores, and the XML documentation says so.
        var factory = new OpusAudioFileWriterFactory();
        var format = new WaveFormat(44100, 16, 2);

        using var output = new MemoryStream();

        //Act
        using var writer = factory.Create(output, format);

        //Assert
        ReferenceEquals(writer.WaveFormat, format).Should().BeTrue();
        writer.WaveFormat.SampleRate.Should().Be(44100);
        writer.WaveFormat.Channels.Should().Be(2);
    }

    // ----- what the factory refuses -----

    [Fact]
    public void A_format_whose_sample_rate_is_not_positive_is_refused()
    {
        //Arrange
        // The encoding is ignored, but the rate and the channel count are the two things the
        // encoder genuinely cannot do without.
        var factory = new OpusAudioFileWriterFactory();

        using var output = new MemoryStream();

        //Act
        var act = () => factory.Create(output, new WaveFormat(0, 16, 2));

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_multichannel_format_is_refused()
    {
        //Arrange
        var factory = new OpusAudioFileWriterFactory();

        using var output = new MemoryStream();

        //Act
        var act = () => factory.Create(output, WaveFormat.CreateIeeeFloatWaveFormat(48000, 6));

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_stream_that_cannot_be_written_to_is_refused()
    {
        //Arrange
        var factory = new OpusAudioFileWriterFactory();
        var format = factory.DefaultFormat(SampleRate, 2);

        using var readOnly = new MemoryStream(new byte[16], writable: false);

        //Act
        var noStream = () => factory.Create(null, format);
        var noFormat = () => factory.Create(new MemoryStream(), null);
        var notWritable = () => factory.Create(readOnly, format);

        //Assert
        noStream.Should().Throw<ArgumentNullException>();
        noFormat.Should().Throw<ArgumentNullException>();
        notWritable.Should().Throw<ArgumentException>();
    }
}
