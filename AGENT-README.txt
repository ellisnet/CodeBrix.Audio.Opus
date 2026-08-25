================================================================================
AGENT-README: CodeBrix.Audio.Opus
A Guide for AI Coding Agents - CONSUMING the CodeBrix.Audio.Opus.BsdLicenseForever
NuGet package
================================================================================

OVERVIEW
========
CodeBrix.Audio.Opus adds Ogg Opus (.opus) decoding and encoding to
CodeBrix.Audio. It targets .NET 10 or later.

It is a pure managed library with NO native code on any platform: no P/Invoke,
no runtimes/ folder, nothing to rebuild when a platform is added. It works
everywhere CodeBrix.Audio works, including linux-riscv64.

One call at start-up wires it in:

    CodeBrixAudioOpus.Register();

After that .opus plays through AudioFilePlayer, SoundEffectClip, WaveOutEvent,
the CodeBrix.Platform AudioPlayer add-in and the CodeBrix.Platform GameEngine;
opens by file name through AudioFileReader; and can be written by the
CodeBrix.Audio engine's Recorder. None of those needs any other change. The
consuming APPLICATION takes this dependency and makes the call - the add-ins
never do.

In the GameEngine, .opus is not a second-class add-on. It reaches every path a
built-in format reaches: AudioResourceManager loads, SoundChannel clips,
CachedSound decode-once preload, the SFX voice pool, music tracks, and
PlatformAudioFactory.Supports. That falls out of how the engine resolves an
extension - its own table first, then CodeBrix.Audio's AudioFileReaderRegistry,
which is where Register() put .opus - so no engine code names Opus, and none
needs to. A format arriving by that route declares no file-on-disk requirement,
which is the same flag that makes short clips eligible for PCM preload, so
.opus preloads exactly like .ogg rather than decoding on the audio thread.

Provenance: the codec under src/CodeBrix.Audio.Opus/Codec/ is a port of
Concentus 2.2.2 (BSD-3-Clause), demoted to internal types; the Ogg container
layer around it was written here from RFC 3533 and RFC 7845 rather than
vendored. Use the CodeBrix.Audio.Opus namespace - never the upstream project's
namespaces, which do not exist in this assembly. THIRD-PARTY-NOTICES.txt is the
authoritative record of what came from where.


INSTALLATION
============
NuGet package:   CodeBrix.Audio.Opus.BsdLicenseForever
Command:         dotnet add package CodeBrix.Audio.Opus.BsdLicenseForever

Note that the PACKAGE id carries the ".BsdLicenseForever" suffix, but the
NAMESPACE is simply "CodeBrix.Audio.Opus" (no suffix).

  License:        BSD-3-Clause (licence acceptance is required)
  Depends on:     CodeBrix.Audio.MitLicenseForever
  Target:         .NET 10 or later
  Native libs:    NONE. Nothing is P/Invoked and no binaries ship, so the
                  package places no restriction on the runtime identifiers your
                  application may publish for.
  OS limits:      none of its own. Decoding and encoding are pure managed code
                  and run anywhere .NET runs; only PLAYBACK is limited, and that
                  limit belongs to CodeBrix.Audio's bundled engine and its native
                  backend, not to this package.

See also: the CodeBrix.Audio package's own guide, at
https://github.com/ellisnet/CodeBrix.Audio/blob/main/AGENT-README.txt - it
documents AudioFilePlayer, SoundEffectClip, WaveOutEvent, WaveFileWriter and the
reader registry that this package plugs into.


KEY NAMESPACES / USINGS
=======================
  using CodeBrix.Audio.Opus;      // everything a consumer needs

Consumers of the playback and file surfaces will also want the CodeBrix.Audio
namespaces they already use:

  using CodeBrix.Audio.Wave;      // AudioFileReader, WaveOutEvent,
                                  //   SharedAudioOutput, WaveFileWriter
  using CodeBrix.Audio.Playback;  // AudioFilePlayer, SoundEffectClip

Sub-namespaces of this package are implementation detail and entirely internal
apart from the codec factory:

  CodeBrix.Audio.Opus.Codec    the vendored Opus codec (all internal)
  CodeBrix.Audio.Opus.Ogg      the Ogg container layer (all internal)
  CodeBrix.Audio.Opus.Codecs   the engine-facing decoder / encoder / factory
                               (only OpusCodecFactory is public)


