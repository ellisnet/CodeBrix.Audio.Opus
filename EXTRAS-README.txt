================================================================================
EXTRAS-README: CodeBrix.Audio.Opus
Samples, tools and other content in this repository that is not part of a NuGet
package
================================================================================

This repository ships one package and has no sample applications and no demos.
Everything below is developer tooling and test data: none of it is packed, and
none of it is needed to consume CodeBrix.Audio.Opus.BsdLicenseForever.


tools/make_test_fixtures/ - regenerate the audio test fixtures
==============================================================
  Path:   tools/make_test_fixtures/make_fixtures.sh

  WHAT IT IS
    The one script that produced every file under tests/Assets/audio/. Those
    files are NOT third-party audio: they are synthesized here - sine tones and
    a frequency sweep - and encoded with ffmpeg, so a year from now they can be
    regenerated instead of being mystery binaries. It also rewrites the
    tests/Assets/audio/AUDIO-FIXTURES.txt manifest that describes them.

  HOW TO RUN IT
      cd tools/make_test_fixtures
      ./make_fixtures.sh                    # writes ../../tests/Assets/audio
      OUT_DIR=/tmp/fixtures ./make_fixtures.sh

  PREREQUISITES (installed by YOU - the script never installs anything)
    ffmpeg, built with the libopus and libvorbis encoders.
      Debian-based Linux:  sudo apt install ffmpeg
      macOS (Homebrew):    brew install ffmpeg
      Windows (winget):    winget install Gyan.FFmpeg
    Verify with:           ffmpeg -hide_banner -encoders | grep -E 'libopus|libvorbis'
    If ffmpeg is missing the script names it, prints the install command, and
    stops.

  WHAT IT DEMONSTRATES / WHY IT MATTERS
    That the test corpus is reproducible and licence-clean. REGENERATE
    DELIBERATELY, not as a side effect of adding one fixture: the Ogg files
    never come out byte-identical between runs, because an Ogg muxer assigns a
    random stream serial number each time and the encoder version lands in the
    vendor string. Never write a test that assumes those bytes are stable.


tests/Assets/audio/ - the generated fixture set
===============================================
  Path:   tests/Assets/audio/  (manifest: AUDIO-FIXTURES.txt)

  Copied next to the test assembly by a Content item in the test csproj, so the
  tests find them without any path juggling. Each fixture exercises something
  specific:

    opus-tone-stereo-48000.opus      the everyday stereo case.
    opus-tone-mono-from-16000.opus   encoded FROM 16 kHz, so the rate the header
                                     DECLARES and the rate Opus DECODES at
                                     disagree. The voice-note shape, and the
                                     only fixture that catches a decoder
                                     treating the declared rate as real.
    opus-sweep-stereo-48000.opus     a 2 s sweep whose instantaneous frequency
                                     identifies the position, so a seek can be
                                     verified from the audio itself.
    opus-truncated.opus              cut off mid-stream; must fail cleanly.
    vorbis-tone-stereo-44100.ogg     NOT Opus. Every Ogg stream is stamped with
                                     the format id "ogg", so the Opus factory is
                                     offered this one and must DECLINE it rather
                                     than accept it and then fail.
    <name>.ffmpeg.wav                ffmpeg's OWN decode of the matching .opus.
                                     Opus is lossy, so this library's output is
                                     compared against a second, independent
                                     implementation within a tolerance - a much
                                     stronger check than a round trip through
                                     this library alone, which would pass even
                                     if the encoder and decoder shared a bug.


tests/CodeBrix.Audio.Opus.Tests/ - the test project
===================================================
  Path:   tests/CodeBrix.Audio.Opus.Tests/

  Not a sample, but it is the other non-package content in the repository, and
  it is the best worked example of every public API. AGENT-README.txt's
  "WORKING EXAMPLES ON GITHUB" section maps each feature to the file that
  exercises it.

  Run it with:
      dotnet test CodeBrix.Audio.Opus.slnx

  The tests that open a real audio device and MAKE SOUND are opt-in, so an
  ordinary run is silent and headless-safe:
      CODEBRIX_AUDIO_RUN_PLAYBACK_TESTS=1 dotnet test CodeBrix.Audio.Opus.slnx

  That is the same switch CodeBrix.Audio's own sounding tests use, so one
  environment variable governs the family.
================================================================================
