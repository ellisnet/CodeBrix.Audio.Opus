================================================================================
AGENT-README: CodeBrix.Audio.Opus
A Comprehensive Guide for AI Coding Agents
================================================================================

OVERVIEW
--------------------------------------------------------------------------------
CodeBrix.Audio.Opus adds Ogg Opus (.opus) decoding and encoding to CodeBrix.Audio.
It is a pure managed library with NO native code on any platform: no P/Invoke, no
runtimes/ folder, nothing to rebuild when a platform is added. It works
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


WHY THIS IS A SEPARATE PACKAGE (read before proposing a merge)
--------------------------------------------------------------------------------
CodeBrix.Audio holds a deliberate licence bar of MIT or more permissive, and its
package id - CodeBrix.Audio.MitLicenseForever - says so out loud. Opus cannot
clear that bar. Every managed Opus implementation is a port of libopus, and
libopus is BSD-3-Clause, whose third clause adds a no-endorsement condition that
MIT does not have. So the codec lives here, in a BSD-3-Clause package, and
CodeBrix.Audio stays what it claims to be.

Do NOT propose folding this into CodeBrix.Audio. That decision is recorded in
CodeBrix.Audio's own AGENT-README as the standing precedent for the family.


INSTALLATION
--------------------------------------------------------------------------------
NuGet package:   CodeBrix.Audio.Opus.BsdLicenseForever
Command:         dotnet add package CodeBrix.Audio.Opus.BsdLicenseForever

Note that the PACKAGE id carries the ".BsdLicenseForever" suffix, but the
NAMESPACE is simply "CodeBrix.Audio.Opus" (no suffix).

Depends on:      CodeBrix.Audio.MitLicenseForever
Target framework: .NET 10.0 or higher.


KEY NAMESPACE
--------------------------------------------------------------------------------
  using CodeBrix.Audio.Opus;   // everything a consumer needs

Sub-namespaces are implementation detail and entirely internal:
  CodeBrix.Audio.Opus.Codec   the vendored Opus codec
  CodeBrix.Audio.Opus.Ogg     the Ogg container layer
  CodeBrix.Audio.Opus.Codecs  the engine-facing decoder / encoder / factory


CORE API REFERENCE
--------------------------------------------------------------------------------
  CodeBrixAudioOpus       Register()             - the one call; idempotent and
                                                   thread-safe
                          Register(AudioEngine)  - for a consumer driving its own
                                                   engine rather than the shared
                                                   output
                          IsRegistered

  OpusFileReader          A WaveStream of 48 kHz 32-bit float, the peer of
                          OggVorbisFileReader. Construct over a file name (the
                          reader owns the file) or a Stream (the CALLER owns it -
                          that is the reader-registry contract).
                          .WaveFormat .Length .Position .TotalTime
                          .EncoderInputSampleRate .PreSkip .Tags .EncoderVendor

  OpusFileWriter          Writes .opus, the peer of WaveFileWriter. Takes any
                          input sample rate and resamples to 48 kHz.
                          Write(ReadOnlySpan<float>) / Write(float[], int, int)
                          Finish() - also called by Dispose()
                          .PreSkip

  OpusFileWriterOptions   Bitrate (default 96 kbps), Profile (Music | Voice),
                          UseVariableBitrate (default true), Complexity (0-10,
                          default 10), Tags. init-only, with Validate().

  OpusEncodingProfile     Music | Voice

  OpusCodecFactory        The ICodecFactory the engine talks to. Public so a
                          consumer can register it by hand, but Register() is the
                          friendly path.
                          FactoryId "CodeBrix.Audio.Opus.ManagedOpus", Priority -10,
                          SupportedFormatIds ["ogg", "opus"]

Error model: an unusable stream throws InvalidDataException with a message that
names the problem. Readers and writers are IDisposable; dispose them.


