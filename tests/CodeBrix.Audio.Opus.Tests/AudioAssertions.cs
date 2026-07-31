using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Wave;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Helpers for measuring decoded audio.
/// </summary>
/// <remarks>
/// Opus is lossy, so a decoder cannot be held to a byte-for-byte comparison the way the FLAC
/// decoder in CodeBrix.Audio can. These helpers support the comparison that IS meaningful:
/// against a second, independent implementation's decode of the same file, within a tolerance.
/// </remarks>
internal static class AudioAssertions
{
    /// <summary>Decodes an entire .opus fixture to interleaved floats.</summary>
    public static float[] DecodeAll(string fixtureName)
    {
        using var reader = new OpusFileReader(TestAssets.Path(fixtureName));

        return ReadAll(reader);
    }

    /// <summary>Reads a WaveStream of 32-bit float to the end.</summary>
    public static float[] ReadAll(WaveStream reader)
    {
        var samples = new List<float>();
        var buffer = new byte[16384];
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i + 3 < read; i += 4)
            {
                samples.Add(BitConverter.ToSingle(buffer, i));
            }
        }

        return samples.ToArray();
    }

    /// <summary>Reads an ISampleProvider to the end.</summary>
    public static float[] ReadAllSamples(ISampleProvider provider)
    {
        var samples = new List<float>();
        var buffer = new float[16384];
        int read;

        while ((read = provider.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++) samples.Add(buffer[i]);
        }

        return samples.ToArray();
    }

    /// <summary>Reads ffmpeg's 16-bit PCM decode of a fixture as floats in [-1, 1].</summary>
    public static float[] ReadFfmpegReference(string opusFixtureName)
    {
        var path = TestAssets.FfmpegReferenceFor(opusFixtureName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The ffmpeg reference decode for '{opusFixtureName}' is missing. Regenerate the " +
                "fixtures with tools/make_test_fixtures/make_fixtures.sh.", path);
        }

        using var wav = new WaveFileReader(path);
        var provider = wav.ToSampleProvider();

        var samples = new List<float>();
        var buffer = new float[16384];
        int read;

        while ((read = provider.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++) samples.Add(buffer[i]);
        }

        return samples.ToArray();
    }

    /// <summary>Root-mean-square level of a signal.</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;

        var sum = 0.0;
        foreach (var sample in samples) sum += (double)sample * sample;

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// RMS of the difference between two signals, relative to the RMS of the reference. 0 means
    /// identical; 1 means the error is as large as the signal.
    /// </summary>
    public static double RelativeRmsError(ReadOnlySpan<float> actual, ReadOnlySpan<float> reference)
    {
        var length = Math.Min(actual.Length, reference.Length);
        if (length == 0) return double.PositiveInfinity;

        var errorSum = 0.0;
        var referenceSum = 0.0;

        for (var i = 0; i < length; i++)
        {
            var difference = (double)actual[i] - reference[i];
            errorSum += difference * difference;
            referenceSum += (double)reference[i] * reference[i];
        }

        if (referenceSum == 0) return errorSum == 0 ? 0 : double.PositiveInfinity;

        return Math.Sqrt(errorSum / referenceSum);
    }

    /// <summary>
    /// Finds the sample offset at which two signals agree best, and the error there.
    /// </summary>
    /// <param name="actual">The signal under test.</param>
    /// <param name="reference">The signal to compare against.</param>
    /// <param name="maxShift">How far to search, in samples, in each direction.</param>
    /// <returns>The smallest relative RMS error found, and the shift that produced it.</returns>
    /// <remarks>
    /// A positive shift means <paramref name="actual" /> lags the reference; negative means it
    /// leads. Separating the offset from the error matters when comparing two implementations of
    /// a lossy codec: a couple of samples of skew and a genuine decode fault look identical in a
    /// plain RMS comparison, and only one of them is a defect.
    /// </remarks>
    public static (double Error, int Shift) BestAlignment(
        float[] actual, float[] reference, int maxShift)
    {
        var bestError = double.MaxValue;
        var bestShift = 0;

        for (var shift = -maxShift; shift <= maxShift; shift++)
        {
            var a = shift >= 0
                ? actual.AsSpan(Math.Min(shift, actual.Length))
                : actual.AsSpan();

            var b = shift >= 0
                ? reference.AsSpan()
                : reference.AsSpan(Math.Min(-shift, reference.Length));

            var error = RelativeRmsError(a, b);

            if (error < bestError)
            {
                bestError = error;
                bestShift = shift;
            }
        }

        return (bestError, bestShift);
    }

    /// <summary>
    /// Estimates the dominant frequency of a block of mono samples by counting zero crossings.
    /// </summary>
    /// <remarks>
    /// Crude, but exactly what the sweep fixture needs: after seeking into a linear sweep the
    /// instantaneous frequency says where in the file the decoder actually landed, so a seek can
    /// be checked against the audio rather than against the library's own bookkeeping.
    /// </remarks>
    public static double EstimateFrequency(ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        var crossings = 0;
        var previous = 0f;
        var count = 0;

        for (var i = 0; i < samples.Length; i += channels)
        {
            var current = samples[i];

            if (count > 0 && ((previous < 0 && current >= 0) || (previous >= 0 && current < 0)))
            {
                crossings++;
            }

            previous = current;
            count++;
        }

        if (count < 2) return 0;

        var seconds = (double)count / sampleRate;

        return crossings / 2.0 / seconds;
    }
}
