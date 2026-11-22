#!/bin/sh
# Compiles the pure-Swift Segmenter sources (same list as tests/parity/run_parity.py) with main.swift and runs it.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
SRC=$HERE/../../../../Sources/voice-switch
OUT=$(mktemp -d "${TMPDIR:-/tmp}/voice-switch-segmenter-voice.XXXXXX")
trap 'rm -rf "$OUT"' EXIT
swiftc -module-name voice_switch -module-cache-path "$OUT/module-cache" \
  "$SRC/SecretLog.swift" "$SRC/Config.swift" "$SRC/WakeSafety.swift" "$SRC/Transcript.swift" "$SRC/Segmenter.swift" \
  "$SRC/Platform.swift" "$SRC/DictationJournal.swift" "$SRC/SuperwhisperModes.swift" "$SRC/PaneRoute.swift" \
  "$HERE/main.swift" -o "$OUT/segmenter-voice"
"$OUT/segmenter-voice"
