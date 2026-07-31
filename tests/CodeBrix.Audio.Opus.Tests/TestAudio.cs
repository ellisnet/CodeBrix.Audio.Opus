using System;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// The audio every audible test in the CodeBrix family plays: the five-tone motif from
/// "Close Encounters of the Third Kind".
/// </summary>
/// <remarks>
/// One recognisable tune means a good run is obvious by ear and a broken one sounds broken. The
/// tones and timings match CodeBrix.Audio's own TestAudio so a run of both suites sounds like one
/// thing - that class is internal to its assembly, so the values are reproduced rather than
/// referenced. Keep them in step.
/// </remarks>
internal static class TestAudio
{
    // Pitched an octave up from the film's own tones, so the lowest note is F4 at 349 Hz -
    // comfortably within what small speakers reproduce, while the octave drop is still an
    // octave drop.

    /// <summary>The five tones, in order: G5, A5, F5, F4, C5.</summary>
    public static readonly double[] CloseEncountersTones = [783.99, 880.00, 698.46, 349.23, 523.25];

    /// <summary>How long each tone sounds.</summary>
    public const double CloseEncountersNoteSeconds = 0.30;

    /// <summary>The silence between tones, so the five stay distinct.</summary>
    public const double CloseEncountersGapSeconds = 0.06;

    /// <summary>Total length of the motif.</summary>
    public static TimeSpan CloseEncountersDuration =>
        TimeSpan.FromSeconds(CloseEncountersTones.Length *
                             (CloseEncountersNoteSeconds + CloseEncountersGapSeconds));

    /// <summary>Renders the motif as interleaved float samples in [-1, 1].</summary>
    /// <param name="sampleRate">Sample rate to render at.</param>
    /// <param name="channels">Channel count; every channel gets the same audio.</param>
    /// <returns>The rendered samples.</returns>
    public static float[] BuildCloseEncountersSamples(int sampleRate, int channels)
    {
        var noteFrames = (int)(sampleRate * CloseEncountersNoteSeconds);
        var gapFrames = (int)(sampleRate * CloseEncountersGapSeconds);
        var totalFrames = CloseEncountersTones.Length * (noteFrames + gapFrames);
        var samples = new float[totalFrames * channels];

        // A short fade at each end of a note: a tone that starts or stops at full amplitude
        // clicks, and five clicks would be the most audible thing in the test run.
        var fadeFrames = Math.Max(1, sampleRate / 100);

        var frame = 0;
        foreach (var frequency in CloseEncountersTones)
        {
            for (var n = 0; n < noteFrames; n++, frame++)
            {
                var envelope = Math.Min(1.0, Math.Min(n, noteFrames - 1 - n) / (double)fadeFrames);
                var value = (float)(0.4 * envelope * Math.Sin(2.0 * Math.PI * frequency * n / sampleRate));

                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(frame * channels) + channel] = value;
                }
            }

            frame += gapFrames; // left silent
        }

        return samples;
    }
}
