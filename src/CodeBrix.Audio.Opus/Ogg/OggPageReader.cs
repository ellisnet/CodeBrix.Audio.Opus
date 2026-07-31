using System;
using System.Collections.Generic;
using System.IO;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// Reads Ogg pages from a stream and reassembles the packets they carry (RFC 3533).
/// </summary>
/// <remarks>
/// Only one logical bitstream is followed - the first one seen. Chained and multiplexed Ogg
/// streams exist, but an .opus file is a single logical stream, and following more than one would
/// mean deciding which is "the" audio, which is not this layer's business.
/// </remarks>
internal sealed class OggPageReader : IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly Queue<OggPacket> ready = new Queue<OggPacket>();
    private readonly List<byte> partial = new List<byte>();

    private bool serialNumberKnown;
    private uint serialNumber;
    private bool endOfStreamSeen;
    private bool disposed;

    /// <summary>Creates a reader over a stream positioned at the start of an Ogg bitstream.</summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="leaveOpen">When false the stream is disposed along with this reader.</param>
    public OggPageReader(Stream stream, bool leaveOpen = true)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }

    /// <summary>The serial number of the logical bitstream being followed.</summary>
    public uint StreamSerialNumber => serialNumber;

    /// <summary>Whether the end-of-stream page has been read.</summary>
    public bool EndOfStreamReached => endOfStreamSeen && ready.Count == 0;

    /// <summary>Reads the next complete packet.</summary>
    /// <param name="packet">The packet read, or null at the end of the stream.</param>
    /// <returns>True when a packet was produced.</returns>
    public bool TryReadPacket(out OggPacket packet)
    {
        while (ready.Count == 0)
        {
            if (!ReadAndSplitNextPage())
            {
                packet = null;
                return false;
            }
        }

        packet = ready.Dequeue();
        return true;
    }

    /// <summary>
    /// Reads the next page belonging to the followed bitstream, scanning forward for the capture
    /// pattern if the stream is not already sitting on one.
    /// </summary>
    /// <param name="page">The page read, or null.</param>
    /// <returns>True when a page was read.</returns>
    public bool TryReadPage(out OggPage page)
    {
        while (true)
        {
            if (!Resync()) { page = null; return false; }

            if (!OggPage.TryRead(stream, out var candidate))
            {
                // Not a real page after all (or truncated). Step past the false capture pattern
                // and keep looking; a truncated tail simply ends the stream.
                if (!stream.CanSeek) { page = null; return false; }
                if (stream.Position >= stream.Length) { page = null; return false; }
                continue;
            }

            if (!serialNumberKnown)
            {
                serialNumber = candidate.StreamSerialNumber;
                serialNumberKnown = true;
            }
            else if (candidate.StreamSerialNumber != serialNumber)
            {
                // A page from another logical bitstream: skip it.
                continue;
            }

            page = candidate;
            return true;
        }
    }

    /// <summary>Repositions the reader at a byte offset and discards any partial packet.</summary>
    public void SeekToOffset(long offset)
    {
        stream.Position = offset;
        ready.Clear();
        partial.Clear();
        endOfStreamSeen = false;
    }

    /// <summary>
    /// Finds the granule position of the last page in the stream, which for Opus is the total
    /// sample count including the pre-skip.
    /// </summary>
    /// <returns>The final granule position, or -1 when it cannot be determined.</returns>
    public long ReadLastGranulePosition()
    {
        if (!stream.CanSeek) return -1;

        var savedPosition = stream.Position;

        try
        {
            // The last page is at the end, but its size is unknown, so walk back in windows
            // until a page header turns up, then read forward to the final one.
            const int windowSize = 65536;
            var length = stream.Length;
            var window = Math.Min(windowSize, length);
            var lastGranule = -1L;

            while (window <= length)
            {
                stream.Position = length - window;

                var granule = ScanForwardForLastGranule();
                if (granule >= 0)
                {
                    lastGranule = granule;
                    break;
                }

                if (window == length) break;
                window = Math.Min(window * 4, length);
            }

            return lastGranule;
        }
        finally
        {
            stream.Position = savedPosition;
        }
    }

    /// <summary>
    /// Positions the reader so that decoding forward from here reaches <paramref name="granule"/>,
    /// landing on the last page whose granule position does not exceed it.
    /// </summary>
    /// <param name="granule">The target granule position.</param>
    /// <param name="firstAudioPageOffset">Offset of the first audio page, the search floor.</param>
    /// <param name="startGranule">Granule position already accounted for at the returned offset.</param>
    /// <returns>True when the reader was positioned.</returns>
    public bool SeekToGranule(long granule, long firstAudioPageOffset, out long startGranule)
    {
        startGranule = 0;

        if (!stream.CanSeek) return false;

        var lo = firstAudioPageOffset;
        var hi = stream.Length;
        var bestOffset = firstAudioPageOffset;
        var bestGranule = 0L;

        // Binary search by byte offset. Each probe scans forward from the midpoint for the next
        // page header, so the comparison is always against a real page.
        var guard = 0;
        while (lo < hi && guard++ < 128)
        {
            var mid = lo + ((hi - lo) / 2);

            stream.Position = mid;
            if (!Resync() || !OggPage.TryRead(stream, out var page))
            {
                hi = mid;
                continue;
            }

            var pageOffset = page.FileOffset;
            var pageEnd = pageOffset + page.TotalSize;

            if (page.GranulePosition >= 0 && page.GranulePosition <= granule)
            {
                bestOffset = pageOffset;
                bestGranule = page.GranulePosition;
                if (pageEnd <= lo) break;
                lo = pageEnd;
            }
            else
            {
                if (pageOffset <= lo) break;
                hi = pageOffset;
            }
        }

        SeekToOffset(bestOffset);
        startGranule = bestGranule;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (!leaveOpen) stream.Dispose();
    }

    private long ScanForwardForLastGranule()
    {
        var granule = -1L;

        while (Resync())
        {
            if (!OggPage.TryRead(stream, out var page)) break;
            if (page.GranulePosition >= 0) granule = page.GranulePosition;
            if (page.IsEndOfStream) break;
        }

        return granule;
    }

    /// <summary>Advances the stream to the next capture pattern, if it is not already on one.</summary>
    private bool Resync()
    {
        Span<byte> window = stackalloc byte[4];
        var filled = 0;

        while (true)
        {
            if (filled < 4)
            {
                var b = stream.ReadByte();
                if (b < 0) return false;
                window[filled++] = (byte)b;
                if (filled < 4) continue;
            }

            if (window[0] == (byte)'O' && window[1] == (byte)'g' &&
                window[2] == (byte)'g' && window[3] == (byte)'S')
            {
                if (!stream.CanSeek) return false;
                stream.Position -= 4;
                return true;
            }

            window[0] = window[1];
            window[1] = window[2];
            window[2] = window[3];
            filled = 3;
        }
    }

    private bool ReadAndSplitNextPage()
    {
        if (endOfStreamSeen) return false;
        if (!TryReadPage(out var page)) return false;

        // A page that does not claim continuation starts a fresh packet, so anything held over
        // from a previous page was truncated - drop it rather than splicing unrelated bytes.
        if (!page.IsContinued && partial.Count > 0) partial.Clear();

        // Find which segment completes the last packet on this page: only that packet carries the
        // page's granule position.
        var lastCompletingSegment = -1;
        for (var i = 0; i < page.SegmentTable.Length; i++)
        {
            if (page.SegmentTable[i] < 255) lastCompletingSegment = i;
        }

        var offset = 0;
        for (var i = 0; i < page.SegmentTable.Length; i++)
        {
            int lacing = page.SegmentTable[i];

            for (var j = 0; j < lacing; j++) partial.Add(page.Data[offset + j]);
            offset += lacing;

            if (lacing == 255) continue;   // packet continues into the next segment or page

            ready.Enqueue(new OggPacket
            {
                Data = partial.ToArray(),
                GranulePosition = i == lastCompletingSegment ? page.GranulePosition : -1,
                IsEndOfStream = page.IsEndOfStream && i == lastCompletingSegment
            });

            partial.Clear();
        }

        if (page.IsEndOfStream) endOfStreamSeen = true;

        return true;
    }
}
