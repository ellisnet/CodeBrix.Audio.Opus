using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CodeBrix.Audio.Opus.Codecs;
using CodeBrix.Audio.Opus.Ogg;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using SilverAssertions.Collections;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using SilverAssertions.Specialized;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for <see cref="OpusPacketEncoder" />, the packet-level encoder a container muxer feeds on.
/// </summary>
/// <remarks>
/// <para>
/// The decisive claim is that the packets are the SAME packets an .opus file of the same input
/// holds - same bytes, same header, same tail - because both faces sit on one encoding core. So
/// most tests here write the file with <see cref="OpusFileWriter" />, lift its packets back out
/// with the package's own Ogg reader, and compare.
/// </para>
/// <para>
/// The ffmpeg test closes the loop from outside: the packets, re-wrapped in Ogg pages by this
/// test, have to decode in an independent implementation to the tone that went in.
/// </para>
/// </remarks>
public class OpusPacketEncoderTests
{
    private const int Frame = 960;

    /// <summary>Generates a tone: a different frequency per channel, so a swap cannot hide.</summary>
    private static float[] Tone(int sampleRate, int channels, double seconds, double frequency = 440)
    {
        var frames = (int)(sampleRate * seconds);
        return Tone(sampleRate, channels, frames, frequency);
    }

