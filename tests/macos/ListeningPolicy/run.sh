#!/bin/sh
set -eu
cd "$(dirname "$0")/../../.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
python3 tests/core_sources.py listening-policy Sources/voice-switch swiftc \
  -module-name voice_switch -package-name voice-switch tests/macos/ListeningPolicy/main.swift -o "$work/listening-policy"
"$work/listening-policy"
