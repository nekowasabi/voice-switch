#!/bin/sh
set -eu
cd "$(dirname "$0")/../../.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
python3 tests/core_sources.py superwhisper-result Sources/voice-switch swiftc \
  -module-name voice_switch -package-name voice-switch tests/macos/SuperwhisperResult/main.swift -o "$work/superwhisper-result"
"$work/superwhisper-result"
