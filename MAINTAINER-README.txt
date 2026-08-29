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
                                  (implements ISoundEncoder), OpusCodecFactory,
                                  and the packet seam - OpusPacketSoundDecoder
                                  (implements IPacketSoundDecoder) with
                                  OpusPacketCodecFactory
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


THE PACKET SEAM
===============
CodeBrix.Audio has a second decoding seam beside the stream one:
IPacketCodecFactory / IPacketSoundDecoder, for audio a demultiplexer lifted out
of a media container as bare codec packets. This repository implements it for
Opus in Codecs/, as a sibling of the stream implementation, and
CodeBrixAudioOpus.Register() registers both. Consumer documentation is in
AGENT-README.txt.

WHERE THE DECODER SITS. OpusPacketSoundDecoder wraps the SAME internal
OpusDecoder the Ogg reader uses, constructed the same way -
new OpusDecoder(48000, head.ChannelCount) - and calls the same
Decode(ReadOnlySpan<byte>, Span<float>, frame_size, decode_fec: false). Nothing
in Codec/ was touched, and nothing in Ogg/ is on the packet path except
OpusHead, whose TryParse is the whole header path because a container's
CodecPrivate IS the identification header. That is why the round-trip test can
demand SAMPLE-EXACT agreement with OggOpusReader rather than a tolerance: one
codec, one set of packets, two ways in.

THE UNIT OF PreSkipSamples IS FRAMES PER CHANNEL. The interface says so
("samples PER CHANNEL"), OpusHead.PreSkip is already in that unit, and the
Vorbis implementation reports 0 in it. So PreSkipSamples is head.PreSkip
verbatim, with no channel multiplication - unlike MaxSamplesPerPacket, which
counts INTERLEAVED samples and is 5760 * channels. Getting those two units the
same way round is the mistake to watch for; the tests pin both.

WHAT THE DECODER DELIBERATELY DOES NOT DO. It does not discard the pre-skip and
it does not trim the tail. The stream reader does both because it has the
granule positions that state them; a packet decoder has neither the position in
the stream nor the container's fields, so it reports the pre-skip and emits
everything the packets contain, and the caller applies both. There is no
Flush/Drain member because the interface has none - the same decision
CodeBrix.Audio recorded for Vorbis.

A CORRUPT PACKET IS WRAPPED. The vendored codec's OpusException is INTERNAL to
this assembly, so letting it escape the packet seam would hand a caller an
exception type it cannot name in a catch clause. DecodePacket wraps any decode
failure in InvalidDataException, which is what the stream reader already does
for the same failure, and a test pins it.

AN EMPTY PACKET IS PACKET LOSS. DecodePacket with a zero-length packet runs the
codec's loss concealment (Decode with empty input), over the last REAL packet's
duration or 20 ms when nothing has been decoded yet. That is a real capability
of the vendored codec rather than a stub, and a test pins that the decoder is
still usable for the next real packet afterwards.

  THE LENGTH IS REMEMBERED HERE, NOT READ OFF THE CODEC (2026-08-29). It used to
  be IOpusDecoder.LastPacketDuration, which the codec updates for CONCEALMENT as
  well as for decoding: after a 120 ms ConcealLoss it reads 5760, and the next
  empty packet would then have been taken to mean 120 ms of loss rather than one
  20 ms packet. OpusPacketSoundDecoder now keeps lastRealPacketFrames itself,
  set only by a packet that carried bytes and cleared by Reset() exactly as the
  codec clears its own. For every path that existed before ConcealLoss the two
  values are identical, so nothing observable changed; the regression test is
  OpusPacketLossTests.Concealment_does_not_redefine_what_one_lost_packet_means.

REAL LOSS CONCEALMENT - ConcealLoss AND SupportsLossConcealment (2026-08-29)
---------------------------------------------------------------------------
CodeBrix.Audio's IPacketSoundDecoder gained two DEFAULT interface members: a
SupportsLossConcealment that is false, and a ConcealLoss(lostFrames, output)
that forwards to the empty-packet convention. This decoder OVERRIDES BOTH,
because Opus has concealment in the specification and the empty-packet form
cannot say how long a gap was.

