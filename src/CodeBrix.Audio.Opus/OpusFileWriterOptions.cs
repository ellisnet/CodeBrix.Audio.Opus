using System;
using System.Collections.Generic;

namespace CodeBrix.Audio.Opus;

/// <summary>
/// How the encoder should be tuned. The defaults suit ordinary music and speech; most callers
/// never need to change them.
/// </summary>
/// <remarks>
/// Deliberately small. The Opus codec exposes a large control surface - forward error correction,
/// packet-loss percentage, discontinuous transmission, bandwidth ceilings, frame duration - but
/// those are streaming concerns: a file on disk drops no packets, and discontinuous transmission
/// writes gaps into one. If a further setting turns out to be needed it will be added here by
/// name.
/// </remarks>
public sealed class OpusFileWriterOptions
{
    /// <summary>
    /// Target bitrate in bits per second. Defaults to 96 kbps, which is transparent enough for
    /// music at 48 kHz stereo; 24-32 kbps is ample for speech.
    /// </summary>
    public int Bitrate { get; init; } = 96_000;

    /// <summary>
    /// What the encoder should optimise for. Defaults to <see cref="OpusEncodingProfile.Music" />.
    /// </summary>
    /// <remarks>
    /// This is fixed when the encoder is constructed, so it cannot be changed on a writer that is
    /// already open - construct a new writer instead.
    /// </remarks>
    public OpusEncodingProfile Profile { get; init; } = OpusEncodingProfile.Music;

    /// <summary>
    /// Whether the bitrate may vary with the material. On by default, and recommended: constant
    /// bitrate spends the same bits on silence as on a chorus.
    /// </summary>
    public bool UseVariableBitrate { get; init; } = true;

    /// <summary>
    /// Encoder complexity from 0 to 10, defaulting to 10. Lower values trade quality for CPU,
    /// which is worth doing on a slow device encoding in real time.
    /// </summary>
    public int Complexity { get; init; } = 10;

    /// <summary>
    /// Tags to write into the comment header, keyed by field name - "TITLE", "ARTIST" and so on.
    /// Field names are case-insensitive and are written upper-cased.
    /// </summary>
    public IDictionary<string, string> Tags { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates the option values, throwing when one is out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its permitted range.</exception>
    public void Validate()
    {
        if (Bitrate < 500 || Bitrate > 512_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Bitrate), Bitrate,
                "An Opus bitrate must be between 500 and 512000 bits per second.");
        }

        if (Complexity is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(Complexity), Complexity,
                "Opus encoder complexity runs from 0 to 10.");
        }
    }
}
