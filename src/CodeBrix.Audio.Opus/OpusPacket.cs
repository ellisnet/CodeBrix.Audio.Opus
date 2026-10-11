using System;

namespace CodeBrix.Audio.Opus;

/// <summary>
/// One encoded Opus packet from an <see cref="OpusPacketEncoder" />: the bytes a container stores
/// as one packet (or block), with its position on the stream timeline.
/// </summary>
public readonly struct OpusPacket
{
    /// <summary>The rate of the stream timeline, always 48000.</summary>
    private const long TimelineRate = 48000;

    /// <summary>Creates a packet.</summary>
    /// <param name="data">The encoded Opus packet.</param>
    /// <param name="firstSample">The packet's first sample on the 48 kHz stream timeline.</param>
    /// <param name="sampleCount">How many 48 kHz samples (per channel) the packet decodes to.</param>
    public OpusPacket(ReadOnlyMemory<byte> data, long firstSample, int sampleCount)
    {
        Data = data;
        FirstSample = firstSample;
        SampleCount = sampleCount;
    }

    /// <summary>The encoded Opus packet - exactly one packet, never part of one.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// The position of the packet's first sample on the STREAM timeline, in 48 kHz samples per
    /// channel. The first packet starts at 0 with the encoder's pre-skip still inside it, as
    /// RFC 7845 counts and as a Matroska track with a CodecDelay expects.
    /// </summary>
    public long FirstSample { get; }

    /// <summary>
    /// How many 48 kHz samples (per channel) the packet decodes to: 960 (20 ms) for every packet
    /// an <see cref="OpusPacketEncoder" /> makes.
    /// </summary>
    public int SampleCount { get; }

    /// <summary><see cref="FirstSample" /> as a time on the stream timeline.</summary>
    public TimeSpan Timestamp => TimeSpan.FromTicks(FirstSample * TimeSpan.TicksPerSecond / TimelineRate);

    /// <summary><see cref="SampleCount" /> as a duration.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(SampleCount * TimeSpan.TicksPerSecond / TimelineRate);
}