WHY THE OVERRIDE EXISTS AT ALL. The empty-packet path conceals ONE PACKET, and
guesses its length. A container that records a discontinuity knows the real
length, and a gap concealed at the wrong length slides everything after it. The
default member would have been correct-ish and quietly wrong on length; this is
correct on length.

THE CHUNKING RULE, and where the 2.5 ms comes from. Codec/Opus/Structs/
OpusDecoder.cs, opus_decode_native, refuses a PLC frame size that is not a
multiple of Fs/400 - 120 frames at 48 kHz - and opus_decode_frame clamps a
request to Fs/25*3 = 5760. So:

    room   = min(output.Length / channels, 5760)
    if room < 120                 -> return 0        (caller fills the gap)
    chunk  = (min(lostFrames, room) / 120) * 120     (round DOWN to steps)
    if chunk == 0 -> chunk = 120                     (the remainder; round UP)
    return min(chunk, lostFrames) * channels

  ROUNDING DOWN is what makes a gap that is a whole number of steps come out
  exactly, over as few calls as possible: 9600 frames (200 ms) is 5760 + 3840,
  two calls, not seven.

  THE REMAINDER IS THE ONLY INTERESTING CASE. A gap of 1000 frames is 960 and
  then 40, and there is no 40-frame concealment to ask the codec for. The last
  call runs a whole 120-frame step and RETURNS 40. That was a deliberate choice
  between two defensible answers:
      - return the 120 that were produced, and let the caller trim. The player
        does cap what comes back at the remaining gap, so this works THERE; a
        caller looping on the interface's own rule would overshoot by 80 frames.
      - return the 40 that were asked for. The loop terminates exactly, the
        timeline is exact for every caller, and the surplus sits unread past the
        returned count.
  The second was taken. What it costs is that the DECODER'S STATE advances by up
  to 2.5 ms more than was lost - unavoidable, since the codec has no shorter
  step - and that is documented on the method rather than hidden.

WHY THE CANONICAL FRAME LADDER (120/240/480/960/1920/2880/5760) IS NOT USED.
Any multiple of 120 is legal at the API, and opus_decode_native decomposes what
it is given by the last packet's own frame size, so a 3840-frame request becomes
four 960-frame PLC frames internally. Restricting the chunk to the ladder would
only add calls. Every ladder value AND several non-ladder multiples are exercised
by OpusPacketLossTests.Every_fixture_conceals_every_step_size_without_complaint,
on all three fixtures including the 16 kHz-sourced one, which is the one that
reaches the codec's speech path.

