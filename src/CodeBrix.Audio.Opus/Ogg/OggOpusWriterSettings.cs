using System;
using System.Collections.Generic;
using CodeBrix.Audio.Opus.Codec.Enums;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// Encoder settings in the terms the vendored codec understands.
/// </summary>
/// <remarks>
/// This is the internal counterpart of the public <see cref="OpusFileWriterOptions" />. Keeping
/// the two separate is deliberate: the public options name things the way a CodeBrix consumer
/// thinks about them, and no vendored type appears in the public API surface.
/// </remarks>
internal sealed class OggOpusWriterSettings
{
    /// <summary>Encoder tuning: voice or general audio.</summary>
    public OpusApplication Application { get; set; } = OpusApplication.OPUS_APPLICATION_AUDIO;

    /// <summary>Signal hint given to the encoder.</summary>
    public OpusSignal SignalType { get; set; } = OpusSignal.OPUS_SIGNAL_AUTO;

    /// <summary>Target bitrate in bits per second.</summary>
    public int Bitrate { get; set; } = 96000;

    /// <summary>Whether the bitrate may vary.</summary>
    public bool UseVariableBitrate { get; set; } = true;

    /// <summary>Encoder complexity, 0 to 10.</summary>
    public int Complexity { get; set; } = 10;

    /// <summary>Quality of the input resampler, 0 to 10, when the input is not already 48 kHz.</summary>
    public int ResamplerQuality { get; set; } = 5;

    /// <summary>The vendor string written into the comment header.</summary>
    public string Vendor { get; set; } = "CodeBrix.Audio.Opus";

    /// <summary>Tags to write, keyed by field name.</summary>
    public Dictionary<string, List<string>> Tags { get; } =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
}
