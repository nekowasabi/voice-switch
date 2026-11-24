#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
python3 tests/core_sources.py enroll-words Sources/voice-switch swiftc \
  -module-name voice_switch -package-name voice-switch tests/enroll-words/main.swift -o "$work/enroll"
"$work/enroll" "$(pwd)" "$work/scratch"
