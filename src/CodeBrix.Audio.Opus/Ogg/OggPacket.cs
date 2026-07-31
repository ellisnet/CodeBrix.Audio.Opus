using System;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// One complete Ogg packet, reassembled from however many page segments carried it.
/// </summary>
internal sealed class OggPacket
{
    /// <summary>The packet bytes. For an Opus stream this is one Opus packet.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// The granule position of the page this packet completed on, or -1 when the packet is not
    /// the last one to complete on its page.
    /// </summary>
    /// <remarks>
    /// RFC 3533 gives a page ONE granule position, and it belongs to the last packet completed on
    /// that page. Earlier packets on the same page have no granule of their own, which is why
    /// this is -1 for them rather than a copy of the page's value - a copy would read as a
    /// timestamp and quietly skew any duration or seek arithmetic built on it.
    /// </remarks>
    public long GranulePosition { get; set; } = -1;

    /// <summary>Whether this packet completed on the final page of the logical bitstream.</summary>
    public bool IsEndOfStream { get; set; }
}
