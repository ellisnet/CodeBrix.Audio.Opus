using System;
using System.Diagnostics;
using System.IO;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using SilverAssertions.Specialized;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for encoding .opus files.
/// </summary>
/// <remarks>
/// A round trip through this library alone would pass even if the encoder and decoder shared a
/// bug, so the decisive test here hands what this library WROTE to ffmpeg and checks that ffmpeg
/// agrees about it. A file only this package can read is not an .opus file.
/// </remarks>
public class OpusFileWriterTests
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

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".opus");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_written_file_reads_back_as_the_audio_that_went_in(int channels)
    {
        //Arrange
        var source = Tone(SampleRate, channels, 0.5);
        var path = TempFile();

        //Act
        using (var writer = new OpusFileWriter(path, SampleRate, channels))
        {
            writer.Write(source);
        }

        using var reader = new OpusFileReader(path);
        var decoded = AudioAssertions.ReadAll(reader);

        //Assert
        try
        {
            reader.WaveFormat.Channels.Should().Be(channels);
            reader.WaveFormat.SampleRate.Should().Be(SampleRate);

            // Exactly the sample count that went in: the encoder pads the last frame, and the
            // closing page's granule position is what trims that padding back off.
            (decoded.Length / channels).Should().Be(source.Length / channels);

            // Opus is lossy, so this is a tolerance - but a tone survives encoding well.
            AudioAssertions.RelativeRmsError(decoded, source).Should().BeLessThan(0.25);
            AudioAssertions.Rms(decoded).Should().BeApproximately(AudioAssertions.Rms(source), 0.05);
        }
        finally
        {
            reader.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void The_written_header_records_the_declared_input_rate_not_the_encode_rate()
    {
        //Arrange
        // 44.1 kHz in, 48 kHz out. The header keeps a record of what the encoder was given, which
        // is what RFC 7845 asks for - it is not a decoding parameter.
        var source = Tone(44100, 2, 0.5);
        var path = TempFile();

        //Act
        using (var writer = new OpusFileWriter(path, 44100, 2))
        {
            writer.Write(source);
        }

        using var reader = new OpusFileReader(path);

        //Assert
        try
        {
            reader.EncoderInputSampleRate.Should().Be(44100);
            reader.WaveFormat.SampleRate.Should().Be(48000);
            reader.PreSkip.Should().BeGreaterThan(0);
        }
        finally
        {
            reader.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Input_at_another_rate_is_resampled_and_keeps_its_duration()
    {
        //Arrange
        // The writer converts rather than rejecting - the SoundEffectClip precedent, not the
        // WaveOutEvent one.
        var source = Tone(44100, 1, 1.0);
        var path = TempFile();

        //Act
        using (var writer = new OpusFileWriter(path, 44100, 1))
        {
            writer.Write(source);
        }

        using var reader = new OpusFileReader(path);

        //Assert
        try
        {
            // One second in, one second out - now expressed in 48 kHz samples.
            reader.TotalTime.TotalSeconds.Should().BeApproximately(1.0, 0.02);
        }
        finally
        {
            reader.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Tags_survive_a_round_trip()
    {
        //Arrange
        var options = new OpusFileWriterOptions { Bitrate = 64_000 };
        options.Tags["TITLE"] = "A test tone";
        options.Tags["ARTIST"] = "CodeBrix";

        var path = TempFile();

        //Act
        using (var writer = new OpusFileWriter(path, SampleRate, 1, options))
        {
            writer.Write(Tone(SampleRate, 1, 0.25));
        }

        using var reader = new OpusFileReader(path);

        //Assert
        try
        {
            reader.Tags.Should().ContainKey("TITLE");
            reader.Tags["TITLE"][0].Should().Be("A test tone");
            reader.Tags["ARTIST"][0].Should().Be("CodeBrix");
            reader.EncoderVendor.Should().Be("CodeBrix.Audio.Opus");
        }
        finally
        {
            reader.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void The_voice_profile_produces_a_usable_file_at_a_speech_bitrate()
    {
        //Arrange
        var options = new OpusFileWriterOptions
        {
            Profile = OpusEncodingProfile.Voice,
            Bitrate = 24_000
        };

        var path = TempFile();

        //Act
        using (var writer = new OpusFileWriter(path, 16000, 1, options))
        {
            writer.Write(Tone(16000, 1, 0.5, frequency: 300));
        }

        using var reader = new OpusFileReader(path);
        var decoded = AudioAssertions.ReadAll(reader);

        //Assert
        try
        {
            (decoded.Length).Should().Be(24000);        // 0.5 s at 48 kHz, mono
            AudioAssertions.Rms(decoded).Should().BeGreaterThan(0.05);
        }
        finally
        {
            reader.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Writing_in_several_calls_matches_writing_in_one()
    {
        //Arrange
        var source = Tone(SampleRate, 2, 0.5);
        var wholePath = TempFile();
        var piecesPath = TempFile();

        //Act
        using (var writer = new OpusFileWriter(wholePath, SampleRate, 2))
        {
            writer.Write(source);
        }

        using (var writer = new OpusFileWriter(piecesPath, SampleRate, 2))
        {
            // Deliberately not aligned to the Opus frame: 778 samples is 389 stereo frames, a whole
            // number of sample frames but not a whole 20 ms (960-frame) Opus frame.
            for (var offset = 0; offset < source.Length; offset += 778)
            {
                writer.Write(source, offset, Math.Min(778, source.Length - offset));
            }
        }

        //Assert
        try
        {
            using var whole = new OpusFileReader(wholePath);
            using var pieces = new OpusFileReader(piecesPath);

            var a = AudioAssertions.ReadAll(whole);
            var b = AudioAssertions.ReadAll(pieces);

            b.Length.Should().Be(a.Length);
            AudioAssertions.RelativeRmsError(b, a).Should().BeLessThan(0.001);
        }
        finally
        {
            File.Delete(wholePath);
            File.Delete(piecesPath);
        }
    }

    [Fact]
    public void Options_out_of_range_are_rejected()
    {
        //Arrange
        var badBitrate = new OpusFileWriterOptions { Bitrate = 10 };
        var badComplexity = new OpusFileWriterOptions { Complexity = 42 };

        //Act
        var withBadBitrate = () => new OpusFileWriter(new MemoryStream(), 48000, 2, badBitrate);
        var withBadComplexity = () => new OpusFileWriter(new MemoryStream(), 48000, 2, badComplexity);

        //Assert
        withBadBitrate.Should().Throw<ArgumentOutOfRangeException>();
        withBadComplexity.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Multichannel_is_declined_rather_than_mis_mapped()
    {
        //Arrange / Act
        var write = () => new OpusFileWriter(new MemoryStream(), 48000, 6);

        //Assert
        write.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_caller_keeps_ownership_of_a_stream_it_supplied()
    {
        //Arrange
        var stream = new MemoryStream();

        //Act
        using (var writer = new OpusFileWriter(stream, SampleRate, 1))
        {
            writer.Write(Tone(SampleRate, 1, 0.1));
        }

        //Assert
        stream.CanRead.Should().BeTrue();
        stream.Length.Should().BeGreaterThan(0);
        stream.Dispose();
    }

    [Fact]
    public void Ffmpeg_can_decode_what_this_library_wrote()
    {
        //Arrange
        // The test that matters. Everything else here is this library agreeing with itself; this
        // asks a completely independent implementation whether the file is really an .opus file.
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            Assert.Skip("ffmpeg is not on PATH, so the cross-implementation check cannot run.");
        }

        var source = Tone(SampleRate, 2, 0.5);
        var opusPath = TempFile();
        var wavPath = Path.ChangeExtension(opusPath, ".wav");

        using (var writer = new OpusFileWriter(opusPath, SampleRate, 2))
        {
            writer.Write(source);
        }

        //Act
        var exitCode = RunFfmpeg(ffmpeg, $"-hide_banner -loglevel error -y -i \"{opusPath}\" -c:a pcm_s16le \"{wavPath}\"");

        //Assert
        try
        {
            exitCode.Should().Be(0, "ffmpeg should decode a file this library wrote");

            using var wav = new WaveFileReader(wavPath);

            wav.WaveFormat.SampleRate.Should().Be(48000);
            wav.WaveFormat.Channels.Should().Be(2);

            // ffmpeg's decode has to be the right length AND carry the tone - a file that
            // decoded to the correct duration of silence would pass length alone.
            var frames = wav.Length / wav.WaveFormat.BlockAlign;
            frames.Should().Be(source.Length / 2);

            var decoded = AudioAssertions.ReadAllSamples(wav.ToSampleProvider());
            AudioAssertions.Rms(decoded).Should().BeApproximately(AudioAssertions.Rms(source), 0.05);
        }
        finally
        {
            File.Delete(opusPath);
            if (File.Exists(wavPath)) File.Delete(wavPath);
        }
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void A_partial_stereo_frame_is_refused_by_both_write_overloads(int rate)
    {
        //Arrange
        // At 48 kHz a stray sample would swap left and right for the rest of the stream; on the
        // resampled path it would be dropped. Either way the input is wrong, so it is refused.
        using var stream = new MemoryStream();
        using var writer = new OpusFileWriter(stream, rate, 2);

        //Act
        var span = () => writer.Write(new float[777].AsSpan());
        var array = () => writer.Write(new float[10], 1, 3);

        //Assert
        span.Should().Throw<ArgumentException>()
            .WithMessage("The sample count (777) must be a whole number of 2-channel frames.*");
        array.Should().Throw<ArgumentException>()
            .WithMessage("The sample count (3) must be a whole number of 2-channel frames.*");
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void A_refused_partial_frame_leaves_the_writer_usable_and_the_file_exact(int rate)
    {
        //Arrange
        var source = Tone(rate, 2, 0.5);
        using var stream = new MemoryStream();

        //Act
        using (var writer = new OpusFileWriter(stream, rate, 2))
        {
            writer.Write(source.AsSpan(0, 1000));
            var partial = () => writer.Write(source.AsSpan(1000, 7));
            partial.Should().Throw<ArgumentException>();
            writer.Write(source.AsSpan(1000));
        }

        stream.Position = 0;
        using var reader = new OpusFileReader(stream);
        var decoded = AudioAssertions.ReadAll(reader);

        //Assert
        var frames = source.Length / 2;
        (decoded.Length / 2).Should().Be((int)Math.Round(frames * 48000.0 / rate));
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Whole_frames_in_odd_sized_chunks_give_the_right_total_sample_count(int rate)
    {
        //Arrange
        // 778 interleaved samples = 389 stereo frames per call: whole sample frames, never a whole
        // 20 ms Opus frame, and an odd number of frames at that.
        var source = Tone(rate, 2, 0.5);
        using var stream = new MemoryStream();

        //Act
        using (var writer = new OpusFileWriter(stream, rate, 2))
        {
            for (var offset = 0; offset < source.Length; offset += 778)
            {
                writer.Write(source.AsSpan(offset, Math.Min(778, source.Length - offset)));
            }
        }

        stream.Position = 0;
        using var reader = new OpusFileReader(stream);
        var decoded = AudioAssertions.ReadAll(reader);

        //Assert
        var frames = source.Length / 2;
        (decoded.Length / 2).Should().Be((int)Math.Round(frames * 48000.0 / rate));
    }

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(44100, 2)]
    [InlineData(22050, 1)]
    [InlineData(22050, 2)]
    public void Resampled_input_decodes_to_exactly_its_own_length_at_48_khz(int rate, int channels)
    {
        //Arrange
        // The resampler's delay is drained at the end and discarded through the pre-skip at the
        // start, so the decoded stream is as long as the input - not short by the delay.
        const double seconds = 1.5;
        var source = Tone(rate, channels, seconds);

        //Act
        var decoded = WriteAndDecode(source, rate, channels);

        //Assert
        (decoded.Length / channels).Should().BeCloseTo((int)Math.Round(seconds * 48000), 1);
    }

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(44100, 2)]
    [InlineData(22050, 1)]
    [InlineData(22050, 2)]
    public void Resampled_input_starts_where_the_same_tone_at_48_khz_starts(int rate, int channels)
    {
        //Arrange
        // 100 ms of silence, then a tone. With the resampler's delay in the pre-skip the decoded
        // onset lands where the 48 kHz encoding of the same signal puts it; without it the
        // resampled stream would start late by the delay (some 40 samples at 44.1 kHz).
        var source = SilenceThenTone(rate, channels);
        var reference = SilenceThenTone(48000, channels);

        //Act
        var decoded = WriteAndDecode(source, rate, channels);
        var expected = WriteAndDecode(reference, 48000, channels);

        //Assert
        var onset = Onset(decoded, channels);
        var expectedOnset = Onset(expected, channels);
        expectedOnset.Should().BeCloseTo(4800, 48);
        onset.Should().BeCloseTo(expectedOnset, 3);

        // And the whole signal lines up, not just its first edge.
        var (_, shift) = AudioAssertions.BestAlignment(decoded, expected, 64 * channels);
        (shift / channels).Should().BeCloseTo(0, 2);
    }

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(44100, 2)]
    [InlineData(22050, 1)]
    [InlineData(22050, 2)]
    public void Resampled_input_keeps_its_last_samples_rather_than_ending_in_padding(int rate, int channels)
    {
        //Arrange
        // A tone that runs right to the end. The resampler still holds the last of it when the
        // caller stops writing; unless that tail is drained, the stream ends with the delay's worth
        // of silent padding where the tone should be.
        var source = Tone(rate, channels, 0.5);
        var reference = Tone(48000, channels, 0.5);
        const int tailFrames = 32;

        //Act
        var decoded = WriteAndDecode(source, rate, channels);
        var expected = WriteAndDecode(reference, 48000, channels);

        //Assert
        var tail = decoded.AsSpan(decoded.Length - (tailFrames * channels));
        var expectedTail = expected.AsSpan(expected.Length - (tailFrames * channels));
        AudioAssertions.Rms(tail).Should().BeGreaterThan(0.2);
        AudioAssertions.Rms(tail).Should().BeApproximately(AudioAssertions.Rms(expectedTail), 0.1);
    }

    /// <summary>100 ms of silence followed by 400 ms of the tone, at the given rate.</summary>
    private static float[] SilenceThenTone(int rate, int channels)
    {
        var silentFrames = rate / 10;
        var tone = Tone(rate, channels, 0.4);
        var samples = new float[(silentFrames * channels) + tone.Length];
        tone.CopyTo(samples, silentFrames * channels);

        return samples;
    }

    /// <summary>The first frame in which any channel rises above a clear threshold.</summary>
    private static int Onset(float[] interleaved, int channels)
    {
        for (var i = 0; i < interleaved.Length; i++)
        {
            if (Math.Abs(interleaved[i]) > 0.1f) return i / channels;
        }

        return -1;
    }

    private static float[] WriteAndDecode(float[] source, int rate, int channels)
    {
        using var stream = new MemoryStream();

        using (var writer = new OpusFileWriter(stream, rate, channels))
        {
            writer.Write(source);
        }

        stream.Position = 0;
        using var reader = new OpusFileReader(stream);

        return AudioAssertions.ReadAll(reader);
    }

    private static string FindFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            foreach (var name in new[] { "ffmpeg", "ffmpeg.exe" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static int RunFfmpeg(string ffmpeg, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        });

        process.WaitForExit(30_000);

        return process.ExitCode;
    }
}