A TOO-SMALL BUFFER RETURNS 0 RATHER THAN THROWING, unlike DecodePacket, which
throws ArgumentException naming MaxSamplesPerPacket. The interface makes zero a
legitimate answer to ConcealLoss ("a caller that gets nothing back fills the gap
itself"), and a player that hit an exception here would end a track over a lost
packet. It cannot happen for a buffer sized to MaxSamplesPerPacket, and it is
documented on the method.

A CODEC FAILURE IS STILL WRAPPED AND STILL THROWN. Every length ConcealLoss asks
for is one the codec accepts, so InvalidDataException from here means a defect
in this file or in the vendored codec, not bad data. Swallowing it would make
that defect indistinguishable from a codec with no concealment - which is the
one thing SupportsLossConcealment = true promises it is not.

AFTER Reset(), CONCEALMENT IS SILENCE, AND THAT IS THE CODEC'S OWN ANSWER.
ResetState() sets prev_mode = 0 and frame_size = Fs/400, and opus_decode_frame's
"if we haven't got any packet yet, all we can do is return zeros" branch then
writes zeros and returns the size asked for. So the gap comes out the right
LENGTH with no audio in it, and nothing throws and nothing loops. Worth knowing:
the do-loop in opus_decode_native would spin for ever if that branch ever
returned 0, which it cannot, because this.frame_size is never below 120 after an
init or a reset. Do not "simplify" ResetState.

CONCEALMENT ADVANCES MEDIA TIME, and the first packet after a gap is decoded
against the state the concealment left. Measured on the 2 s sweep, two 20 ms
packets dropped at frame 19200 and concealed:

    the concealed 40 ms       0.308 RMS against 0.355 for the real audio
    first 20 ms after it      0.466 relative RMS  - plainly wrong, as expected
    from  80 ms after it on   0.0070
    from 240 ms after it on   0.000035

The tests hold 80 ms to < 0.02 and 240 ms to < 0.0005, and assert that the first
20 ms IS wrong (> 0.1) so a change that made the numbers "better" by producing
silence could not pass unnoticed. The shape is the seek pre-roll's shape, for
the same reason.

WHAT THE PLAYER DOES WITH IT, and why nothing here has to know. PacketAudioPlayer
converts an AudioPacket.Loss to frames, then asks ConcealLoss for the frames
STILL MISSING - not for a chunk size - caps what comes back at both the remaining
gap and its buffer, and writes silence of the same length if a call returns
nothing. That is why returning less than was asked for is safe, and why this
implementation never tries to cover a whole gap in one call.

TESTING THE PLAYER SEAM FROM HERE IS ONLY POSSIBLE WITH A DEVICE.
PacketAudioPlayer.PacketDecoderAdapter is internal to CodeBrix.Audio with
InternalsVisibleTo("CodeBrix.Audio.Tests") only, so this repository cannot drive
the player device-free the way CodeBrix.Audio's own loss tests do. The
end-to-end claim is therefore made by OpusPacketLossPlaybackTests, gated on
CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1, which plays a motif with two packets
dropped and asserts that PacketAudioPlayer.Position reports exactly the frame
count an unbroken decode produces. Position counts frames read from the PACKETS
(ReadSourceSamples), before any channel or rate conversion, so the device's own
rate does not enter into it - but the test still configures the shared output to
48 kHz WHEN IT IS NOT ALREADY RUNNING, because Configure throws on a running
output and another sounding test may have started it first.

THE RESET / PRE-ROLL MEASUREMENT. Reset() is OpusDecoder.ResetState(), and a
test pins that a reset decoder and a brand-new one, fed the same packets, agree
sample for sample - so the residual error after a seek belongs to the codec, not
to the reset. Measured on the 2 s sweep fixture, feeding pre-roll packets before
a mid-stream target and comparing the packet after them with an uninterrupted
decode:

     80 ms (4 packets)   0.074 relative RMS   largest sample difference 0.085
    120 ms (6 packets)   0.016
    160 ms (8 packets)   0.003
    240 ms (12 packets)  0.00002              largest difference 0.000031

80 ms is what RFC 7845 section 4.2 and Matroska's SeekPreRoll ask for, and it is
plainly NOT convergence for tonal material - the CELT post-filter is a comb
filter and a pure sweep keeps it ringing. The tests assert the 80 ms figure with
headroom and assert near-exactness at 240 ms, so a regression in either
direction shows up. Do not "tighten" the 80 ms bound to the 240 ms one.

THE OUTPUT GAIN IS APPLIED, on both seams (Jeremy, 2026-08-28). RFC 7845 section
5.1 makes the header's output gain a gain the DECODER applies, and until this
change neither path did. Both now set IOpusDecoder.Gain from
OpusHead.OutputGainQ78 once, immediately after constructing the decoder -
OggOpusReader's constructor and OpusPacketSoundDecoder's.

  * THE UNITS LINE UP EXACTLY, which is why the codec's own control is used
    rather than a multiply in managed code. The header field is signed Q7.8 dB
    (gain_dB = value / 256, RFC 7845), and the codec's decode_gain is the same
    value: opus_decode_frame scales by celt_exp2(QCONST16(6.48814081e-4, 25) *
    decode_gain), and 6.48814081e-4 is log2(10) / 20 / 256, so the factor is
    10^((value/256)/20) with the same sign convention. Verified in
    Codec/Opus/Structs/OpusDecoder.cs before relying on it.
  * ONE IMPLEMENTATION, ONE ROUNDING. Applying it inside the codec is what keeps
    the two seams sample-IDENTICAL at a non-zero gain - measured 0.00000000
    largest difference, and a test asserts exact equality. Two managed
    multiplies would have been two roundings.
  * IT SURVIVES ResetState(). decode_gain sits above OPUS_DECODER_RESET_START,
    so PartialReset() leaves it alone and a seek does not silently restore full
    volume. Nothing here re-applies it, so two tests guard that - one per path.
  * IT SATURATES rather than wrapping, at the codec's int16 intermediate. A
    boost that asks for more than full scale clips - the reference behaviour.
  * THIS IS A DELIBERATE BEHAVIOUR CHANGE. A file with a non-zero output gain
    now decodes at a different level than it did in earlier releases -
    correctly, per the specification. Nothing in practice writes a non-zero gain
    (every committed fixture stores 0, and so does everything ffmpeg produces),
    which is why no existing test pinned the old behaviour and none had to
    change.
    OpusOutputGainTests.cs re-serialises a fixture's header with a gain in it
    rather than adding a binary asset.

THE PIN TO CodeBrix.Audio. The packet seam arrived in CodeBrix.Audio, so this
repository cannot build against a package older than the one carrying it. The
pin in the library csproj must name a PUBLISHED version on nuget.org whenever
this package is published - a pin at a locally packed build would ship a .nupkg
declaring a dependency nobody can restore.

  >> AS THINGS STAND THE PIN IS A LOCAL PACK AND MUST BE RAISED BEFORE PUBLISH.
     CodeBrix.Audio.MitLicenseForever 1.0.241.460 is the LOCAL build carrying
     ConcealLoss, SupportsLossConcealment and AudioPacket.Loss, packed to
     ~/ClaudeHome/localfeed_codebrix_audio_2026-08-29/. Raise the pin to the
     published version of that work, restore from nuget.org ALONE with --force,
     and re-run the suite before publishing this package. (The previous pin,
     1.0.241.72, is published but has neither member, so it will not build the
     ConcealLoss override.)

VERIFYING AGAINST AN UNPUBLISHED CodeBrix.Audio. That situation recurs every time
the two repositories change together, so the method is recorded rather than the
episode. Pack CodeBrix.Audio into a folder, raise the pin to that build's
version, and restore from the folder WITHOUT adding a nuget.config to this
repository:

    dotnet restore CodeBrix.Audio.Opus.slnx \
        -p:RestoreSources="<local-feed-folder>%3Bhttps://api.nuget.org/v3/index.json"
    dotnet build CodeBrix.Audio.Opus.slnx -c Release --no-restore

(%3B is an MSBuild-escaped ';'. The restore puts the package in the global
packages folder, so every later build resolves it from there without the
argument.) Then, once the real package is on nuget.org, raise the pin again and
prove the result restores from nuget.org ALONE:

    dotnet restore CodeBrix.Audio.Opus.slnx --force \
        -p:RestoreSources="https://api.nuget.org/v3/index.json"

--force is what stops a stale local-feed resolution from being reused, and
obj/project.assets.json is where to confirm which version was actually taken.


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

  - Multichannel (channel mapping family 1) is declined rather than mis-mapped,
    on both seams. The multistream codec IS vendored, so adding family 1 later
    is an addition, not a rewrite. The two seams decline DIFFERENTLY on purpose:
    the stream reader throws InvalidDataException because it has already
    committed to the stream, while the packet factory throws
    NotSupportedException because null there means "not my codec" and would be
    reported to the application as "no registered packet decoder".
================================================================================