WHY THIS IS A SEPARATE PACKAGE (read before proposing a merge)
==============================================================
CodeBrix.Audio holds a deliberate licence bar of MIT or more permissive, and its
package id - CodeBrix.Audio.MitLicenseForever - says so out loud. Opus cannot
clear that bar. Every managed Opus implementation is a port of libopus, and
libopus is BSD-3-Clause, whose third clause adds a no-endorsement condition that
MIT does not have. So the codec lives here, in a BSD-3-Clause package, and
CodeBrix.Audio stays what it claims to be.

Do NOT propose folding this into CodeBrix.Audio. That decision is the standing
precedent for the family: a licence that adds a condition gets its own package.

What this means for YOUR application: taking this dependency means accepting
BSD-3-Clause terms alongside MIT ones. Its third clause forbids using the
copyright holders' names to endorse your product without permission, and the
notice text must travel with any redistribution.


CORE API REFERENCE
==================
Six public types. Every one of them is listed here.

CodeBrixAudioOpus  (static, CodeBrix.Audio.Opus)
------------------------------------------------
    static void Register()
    static void Register(AudioEngine engine)
    static bool IsRegistered { get; }

  Register() is the one call. It registers the codec factory with
  SharedAudioOutput and registers ".opus" with AudioFileReaderRegistry. It is
  idempotent and thread-safe; calling it twice does nothing.
  Register(AudioEngine) is for a consumer driving its OWN engine rather than the
  shared output - it registers the factory with that engine only, and does not
  affect SharedAudioOutput. Pair it with CodeBrix.Audio's
  ManagedCodecs.RegisterAll(engine). It throws ArgumentNullException on a null
  engine.
  There is deliberately no module initializer doing this for you: a module
  initializer only runs once something in the assembly is touched, so under
  trimming or lazy assembly loading the package would work in a debug build and
  silently fail to register in a trimmed publish.

OpusFileReader : WaveStream  (CodeBrix.Audio.Opus)
---------------------------------------------------
    OpusFileReader(string fileName)
    OpusFileReader(Stream inputStream)

    override WaveFormat WaveFormat { get; }
    override long Length { get; }
    override long Position { get; set; }
    TimeSpan TotalTime { get; }                     // inherited from WaveStream
    int EncoderInputSampleRate { get; }
    int PreSkip { get; }
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tags { get; }
    string EncoderVendor { get; }
    override int Read(byte[] buffer, int offset, int count)
    override int Read(Span<byte> buffer)
    void Dispose()                                  // inherited from Stream

  The peer of CodeBrix.Audio's OggVorbisFileReader. WaveFormat is always
  48 kHz 32-bit IEEE float; Channels comes from the file (1 or 2).

  OWNERSHIP DIFFERS BY CONSTRUCTOR. The string overload opens the file and the
  reader owns it - dispose the reader and the file closes. The Stream overload
  does NOT own the stream: the CALLER disposes it. That is the contract
  CodeBrix.Audio's reader registry relies on, which is why Register() hands the
  registry the Stream overload.

  UNITS. Length and Position are in BYTES, like every WaveStream - one frame is
  Channels * sizeof(float) bytes. Position setting seeks; the seek is to a
  sample boundary, and Length and TotalTime EXCLUDE the encoder's pre-skip, so
  they describe audio that is actually heard rather than the padded stream on
  disk. Length is 0 when the stream does not report a total sample count.

  Read() returns whole samples only: a caller asking for a partial float gets
  the floats that fit. It returns 0 at end of stream.

  Tags are the stream's Vorbis comments, keyed case-insensitively by field name
  ("TITLE", "ARTIST"), each mapping to the LIST of values for that field -
  Opus comment headers may repeat a field. EncoderVendor is the vendor string
  from the comment header.

OpusFileWriter : IDisposable  (CodeBrix.Audio.Opus)
----------------------------------------------------
    OpusFileWriter(string fileName, int sampleRate, int channels,
                   OpusFileWriterOptions options = null)
    OpusFileWriter(Stream outputStream, int sampleRate, int channels,
                   OpusFileWriterOptions options = null)

    int PreSkip { get; }
    void Write(ReadOnlySpan<float> samples)
    void Write(float[] samples, int offset, int count)
    void Finish()
    void Dispose()

  The peer of CodeBrix.Audio's WaveFileWriter. Samples are INTERLEAVED floats in
  [-1, 1]. channels must be 1 or 2. Any input sampleRate is accepted and
  resampled to the 48 kHz Opus encodes at; the rate you declare is still
  recorded in the file header as the rate the encoder was given.

  Same ownership split as the reader: the string overload creates the file
  (overwriting an existing one) and owns it; the Stream overload does not own
  the stream.

  Finish() pads and flushes the final partial frame and writes the closing page
  carrying the true sample count. Dispose() calls it, and it is safe to call
  twice. DISPOSE THE WRITER - see COMMON PITFALLS.

