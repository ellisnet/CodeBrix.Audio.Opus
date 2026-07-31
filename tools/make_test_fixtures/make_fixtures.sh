#!/usr/bin/env bash
# ==============================================================================================
# make_fixtures.sh - regenerate the checked-in audio test fixtures
# ==============================================================================================
#
# WHAT THIS IS
#   The files under tests/Assets/audio/ are NOT third-party audio. They are synthesized here -
#   sine tones and a frequency sweep - and encoded with ffmpeg. This script is what produced
#   them, so a year from now they can be regenerated instead of being mystery binaries.
#
#   Each one exercises something specific:
#     * opus-tone-stereo-48000.opus       the everyday case
#     * opus-tone-mono-from-16000.opus    encoded FROM 16 kHz, so the rate the header DECLARES
#                                         and the rate Opus DECODES at disagree. This is the
#                                         voice-note shape, and the only fixture that catches a
#                                         decoder treating the declared rate as real.
#     * opus-sweep-stereo-48000.opus      2 s sweep; its instantaneous frequency identifies the
#                                         position, so a seek can be verified from the audio
#     * opus-truncated.opus               cut off mid-stream; must fail cleanly
#     * vorbis-tone-stereo-44100.ogg      NOT Opus. Every Ogg stream is stamped with the format
#                                         id "ogg", so the Opus factory is offered this one and
#                                         must decline it rather than accept and fail.
#
#   Every .opus fixture also ships with <name>.ffmpeg.wav - ffmpeg's OWN decode of it. Opus is
#   lossy, so a decoder cannot be checked byte-for-byte the way the FLAC decoder in
#   CodeBrix.Audio is; instead this library's output is compared against a second, independent
#   implementation's output within a tolerance. That is a much stronger check than a round trip
#   through this library alone, which would pass even if the encoder and decoder shared a bug.
#
# USAGE
#   cd tools/make_test_fixtures
#   ./make_fixtures.sh              # regenerate everything into ../../tests/Assets/audio
#   OUT_DIR=/tmp/fixtures ./make_fixtures.sh
#
# PREREQUISITES (installed by YOU - this script never installs anything)
#   ffmpeg, built with the libopus and libvorbis encoders.
#     Debian-based Linux:  sudo apt install ffmpeg
#     macOS (Homebrew):    brew install ffmpeg
#     Windows (winget):    winget install Gyan.FFmpeg
#   Verify with:           ffmpeg -hide_banner -encoders | grep -E 'libopus|libvorbis'
#
# NOTE ON REPRODUCIBILITY
#   The Ogg files NEVER come out byte-identical between runs: an Ogg muxer assigns a RANDOM
#   stream serial number each time. The encoder version also lands in the vendor string. So
#   regenerate deliberately - not as a side effect of adding one fixture - and never write a test
#   that assumes the bytes are stable.
# ==============================================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${OUT_DIR:-$SCRIPT_DIR/../../tests/Assets/audio}"
MANIFEST="$OUT_DIR/AUDIO-FIXTURES.txt"

if ! command -v ffmpeg > /dev/null 2>&1; then
    cat >&2 <<'EOF'
ERROR: ffmpeg was not found on PATH.

  This script does not install anything. Install ffmpeg yourself, then re-run:

    Debian-based Linux:  sudo apt install ffmpeg
    macOS (Homebrew):    brew install ffmpeg
    Windows (winget):    winget install Gyan.FFmpeg
EOF
    exit 1
fi

# Read the encoder list once. (Piping it into `grep -q` would make grep exit early, kill ffmpeg
# with SIGPIPE, and - under `set -o pipefail` - fail the check even on a match.)
FFMPEG_ENCODERS="$(ffmpeg -hide_banner -encoders 2>/dev/null || true)"

for enc in libopus libvorbis; do
    if ! printf '%s\n' "$FFMPEG_ENCODERS" | grep -E "^ [A-Z.]+ ${enc}( |\$)" > /dev/null; then
        echo "ERROR: this ffmpeg has no '${enc}' encoder. Install a full ffmpeg build." >&2
        exit 1
    fi
done

FFMPEG_VERSION="$(ffmpeg -hide_banner -version | head -1)"

mkdir -p "$OUT_DIR"
FF="ffmpeg -hide_banner -loglevel error -y"

echo "Writing fixtures to: $OUT_DIR"
echo "Using: $FFMPEG_VERSION"
echo

# ---------------------------------------------------------------------------------------------
# Source generators.
#   tone  <out.wav> <freq> <rate> <channels> <seconds>
#   sweep <out.wav> <rate> <channels> <seconds>
# A stereo tone puts a different frequency in each channel so a channel swap cannot hide.
#
# NOTE ON LEVEL: ffmpeg's `sine` source emits at roughly -18 dBFS. `volume=4` brings that to
# about -6 dBFS, which is what the tests assert against.
# ---------------------------------------------------------------------------------------------
tone() {
    local out="$1" freq="$2" rate="$3" ch="$4" secs="$5"
    if [ "$ch" = "1" ]; then
        $FF -f lavfi -i "sine=frequency=${freq}:sample_rate=${rate}:duration=${secs}" \
            -af "volume=4" -c:a pcm_s16le "$out"
    else
        local freq2=$((freq * 3 / 2))
        $FF -f lavfi -i "sine=frequency=${freq}:sample_rate=${rate}:duration=${secs}" \
            -f lavfi -i "sine=frequency=${freq2}:sample_rate=${rate}:duration=${secs}" \
            -filter_complex "[0:a][1:a]amerge=inputs=2,volume=4[a]" -map "[a]" \
            -c:a pcm_s16le "$out"
    fi
}

