#!/bin/sh
set -eu
cd "$(dirname "$0")/../../.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
python3 tests/core_sources.py mac-dictation-session Sources/voice-switch swiftc \
  -module-name voice_switch -package-name voice-switch tests/macos/MacDictationSession/main.swift -o "$work/session"
# Finding 8: a hanging tmux must not block routing. Fake tmux first on PATH (exec so SIGTERM kills it).
mkdir -p "$work/fakebin"
printf '#!/bin/sh\nexec sleep 60\n' > "$work/fakebin/tmux"
chmod +x "$work/fakebin/tmux"
PATH="$work/fakebin:$PATH" TMUX_FAKEBIN="$work/fakebin" "$work/session"