OpusFileWriterOptions  (CodeBrix.Audio.Opus)
---------------------------------------------
    int Bitrate { get; init; }                       // default 96_000 bps
    OpusEncodingProfile Profile { get; init; }       // default Music
    bool UseVariableBitrate { get; init; }           // default true
    int Complexity { get; init; }                    // default 10, range 0-10
    IDictionary<string, string> Tags { get; }        // case-insensitive keys
    void Validate()

  Init-only properties, so build one with an object initializer. Tags is a
  get-only dictionary you add to; keys are field names ("TITLE", "ARTIST") and
  are written upper-cased. Validate() throws ArgumentOutOfRangeException for a
  Bitrate outside 500..512000 or a Complexity outside 0..10; the writer's
  constructor calls it for you, so an invalid option throws there.

  The option set is deliberately small. Opus also exposes forward error
  correction, packet-loss percentage, discontinuous transmission, bandwidth
  ceilings and frame duration, but those are STREAMING concerns - a file on disk
  drops no packets, and discontinuous transmission writes gaps into one.

OpusEncodingProfile  (enum, CodeBrix.Audio.Opus)
-------------------------------------------------
    Music = 0     general audio: music, podcasts, game soundtracks
    Voice = 1     speech: voice notes, push-to-talk, in-app memos

  The two profiles tune the codec differently enough to be audible at low
  bitrates. Fixed when the encoder is constructed, so it cannot be changed on an
  open writer - construct a new writer instead.

OpusCodecFactory : ICodecFactory  (CodeBrix.Audio.Opus.Codecs)
---------------------------------------------------------------
    const string OpusFormatId = "opus"
    const string OggFormatId  = "ogg"
    string FactoryId { get; }                        // "CodeBrix.Audio.Opus.ManagedOpus"
    IReadOnlyCollection<string> SupportedFormatIds { get; }   // ["ogg", "opus"]
    int Priority { get; }                            // -10
    ISoundDecoder CreateDecoder(Stream stream, string formatId, AudioFormat format)
    ISoundDecoder TryCreateDecoder(Stream stream, out AudioFormat detectedFormat,
                                   AudioFormat? hintFormat = null)
    ISoundEncoder CreateEncoder(Stream stream, string formatId, AudioFormat format)

  The ICodecFactory the engine talks to. Public so a consumer can register it by
  hand - SharedAudioOutput.RegisterCodecFactory(new OpusCodecFactory()) - but
  Register() is the friendly path and reuses ONE instance, which matters (see
  COMMON PITFALLS). Priority -10 sits below the engine's built-in native factory
  at 0, matching CodeBrix.Audio's own managed Vorbis and FLAC factories.

  CreateEncoder is what the engine's Recorder reaches, so
  new Recorder(captureDevice, stream, "opus") records straight to Ogg Opus once
  the factory is registered. It accepts only the "opus" format id and only 1 or
  2 channels, returning null otherwise.

ERROR MODEL
-----------
  An unusable stream throws InvalidDataException with a message that names the
  problem. A null stream argument throws ArgumentNullException; a null or blank
  file name throws ArgumentException. Using a disposed reader or writer throws
  ObjectDisposedException. Readers and writers are IDisposable; dispose them.


COMPLETE EXAMPLES
=================
Turn Opus on (do this once, at start-up, before anything opens audio):

    using CodeBrix.Audio.Opus;

    CodeBrixAudioOpus.Register();

Play a .opus file with the media transport (the shortest useful path):

    using CodeBrix.Audio.Opus;
    using CodeBrix.Audio.Playback;

    CodeBrixAudioOpus.Register();          // once, at start-up

    var media = new AudioFilePlayer();
    media.Load("podcast.opus");            // Duration is available now
    media.Play();
    // media.Position / media.Duration are TimeSpans; Seek, Volume, Pause, Stop,
    // IsLooping and PlaybackEnded all behave exactly as for .mp3 or .flac.
    // media.Dispose() when finished.

