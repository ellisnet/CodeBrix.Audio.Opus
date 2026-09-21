using System;
using System.IO;
using CodeBrix.Audio.Wave;

namespace CodeBrix.Audio.Opus.Internal;

/// <summary>
/// The <see cref="IAudioFileWriter" /> seam over <see cref="OpusFileWriter" />, which is this
/// package's own writer and predates the seam.
/// </summary>
/// <remarks>
/// <para>
/// Only the FACTORY is public, as it is for CodeBrix.Audio's own WAV and AIFF adapters: a consumer
/// that wants an Opus writer directly has <see cref="OpusFileWriter" />, and a second public writer
/// type standing beside it would only invite the question of which one to use.
/// </para>
/// <para>
/// No dispose-ignoring wrapper is needed around the stream. <see cref="OpusFileWriter" />'s
/// <see cref="Stream" /> constructor does not own what it was handed, so finishing the file leaves
/// the caller's stream open - which is exactly what the seam requires.
/// </para>
/// </remarks>
internal sealed class OpusStreamAudioFileWriter : IAudioFileWriter
{
    private readonly WaveFormat format;

    private OpusFileWriter writer;
    private long samplesWritten;
    private bool finished;

    /// <summary>Creates the adapter and its encoder, writing the Ogg headers at once.</summary>
    /// <param name="stream">The stream to write to. It is not owned by this writer.</param>
    /// <param name="format">
    /// The format to write at. Only its sample rate and channel count are used; its encoding and
    /// bit depth are ignored, because an Opus file stores a compressed payload.
    /// </param>
    /// <param name="options">The encoder settings, from the factory.</param>
    internal OpusStreamAudioFileWriter(Stream stream, WaveFormat format, OpusFileWriterOptions options)
    {
        this.format = format;

        writer = new OpusFileWriter(stream, format.SampleRate, format.Channels, options);
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE FORMAT THE CALLER PASSED, unchanged - which is what the seam documents this property as:
    /// the format the writer was created with. Its sample rate and channel count are what the
    /// encoder is being fed and are therefore exact. Its ENCODING AND BIT DEPTH ARE NOT WHAT THE
    /// FILE STORES, because an Opus file stores a compressed payload; a caller that passed
    /// <c>new WaveFormat(44100, 16, 2)</c> reads 16-bit PCM back here and gets the same .opus file
    /// as a caller that passed 32-bit float.
    /// </remarks>
    public WaveFormat WaveFormat => format;

    /// <inheritdoc />
    public long SamplesWritten => samplesWritten;

    /// <inheritdoc />
    public void Write(float[] samples, int offset, int count)
    {
        CheckBuffer(samples, offset, count);
        CheckOpen();

        writer.Write(samples, offset, count);
        samplesWritten += count;
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> samples)
    {
        CheckOpen();

        writer.Write(samples);
        samplesWritten += samples.Length;
    }

    /// <inheritdoc />
    public void Finish()
    {
        if (finished)
        {
            return;
        }

        finished = true;

        // Disposing the OpusFileWriter is what pads and flushes the final frame and writes the
        // closing page carrying the true sample count. The stream underneath survives it.
        writer.Dispose();
        writer = null;
    }

    /// <summary>Finishes the file if it is not finished already.</summary>
    public void Dispose() => Finish();

    private static void CheckBuffer(float[] samples, int offset, int count)
    {
        if (samples == null)
        {
            throw new ArgumentNullException(nameof(samples));
        }

        if (offset < 0 || offset > samples.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset, "The offset must fall inside the buffer.");
        }

        if (count < 0 || offset + count > samples.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, "The count must not run past the end of the buffer.");
        }
    }

    private void CheckOpen()
    {
        if (finished)
        {
            throw new InvalidOperationException(
                "This Opus file has been finished; nothing more can be written to it.");
        }
    }
}
