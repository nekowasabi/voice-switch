#!/bin/sh
set -eu
cd "$(dirname "$0")/../../.."
work=$(mktemp -d)
probe=$(mktemp -d)
trap 'rm -rf "$work" "$probe"' EXIT
# LatestRecognition imports the macOS-only os module, so the harness cannot
# compile on Linux. Probe for it instead of swallowing a real build failure:
# when os is missing, skip loudly; otherwise a broken harness still fails.
echo 'import os' > "$probe/os.swift"
if ! swiftc -module-name os_probe "$probe/os.swift" -o "$probe/os-probe" 2>/dev/null; then
  echo "SKIP recognition slot: macOS-only 'os' module unavailable; Swift behavior tests were NOT run"
  exit 0
fi
python3 tests/core_sources.py recognition-slot Sources/voice-switch swiftc \
  -module-name voice_switch -package-name voice-switch tests/macos/RecognitionSlot/main.swift -o "$work/recognition-slot"
"$work/recognition-slot"
