#!/bin/sh
set -eu
cd "$(dirname "$0")/../../.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
python3 tests/core_sources.py mac-dictation-session Sources/voice-switch swiftc \
  -module-name voice_switch -package-name voice-switch tests/macos/MacDictationSession/main.swift -o "$work/session"
"$work/session"
