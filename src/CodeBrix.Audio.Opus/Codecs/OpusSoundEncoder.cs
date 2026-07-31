using System;
using System.IO;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus.Codecs;

/// <summary>
/// Writes engine audio out as an Ogg Opus stream, which is what lets the engine's Recorder
/// capture straight to a .opus file.
/// </summary>
internal sealed class OpusSoundEncoder : ISoundEncoder
{
    private readonly OggOpusWriter writer;
    private readonly int channels;

    /// <summary>Creates an encoder writing to a stream.</summary>
    /// <param name="stream">The stream to write to, which this encoder does not own.</param>
    /// <param name="channels">Channels in the audio being encoded.</param>
    /// <param name="sampleRate">Sample rate of the audio being encoded.</param>
    /// <param name="settings">Encoder settings.</param>
    public OpusSoundEncoder(Stream stream, int channels, int sampleRate,
        OggOpusWriterSettings settings)
    {
        this.channels = channels;

        writer = new OggOpusWriter(stream, channels, sampleRate, settings,
            NewSerialNumber(), leaveOpen: true);
    }

    /// <inheritdoc />
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public int Encode(Span<float> samples)
    {
        if (IsDisposed) return 0;
        if (samples.Length == 0) return 0;

        writer.Write(samples);

        // Everything handed over is buffered and will reach the file, so the whole span counts as
        // encoded even though the packet for it may not be written until a later call.
        return samples.Length - (samples.Length % channels);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        writer.Dispose();
    }

    private static uint NewSerialNumber() =>
        (uint)Environment.TickCount64 ^ (uint)Guid.NewGuid().GetHashCode();
}