Decode a .opus file to float samples, and read its tags:

    using System;
    using CodeBrix.Audio.Opus;

    using var reader = new OpusFileReader("voice-note.opus");

    Console.WriteLine(reader.WaveFormat.SampleRate);        // always 48000
    Console.WriteLine(reader.WaveFormat.Channels);          // 1 or 2
    Console.WriteLine(reader.TotalTime);                    // pre-skip excluded
    Console.WriteLine(reader.EncoderInputSampleRate);        // often 16000 - do
                                                             // NOT resample by it
    if (reader.Tags.TryGetValue("TITLE", out var titles))
        Console.WriteLine(titles[0]);

    var frame = reader.WaveFormat.Channels * sizeof(float);
    var bytes = new byte[frame * 4096];
    int read;
    while ((read = reader.Read(bytes, 0, bytes.Length)) > 0)
    {
        var samples = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, read));
        // samples holds interleaved 48 kHz floats in [-1, 1]
    }
    // needs: using System.Runtime.InteropServices;

Hand a reader straight to WaveOutEvent (no registration needed - this path
constructs the reader yourself):

    using CodeBrix.Audio.Opus;
    using CodeBrix.Audio.Wave;

    // Opus always decodes at 48 kHz, and WaveOutEvent does not resample, so pin
    // the shared output to 48 kHz once at start-up.
    SharedAudioOutput.Configure(sampleRate: 48000);

    var player = new WaveOutEvent();
    player.Init(new OpusFileReader("clip.opus"));   // OpusFileReader is a WaveStream
    player.PlaybackStopped += (s, e) => { /* ended; e.Exception is null on normal end */ };
    player.Play();
    // player.Volume = 0.5f;  Pause / Stop / Dispose as usual.

Open a .opus by file name through the registry, alongside every other format:

    using CodeBrix.Audio.Opus;
    using CodeBrix.Audio.Wave;

    CodeBrixAudioOpus.Register();

    using var stream = AudioFileReaderRegistry.OpenFile("clip.opus");
    // A FileOwningWaveStream: disposing it disposes the OpusFileReader and THEN
    // closes the file. `using` it, or the file stays locked on Windows.
    // stream.WaveFormat is 48 kHz 32-bit float.

Encode float samples to a .opus file:

    using CodeBrix.Audio.Opus;

    var options = new OpusFileWriterOptions
    {
        Bitrate = 64_000,
        Profile = OpusEncodingProfile.Voice,
        UseVariableBitrate = true,
        Complexity = 8
    };
    options.Tags["TITLE"] = "Field recording";
    options.Tags["ARTIST"] = "Me";

    using (var writer = new OpusFileWriter("memo.opus", sampleRate: 48000,
                                           channels: 1, options))
    {
        float[] mono = GenerateSamples();          // interleaved, in [-1, 1]
        writer.Write(mono, 0, mono.Length);
    }
    // Dispose (here, the end of the `using`) is what finishes the file.

Transcode a .wav to .opus, streaming (any input rate; the writer resamples):

    using CodeBrix.Audio.Opus;
    using CodeBrix.Audio.Wave;

    using var source = new AudioFileReader("input.wav");   // 32-bit float
    using var writer = new OpusFileWriter("output.opus",
        source.WaveFormat.SampleRate, source.WaveFormat.Channels);

    var buffer = new float[source.WaveFormat.SampleRate * source.WaveFormat.Channels];
    int n;
    while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
    {
        writer.Write(buffer, 0, n);
    }

Write to a stream you own (the writer will NOT close it):

    using var output = new MemoryStream();
    using (var writer = new OpusFileWriter(output, 48000, 2))
    {
        writer.Write(interleavedStereo);
    }
    var opusBytes = output.ToArray();   // `output` is still open here

Drive your own engine rather than the shared output:

    using CodeBrix.Audio.Codecs;
    using CodeBrix.Audio.Opus;

    ManagedCodecs.RegisterAll(engine);   // CodeBrix.Audio's managed Vorbis + FLAC
    CodeBrixAudioOpus.Register(engine);  // ...and Opus


MINIMUM VIABLE PROJECT
======================
Console application that plays a .opus file to the end. Two files.

OpusDemo.csproj:

    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>disable</Nullable>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="CodeBrix.Audio.Opus.BsdLicenseForever" />
      </ItemGroup>
    </Project>