COMMON PITFALLS
--------------------------------------------------------------------------------
  - THE 48 kHz RULE. An Opus stream ALWAYS decodes at 48 kHz. The sample rate in
    an Opus header is the rate the ENCODER was given - 16000 for a typical
    WhatsApp / Telegram voice note, and permitted to be 0 - and RFC 7845 marks it
    informational. It is surfaced as OpusFileReader.EncoderInputSampleRate and
    must NEVER be used to convert anything. Inside OpusSoundDecoder the base
    class is told the source rate is 48000, unconditionally; pass the declared
    rate there instead and a 16 kHz voice note plays three times too slow.

  - THE PRE-SKIP. An Ogg Opus granule position counts 48 kHz samples INCLUDING
    the encoder's priming samples, which are not audio anyone should hear. So
    audible length = final granule - pre-skip, and the first samples decoded are
    discarded. Get this wrong and every file reads a few milliseconds long and
    starts early, with a click. Both the reader and the writer handle it, and
    tests pin both directions.

  - DISPOSE THE WRITER. OpusFileWriter only produces a complete, correctly
    described file on Dispose(): the final partial frame is padded and flushed
    there, and the closing page records the true sample count so a decoder trims
    that padding rather than playing it. Same rule, same reason, as
    WaveFileWriter.

  - THE OGG FORMAT-ID SHARING RULE. CodeBrix.Audio's metadata layer stamps EVERY
    Ogg stream with the format identifier "ogg", whatever codec is inside. So
    OpusCodecFactory is offered Vorbis and Ogg FLAC streams, and VorbisCodecFactory
    is offered Opus streams. Each sniffs with OggCodecSniffer and returns NULL for
    anything else - that is what lets them coexist. It also means the factory must
    reset the stream position on entry: the engine does not rewind between
    factories on the format-id path.

  - ENCODING IS SELECTED BY THE "opus" FORMAT ID, not "ogg". An encoder cannot
    sniff what it has not written yet, and "ogg" would not say which codec was
    meant. Nothing competes for "opus" - the engine's native factory declines
    every encode except "wav".

  - MONO AND STEREO ONLY. Channel mapping family 0. A family-1 (multichannel)
    stream is declined with a message saying so rather than mis-mapped. The
    multistream codec IS vendored, so adding family 1 later is an addition, not a
    rewrite.

  - Register() is idempotent, and holds ONE factory instance on purpose.
    SharedAudioOutput.RegisterCodecFactory de-duplicates on the instance, so
    handing it a freshly constructed factory each call would register the codec
    repeatedly.

  - IN THE GAMEENGINE, REGISTER BEFORE THE FIRST LOAD, NOT BEFORE THE FIRST
    PLAY. The engine resolves an audio extension when an asset is LOADED, so
    CodeBrixAudioOpus.Register() has to run ahead of every .opus load - which
    includes any audio an AssetsFile brings in at start-up, before a line of
    game code runs. Get the order wrong and the load throws
    NotSupportedException. That message names this package and this call by
    name, because a licence-driven packaging split earns a better error than
    "format not supported".


CODING CONVENTIONS (CodeBrix family)
--------------------------------------------------------------------------------
Nothing from here to the end of the file is needed to CONSUME the package.

  - Target framework net10.0 only; no multi-targeting.
  - Nullable reference types are OFF. Do NOT add `?` to reference types and do
    NOT use the null-forgiveness `!` operator. Value-type nullables are fine.
  - No global usings; no ImplicitUsings. Usings are explicit, at the top of the
    file, System.* first.
  - File-scoped namespaces only.
  - <GenerateDocumentationFile> is ON; every public/protected member carries an
    XML doc comment. Never suppress CS1591 - fix it at source.
  - No project-level warning suppression. There is no <NoWarn> in any csproj
    here and none should be added. The single scoped exception lives in
    .editorconfig and is explained there: CS1573 is disabled for the VENDORED
    Codec/ directory only, because those 118 warnings are upstream's, on internal
    members, and neither fixable nor deletable honestly. Code written in this
    repository is not covered by it.
  - Vendored files carry a `//was previously: <upstream ns>;` provenance comment
    on the namespace line and keep their upstream licence headers verbatim.
  - Tests use xUnit v3 + SilverAssertions.


