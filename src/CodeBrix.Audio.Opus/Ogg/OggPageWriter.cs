using System;
using System.Collections.Generic;
using System.IO;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// Lays packets into Ogg pages and writes them to a stream (RFC 3533).
/// </summary>
/// <remarks>
/// Packets are never split across pages here. A page can describe up to 255 segments of 255
/// bytes, so a single page holds any packet up to 65,025 bytes; the largest Opus packet is well
/// under 4,000. The writer simply starts a new page when the next packet will not fit, which
/// means the continued-packet flag is never set on output.
/// </remarks>
internal sealed class OggPageWriter : IDisposable
{
    private const int MaxSegmentsPerPage = 255;

    private readonly Stream destination;
    private readonly bool leaveOpen;
    private readonly uint serialNumber;
    private readonly List<byte[]> pending = new List<byte[]>();

    private int pendingSegments;
    private long pendingGranule = -1;
    private uint sequenceNumber;
    private bool firstPageWritten;
    private bool disposed;

    /// <summary>Creates a writer for one logical bitstream.</summary>
    /// <param name="destination">The stream to write pages to.</param>
    /// <param name="serialNumber">The logical bitstream serial number.</param>
    /// <param name="leaveOpen">When false the stream is disposed along with this writer.</param>
    public OggPageWriter(Stream destination, uint serialNumber, bool leaveOpen = true)
    {
        this.destination = destination ?? throw new ArgumentNullException(nameof(destination));
        this.serialNumber = serialNumber;
        this.leaveOpen = leaveOpen;
    }

    /// <summary>Queues a packet, emitting a page first if this one will not fit.</summary>
    /// <param name="packet">The packet bytes.</param>
    /// <param name="granulePosition">
    /// The granule position after this packet, which becomes the page's granule if the packet is
    /// the last one on it.
    /// </param>
    public void WritePacket(byte[] packet, long granulePosition)
    {
        if (packet == null) throw new ArgumentNullException(nameof(packet));

        var segments = (packet.Length / 255) + 1;

        if (pendingSegments + segments > MaxSegmentsPerPage) FlushPage(false);

        pending.Add(packet);
        pendingSegments += segments;
        pendingGranule = granulePosition;
    }

    /// <summary>Emits everything queued as one page, on its own.</summary>
    /// <param name="endOfStream">Whether this page ends the logical bitstream.</param>
    public void FlushPage(bool endOfStream)
    {
        if (pending.Count == 0 && !endOfStream) return;

        var segmentTable = new List<byte>();
        var payloadLength = 0;

        foreach (var packet in pending)
        {
            var remaining = packet.Length;

            while (remaining >= 255)
            {
                segmentTable.Add(255);
                remaining -= 255;
            }

            // The terminating lacing value is always < 255, and is 0 for a packet whose length is
            // an exact multiple of 255 - that trailing zero is what marks the packet complete.
            segmentTable.Add((byte)remaining);
            payloadLength += packet.Length;
        }

        var data = new byte[payloadLength];
        var offset = 0;
        foreach (var packet in pending)
        {
            packet.CopyTo(data, offset);
            offset += packet.Length;
        }

        byte flags = 0;
        if (!firstPageWritten) flags |= OggPage.FlagBeginningOfStream;
        if (endOfStream) flags |= OggPage.FlagEndOfStream;

        var page = new OggPage
        {
            Flags = flags,
            GranulePosition = pendingGranule,
            StreamSerialNumber = serialNumber,
            SequenceNumber = sequenceNumber++,
            SegmentTable = segmentTable.ToArray(),
            Data = data
        };

        var bytes = page.ToBytes();
        destination.Write(bytes, 0, bytes.Length);

        firstPageWritten = true;
        pending.Clear();
        pendingSegments = 0;
    }

    /// <summary>Sets the granule position the next flushed page will carry.</summary>
    /// <remarks>
    /// Used for the final page, whose granule must state the true sample count so a decoder trims
    /// the padding in the last frame rather than playing it.
    /// </remarks>
    public void SetPendingGranule(long granulePosition) => pendingGranule = granulePosition;

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (!leaveOpen) destination.Dispose();
    }
}
