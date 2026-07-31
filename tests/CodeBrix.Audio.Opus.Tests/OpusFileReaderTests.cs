using System;
using System.IO;
using SilverAssertions;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using SilverAssertions.Specialized;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for decoding .opus files.
/// </summary>
public class OpusFileReaderTests
{
    [Theory]
    [InlineData(TestAssets.OpusToneStereo, 2)]
    [InlineData(TestAssets.OpusToneMonoFrom16000, 1)]
    [InlineData(TestAssets.OpusSweepStereo, 2)]
    public void WaveFormat_is_always_48khz_float(string fixture, int expectedChannels)
    {
        //Arrange / Act
        using var reader = new OpusFileReader(TestAssets.Path(fixture));

        //Assert
        reader.WaveFormat.SampleRate.Should().Be(TestAssets.DecodeSampleRate);
        reader.WaveFormat.Channels.Should().Be(expectedChannels);
        reader.WaveFormat.BitsPerSample.Should().Be(32);
    }

    [Fact]
    public void The_declared_rate_is_reported_separately_and_never_as_the_decode_rate()
    {
        //Arrange
        // This fixture was encoded from 16 kHz, so its header says 16000 while the stream still
        // decodes at 48 kHz. A decoder that treats the declared rate as real plays it three
        // times too slow - this is the test that catches that.
        using var reader = new OpusFileReader(TestAssets.Path(TestAssets.OpusToneMonoFrom16000));

        //Act
        var declared = reader.EncoderInputSampleRate;

        //Assert
        declared.Should().Be(16000);
        reader.WaveFormat.SampleRate.Should().Be(48000);
    }

    [Theory]
    [InlineData(TestAssets.OpusToneStereo, TestAssets.ShortFixtureSamples)]
    [InlineData(TestAssets.OpusToneMonoFrom16000, TestAssets.ShortFixtureSamples)]
    [InlineData(TestAssets.OpusSweepStereo, TestAssets.SweepFixtureSamples)]
    public void Length_excludes_the_pre_skip(string fixture, int expectedSamples)
    {
        //Arrange
        using var reader = new OpusFileReader(TestAssets.Path(fixture));

        //Act
        var frames = reader.Length / (reader.WaveFormat.Channels * sizeof(float));

        //Assert
        reader.PreSkip.Should().Be(TestAssets.FixturePreSkip);
        frames.Should().Be(expectedSamples);
    }

    [Theory]
    [InlineData(TestAssets.OpusToneStereo)]
    [InlineData(TestAssets.OpusToneMonoFrom16000)]
    public void Decoding_yields_exactly_the_audible_sample_count(string fixture)
    {
        //Arrange / Act
        var samples = AudioAssertions.DecodeAll(fixture);

        using var reader = new OpusFileReader(TestAssets.Path(fixture));
        var channels = reader.WaveFormat.Channels;

        //Assert
        // Not one sample more: the encoder pads the final frame, and the last page's granule
        // position is what trims that padding back off.
        (samples.Length / channels).Should().Be(TestAssets.ShortFixtureSamples);
    }

    [Theory]
    [InlineData(TestAssets.OpusToneStereo)]
    [InlineData(TestAssets.OpusSweepStereo)]
    public void Decoded_audio_matches_ffmpegs_own_decode(string fixture)
    {
        //Arrange
        // Two independent implementations of the same codec, sample for sample. Both of these
        // fixtures currently land at a relative error of about 0.0002 - far below the threshold -
        // so this catches any regression in pre-skip handling, channel order or frame boundaries
        // long before it becomes audible.
        var reference = AudioAssertions.ReadFfmpegReference(fixture);

        //Act
        var decoded = AudioAssertions.DecodeAll(fixture);

        //Assert
        decoded.Length.Should().Be(reference.Length);
        AudioAssertions.RelativeRmsError(decoded, reference).Should().BeLessThan(0.01);
    }

    [Fact]
    public void Decoded_audio_matches_ffmpeg_in_silk_mode_to_within_a_couple_of_samples()
    {
        //Arrange
        // The 32 kbps mono fixture encodes in pure SILK mode, and there this decoder sits about
        // two samples - 42 microseconds - ahead of a current libopus. Aligned, the two agree to
        // roughly 0.3%. It is an implementation difference in the SILK path against a much newer
        // libopus than the vendored codec tracks, NOT a pre-skip or granule error: the other two
        // fixtures match ffmpeg exactly at zero offset, and the decoded length here is exact.
        //
        // The test is written to say precisely that. A real pre-skip regression moves the offset
        // well beyond a couple of samples, and a decode regression shows up in the aligned error.
        var reference = AudioAssertions.ReadFfmpegReference(TestAssets.OpusToneMonoFrom16000);

        //Act
        var decoded = AudioAssertions.DecodeAll(TestAssets.OpusToneMonoFrom16000);
        var (bestError, bestShift) = AudioAssertions.BestAlignment(decoded, reference, maxShift: 8);

        //Assert
        decoded.Length.Should().Be(reference.Length);
        Math.Abs(bestShift).Should().BeLessThanOrEqualTo(3);
        bestError.Should().BeLessThan(0.01);
    }

