using System;
using System.IO;
using CodeBrix.Audio.Opus.Internal;
using CodeBrix.Audio.Opus.Ogg;
using SilverAssertions;
using SilverAssertions.Numeric;
using SilverAssertions.Specialized;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for <see cref="OggOpusWriter" />, the Ogg layer that <see cref="OpusFileWriter" /> and the
/// engine's Opus sound encoder both write through.
/// </summary>
public class OggOpusWriterTests
{
    private const uint SerialNumber = 0x5EED1234;

    private static float[] Tone(int sampleRate, int channels, double seconds)
    {
        var frames = (int)(sampleRate * seconds);
        var samples = new float[frames * channels];

        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                var f = 440 * (c == 1 ? 1.5 : 1.0);
                samples[(i * channels) + c] = (float)(0.5 * Math.Sin(2 * Math.PI * f * i / sampleRate));
            }
        }

        return samples;
    }

    private static OggOpusWriterSettings Settings() =>
        OpusEncoderSettingsMapper.ToSettings(new OpusFileWriterOptions());

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void A_partial_stereo_frame_is_refused(int rate)
    {
        //Arrange
        using var stream = new MemoryStream();
        using var writer = new OggOpusWriter(stream, 2, rate, Settings(), SerialNumber);

        //Act
        var write = () => writer.Write(new float[1001]);

        //Assert
        write.Should().Throw<ArgumentException>()
            .WithMessage("The sample count (1001) must be a whole number of 2-channel frames.*");
    }

    [Fact]
    public void An_empty_write_is_still_a_no_op()
    {
        //Arrange
        using var stream = new MemoryStream();
        using var writer = new OggOpusWriter(stream, 2, 48000, Settings(), SerialNumber);
        var before = stream.Length;

        //Act
        writer.Write(ReadOnlySpan<float>.Empty);

        //Assert
        stream.Length.Should().Be(before);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(22050)]
    public void Whole_frames_in_odd_sized_chunks_end_on_the_exact_granule(int rate)
    {
        //Arrange
        var source = Tone(rate, 2, 0.5);
        using var stream = new MemoryStream();
        int preSkip;

        //Act
        using (var writer = new OggOpusWriter(stream, 2, rate, Settings(), SerialNumber))
        {
            preSkip = writer.PreSkip;
            for (var offset = 0; offset < source.Length; offset += 778)
            {
                writer.Write(source.AsSpan(offset, Math.Min(778, source.Length - offset)));
            }
        }

        stream.Position = 0;
        using var reader = new OggOpusReader(stream, leaveOpen: true);

        //Assert
        var frames = (long)source.Length / 2;
        var exactLength = ((frames * 48000) + (rate / 2)) / rate;
        reader.Head.PreSkip.Should().Be(preSkip);
        reader.TotalGranule.Should().Be(preSkip + exactLength);
    }

    [Theory]
    [InlineData(1, 44100)]
    [InlineData(2, 22050)]
    [InlineData(1, 16000)]
    public void The_pre_skip_includes_the_resampler_delay_for_input_not_at_48_khz(int channels, int rate)
    {
        //Arrange
        using var resampled = new MemoryStream();
        using var native = new MemoryStream();

        //Act
        using var resampledWriter = new OggOpusWriter(resampled, channels, rate, Settings(), SerialNumber);
        using var nativeWriter = new OggOpusWriter(native, channels, 48000, Settings(), SerialNumber);
        var delay = new OpusEncodingCore(channels, rate, Settings()).ResamplerDelay;

        //Assert
        delay.Should().BeGreaterThan(0);
        resampledWriter.PreSkip.Should().Be(nativeWriter.PreSkip + delay);
        new OpusEncodingCore(channels, 48000, Settings()).ResamplerDelay.Should().Be(0);
    }
}
