using System;
using System.Buffers.Binary;
using System.IO;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// One Ogg page: the fixed 27-byte header, its segment table, and the payload those segments
/// describe. See RFC 3533 section 6.
/// </summary>
internal sealed class OggPage
{
    /// <summary>The four bytes every page starts with.</summary>
    public static readonly byte[] CapturePattern = "OggS"u8.ToArray();

    /// <summary>Size of the fixed part of the header, before the segment table.</summary>
    public const int FixedHeaderSize = 27;

    /// <summary>This page continues a packet begun on an earlier page.</summary>
    public const byte FlagContinued = 0x01;

    /// <summary>First page of the logical bitstream.</summary>
    public const byte FlagBeginningOfStream = 0x02;

    /// <summary>Last page of the logical bitstream.</summary>
    public const byte FlagEndOfStream = 0x04;

    /// <summary>The header type flags.</summary>
    public byte Flags { get; set; }

    /// <summary>
    /// The granule position at the end of this page, or -1 when the page contains no completed
    /// packet. For Opus this counts 48 kHz samples from the start of the stream, INCLUDING the
    /// pre-skip - see RFC 7845 section 4.
    /// </summary>
    public long GranulePosition { get; set; }

    /// <summary>Identifies the logical bitstream this page belongs to.</summary>
    public uint StreamSerialNumber { get; set; }

    /// <summary>Page counter within the logical bitstream, starting at 0.</summary>
    public uint SequenceNumber { get; set; }

    /// <summary>The lacing values: one byte per segment, each 0-255.</summary>
    public byte[] SegmentTable { get; set; } = Array.Empty<byte>();

    /// <summary>The concatenated segment payloads.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>Byte offset of this page within the stream it was read from.</summary>
    public long FileOffset { get; set; }

    /// <summary>Total size of the page on disk, header plus segment table plus payload.</summary>
    public int TotalSize => FixedHeaderSize + SegmentTable.Length + Data.Length;

    /// <summary>Whether this is the last page of the logical bitstream.</summary>
    public bool IsEndOfStream => (Flags & FlagEndOfStream) != 0;

    /// <summary>Whether this page opens with the tail of a packet from the previous page.</summary>
    public bool IsContinued => (Flags & FlagContinued) != 0;

    /// <summary>
    /// Reads the page starting at the stream's current position.
    /// </summary>
    /// <param name="stream">A stream positioned at a capture pattern.</param>
    /// <param name="page">The page read, or null.</param>
    /// <param name="validateCrc">
    /// When true a page whose checksum does not match is rejected. Reading is tolerant by
    /// default: a truncated final page is common in the wild, and the decoder above this layer
    /// would rather have the audio than a hard failure.
    /// </param>
    /// <returns>True when a whole page was read.</returns>
    public static bool TryRead(Stream stream, out OggPage page, bool validateCrc = false)
    {
        page = null;

        if (stream == null) throw new ArgumentNullException(nameof(stream));

        var offset = stream.CanSeek ? stream.Position : 0L;

        var header = new byte[FixedHeaderSize];
        if (!ReadExactly(stream, header, FixedHeaderSize)) return false;

        if (header[0] != CapturePattern[0] || header[1] != CapturePattern[1] ||
            header[2] != CapturePattern[2] || header[3] != CapturePattern[3])
        {
            return false;
        }

        // Byte 4 is the stream structure version, which RFC 3533 fixes at 0. A different value
        // means a format this code was not written against, so decline rather than guess.
        if (header[4] != 0) return false;

        var segmentCount = header[26];
        var segmentTable = new byte[segmentCount];
        if (!ReadExactly(stream, segmentTable, segmentCount)) return false;

        var payloadLength = 0;
        foreach (var lacing in segmentTable) payloadLength += lacing;

        var data = new byte[payloadLength];
        if (!ReadExactly(stream, data, payloadLength)) return false;

        if (validateCrc && !ChecksumMatches(header, segmentTable, data)) return false;

        page = new OggPage
        {
            Flags = header[5],
            GranulePosition = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6, 8)),
            StreamSerialNumber = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14, 4)),
            SequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18, 4)),
            SegmentTable = segmentTable,
            Data = data,
            FileOffset = offset
        };

        return true;
    }

    /// <summary>Serializes the page, computing and inserting the checksum.</summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[TotalSize];

        CapturePattern.CopyTo(buffer, 0);
        buffer[4] = 0;
        buffer[5] = Flags;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(6, 8), GranulePosition);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(14, 4), StreamSerialNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(18, 4), SequenceNumber);
        // Bytes 22-25 are the checksum, and must be zero while it is being computed.
        buffer[26] = (byte)SegmentTable.Length;
        SegmentTable.CopyTo(buffer, FixedHeaderSize);
        Data.CopyTo(buffer, FixedHeaderSize + SegmentTable.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(22, 4), OggCrc.Compute(buffer));

        return buffer;
    }

    private static bool ChecksumMatches(byte[] header, byte[] segmentTable, byte[] data)
    {
        var stated = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22, 4));

        var whole = new byte[header.Length + segmentTable.Length + data.Length];
        header.CopyTo(whole, 0);
        segmentTable.CopyTo(whole, header.Length);
        data.CopyTo(whole, header.Length + segmentTable.Length);

        // The checksum field reads as zero for the purposes of computing it.
        whole[22] = whole[23] = whole[24] = whole[25] = 0;

        return OggCrc.Compute(whole) == stated;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var read = 0;

        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }

        return true;
    }
}
