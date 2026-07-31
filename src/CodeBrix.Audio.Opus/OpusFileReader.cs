using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Opus.Ogg;
using CodeBrix.Audio.Wave;

namespace CodeBrix.Audio.Opus;

/// <summary>
/// Reads a .opus file or stream as a <see cref="WaveStream" /> of 32-bit float samples - the peer
/// of CodeBrix.Audio's OggVorbisFileReader and FlacFileReader.
/// </summary>
/// <remarks>
/// <para>
/// The output is always 48 kHz, because that is the only rate Opus decodes at. The rate recorded
/// in the file's header is the rate its ENCODER was given - 16 kHz for a typical messenger voice
/// note - and RFC 7845 marks it informational; it is surfaced as
/// <see cref="EncoderInputSampleRate" /> for reference and never used to convert anything.
/// </para>
/// <para>
/// <see cref="WaveStream.TotalTime" /> and <see cref="Length" /> exclude the encoder's pre-skip, so they
/// describe the audio that is actually heard rather than the padded stream on disk.
/// </para>
/// </remarks>
public sealed class OpusFileReader : WaveStream
{
    private readonly OggOpusReader reader;
    private readonly WaveFormat format;
    private readonly Stream ownedStream;
    private readonly int bytesPerFrame;

    private bool disposed;

    /// <summary>Opens a .opus file.</summary>
    /// <param name="fileName">Path to the file.</param>
    /// <exception cref="ArgumentException"><paramref name="fileName" /> is null or blank.</exception>
    /// <exception cref="InvalidDataException">The file is not a usable Ogg Opus stream.</exception>
    public OpusFileReader(string fileName)
        : this(OpenFile(fileName), ownsStream: true)
    {
    }

    /// <summary>Opens a .opus stream.</summary>
    /// <param name="inputStream">
    /// The stream to read. It is NOT owned by this reader: the caller keeps responsibility for
    /// disposing it, which is the contract CodeBrix.Audio's reader registry relies on.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inputStream" /> is null.</exception>
    /// <exception cref="InvalidDataException">The stream is not a usable Ogg Opus stream.</exception>
    public OpusFileReader(Stream inputStream)
        : this(inputStream, ownsStream: false)
    {
    }

    private OpusFileReader(Stream inputStream, bool ownsStream)
    {
        if (inputStream == null) throw new ArgumentNullException(nameof(inputStream));

        ownedStream = ownsStream ? inputStream : null;

        try
        {
            reader = new OggOpusReader(inputStream, leaveOpen: true);
        }
        catch
        {
            ownedStream?.Dispose();
            throw;
        }

        format = WaveFormat.CreateIeeeFloatWaveFormat(
            OggOpusReader.DecodeSampleRate, reader.Channels);
        bytesPerFrame = reader.Channels * sizeof(float);
    }

    /// <summary>The format of the samples returned: 48 kHz 32-bit IEEE float.</summary>
    public override WaveFormat WaveFormat => format;

    /// <summary>Length of the audible audio in bytes.</summary>
    public override long Length =>
        reader.TotalSamples < 0 ? 0 : reader.TotalSamples * bytesPerFrame;

    /// <summary>The current position in bytes.</summary>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    public override long Position
    {
        get => reader.Position * bytesPerFrame;
        set
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            reader.Seek(Math.Max(0, value) / bytesPerFrame);
        }
    }

    /// <summary>
    /// The sample rate the encoder was given, as recorded in the file's identification header.
    /// </summary>
    /// <remarks>
    /// Informational only, and frequently NOT 48000 - a WhatsApp or Telegram voice note usually
    /// says 16000 here. The decoded audio is 48 kHz regardless; see <see cref="WaveFormat" />.
    /// </remarks>
    public int EncoderInputSampleRate => reader.Head.InputSampleRate;

    /// <summary>Samples the encoder used to prime the stream, discarded during decoding.</summary>
    public int PreSkip => reader.Head.PreSkip;

    /// <summary>The stream's Vorbis comments, keyed by upper-cased field name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Tags => BuildTags();

    /// <summary>The encoder that produced the stream, from the comment header.</summary>
    public string EncoderVendor => reader.Tags.Vendor;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        // Whole samples only: a caller asking for a partial float gets the floats that fit.
        var sampleCount = buffer.Length / sizeof(float);
        if (sampleCount == 0) return 0;

        var samples = new float[sampleCount];
        var read = reader.Read(samples);
        if (read <= 0) return 0;

        MemoryMarshalCopy(samples.AsSpan(0, read), buffer);

        return read * sizeof(float);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            disposed = true;
            reader.Dispose();
            ownedStream?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Stream OpenFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("A file name is required.", nameof(fileName));
        }

        return File.OpenRead(fileName);
    }

    private static void MemoryMarshalCopy(ReadOnlySpan<float> source, Span<byte> destination)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(source);
        bytes.CopyTo(destination);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildTags()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in reader.Tags.Tags) result[pair.Key] = pair.Value.AsReadOnly();

        return result;
    }
}
