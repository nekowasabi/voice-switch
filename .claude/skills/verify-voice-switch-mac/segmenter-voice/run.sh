#!/bin/sh
# Compiles the same Core subset as the Swift parity harness and runs the Segmenter probe.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../../../.." && pwd)
OUT=$(mktemp -d "${TMPDIR:-/tmp}/voice-switch-segmenter-voice.XXXXXX")
trap 'rm -rf "$OUT"' EXIT
python3 "$ROOT/tests/core_sources.py" segmenter "$ROOT/Sources/voice-switch" \
  swiftc -module-cache-path "$OUT/module-cache" "$HERE/main.swift" -o "$OUT/segmenter-voice"
"$OUT/segmenter-voice"