(CodeBrix.Audio.MitLicenseForever arrives transitively; reference it explicitly
only if you want to pin it. Version attributes are omitted here on purpose -
add the current version, or use central package management.)

Program.cs:

    using System;
    using System.Threading;
    using CodeBrix.Audio.Opus;
    using CodeBrix.Audio.Playback;

    CodeBrixAudioOpus.Register();

    var finished = new ManualResetEventSlim(false);

    using var media = new AudioFilePlayer();
    media.PlaybackEnded += (s, e) => finished.Set();
    media.Load(args[0]);                       // path to a .opus file

    Console.WriteLine($"Playing {media.Duration}");
    media.Play();
    finished.Wait();

Run it with:  dotnet run -- clip.opus


PERFORMANCE TIPS
================
  - REGISTER ONCE, AT START-UP. Register() takes a lock and is cheap after the
    first call, but calling it per file is pointless work in a hot path.

  - PREFER SoundEffectClip FOR SHORT, REPEATED SOUNDS. It decodes the .opus once
    into memory and then plays it as often as you like, including overlapping
    itself. Decoding Opus on every trigger is far more expensive than the
    playback itself. AudioFilePlayer is the right choice for long tracks, where
    streaming from disk is what you want.

  - Complexity trades CPU for quality at a fixed bitrate. The default of 10 is
    right for offline encoding, where the encode is not racing anything. Drop it
    for real-time encoding on a slow device; the bitrate, not the complexity, is
    what dominates the result.

  - Leave UseVariableBitrate on. Constant bitrate spends the same bits on
    silence as on a chorus.

  - Use the Voice profile for speech at low bitrates: 24-32 kbps is ample for
    speech in that profile, where the Music profile would not stay clear.

  - Read in reasonably sized blocks. Every Read() allocates a float array sized
    to the request, so thousands of tiny reads produce thousands of small
    allocations - a buffer of a few thousand frames costs nothing and avoids
    them.

  - SEEKING COSTS DECODING. Setting Position seeks the underlying Ogg stream by
    granule position; it is not free, and a scrubber that sets Position on every
    pointer-move event will do real work per event. Throttle it.

  - Decoding is managed code with no native fast path, so it costs more CPU per
    second of audio than the engine's native decoder does for WAV or MP3. For a
    handful of streams this is irrelevant; for dozens of simultaneous ones,
    preload with SoundEffectClip instead.