ARCHITECTURE
--------------------------------------------------------------------------------
src/CodeBrix.Audio.Opus/
  CodeBrixAudioOpus.cs        the Register() entry point
  OpusFileReader.cs           public WaveStream
  OpusFileWriter.cs           public writer
  OpusFileWriterOptions.cs    public options
  OpusEncodingProfile.cs      public enum
  Codecs/                     engine-facing: OpusSoundDecoder (: ManagedSoundDecoder),
                              OpusSoundEncoder (: ISoundEncoder), OpusCodecFactory
  Ogg/                        the Ogg container layer - WRITTEN HERE, from the
                              specifications, not vendored. OggCrc, OggPage,
                              OggPacket, OggPageReader, OggPageWriter, OpusHead,
                              OpusTags, OggOpusReader, OggOpusWriter
  Codec/                      the VENDORED Opus codec (see below)

The Ogg layer is written from RFC 3533 (the Ogg container) and RFC 7845 (Ogg
Opus). It is not a fork of NVorbis, Concentus.Oggfile or anything else, and it
deliberately does NOT reach into CodeBrix.Audio's own Ogg layer, which is
internal to that assembly. Writing it kept ~3,900 lines of second-hand container
machinery out of this repository and let the granule-position seek be designed in
rather than bolted onto a packet-granularity API.


THE VENDORED CODEC, AND HOW TO RE-PORT IT
--------------------------------------------------------------------------------
src/CodeBrix.Audio.Opus/Codec/ is 133 .cs files ported from Concentus 2.2.2
(github.com/lostromb/concentus, commit 3885c4e4, BSD-3-Clause). Everything the
licence requires is in THIRD-PARTY-NOTICES.txt: the full licence text, all
ELEVEN copyright holders, what was vendored, what was omitted and what changed.

This is a ONE-AND-DONE vendoring. This package does not track upstream and will
not be re-synced with it, which is the opposite of how CodeBrix.Audio.Engine
treats SoundFlow. There is no re-vendor checklist here, and the vendored code may
be edited in place.

The port is reproducible. It was produced by ~/ClaudeHome/port_concentus.py:

    port_concentus.py <upstream>/CSharp/Concentus src/CodeBrix.Audio.Opus/Codec \
        --ns Concentus=CodeBrix.Audio.Opus.Codec \
        --skip AssemblyInfo.cs --skip OpusCodecFactory.cs \
        --skip-dir Native --drop-using Concentus.Native

What that does, and why:
  - Renames namespaces and adds the provenance comments. The upstream project
    name is NOT in the live namespace - family de-branding rule: an upstream's
    name belongs in comments, provenance markers and licence text.
  - Converts block-scoped namespaces to file-scoped.
  - Demotes top-level public types to internal. This is why the package has no
    CS1591 obligation over 133 vendored files, and why no vendored type can leak
    into the public API.
  - Strips conditional-compilation branches dead on .NET 10, keeping PARITY
    defined (upstream's csproj defines it in both configurations, and those
    branches are the ones that stay bit-exact with the C reference).
  - Removes [Obsolete] attributes, which all redirected to the native-probing
    factory that is not vendored here.
  - Rewrites `span != null` to `!span.IsEmpty` in SpeexResampler (CA2265).

Native code is EXCLUDED on purpose: Concentus/Native/ and OpusCodecFactory.cs are
skipped, so nothing here P/Invokes libopus and no binaries ship. Do not add them.


TESTING
--------------------------------------------------------------------------------
Tests live under tests/CodeBrix.Audio.Opus.Tests/ and use xUnit v3 with
SilverAssertions. Run them with:

    dotnet test CodeBrix.Audio.Opus.slnx

Fixtures under tests/Assets/audio/ are synthesized locally by
tools/make_test_fixtures/make_fixtures.sh - tones encoded with ffmpeg, never
third-party audio. tests/Assets/audio/AUDIO-FIXTURES.txt says what each is for.
One fixture is deliberately encoded FROM 16 kHz so its declared rate and its
decode rate disagree; that is the one that catches the 48 kHz rule above, and
nothing else in the set would.

Note that .opus fixtures never regenerate byte-identically: an Ogg muxer assigns
a random stream serial number per run. Do not write a test or a build gate that
assumes otherwise.

The encoder is held to account by ffmpeg rather than by symmetry: a round trip
through this library alone would pass even if both halves shared a bug, so the
tests also decode what this library WROTE using ffmpeg, and compare.
================================================================================
