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
            // Deliberately not frame-aligned: 777 samples is not a whole 20 ms Opus frame.
            for (var offset = 0; offset < source.Length; offset += 777)
            {
                writer.Write(source, offset, Math.Min(777, source.Length - offset));
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