COMMON PITFALLS TO AVOID
========================
  - THE 48 kHz RULE. An Opus stream ALWAYS decodes at 48 kHz. The sample rate in
    an Opus header is the rate the ENCODER was given - 16000 for a typical
    messenger voice note, and permitted to be 0 - and RFC 7845 marks it
    informational. It is surfaced as OpusFileReader.EncoderInputSampleRate and
    must NEVER be used to convert anything. Build your pipeline from
    reader.WaveFormat.SampleRate (always 48000), not from that property; treat
    a 16 kHz voice note as 16 kHz and it plays three times too slow.

  - THE PRE-SKIP. An Ogg Opus granule position counts 48 kHz samples INCLUDING
    the encoder's priming samples, which are not audio anyone should hear. So
    audible length = final granule - pre-skip, and the first samples decoded are
    discarded. Get this wrong and every file reads a few milliseconds long and
    starts early, with a click. Both the reader and the writer handle it - which
    is why you should take Length and TotalTime from the reader rather than
    computing them from the file yourself.

  - DISPOSE THE WRITER. OpusFileWriter only produces a complete, correctly
    described file on Dispose(): the final partial frame is padded and flushed
    there, and the closing page records the true sample count so a decoder trims
    that padding rather than playing it. Same rule, same reason, as
    WaveFileWriter. An undisposed writer leaves a file that is missing its tail
    and misreports its length.

  - STREAM OWNERSHIP IS NOT SYMMETRIC BETWEEN THE TWO CONSTRUCTORS. The file-name
    overload owns the file; the Stream overload does not. Dispose the stream you
    opened, and do not assume disposing the reader closed it.

  - THE OGG FORMAT-ID SHARING RULE. CodeBrix.Audio's metadata layer stamps EVERY
    Ogg stream with the format identifier "ogg", whatever codec is inside. So
    OpusCodecFactory is offered Vorbis and Ogg FLAC streams, and VorbisCodecFactory
    is offered Opus streams. Each sniffs with OggCodecSniffer and returns NULL for
    anything else - that is what lets them coexist. It also means the factory must
    reset the stream position on entry: the engine does not rewind between
    factories on the format-id path. If you write another Ogg-carried codec's
    factory, it must obey both halves of that rule.

  - ENCODING IS SELECTED BY THE "opus" FORMAT ID, not "ogg". An encoder cannot
    sniff what it has not written yet, and "ogg" would not say which codec was
    meant. Nothing competes for "opus" - the engine's native factory declines
    every encode except "wav".

  - MONO AND STEREO ONLY. Channel mapping family 0. A family-1 (multichannel)
    stream is declined with a message saying so rather than mis-mapped.

  - Register() is idempotent, and holds ONE factory instance on purpose.
    SharedAudioOutput.RegisterCodecFactory de-duplicates on the instance, so
    handing it a freshly constructed factory each call would register the codec
    repeatedly. If you register OpusCodecFactory by hand, hold your own single
    instance for the same reason.

  - Register(AudioEngine) IS NOT Register(). The overload touches only the engine
    you pass and does NOT register the ".opus" file extension with
    AudioFileReaderRegistry, so AudioFileReader still will not open a .opus after
    it. Call the parameterless Register() for the shared-output path.

  - IN THE GAMEENGINE, REGISTER BEFORE THE FIRST LOAD, NOT BEFORE THE FIRST
    PLAY. The engine resolves an audio extension when an asset is LOADED, so
    CodeBrixAudioOpus.Register() has to run ahead of every .opus load - which
    includes any audio an AssetsFile brings in at start-up, before a line of
    game code runs. Get the order wrong and the load throws
    NotSupportedException. That message names this package and this call by
    name, because a licence-driven packaging split earns a better error than
    "format not supported".

  - WaveOutEvent DOES NOT RESAMPLE. Because Opus decodes at 48 kHz and the shared
    output adopts the rate of the first sound played, mixing a 44.1 kHz WAV and a
    .opus through WaveOutEvent will see one of them rejected by Init. Call
    SharedAudioOutput.Configure(48000) once at start-up, or use AudioFilePlayer /
    SoundEffectClip, which both convert.

  - Position and Length are BYTES, not samples and not seconds. Divide by
    Channels * sizeof(float) for frames, or use TotalTime.


WHAT THIS PACKAGE DOES NOT DO
=============================
  - It does not decode or encode multichannel Opus (channel mapping family 1),
    or the Opus-in-Matroska / Opus-in-WebM / raw-packet-over-RTP containers.
    Ogg Opus files only. The multistream codec IS vendored, so adding family 1
    later would be an addition rather than a rewrite.

  - It does not P/Invoke libopus, ship native binaries, or provide a native
    path. If you need libopus itself, this is not that package.

  - It does not play audio. Playback, devices, mixing, transports and recording
    all belong to CodeBrix.Audio and its bundled engine; this package only makes
    .opus one of the formats those can handle.

  - It does not resample on the way OUT. The reader hands back 48 kHz because
    that is what Opus decodes to. If you need another rate, convert it yourself
    or let AudioFilePlayer / SoundEffectClip do it.

  - It does not expose the codec's streaming controls - forward error
    correction, packet-loss percentage, discontinuous transmission, bandwidth
    ceilings, frame duration. Those are for live streams, not for files.

  - It does not edit tags in place. Tags are read from a stream and written when
    a stream is created; there is no re-tag-this-file operation.

  - It registers nothing on its own. Without your CodeBrixAudioOpus.Register()
    call, taking this dependency changes nothing about how your application
    behaves.


