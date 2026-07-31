using System;
using System.IO;
using CodeBrix.Audio.Engine.Abstracts;
using CodeBrix.Audio.Opus.Codecs;
using CodeBrix.Audio.Wave;

namespace CodeBrix.Audio.Opus;

/// <summary>
/// The one call that turns on Opus support: <c>CodeBrixAudioOpus.Register();</c>
/// </summary>
/// <remarks>
/// <para>
/// Call it once, early, before anything opens audio. Afterwards .opus files play through
/// AudioFilePlayer, SoundEffectClip, WaveOutEvent, the CodeBrix.Platform AudioPlayer add-in and
/// the GameEngine, open by file name through AudioFileReader, and can be recorded by the engine's
/// Recorder - none of which need any other change.
/// </para>
/// <para>
/// There is deliberately NO module initializer doing this for you. A module initializer only runs
/// once something in the assembly is touched, which trimming and lazy assembly loading make
/// unreliable: the package would work in a debug build and silently fail to register in a trimmed
/// publish. An explicit call is the contract.
/// </para>
/// </remarks>
public static class CodeBrixAudioOpus
{
    private static readonly object Gate = new object();

    // ONE factory instance, reused. SharedAudioOutput.RegisterCodecFactory de-duplicates on the
    // instance, so handing it a freshly constructed factory on every call would register the same
    // codec repeatedly.
    private static readonly OpusCodecFactory Factory = new OpusCodecFactory();

    private static bool registered;

    /// <summary>
    /// Registers Opus with the shared audio output and the file-name reader registry.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to call from any thread; calling it more than once does nothing.
    /// </remarks>
    public static void Register()
    {
        lock (Gate)
        {
            if (registered) return;

            SharedAudioOutput.RegisterCodecFactory(Factory);
            AudioFileReaderRegistry.Register(".opus", stream => new OpusFileReader(stream));

            registered = true;
        }
    }

    /// <summary>
    /// Registers Opus with an engine the consumer drives itself, rather than the shared output.
    /// </summary>
    /// <param name="engine">The engine to register with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine" /> is null.</exception>
    /// <remarks>
    /// Use this alongside CodeBrix.Audio's ManagedCodecs.RegisterAll when running your own
    /// <see cref="AudioEngine" />. It does not affect the shared output; call
    /// <see cref="Register()" /> for that.
    /// </remarks>
    public static void Register(AudioEngine engine)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));

        engine.RegisterCodecFactory(Factory);
    }

    /// <summary>Whether <see cref="Register()" /> has run.</summary>
    public static bool IsRegistered
    {
        get { lock (Gate) { return registered; } }
    }
}
