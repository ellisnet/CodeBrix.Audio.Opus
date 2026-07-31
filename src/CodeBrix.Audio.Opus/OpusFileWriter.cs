using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Opus.Codec.Enums;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus;

/// <summary>
/// Writes 32-bit float samples to a .opus file or stream - the Opus counterpart of
/// CodeBrix.Audio's WaveFileWriter.
/// </summary>
/// <remarks>
/// <para>
/// DISPOSE THIS WRITER. Like WaveFileWriter, it only produces a complete, correctly-described
/// file on <see cref="Dispose()" />: the final partial frame is padded and flushed there, and the
/// closing page records the true sample count so a decoder trims that padding instead of playing
/// it. An undisposed writer leaves a file that is missing its tail and misreports its length.
/// </para>
/// <para>
/// Any input sample rate is accepted. Opus encodes at 48 kHz, so anything else is resampled on
/// the way in; the rate you declare is still recorded in the file header as the rate the encoder
/// was given.
/// </para>
/// </remarks>
public sealed class OpusFileWriter : IDisposable
{
    private readonly OggOpusWriter writer;
    private readonly Stream ownedStream;

    private bool disposed;

    /// <summary>Creates a .opus file.</summary>
    /// <param name="fileName">Path to write to; an existing file is overwritten.</param>
    /// <param name="sampleRate">The rate of the samples that will be written.</param>
    /// <param name="channels">Channels in the input: 1 or 2.</param>
    /// <param name="options">Encoder options, or null for the defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="fileName" /> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A rate, channel count or option is invalid.</exception>
    public OpusFileWriter(string fileName, int sampleRate, int channels,
        OpusFileWriterOptions options = null)
        : this(CreateFile(fileName), sampleRate, channels, options, ownsStream: true)
    {
    }

    /// <summary>Writes a .opus stream.</summary>
    /// <param name="outputStream">
    /// The stream to write to. It is NOT owned by this writer; the caller disposes it.
    /// </param>
    /// <param name="sampleRate">The rate of the samples that will be written.</param>
    /// <param name="channels">Channels in the input: 1 or 2.</param>
    /// <param name="options">Encoder options, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outputStream" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A rate, channel count or option is invalid.</exception>
    public OpusFileWriter(Stream outputStream, int sampleRate, int channels,
        OpusFileWriterOptions options = null)
        : this(outputStream, sampleRate, channels, options, ownsStream: false)
    {
    }

    private OpusFileWriter(Stream outputStream, int sampleRate, int channels,
        OpusFileWriterOptions options, bool ownsStream)
    {
        if (outputStream == null) throw new ArgumentNullException(nameof(outputStream));

        options ??= new OpusFileWriterOptions();
        options.Validate();

        ownedStream = ownsStream ? outputStream : null;

        try
        {
            writer = new OggOpusWriter(outputStream, channels, sampleRate,
                BuildSettings(options), NewSerialNumber(), leaveOpen: true);
        }
        catch
        {
            ownedStream?.Dispose();
            throw;
        }
    }

    /// <summary>The pre-skip recorded in the file, in 48 kHz samples.</summary>
    public int PreSkip => writer.PreSkip;

    /// <summary>Encodes and writes interleaved samples in [-1, 1].</summary>
    /// <param name="samples">The samples to write.</param>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        writer.Write(samples);
    }

    /// <summary>Encodes and writes interleaved samples in [-1, 1].</summary>
    /// <param name="samples">The buffer holding the samples.</param>
    /// <param name="offset">Index of the first sample to write.</param>
    /// <param name="count">How many samples to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="samples" /> is null.</exception>
    public void Write(float[] samples, int offset, int count)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));

        Write(samples.AsSpan(offset, count));
    }

    /// <summary>
    /// Finishes the file: pads and flushes the last frame, then writes the closing page with the
    /// true sample count. Called by <see cref="Dispose()" />, and safe to call twice.
    /// </summary>
    public void Finish()
    {
        if (disposed) return;

        writer.Finish();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        try
        {
            writer.Dispose();
        }
        finally
        {
            ownedStream?.Dispose();
        }
    }

    private static Stream CreateFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("A file name is required.", nameof(fileName));
        }

        return File.Create(fileName);
    }

    private static OggOpusWriterSettings BuildSettings(OpusFileWriterOptions options)
    {
        var settings = new OggOpusWriterSettings
        {
            Bitrate = options.Bitrate,
            UseVariableBitrate = options.UseVariableBitrate,
            Complexity = options.Complexity,
            Application = options.Profile == OpusEncodingProfile.Voice
                ? OpusApplication.OPUS_APPLICATION_VOIP
                : OpusApplication.OPUS_APPLICATION_AUDIO,
            SignalType = options.Profile == OpusEncodingProfile.Voice
                ? OpusSignal.OPUS_SIGNAL_VOICE
                : OpusSignal.OPUS_SIGNAL_MUSIC
        };

        foreach (var pair in options.Tags)
        {
            settings.Tags[pair.Key.ToUpperInvariant()] = new List<string> { pair.Value };
        }

        return settings;
    }

    /// <summary>
    /// Picks the logical bitstream serial number. Ogg wants it to be effectively unique per
    /// stream so that multiplexed streams can be told apart.
    /// </summary>
    private static uint NewSerialNumber() =>
        (uint)Environment.TickCount64 ^ (uint)Guid.NewGuid().GetHashCode();
}
