namespace CodeBrix.Audio.Opus;

/// <summary>
/// What an Opus encoder should optimise for. The two profiles tune the codec differently enough
/// to be audible at low bitrates.
/// </summary>
public enum OpusEncodingProfile
{
    /// <summary>
    /// General audio: music, podcasts, game soundtracks, anything meant to reproduce the source
    /// as closely as possible.
    /// </summary>
    Music = 0,

    /// <summary>
    /// Speech: voice notes, push-to-talk, in-app memos. Favours intelligibility, and stays clear
    /// at bitrates where the music profile would not.
    /// </summary>
    Voice = 1
}