sweep() {
    local out="$1" rate="$2" ch="$3" secs="$4"
    local layout="mono"; [ "$ch" = "2" ] && layout="stereo"
    # A linear sweep makes any playback position identifiable from the audio itself, which is
    # what the seek tests rely on.
    $FF -f lavfi -i "aevalsrc=0.5*sin(2*PI*(200+1800*t/${secs})*t):s=${rate}:d=${secs}:c=${layout}" \
        -c:a pcm_s16le "$out"
}

# opus_case <name> <source-kind> <source-rate> <channels> <seconds> <bitrate>
#
# Encodes to .opus, then decodes that .opus BACK with ffmpeg to <name>.ffmpeg.wav as the
# reference this library's decoder is measured against.
opus_case() {
    local name="$1" kind="$2" rate="$3" ch="$4" secs="$5" bitrate="$6"
    local wav="$OUT_DIR/${name}.src.wav"
    local opus="$OUT_DIR/${name}.opus"

    case "$kind" in
        tone)  tone  "$wav" 440 "$rate" "$ch" "$secs" ;;
        sweep) sweep "$wav" "$rate" "$ch" "$secs" ;;
        *) echo "unknown source kind: $kind" >&2; exit 1 ;;
    esac

    $FF -i "$wav" -c:a libopus -b:a "$bitrate" "$opus"
    $FF -i "$opus" -c:a pcm_s16le "$OUT_DIR/${name}.ffmpeg.wav"
    rm -f "$wav"

    echo "  ${name}.opus (+ .ffmpeg.wav reference)"
}

echo "--- Ogg Opus ---"

# The everyday case: stereo, already at the rate Opus encodes in.
opus_case opus-tone-stereo-48000 tone 48000 2 0.25 96k

# Encoded FROM 16 kHz. OpusHead will declare 16000 while the stream still decodes at 48000 -
# the messenger voice-note shape, and the fixture that catches the 48 kHz rule.
opus_case opus-tone-mono-from-16000 tone 16000 1 0.25 32k

# 2 s sweep for the seek tests. The aevalsrc phase below is 2*PI*(200 + 1800*t/2)*t, so the
# INSTANTANEOUS frequency is 200 + 1800*t Hz: 200 Hz at the start, 2000 Hz one second in,
# 3800 Hz at the end. (Reading the 200..2000 in the formula as the frequency range is the
# easy mistake - it is the range of the phase COEFFICIENT, and sweeps twice as far.)
opus_case opus-sweep-stereo-48000 sweep 48000 2 2.0 96k

# Truncated mid-stream: must fail cleanly rather than hang or read past the end.
head -c 2000 "$OUT_DIR/opus-sweep-stereo-48000.opus" > "$OUT_DIR/opus-truncated.opus"
echo "  opus-truncated.opus"

echo "--- Ogg Vorbis (the not-Opus control) ---"

tone "$OUT_DIR/vorbis-tone-stereo-44100.wav" 440 44100 2 0.25
$FF -i "$OUT_DIR/vorbis-tone-stereo-44100.wav" -c:a libvorbis -qscale:a 5 \
    "$OUT_DIR/vorbis-tone-stereo-44100.ogg"
rm -f "$OUT_DIR/vorbis-tone-stereo-44100.wav"
echo "  vorbis-tone-stereo-44100.ogg"

# ---------------------------------------------------------------------------------------------
# Manifest
# ---------------------------------------------------------------------------------------------
{
    echo "=============================================================================="
    echo "tests/Assets/audio - generated audio test fixtures"
    echo "=============================================================================="
    echo
    echo "These files are NOT third-party audio. Every one is synthesized (sine tones,"
    echo "a frequency sweep) and encoded locally by tools/make_test_fixtures/make_fixtures.sh."
    echo "Re-run that script to regenerate them."
    echo
    echo "Generated by : tools/make_test_fixtures/make_fixtures.sh"
    echo "Encoder      : $FFMPEG_VERSION"
    echo
    echo "WHAT EACH FIXTURE IS FOR"
    echo "------------------------------------------------------------------------------"
    echo "  opus-tone-stereo-48000.opus      the everyday stereo case"
    echo "  opus-tone-mono-from-16000.opus   encoded FROM 16 kHz, so the header DECLARES"
    echo "                                   16000 while the stream DECODES at 48000. The"
    echo "                                   voice-note shape, and the one fixture that"
    echo "                                   catches a decoder trusting the declared rate"
    echo "  opus-sweep-stereo-48000.opus     2 s sweep, instantaneous frequency 200 + 1800*t"
    echo "                                   frequency identifies the position, so seeks can"
    echo "                                   be verified from the audio itself"
    echo "  opus-truncated.opus              truncated stream; must fail cleanly"
    echo "  vorbis-tone-stereo-44100.ogg     NOT Opus. Every Ogg stream is stamped with the"
    echo "                                   format id \"ogg\", so the Opus factory is offered"
    echo "                                   this and must DECLINE it"
    echo "  *.ffmpeg.wav                     ffmpeg's own decode of the matching .opus. Opus"
    echo "                                   is lossy, so the decoder is checked against a"
    echo "                                   second independent implementation within a"
    echo "                                   tolerance, rather than byte-for-byte"
    echo
    echo "NOTE ON REGENERATION: an Ogg muxer picks a RANDOM stream serial number per run, so"
    echo "these files never regenerate byte-identically even on the same ffmpeg build. The"
    echo "SHA256s below identify the committed files; they are not a reproducibility check."
    echo
    echo "SHA256"
    echo "------------------------------------------------------------------------------"
} > "$MANIFEST"

(cd "$OUT_DIR" && sha256sum ./*.opus ./*.ogg ./*.wav | sed 's|\./||') >> "$MANIFEST"

echo
echo "Manifest: $MANIFEST"
echo "Total size: $(du -sh "$OUT_DIR" | cut -f1)"
