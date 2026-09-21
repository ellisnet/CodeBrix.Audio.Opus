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
/// the GameEngine, open by file name through AudioFileReader, are written by file name through
/// AudioFileWriterRegistry, and can be recorded by the engine's Recorder - none of which need any
/// other change.
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

    // The packet seam's factory, and one instance for the same reason: SharedAudioOutput
    // .RegisterPacketCodecFactory de-duplicates on the instance too.
    private static readonly OpusPacketCodecFactory PacketFactory = new OpusPacketCodecFactory();

    // The writer seam's factory, held the same way. AudioFileWriterRegistry keys on the EXTENSION
    // rather than on the instance, so a fresh factory per call would replace rather than stack up -
    // but one shared instance means every .opus written by file name is written with the same
    // encoder settings, which is what a consumer replacing it with its own factory expects to be
    // replacing.
    private static readonly OpusAudioFileWriterFactory WriterFactory = new OpusAudioFileWriterFactory();

    private static bool registered;

    /// <summary>
    /// Registers Opus with the shared audio output and with the file-name reader AND writer
    /// registries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent and safe to call from any thread; calling it more than once does nothing.
    /// </para>
    /// <para>
    /// It registers BOTH audio seams: the stream seam, which is what opens a .opus file, and the
    /// packet seam, which decodes the bare Opus packets a media container carries. An application
    /// that only ever plays files is unaffected by the second one; an application demultiplexing a
    /// container gets it from the call it was already making.
    /// </para>
    /// <para>
    /// It covers READING AND WRITING. ".opus" goes into AudioFileReaderRegistry, so a .opus file
    /// opens by name, and an <see cref="OpusAudioFileWriterFactory" /> goes into
    /// AudioFileWriterRegistry, so a .opus file is WRITTEN by name too - which is what makes
    /// CodeBrix.Audio's <c>SoundFontRenderer.RenderToFile(synthesizer, sequence, "tune.opus")</c>
    /// produce an Opus file. Anyone already making this call gets the write side from the call they
    /// were already making. Register an <see cref="OpusAudioFileWriterFactory" /> of your own
    /// AFTERWARDS to write .opus at settings other than the encoder defaults.
    /// </para>
    /// </remarks>
    public static void Register()
    {
        lock (Gate)
        {
            if (registered) return;

            SharedAudioOutput.RegisterCodecFactory(Factory);
            SharedAudioOutput.RegisterPacketCodecFactory(PacketFactory);
            AudioFileReaderRegistry.Register(".opus", stream => new OpusFileReader(stream));
            AudioFileWriterRegistry.Register(WriterFactory);

            registered = true;
        }
    }

    /// <summary>
    /// Registers Opus with an engine the consumer drives itself, rather than the shared output.
    /// </summary>
    /// <param name="engine">The engine to register with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine" /> is null.</exception>
    /// <remarks>
    /// <para>
    /// Use this alongside CodeBrix.Audio's ManagedCodecs.RegisterAll when running your own
    /// <see cref="AudioEngine" />. It does not affect the shared output; call
    /// <see cref="Register()" /> for that.
    /// </para>
    /// <para>
    /// Like <see cref="Register()" /> it registers both seams - the stream one and the packet one -
    /// on the engine you pass. It does NOT register the ".opus" file extension for reading or for
    /// writing: those two registries are process-wide rather than an engine's, so call
    /// <see cref="Register()" /> when you want a .opus opened or written by file name.
    /// </para>
    /// </remarks>
    public static void Register(AudioEngine engine)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));

        engine.RegisterCodecFactory(Factory);
        engine.RegisterPacketCodecFactory(PacketFactory);
    }

    /// <summary>Whether <see cref="Register()" /> has run.</summary>
    public static bool IsRegistered
    {
        get { lock (Gate) { return registered; } }
    }
}
