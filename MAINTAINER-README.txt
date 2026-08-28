================================================================================
MAINTAINER-README: CodeBrix.Audio.Opus
Notes for people and agents MAINTAINING this repository - not for package
consumers
================================================================================

If you are CONSUMING the NuGet package, read AGENT-README.txt instead. Nothing
in this file is needed to use the package.


PURPOSE AND SCOPE
=================
This repository produces exactly one NuGet package:

  CodeBrix.Audio.Opus.BsdLicenseForever
      Assembly:      CodeBrix.Audio.Opus
      Project:       src/CodeBrix.Audio.Opus/CodeBrix.Audio.Opus.csproj
      License:       BSD-3-Clause
      Consumer doc:  AGENT-README.txt (repo root)

It adds Ogg Opus decoding and encoding to CodeBrix.Audio, which it takes a
package dependency on. There is no second package and no native payload.

WHY THE PACKAGE IS SEPARATE, AND WHY IT STAYS SEPARATE. CodeBrix.Audio holds a
licence bar of MIT or more permissive, and its package id -
CodeBrix.Audio.MitLicenseForever - says so out loud. Every managed Opus
implementation is a port of libopus, and libopus is BSD-3-Clause, whose third
clause adds a no-endorsement condition MIT does not have. So the codec lives
here rather than there. Do not propose folding it back in; this split is the
standing precedent for the whole family, and the licence-bar rule it enforces is
recorded in CodeBrix.Audio's own MAINTAINER-README.txt.


REPOSITORY LAYOUT
=================
  src/CodeBrix.Audio.Opus/
      CodeBrixAudioOpus.cs        the Register() entry point
      OpusFileReader.cs           public WaveStream
      OpusFileWriter.cs           public writer
      OpusFileWriterOptions.cs    public options
      OpusEncodingProfile.cs      public enum
      InternalsVisibleTo.cs       opens internals to the .Tests assembly
      Codecs/                     engine-facing: OpusSoundDecoder (derives from
                                  ManagedSoundDecoder), OpusSoundEncoder
                                  (implements ISoundEncoder), OpusCodecFactory
      Ogg/                        the Ogg container layer - WRITTEN HERE, from
                                  the specifications, not vendored. OggCrc,
                                  OggPage, OggPacket, OggPageReader,
                                  OggPageWriter, OpusHead, OpusTags,
                                  OggOpusReader, OggOpusWriter,
                                  OggOpusWriterSettings
      Codec/                      the VENDORED Opus codec (see PROVENANCE)
  tests/CodeBrix.Audio.Opus.Tests/  the xUnit v3 test project
  tests/Assets/audio/               generated fixtures (see EXTRAS-README.txt)
  tools/make_test_fixtures/         the fixture generator (see EXTRAS-README.txt)
  CodeBrix.Audio.Opus.slnx          the solution
  global.json                       pins the test runner to
                                    Microsoft.Testing.Platform
  .editorconfig                     carries the one scoped warning suppression
  THIRD-PARTY-NOTICES.txt           the authoritative provenance record

The Ogg layer is written from RFC 3533 (the Ogg container) and RFC 7845 (Ogg
Opus). It is not a fork of NVorbis, of the upstream Opus project's Ogg helper,
or of anything else, and it deliberately does NOT reach into CodeBrix.Audio's
own Ogg layer, which is internal to that assembly. Writing it kept ~3,900 lines
of second-hand container machinery out of this repository and let the
granule-position seek be designed in rather than bolted onto a
packet-granularity API.


BUILDING
========
    dotnet build CodeBrix.Audio.Opus.slnx

There is nothing native to build, nothing to download and no generation step.
GeneratePackageOnBuild is ON in the library csproj, so an ordinary build also
produces a .nupkg (see PACKAGING AND PUBLISHING for what that implies).


TESTING
=======
Tests live under tests/CodeBrix.Audio.Opus.Tests/ and use xUnit v3 with
SilverAssertions. Run them with:

    dotnet test CodeBrix.Audio.Opus.slnx

global.json pins the runner to Microsoft.Testing.Platform, which is what
xunit.v3 4.x expects.

Tests that open a real audio device and make sound are OPT-IN, so a normal run
is silent and headless-safe:

    CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1 dotnet test CodeBrix.Audio.Opus.slnx

That is deliberately the SAME switch CodeBrix.Audio's own sounding tests use, so
one environment variable governs the whole family.

Fixtures under tests/Assets/audio/ are synthesized locally - tones and sweeps
encoded with ffmpeg, never third-party audio - and are copied next to the test
assembly by a Content item in the test csproj. tests/Assets/audio/
AUDIO-FIXTURES.txt says what each one is for. One fixture is deliberately
encoded FROM 16 kHz so its declared rate and its decode rate disagree; that is
the one that catches the 48 kHz rule, and nothing else in the set would.

Note that .opus fixtures never regenerate byte-identically: an Ogg muxer assigns
a random stream serial number per run. Do not write a test or a build gate that
assumes otherwise.