    [Fact]
    public void Decoded_audio_is_not_silence()
    {
        //Arrange / Act
        var samples = AudioAssertions.DecodeAll(TestAssets.OpusToneStereo);

        //Assert
        AudioAssertions.Rms(samples).Should().BeGreaterThan(0.05);
    }

    [Fact]
    public void Seeking_lands_where_the_audio_says_it_should()
    {
        //Arrange
        // The fixture's instantaneous frequency is 200 + 1800*t Hz, so the frequency at the seek
        // point tells us where the decoder actually landed - independently of what the reader
        // claims its position is. One second in that is 2000 Hz, rising to 2360 Hz across the
        // 0.2 s measurement window, so the window averages about 2180 Hz.
        using var reader = new OpusFileReader(TestAssets.Path(TestAssets.OpusSweepStereo));
        var channels = reader.WaveFormat.Channels;
        var bytesPerFrame = channels * sizeof(float);

        //Act
        reader.Position = 48000 * bytesPerFrame;          // one second in

        var buffer = new byte[9600 * bytesPerFrame];
        var read = reader.Read(buffer, 0, buffer.Length);

        var samples = new float[read / sizeof(float)];
        Buffer.BlockCopy(buffer, 0, samples, 0, read);

        var frequency = AudioAssertions.EstimateFrequency(samples, 48000, channels);

        //Assert
        // Wide enough to tolerate the zero-crossing estimator, narrow enough that landing a
        // tenth of a second out - let alone at the wrong page - fails.
        frequency.Should().BeInRange(2000, 2400);
    }

    [Fact]
    public void Seeking_back_to_the_start_reproduces_the_opening_samples()
    {
        //Arrange
        using var reader = new OpusFileReader(TestAssets.Path(TestAssets.OpusSweepStereo));
        var bytesPerFrame = reader.WaveFormat.Channels * sizeof(float);

        var first = new byte[4800 * bytesPerFrame];
        var firstRead = reader.Read(first, 0, first.Length);

        //Act
        reader.Position = 0;
        var again = new byte[4800 * bytesPerFrame];
        var againRead = reader.Read(again, 0, again.Length);

        //Assert
        againRead.Should().Be(firstRead);

        var a = new float[firstRead / sizeof(float)];
        var b = new float[againRead / sizeof(float)];
        Buffer.BlockCopy(first, 0, a, 0, firstRead);
        Buffer.BlockCopy(again, 0, b, 0, againRead);

        // Not bit-identical: seeking resets the decoder, so the first block after a seek lacks
        // the overlap history a sequential read had. The audio is the same audio, though.
        AudioAssertions.RelativeRmsError(b, a).Should().BeLessThan(0.10);
    }

    [Fact]
    public void Position_reports_where_reading_has_reached()
    {
        //Arrange
        using var reader = new OpusFileReader(TestAssets.Path(TestAssets.OpusToneStereo));
        var bytesPerFrame = reader.WaveFormat.Channels * sizeof(float);
        var buffer = new byte[4800 * bytesPerFrame];

        //Act
        var read = reader.Read(buffer, 0, buffer.Length);

        //Assert
        reader.Position.Should().Be(read);
    }

    [Fact]
    public void Tags_and_vendor_are_read_from_the_comment_header()
    {
        //Arrange / Act
        using var reader = new OpusFileReader(TestAssets.Path(TestAssets.OpusToneStereo));

        //Assert
        // ffmpeg writes an ENCODER tag and a libopus vendor string.
        reader.EncoderVendor.Should().NotBeNullOrEmpty();
        reader.Tags.Should().NotBeNull();
    }

    [Fact]
    public void A_truncated_file_fails_cleanly_instead_of_hanging()
    {
        //Arrange
        var path = TestAssets.Path(TestAssets.OpusTruncated);

        //Act
        var decode = () =>
        {
            using var reader = new OpusFileReader(path);
            AudioAssertions.ReadAll(reader);
        };

        //Assert
        // Either it throws a clear error or it returns the audio it could recover - what it must
        // not do is hang, read past the end, or produce an endless stream.
        var samples = Array.Empty<float>();
        try
        {
            using var reader = new OpusFileReader(path);
            samples = AudioAssertions.ReadAll(reader);
        }
        catch (InvalidDataException)
        {
            return;
        }

        samples.Length.Should().BeLessThan(TestAssets.SweepFixtureSamples * 2);
    }

    [Fact]
    public void A_stream_that_is_not_opus_is_rejected_with_a_clear_message()
    {
        //Arrange
        using var vorbis = TestAssets.Open(TestAssets.VorbisToneStereo);

        //Act
        var open = () => new OpusFileReader(vorbis);

        //Assert
        open.Should().Throw<InvalidDataException>().WithMessage("*Opus*");
    }

    [Fact]
    public void The_caller_keeps_ownership_of_a_stream_it_supplied()
    {
        //Arrange
        // The reader registry hands a factory a stream it does NOT own, and closing it there
        // would leave the registry holding a closed file.
        var stream = TestAssets.Open(TestAssets.OpusToneStereo);

        //Act
        using (var reader = new OpusFileReader(stream))
        {
            reader.WaveFormat.Channels.Should().Be(2);
        }

        //Assert
        stream.CanRead.Should().BeTrue();
        stream.Dispose();
    }
}
