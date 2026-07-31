using System;
using System.Buffers.Binary;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// The Opus identification header that opens every Ogg Opus stream (RFC 7845 section 5.1).
/// </summary>
internal sealed class OpusHead
{
    /// <summary>The eight magic bytes the header starts with.</summary>
    public static readonly byte[] Magic = "OpusHead"u8.ToArray();

    /// <summary>Smallest valid header: through the channel mapping family byte.</summary>
    public const int MinimumSize = 19;

    /// <summary>Number of channels in the decoded output, 1-255.</summary>
    public int ChannelCount { get; set; } = 2;

    /// <summary>
    /// Samples to discard from the front of the decoded stream, on the 48 kHz clock.
    /// </summary>
    /// <remarks>
    /// These are the encoder's priming samples. They are counted by every granule position in the
    /// stream but must never be heard, so both the duration arithmetic and the first decode after
    /// opening have to account for them.
    /// </remarks>
    public int PreSkip { get; set; }

    /// <summary>
    /// The sample rate of the audio the ENCODER was given, which RFC 7845 marks informational.
    /// </summary>
    /// <remarks>
    /// An Opus stream always decodes at 48 kHz whatever this says, and it is permitted to be 0
    /// when unknown. It is preserved so a writer can round-trip it and a caller can report "this
    /// came from a 16 kHz source", never to drive resampling.
    /// </remarks>
    public int InputSampleRate { get; set; } = 48000;

    /// <summary>Gain to apply to the decoded output, in Q7.8 dB. Almost always 0.</summary>
    public short OutputGainQ78 { get; set; }

    /// <summary>
    /// The channel mapping family: 0 for mono or stereo, 1 for the multichannel layouts, 255 for
    /// an undefined mapping.
    /// </summary>
    public int ChannelMappingFamily { get; set; }

    /// <summary>The output gain expressed in decibels.</summary>
    public double OutputGainDecibels => OutputGainQ78 / 256.0;

    /// <summary>Whether the mapping is one this package decodes: mono or stereo, family 0.</summary>
    public bool IsSupportedMapping => ChannelMappingFamily == 0 && ChannelCount is 1 or 2;

    /// <summary>Parses an identification header.</summary>
    /// <param name="packet">The packet bytes.</param>
    /// <param name="head">The parsed header, or null.</param>
    /// <returns>True when the packet is a well-formed OpusHead.</returns>
    public static bool TryParse(ReadOnlySpan<byte> packet, out OpusHead head)
    {
        head = null;

        if (packet.Length < MinimumSize) return false;
        if (!packet[..8].SequenceEqual(Magic)) return false;

        // Byte 8 is the version. RFC 7845 says a decoder must accept any version whose upper four
        // bits are zero, treating the lower bits as backward-compatible additions.
        if ((packet[8] & 0xF0) != 0) return false;

        var channels = packet[9];
        if (channels == 0) return false;

        head = new OpusHead
        {
            ChannelCount = channels,
            PreSkip = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2)),
            InputSampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, 4)),
            OutputGainQ78 = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(16, 2)),
            ChannelMappingFamily = packet[18]
        };

        return true;
    }

    /// <summary>Serializes a family-0 identification header.</summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[MinimumSize];

        Magic.CopyTo(buffer, 0);
        buffer[8] = 1;                            // version
        buffer[9] = (byte)ChannelCount;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), (ushort)PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), (uint)InputSampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(16, 2), OutputGainQ78);
        buffer[18] = (byte)ChannelMappingFamily;

        return buffer;
    }
}