The encoder is held to account by ffmpeg rather than by symmetry: a round trip
through this library alone would pass even if both halves shared a bug, so the
tests also decode what this library WROTE using ffmpeg, and compare. Opus is
lossy, so those comparisons are tolerance-based (AudioAssertions.cs) rather than
byte-for-byte.


PACKAGING AND PUBLISHING
========================
  PackageId              CodeBrix.Audio.Opus.BsdLicenseForever
  License expression     BSD-3-Clause, with
                         PackageRequireLicenseAcceptance set
  GeneratePackageOnBuild true - every build writes a fresh .nupkg
  Dependency             CodeBrix.Audio.MitLicenseForever, pinned by a
                         PackageReference version in the library csproj. That
                         pin is the ONE place a version number belongs; it is
                         never written into AGENT-README.txt.
  Packed alongside the assembly:
      icon-codebrix-128.png     the package icon
      README.md                 the nuget.org / GitHub landing page
      AGENT-README.txt          the consumer guide - this is the file that
                                ships to consumers, so keep it consumer-only
      THIRD-PARTY-NOTICES.txt   required by the vendored codec's licence

  MAINTAINER-README.txt, EXTRAS-README.txt and README-INDEX.txt are NOT packed.
  They exist for this repository only.

VERSIONING. The csproj computes the version from the UTC clock at build time:
1.<years since 2026>.<day of year>.<minute of day>. It is strictly increasing
over time and is NOT SemVer - major is pinned to 1 and minor encodes the year,
so major and minor say nothing about API compatibility. Two builds in the same
UTC minute produce the SAME version, so never publish two packages from within
one minute. The full rationale is in a comment block in the csproj; re-baseline
by changing _VersionBaseYear there.

Publishing follows the family rule: tag the repository at the version that was
published, so the latest git tag and the latest nuget.org version agree.


PROVENANCE AND VENDORED SOURCES
===============================
src/CodeBrix.Audio.Opus/Codec/ is 133 .cs files ported from Concentus 2.2.2
(github.com/lostromb/concentus, commit 3885c4e4, BSD-3-Clause). Everything the
licence requires is in THIRD-PARTY-NOTICES.txt: the full licence text, all
ELEVEN copyright holders, what was vendored, what was omitted and what changed.

This is a ONE-AND-DONE vendoring. This package does not track upstream and will
not be re-synced with it - the same policy CodeBrix.Audio applies to its
CodeBrix.Audio.Engine source. There is no re-vendor checklist here, and the
vendored code may be edited in place.

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

Native code is EXCLUDED on purpose: the upstream Native/ directory and its
native-probing OpusCodecFactory.cs are skipped, so nothing here P/Invokes
libopus and no binaries ship. Do not add them - the package's promise of "no
native code on any platform, including linux-riscv64" is why it works where a
native Opus binding would not.

The Ogg layer is NOT vendored; it was written here from RFC 3533 and RFC 7845.
Treat it as first-party code.


CODING CONVENTIONS
==================
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
    Codec/ directory only, because those 118 warnings are upstream's, on
    internal members, and neither fixable nor deletable honestly. Code written
    in this repository - Ogg/, Codecs/ and the public API at the root - is not
    covered by it and builds warning-free on its own merits.
  - Vendored files carry a `//was previously: <upstream ns>;` provenance comment
    on the namespace line and keep their upstream licence headers verbatim.
  - Tests use xUnit v3 + SilverAssertions, are named <Class>Tests.cs, use
    snake_case method names, and mark //Arrange //Act //Assert in the body.


NOTES
=====
  - THE 48 kHz RULE IS THE ONE THAT BREAKS THINGS. An Opus stream always decodes
    at 48 kHz; the rate in the header is the rate the ENCODER was given and is
    informational per RFC 7845. Inside OpusSoundDecoder the base class is told
    the source rate is 48000, UNCONDITIONALLY. Pass the declared rate there
    instead and a 16 kHz voice note plays three times too slow, with nothing in
    the logs to say why. The 16 kHz-sourced fixture exists to catch exactly that
    regression.

  - THE PRE-SKIP IS THE SECOND ONE. Granule positions count priming samples that
    the decoder discards, so audible length = final granule - pre-skip. Both the
    reader and the writer handle it, and tests pin both directions.

  - Register() holds ONE OpusCodecFactory instance in a static field on purpose.
    SharedAudioOutput.RegisterCodecFactory de-duplicates on the INSTANCE, so
    constructing a fresh factory per call would register the codec repeatedly.
    Do not "simplify" that field away.

  - There is deliberately no [ModuleInitializer] doing the registration. A
    module initializer only runs once something in the assembly is touched,
    which trimming and lazy assembly loading make unreliable: the package would
    work in a debug build and silently fail to register in a trimmed publish.
    An explicit call is the contract, and the GameEngine's NotSupportedException
    names this package and that call so a missing one diagnoses itself.

  - Multichannel (channel mapping family 1) is declined rather than mis-mapped.
    The multistream codec IS vendored, so adding family 1 later is an addition,
    not a rewrite.
================================================================================
