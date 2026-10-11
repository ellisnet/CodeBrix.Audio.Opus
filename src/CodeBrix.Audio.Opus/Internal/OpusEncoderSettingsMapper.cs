using System.Collections.Generic;
using CodeBrix.Audio.Opus.Codec.Enums;
using CodeBrix.Audio.Opus.Ogg;

namespace CodeBrix.Audio.Opus.Internal;

/// <summary>
/// Maps the public option classes onto <see cref="OggOpusWriterSettings" />, the settings the
/// encoding core takes, so that the two public encoders tune the codec identically.
/// </summary>
internal static class OpusEncoderSettingsMapper
{
    /// <summary>Maps the file writer's options, tags included.</summary>
    /// <param name="options">Validated options.</param>
    /// <returns>The core settings.</returns>
    public static OggOpusWriterSettings ToSettings(OpusFileWriterOptions options)
    {
        var settings = Map(options.Bitrate, options.Profile, options.UseVariableBitrate, options.Complexity);

        foreach (var pair in options.Tags)
        {
            settings.Tags[pair.Key.ToUpperInvariant()] = new List<string> { pair.Value };
        }

        return settings;
    }

    /// <summary>Maps the packet encoder's options.</summary>
    /// <param name="options">Validated options.</param>
    /// <returns>The core settings.</returns>
    public static OggOpusWriterSettings ToSettings(OpusEncoderOptions options)
    {
        var settings = Map(options.Bitrate, options.Profile, options.UseVariableBitrate, options.Complexity);
        settings.ResamplerQuality = options.ResamplerQuality;

        return settings;
    }

    private static OggOpusWriterSettings Map(int bitrate, OpusEncodingProfile profile,
        bool useVariableBitrate, int complexity) =>
        new OggOpusWriterSettings
        {
            Bitrate = bitrate,
            UseVariableBitrate = useVariableBitrate,
            Complexity = complexity,
            Application = profile == OpusEncodingProfile.Voice
                ? OpusApplication.OPUS_APPLICATION_VOIP
                : OpusApplication.OPUS_APPLICATION_AUDIO,
            SignalType = profile == OpusEncodingProfile.Voice
                ? OpusSignal.OPUS_SIGNAL_VOICE
                : OpusSignal.OPUS_SIGNAL_MUSIC
        };
}
