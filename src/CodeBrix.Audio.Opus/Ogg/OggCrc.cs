using System;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// The CRC used by every Ogg page header, as specified by RFC 3533 section 6.
/// </summary>
/// <remarks>
/// It is a 32-bit CRC with the generator polynomial 0x04C11DB7, zero initial value, no bit
/// reflection on input or output, and no final inversion. Those last three points are what make
/// it differ from the far more common CRC-32 used by zip and PNG, so a general-purpose CRC-32
/// routine cannot stand in for it.
/// <para>
/// The checksum covers the whole page - header, segment table and payload - with the four
/// checksum bytes themselves treated as zero.
/// </para>
/// </remarks>
internal static class OggCrc
{
    private const uint Polynomial = 0x04C11DB7u;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            var value = i << 24;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000u) != 0
                    ? (value << 1) ^ Polynomial
                    : value << 1;
            }

            table[i] = value;
        }

        return table;
    }

    /// <summary>Computes the Ogg page checksum over a buffer.</summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The checksum, ready to be written little-endian into the page header.</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0u;

        foreach (var b in data)
        {
            crc = (crc << 8) ^ Table[((crc >> 24) & 0xFF) ^ b];
        }

        return crc;
    }
}