WORKING EXAMPLES ON GITHUB
==========================
The test suite is the executable documentation for everything above.

  https://github.com/ellisnet/CodeBrix.Audio.Opus/tree/main/tests/CodeBrix.Audio.Opus.Tests

  CodeBrixAudioOpusTests.cs   Register() idempotence; that .opus becomes
                              openable by file name and through
                              AudioFileReaderRegistry afterwards; and that
                              Register(AudioEngine) rejects a null engine.
  OpusFileReaderTests.cs      The 48 kHz rule against the 16 kHz-sourced fixture
                              (declared rate reported separately, never as the
                              decode rate); pre-skip-excluded Length and audible
                              sample count; Position seeking verified from the
                              audio itself using the sweep fixture; Tags and
                              EncoderVendor; caller-keeps-the-stream ownership;
                              and clean failure on a truncated or non-Opus
                              stream.
  OpusFileWriterTests.cs      Round trips read back as the audio that went in,
                              mono and stereo; the header records the DECLARED
                              input rate rather than the encode rate; another
                              input rate is resampled and keeps its duration;
                              tags survive; the Voice profile at a speech
                              bitrate; writing in several calls matches writing
                              in one; out-of-range options rejected;
                              multichannel declined; stream ownership; and
                              ffmpeg decoding what this library wrote.
  OpusCodecFactoryTests.cs    FactoryId / Priority / SupportedFormatIds against
                              the family convention; the Ogg format-id sharing
                              rule (a Vorbis .ogg must be DECLINED, an Opus
                              stream offered as either "ogg" or "opus" must be
                              accepted); rewinding a stream an earlier factory
                              left mid-way; format detection without being told;
                              encoder created for "opus" and NOT for "ogg"; and
                              a factory-built decoder decoding real audio.
  OpusPlaybackTests.cs        The device paths - AudioFilePlayer, SoundEffectClip,
                              seeking during playback, and a voice-note-shaped
                              file playing at the right pitch and speed. Opt-in;
                              see the note below.
  AudioAssertions.cs          How lossy output is compared: tolerance-based,
                              against a second implementation's decode.
  TestAssets.cs, TestAudio.cs, AudibleTestScope.cs   Fixture plumbing.

The fixtures those tests read are described at
https://github.com/ellisnet/CodeBrix.Audio.Opus/blob/main/tests/Assets/audio/AUDIO-FIXTURES.txt

Tests that open a real audio device and make sound are opt-in, so a normal run
is silent and headless-safe. Set CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1 to
include them - the same switch CodeBrix.Audio's own sounding tests use.


QUICK REFERENCE CARD
====================
  PACKAGE   CodeBrix.Audio.Opus.BsdLicenseForever   (BSD-3-Clause)
  NAMESPACE using CodeBrix.Audio.Opus;
  TURN ON   CodeBrixAudioOpus.Register();           // once, at start-up

  DECODE                                   ENCODE
  ------                                   ------
  new OpusFileReader(path)                 new OpusFileWriter(path, rate, ch)
  new OpusFileReader(stream)               new OpusFileWriter(stream, rate, ch)
  .Read(byte[], int, int) / .Read(Span)    .Write(ReadOnlySpan<float>)
  .WaveFormat  -> always 48 kHz float      .Write(float[], int, int)
  .Length / .Position  (BYTES)             .Finish()   (Dispose calls it)
  .TotalTime   (pre-skip excluded)         .PreSkip
  .Tags / .EncoderVendor                   options.Tags["TITLE"] = "..."
  .EncoderInputSampleRate  (INFO ONLY)     Dispose() OR THE FILE IS INCOMPLETE
  .PreSkip

  OPTIONS   new OpusFileWriterOptions { Bitrate = 96_000,
                                        Profile = OpusEncodingProfile.Music,
                                        UseVariableBitrate = true,
                                        Complexity = 10 }
            Bitrate 500..512000, Complexity 0..10, Profile Music | Voice.

  PLAY      AudioFilePlayer.Load(".opus")       long tracks, transport, seek
            SoundEffectClip.Load(".opus")       short, overlapping, decode-once
            new WaveOutEvent().Init(reader)     needs SharedAudioOutput
                                                .Configure(48000) first
  RECORD    new Recorder(device, stream, "opus")

  SIX PUBLIC TYPES
    CodeBrixAudioOpus      Register() / Register(AudioEngine) / IsRegistered
    OpusFileReader         WaveStream, 48 kHz 32-bit float
    OpusFileWriter         IDisposable; DISPOSE IT
    OpusFileWriterOptions  init-only; Validate()
    OpusEncodingProfile    Music | Voice
    OpusCodecFactory       ICodecFactory; FactoryId
                           "CodeBrix.Audio.Opus.ManagedOpus", Priority -10,
                           SupportedFormatIds ["ogg", "opus"]

  THREE RULES YOU WILL OTHERWISE BREAK
    1. 48 kHz always. EncoderInputSampleRate is informational; never convert by it.
    2. Dispose the writer, or the file is truncated and mislabelled.
    3. Mono and stereo only; family-1 multichannel is declined, not mis-mapped.
================================================================================
