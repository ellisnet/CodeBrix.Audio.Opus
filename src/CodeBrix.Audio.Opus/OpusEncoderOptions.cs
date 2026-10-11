using System;

namespace CodeBrix.Audio.Opus;

/// <summary>
/// How an <see cref="OpusPacketEncoder" /> should be tuned. The defaults suit ordinary music and
/// speech; most callers never need to change them.
/// </summary>
/// <remarks>
/// The tuning values mean exactly what they mean on <see cref="OpusFileWriterOptions" />, and the
/// same values produce the same packets. There are no tags here: tags belong to a container, and a
/// packet encoder has none.
/// </remarks>
public sealed class OpusEncoderOptions
{
    /// <summary>
    /// Target bitrate in bits per second, for the whole stream (all channels together). Defaults to
    /// 96 kbps, which is transparent enough for music at 48 kHz stereo; 24-32 kbps is ample for
    /// speech. Must be between 500 and 512000.
    /// </summary>
    public int Bitrate { get; init; } = 96_000;

    /// <summary>
    /// What the encoder should optimise for. Defaults to <see cref="OpusEncodingProfile.Music" />.
    /// </summary>
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
    /// Quality of the input resampler from 0 to 10, defaulting to 5 (the value
    /// <see cref="OpusFileWriter" /> always uses). Only consulted when the input rate is not
    /// 48000 Hz; higher values cost more CPU.
    /// </summary>
    public int ResamplerQuality { get; init; } = 5;

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

        if (ResamplerQuality is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(ResamplerQuality), ResamplerQuality,
                "The resampler quality runs from 0 to 10.");
        }
    }
}
