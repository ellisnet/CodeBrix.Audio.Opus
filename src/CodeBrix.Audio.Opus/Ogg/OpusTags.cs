using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// The Opus comment header carrying the vendor string and the tag list (RFC 7845 section 5.2).
/// </summary>
/// <remarks>
/// The body after the magic bytes is a Vorbis comment block: a vendor string, then a count, then
/// that many "FIELD=value" strings. Field names are case-insensitive by convention and are
/// surfaced here upper-cased, matching how CodeBrix.Audio exposes Vorbis comments elsewhere.
/// </remarks>
internal sealed class OpusTags
{
    /// <summary>The eight magic bytes the header starts with.</summary>
    public static readonly byte[] Magic = "OpusTags"u8.ToArray();

    /// <summary>The encoder that produced the stream.</summary>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>The tags, keyed by upper-cased field name. A field may appear more than once.</summary>
    public Dictionary<string, List<string>> Tags { get; } =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses a comment header.</summary>
    /// <param name="packet">The packet bytes.</param>
    /// <param name="tags">The parsed tags, or null.</param>
    /// <returns>True when the packet is a well-formed OpusTags.</returns>
    public static bool TryParse(ReadOnlySpan<byte> packet, out OpusTags tags)
    {
        tags = null;

        if (packet.Length < 12) return false;
        if (!packet[..8].SequenceEqual(Magic)) return false;

        var result = new OpusTags();
        var offset = 8;

        if (!TryReadString(packet, ref offset, out var vendor)) return false;
        result.Vendor = vendor;

        if (offset + 4 > packet.Length)
        {
            // A header may legitimately stop after the vendor string.
            tags = result;
            return true;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset, 4));
        offset += 4;

        for (var i = 0u; i < count; i++)
        {
            if (!TryReadString(packet, ref offset, out var comment)) break;

            var separator = comment.IndexOf('=');
            if (separator <= 0) continue;

            var name = comment[..separator].ToUpperInvariant();
            var value = comment[(separator + 1)..];

            if (!result.Tags.TryGetValue(name, out var values))
            {
                values = new List<string>();
                result.Tags[name] = values;
            }

            values.Add(value);
        }

        tags = result;
        return true;
    }

    /// <summary>Serializes the comment header.</summary>
    public byte[] ToBytes()
    {
        using var buffer = new MemoryStream();

        buffer.Write(Magic, 0, Magic.Length);
        WriteString(buffer, Vendor);

        var comments = new List<string>();
        foreach (var pair in Tags)
        {
            foreach (var value in pair.Value) comments.Add(pair.Key + "=" + value);
        }

        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)comments.Count);
        buffer.Write(count);

        foreach (var comment in comments) WriteString(buffer, comment);

        return buffer.ToArray();
    }

    private static bool TryReadString(ReadOnlySpan<byte> packet, ref int offset, out string value)
    {
        value = string.Empty;

        if (offset + 4 > packet.Length) return false;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset, 4));
        offset += 4;

        if (length > int.MaxValue || offset + (int)length > packet.Length) return false;

        value = Encoding.UTF8.GetString(packet.Slice(offset, (int)length));
        offset += (int)length;

        return true;
    }

    private static void WriteString(Stream destination, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)bytes.Length);

        destination.Write(length);
        destination.Write(bytes, 0, bytes.Length);
    }
}