    private static float[] Tone(int sampleRate, int channels, int frames, double frequency = 440)
    {
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

    /// <summary>Encodes a whole source, draining after every chunk, and returns header and packets.</summary>
    private static (OpusPacketEncoder Encoder, List<OpusPacket> Packets) EncodeAll(
        float[] source, int channels, int rate, OpusEncoderOptions options = null, int chunk = int.MaxValue)
    {
        var encoder = new OpusPacketEncoder(channels, rate, options);
        var packets = new List<OpusPacket>();

        for (var offset = 0; offset < source.Length; offset += chunk)
        {
            encoder.Encode(source.AsSpan(offset, Math.Min(chunk, source.Length - offset)));
            while (encoder.TryReadPacket(out var packet)) packets.Add(packet);
        }

        encoder.Finish();
        while (encoder.TryReadPacket(out var packet)) packets.Add(packet);

        return (encoder, packets);
    }

    /// <summary>What an .opus file holds once its pages are taken apart.</summary>
    private sealed class LiftedFile
    {
        public byte[] Head { get; set; }
        public List<byte[]> Packets { get; } = new List<byte[]>();
        public long FinalGranule { get; set; }
        public int PreSkip { get; set; }
    }

    private static LiftedFile WriteAndLift(float[] source, int channels, int rate,
        OpusFileWriterOptions options = null, int chunk = int.MaxValue)
    {
        using var stream = new MemoryStream();

        using (var writer = new OpusFileWriter(stream, rate, channels, options))
        {
            for (var offset = 0; offset < source.Length; offset += chunk)
            {
                writer.Write(source.AsSpan(offset, Math.Min(chunk, source.Length - offset)));
            }
        }

        var lifted = new LiftedFile();

        stream.Position = 0;
        using (var pages = new OggPageReader(stream, leaveOpen: true))
        {
            pages.TryReadPacket(out var head).Should().BeTrue();
            lifted.Head = head.Data;
            pages.TryReadPacket(out _).Should().BeTrue();

            while (pages.TryReadPacket(out var packet))
            {
                if (packet.Data.Length == 0) continue;

                lifted.Packets.Add(packet.Data);
                if (packet.GranulePosition >= 0) lifted.FinalGranule = packet.GranulePosition;
            }
        }

        stream.Position = 0;
        using (var reader = new OggOpusReader(stream, leaveOpen: true))
        {
            lifted.PreSkip = reader.Head.PreSkip;
            reader.TotalGranule.Should().Be(lifted.FinalGranule);
        }

        return lifted;
    }

    /// <summary>Decodes packets through the package's public packet decoder, exactly as fed.</summary>
    private static float[] DecodePackets(ReadOnlyMemory<byte> header, IEnumerable<OpusPacket> packets)
    {
        using var decoder = new OpusPacketCodecFactory().CreateDecoder("opus", header, null);
        var output = new float[decoder.MaxSamplesPerPacket];
        var all = new List<float>();

        foreach (var packet in packets)
        {
            var written = decoder.DecodePacket(packet.Data.Span, output);
            for (var i = 0; i < written; i++) all.Add(output[i]);
        }

        return all.ToArray();
    }

    [Theory]
    [InlineData(1, 48000)]
    [InlineData(2, 48000)]
    [InlineData(2, 44100)]
    public void Packets_are_960_samples_each_and_contiguous_from_zero(int channels, int rate)
    {
        //Arrange
        var source = Tone(rate, channels, 0.5);

        //Act
        var (encoder, packets) = EncodeAll(source, channels, rate, chunk: 1000 * channels);

        //Assert
        packets.Count.Should().BeGreaterThan(0);

        for (var i = 0; i < packets.Count; i++)
        {
            packets[i].SampleCount.Should().Be(Frame);
            packets[i].FirstSample.Should().Be((long)i * Frame);
            packets[i].Timestamp.Should().Be(TimeSpan.FromMilliseconds(20 * i));
            packets[i].Duration.Should().Be(TimeSpan.FromMilliseconds(20));
            packets[i].Data.Length.Should().BeGreaterThan(0);
        }

        encoder.SamplesEncoded.Should().Be((long)packets.Count * Frame);
        encoder.SampleRate.Should().Be(48000);
        encoder.Dispose();
    }

    [Theory]
    [InlineData(1, 48000)]
    [InlineData(2, 48000)]
    [InlineData(2, 44100)]
    [InlineData(1, 16000)]
    public void The_identification_header_is_the_opus_head_the_file_writer_writes(int channels, int rate)
    {
        //Arrange
        var source = Tone(rate, channels, 0.1);

        //Act
        using var encoder = new OpusPacketEncoder(channels, rate);
        var file = WriteAndLift(source, channels, rate);

        //Assert
        encoder.IdentificationHeader.Length.Should().Be(19);
        encoder.IdentificationHeader.ToArray().Should().Equal(file.Head);
        encoder.PreSkip.Should().Be(file.PreSkip);

        OpusHead.TryParse(encoder.IdentificationHeader.Span, out var head).Should().BeTrue();
        head.ChannelCount.Should().Be(channels);
        head.InputSampleRate.Should().Be(rate);
        head.OutputGainQ78.Should().Be((short)0);
        head.ChannelMappingFamily.Should().Be(0);
        head.PreSkip.Should().Be(encoder.PreSkip);
    }

    [Theory]
    [InlineData(1, 48000, int.MaxValue)]
    [InlineData(2, 48000, int.MaxValue)]
    [InlineData(2, 44100, int.MaxValue)]
    [InlineData(2, 48000, 777 * 2)]
    [InlineData(2, 44100, 777 * 2)]
    public void The_packets_are_byte_for_byte_the_packets_of_the_file_the_file_writer_writes(
        int channels, int rate, int chunk)
    {
        //Arrange
        var source = Tone(rate, channels, 1.5);

        //Act
        var (encoder, packets) = EncodeAll(source, channels, rate, chunk: chunk);
        var file = WriteAndLift(source, channels, rate, chunk: chunk);

        //Assert
        packets.Count.Should().Be(file.Packets.Count);

        for (var i = 0; i < packets.Count; i++)
        {
            packets[i].Data.ToArray().Should().Equal(file.Packets[i], $"packet {i} should match the file's");
        }

        encoder.Dispose();
    }

    [Fact]
    public void The_same_tuning_gives_the_same_packets_as_the_file_writer()
    {
        //Arrange
        // The two option classes are separate types; one mapping behind them is what keeps a
        // given tuning meaning the same thing on both faces.
        var source = Tone(16000, 1, 0.5, frequency: 300);
        var packetOptions = new OpusEncoderOptions
        {
            Profile = OpusEncodingProfile.Voice,
            Bitrate = 24_000,
            UseVariableBitrate = false,
            Complexity = 5
        };
        var fileOptions = new OpusFileWriterOptions
        {
            Profile = OpusEncodingProfile.Voice,
            Bitrate = 24_000,
            UseVariableBitrate = false,
            Complexity = 5
        };

        //Act
        var (encoder, packets) = EncodeAll(source, 1, 16000, packetOptions);
        var file = WriteAndLift(source, 1, 16000, fileOptions);

        //Assert
        packets.Count.Should().Be(file.Packets.Count);
        for (var i = 0; i < packets.Count; i++)
        {
            packets[i].Data.ToArray().Should().Equal(file.Packets[i]);
        }

        encoder.Dispose();
    }

    [Theory]
    [InlineData(1, 48000, 48000)]       // a whole number of packets
    [InlineData(2, 48000, 24000)]
    [InlineData(1, 48000, 12345)]       // not a multiple of 960
    [InlineData(2, 48000, 961)]
    [InlineData(2, 44100, 22050)]       // resampled
    [InlineData(1, 44100, 10001)]
    public void The_trailing_trim_is_what_the_files_final_granule_implies(int channels, int rate, int frames)
    {
        //Arrange
        var source = Tone(rate, channels, frames);

        //Act
        var (encoder, packets) = EncodeAll(source, channels, rate);
        var file = WriteAndLift(source, channels, rate);

        //Assert
        var emitted = (long)packets.Count * Frame;
        encoder.TrailingTrimSamples.Should().Be((int)(emitted - file.FinalGranule));
        encoder.TrailingTrimSamples.Should().BeInRange(0, Frame - 1);
        encoder.FramesWritten.Should().Be(frames);

        if (rate == 48000)
        {
            // At 48 kHz there is no resampler, so the arithmetic is exact.
            (emitted - encoder.TrailingTrimSamples).Should().Be(encoder.PreSkip + frames);
        }

        encoder.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Decoded_packets_less_pre_skip_and_trailing_trim_are_the_source_tone(int channels)
    {
        //Arrange
        var source = Tone(48000, channels, 0.5);

        //Act
        var (encoder, packets) = EncodeAll(source, channels, 48000, chunk: 4410 * channels);
        var decoded = DecodePackets(encoder.IdentificationHeader, packets);
        var audible = decoded.AsSpan(
            encoder.PreSkip * channels,
            decoded.Length - ((encoder.PreSkip + encoder.TrailingTrimSamples) * channels)).ToArray();

        //Assert
        audible.Length.Should().Be(source.Length);
        AudioAssertions.RelativeRmsError(audible, source).Should().BeLessThan(0.25);
        AudioAssertions.Rms(audible).Should().BeApproximately(AudioAssertions.Rms(source), 0.05);
        encoder.Dispose();
    }

    [Fact]
    public void Resampled_input_decodes_to_the_same_tone_at_48_khz()
    {
        //Arrange
        // 44.1 kHz in; the packets are 48 kHz. The resampler has a little latency of its own, so
        // the comparison allows a small alignment shift against the same tone made at 48 kHz.
        var source = Tone(44100, 2, 1.0);
        var reference = Tone(48000, 2, 1.0);

        //Act
        var (encoder, packets) = EncodeAll(source, 2, 44100);
        var decoded = DecodePackets(encoder.IdentificationHeader, packets);
        var audible = decoded.AsSpan(
            encoder.PreSkip * 2,
            decoded.Length - ((encoder.PreSkip + encoder.TrailingTrimSamples) * 2)).ToArray();
        var (error, _) = AudioAssertions.BestAlignment(audible, reference, 200);

        //Assert
        ((double)audible.Length / 2).Should().BeApproximately(48000, 960);
        error.Should().BeLessThan(0.25);
        AudioAssertions.Rms(audible).Should().BeApproximately(AudioAssertions.Rms(source), 0.05);
        encoder.Dispose();
    }

    [Fact]
    public void Finish_is_idempotent_and_ends_the_stream()
    {
        //Arrange
        using var encoder = new OpusPacketEncoder(2, 48000);
        encoder.Encode(Tone(48000, 2, 0.1));

        //Act
        encoder.Finish();
        var packetsAfterFirst = encoder.SamplesEncoded;
        var trim = encoder.TrailingTrimSamples;
        encoder.Finish();

        //Assert
        encoder.IsFinished.Should().BeTrue();
        encoder.SamplesEncoded.Should().Be(packetsAfterFirst);
        encoder.TrailingTrimSamples.Should().Be(trim);
    }

    [Fact]
    public void Encode_after_finish_is_refused()
    {
        //Arrange
        using var encoder = new OpusPacketEncoder(1, 48000);
        encoder.Finish();

        //Act
        var encodeSpan = () => encoder.Encode(new float[960]);
        var encodeArray = () => encoder.Encode(new float[960], 0, 960);

        //Assert
        encodeSpan.Should().Throw<InvalidOperationException>();
        encodeArray.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Trailing_trim_before_finish_is_refused()
    {
        //Arrange
        using var encoder = new OpusPacketEncoder(1, 48000);
        encoder.Encode(Tone(48000, 1, 0.1));

        //Act
        var read = () => encoder.TrailingTrimSamples;

        //Assert
        read.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Dispose_discards_pending_packets_and_keeps_the_header()
    {
        //Arrange
        var encoder = new OpusPacketEncoder(2, 48000);
        encoder.Encode(Tone(48000, 2, 0.2));
        encoder.PendingPackets.Should().BeGreaterThan(0);
        var header = encoder.IdentificationHeader.ToArray();

        //Act
        encoder.Dispose();
        encoder.Dispose();

        //Assert
        encoder.IsDisposed.Should().BeTrue();
        encoder.IsFinished.Should().BeFalse();
        encoder.IdentificationHeader.ToArray().Should().Equal(header);

        var pending = () => encoder.PendingPackets;
        var take = () => encoder.TryReadPacket(out _);
        var encode = () => encoder.Encode(new float[2]);
        var finish = () => encoder.Finish();

        pending.Should().Throw<ObjectDisposedException>();
        take.Should().Throw<ObjectDisposedException>();
        encode.Should().Throw<ObjectDisposedException>();
        finish.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Pending_packets_and_try_read_packet_keep_their_books()
    {
        //Arrange
        using var encoder = new OpusPacketEncoder(1, 48000);

        //Act / Assert
        encoder.TryReadPacket(out var none).Should().BeFalse();
        none.Data.Length.Should().Be(0);

        // 959 samples: not yet a whole packet.
        encoder.Encode(new float[959]);
        encoder.PendingPackets.Should().Be(0);

        // One more completes the first packet; 2400 more completes two and leaves 480 waiting.
        encoder.Encode(new float[1]);
        encoder.PendingPackets.Should().Be(1);
        encoder.Encode(new float[2400]);
        encoder.PendingPackets.Should().Be(3);
        encoder.SamplesEncoded.Should().Be(3 * Frame);

        encoder.TryReadPacket(out var first).Should().BeTrue();
        first.FirstSample.Should().Be(0);
        encoder.PendingPackets.Should().Be(2);

        encoder.TryReadPacket(out var second).Should().BeTrue();
        second.FirstSample.Should().Be(Frame);
        encoder.TryReadPacket(out _).Should().BeTrue();
        encoder.TryReadPacket(out _).Should().BeFalse();

        encoder.FramesWritten.Should().Be(3360);
        encoder.Duration.Should().Be(TimeSpan.FromMilliseconds(70));

        // Queued packets survive Finish.
        encoder.Finish();
        encoder.PendingPackets.Should().BeGreaterThan(0);
        encoder.SamplesEncoded.Should().Be(encoder.PendingPackets * Frame + (3 * Frame));
    }

    [Fact]
    public void Finishing_with_no_input_still_covers_the_pre_skip_like_the_file_writer()
    {
        //Arrange
        using var encoder = new OpusPacketEncoder(2, 48000);
        var file = WriteAndLift(Array.Empty<float>(), 2, 48000);

        //Act
        encoder.Finish();
        var packets = new List<OpusPacket>();
        while (encoder.TryReadPacket(out var packet)) packets.Add(packet);

        //Assert
        packets.Count.Should().Be(file.Packets.Count);
        ((long)packets.Count * Frame).Should().BeGreaterThanOrEqualTo(encoder.PreSkip);
        encoder.TrailingTrimSamples.Should().Be((int)(((long)packets.Count * Frame) - encoder.PreSkip));
        encoder.FramesWritten.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void Channel_counts_other_than_one_or_two_are_refused(int channels)
    {
        //Arrange / Act
        var create = () => new OpusPacketEncoder(channels, 48000);

        //Assert
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_rate_that_is_not_positive_is_refused()
    {
        //Arrange / Act
        var create = () => new OpusPacketEncoder(2, 0);

        //Assert
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_partial_frame_of_interleaved_samples_is_refused()
    {
        //Arrange
        using var encoder = new OpusPacketEncoder(2, 48000);

        //Act
        var encode = () => encoder.Encode(new float[3]);
        var nullArray = () => encoder.Encode(null, 0, 0);

        //Assert
        // The same message the file writers give, since all of them refuse through the one core.
        encode.Should().Throw<ArgumentException>()
            .WithMessage("The sample count (3) must be a whole number of 2-channel frames.*");
        nullArray.Should().Throw<ArgumentNullException>();
        encoder.FramesWritten.Should().Be(0);
    }

    [Theory]
    [InlineData(499, 10, 5)]
    [InlineData(512_001, 10, 5)]
    [InlineData(96_000, -1, 5)]
    [InlineData(96_000, 11, 5)]
    [InlineData(96_000, 10, -1)]
    [InlineData(96_000, 10, 11)]
    public void Options_out_of_range_are_refused(int bitrate, int complexity, int resamplerQuality)
    {
        //Arrange
        var options = new OpusEncoderOptions
        {
            Bitrate = bitrate,
            Complexity = complexity,
            ResamplerQuality = resamplerQuality
        };

        //Act
        var validate = () => options.Validate();
        var create = () => new OpusPacketEncoder(2, 48000, options);

        //Assert
        validate.Should().Throw<ArgumentOutOfRangeException>();
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Default_options_are_the_documented_defaults()
    {
        //Arrange / Act
        var options = new OpusEncoderOptions();

        //Assert
        options.Bitrate.Should().Be(96_000);
        options.Profile.Should().Be(OpusEncodingProfile.Music);
        options.UseVariableBitrate.Should().BeTrue();
        options.Complexity.Should().Be(10);
        options.ResamplerQuality.Should().Be(5);
    }

    [Fact]
    public void Ffmpeg_decodes_an_ogg_file_rebuilt_from_the_packets_to_the_source_tone()
    {
        //Arrange
        // No Matroska muxer here, so the packets go back into Ogg pages laid by THIS test: the
        // header as the first page, a comment header, each packet with the granule at its end,
        // and the final granule set from TrailingTrimSamples. If ffmpeg then decodes the source's
        // exact length of tone, the packets, the header and the trim are all what a third party
        // expects.
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            Assert.Skip("ffmpeg is not on PATH, so the cross-implementation check cannot run.");
        }

        var source = Tone(48000, 2, 12345);
        var (encoder, packets) = EncodeAll(source, 2, 48000, chunk: 1000);
        var opusPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".opus");
        var wavPath = Path.ChangeExtension(opusPath, ".wav");

        using (var file = File.Create(opusPath))
        using (var pages = new OggPageWriter(file, 0x0BADC0DE))
        {
            pages.WritePacket(encoder.IdentificationHeader.ToArray(), 0);
            pages.FlushPage(endOfStream: false);
            pages.WritePacket(new OpusTags { Vendor = "test" }.ToBytes(), 0);
            pages.FlushPage(endOfStream: false);

            foreach (var packet in packets)
            {
                pages.WritePacket(packet.Data.ToArray(), packet.FirstSample + packet.SampleCount);
            }

            var last = packets[^1];
            pages.SetPendingGranule(last.FirstSample + last.SampleCount - encoder.TrailingTrimSamples);
            pages.FlushPage(endOfStream: true);
        }

        //Act
        var exitCode = RunFfmpeg(ffmpeg, $"-hide_banner -loglevel error -y -i \"{opusPath}\" -c:a pcm_s16le \"{wavPath}\"");

        //Assert
        try
        {
            exitCode.Should().Be(0, "ffmpeg should decode packets this library made");

            using var wav = new WaveFileReader(wavPath);
            wav.WaveFormat.SampleRate.Should().Be(48000);
            wav.WaveFormat.Channels.Should().Be(2);

            var frames = wav.Length / wav.WaveFormat.BlockAlign;
            frames.Should().Be(12345);

            var decoded = AudioAssertions.ReadAllSamples(wav.ToSampleProvider());
            AudioAssertions.Rms(decoded).Should().BeApproximately(AudioAssertions.Rms(source), 0.05);
            AudioAssertions.RelativeRmsError(decoded, source).Should().BeLessThan(0.25);
        }
        finally
        {
            encoder.Dispose();
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
