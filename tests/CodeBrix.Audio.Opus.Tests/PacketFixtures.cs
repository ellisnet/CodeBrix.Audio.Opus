using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Takes an Ogg Opus fixture apart into the pieces a MEDIA CONTAINER would carry: the
/// identification header as codec-private data, and the audio packets on their own.
/// </summary>
/// <remarks>
/// This is the shape a demultiplexer hands out - bare Opus packets with no Ogg framing around them
/// - so it is how the packet seam can be measured against the stream seam on the very same audio.
/// The repository's own <c>OggPageReader</c> does the un-framing, reached through
/// InternalsVisibleTo, because a second page parser written for the tests would only prove itself.
/// </remarks>
internal static class PacketFixtures
{
    /// <summary>An Ogg Opus fixture pulled apart the way a container stores it.</summary>
    internal sealed class SplitStream
    {
        /// <summary>
        /// The identification header bytes, which are exactly what a Matroska or WebM track puts in
        /// its CodecPrivate element.
        /// </summary>
        public byte[] CodecPrivate { get; set; } = Array.Empty<byte>();

        /// <summary>The audio packets, in stream order, headers excluded.</summary>
        public List<byte[]> Packets { get; } = new List<byte[]>();
    }

    /// <summary>Splits a fixture beside the test assembly.</summary>
    /// <param name="fixtureFileName">The fixture's file name, from <see cref="TestAssets" />.</param>
    public static SplitStream Split(string fixtureFileName) =>
        SplitFile(TestAssets.Path(fixtureFileName));

    /// <summary>
    /// Rewrites a fixture's identification header with a different output gain, returning the
    /// whole Ogg file as bytes.
    /// </summary>
    /// <param name="fixtureFileName">The fixture to start from.</param>
    /// <param name="outputGainQ78">The gain to store, in Q7.8 dB (256 units = 1 dB).</param>
    /// <remarks>
    /// The re-serialised header is the same 19 bytes long as the one it replaces, so only the
    /// first page's checksum has to be recomputed - no new binary asset is needed to test a gain
    /// that no encoder writes in practice.
    /// </remarks>
    public static byte[] WithOutputGain(string fixtureFileName, short outputGainQ78)
    {
        var bytes = File.ReadAllBytes(TestAssets.Path(fixtureFileName));
        var head = FindOpusHead(bytes);

        if (!OpusHead.TryParse(bytes.AsSpan(head, OpusHead.MinimumSize), out var parsed))
        {
            throw new InvalidDataException($"'{fixtureFileName}' has no parseable OpusHead.");
        }

        parsed.OutputGainQ78 = outputGainQ78;
        parsed.ToBytes().CopyTo(bytes, head);

        RepairFirstPageChecksum(bytes);

        return bytes;
    }

    /// <summary>The codec-private data of a fixture, with a different output gain stored in it.</summary>
    /// <param name="fixtureFileName">The fixture to start from.</param>
    /// <param name="outputGainQ78">The gain to store, in Q7.8 dB (256 units = 1 dB).</param>
    public static byte[] CodecPrivateWithOutputGain(string fixtureFileName, short outputGainQ78)
    {
        var split = Split(fixtureFileName);

        if (!OpusHead.TryParse(split.CodecPrivate, out var head))
        {
            throw new InvalidDataException($"'{fixtureFileName}' has no parseable OpusHead.");
        }

        head.OutputGainQ78 = outputGainQ78;

        return head.ToBytes();
    }

    /// <summary>Finds the offset of the "OpusHead" magic in a file.</summary>
    private static int FindOpusHead(byte[] bytes)
    {
        var at = bytes.AsSpan().IndexOf(OpusHead.Magic.AsSpan());

        if (at < 0) throw new InvalidDataException("These bytes carry no OpusHead at all.");

        return at;
    }

    /// <summary>
    /// Recomputes the checksum of the first Ogg page, which is the one carrying the
    /// identification header.
    /// </summary>
    private static void RepairFirstPageChecksum(byte[] bytes)
    {
        const int headerSize = 27;

        var segmentCount = bytes[headerSize - 1];
        var pageLength = headerSize + segmentCount;

        for (var i = 0; i < segmentCount; i++)
        {
            pageLength += bytes[headerSize + i];
        }

        // The checksum is computed over the whole page with its own four bytes zeroed.
        bytes.AsSpan(22, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(22, 4), OggCrc.Compute(bytes.AsSpan(0, pageLength)));
    }

    /// <summary>Splits an Ogg Opus file at an arbitrary path.</summary>
    /// <param name="path">The file to split.</param>
    public static SplitStream SplitFile(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var pages = new OggPageReader(stream, leaveOpen: true);

        var split = new SplitStream();

        // Packet 1 is OpusHead, packet 2 is OpusTags, and everything after them is audio. That
        // ordering is RFC 7845 section 3, and it is what lets a container store the first packet as
        // codec-private data and throw the rest of the framing away.
        if (!pages.TryReadPacket(out var identification))
        {
            throw new InvalidDataException($"'{path}' carries no Ogg packets at all.");
        }

        split.CodecPrivate = identification.Data;

        if (!pages.TryReadPacket(out _))
        {
            throw new InvalidDataException($"'{path}' has no comment header after its OpusHead.");
        }

        while (pages.TryReadPacket(out var packet))
        {
            if (packet.Data.Length == 0) continue;

            split.Packets.Add(packet.Data);
        }

        return split;
    }
}
