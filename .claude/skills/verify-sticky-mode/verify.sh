#!/bin/sh
# doctor: tools. path: can the running VoiceSwitch.app run tmux (F1 trigger). drive: anchors, model, breaks, finding, Swift.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$dir/../../.." && pwd)
export PATH="$HOME/swift/usr/bin:$HOME/.local/bin:$PATH"

cmd_doctor() {
  ok=0
  if command -v java >/dev/null 2>&1; then echo "ok   java"; else echo "FAIL java missing"; ok=1; fi
  if command -v swiftc >/dev/null 2>&1; then echo "ok   swiftc"; else echo "FAIL swiftc missing"; ok=1; fi
  if command -v gh >/dev/null 2>&1; then echo "ok   gh"; else echo "FAIL gh missing (TLC download)"; ok=1; fi
  exit "$ok"
}

# The app inherits launchd's PATH when opened from Finder or \`open\`. tmuxExecutable also looks in the install
# locations; when neither has tmux, capture silently becomes a field capture (finding F1).
cmd_path() {
  pid=$(pgrep -x voice-switch | head -n 1 || true)
  if [ -z "$pid" ]; then echo "SKIP path: voice-switch is not running"; return 0; fi
  app_path=$(ps -E -o command= -p "$pid" | tr ' ' '\n' | sed -n 's/^PATH=//p' | head -n 1)
  # Same search as tmuxExecutable in Sources/voice-switch/PaneRoute.swift: the app's PATH, then the install locations.
  for d in $(printf '%s' "$app_path" | tr ':' ' ') /opt/homebrew/bin /usr/local/bin /opt/local/bin; do
    if [ -x "$d/tmux" ]; then echo "ok   path: the running app resolves tmux at $d/tmux (PATH=$app_path)"; return 0; fi
  done
  echo "WARN path: no tmux in the app's PATH ($app_path) or the install locations; sticky in a tmux pane takes the field path (F1)"
}

cmd_drive() {
  echo "== anchors =="; sh "$dir/anchors.sh"
  echo "== tlc =="; sh "$dir/check.sh"
  echo "== break =="; sh "$dir/break.sh"
  echo "== finding F1 =="; sh "$dir/finding.sh"
  echo "== path =="; cmd_path
  echo "== swift stickyStep =="
  out=$(mktemp)
  python3 "$repo/tests/core_sources.py" sticky "$repo/Sources/voice-switch" \
    swiftc "$repo/tests/macos/StickyMode/main.swift" -o "$out"
  "$out" | tail -n 1
  rm -f "$out"
  echo "SKIP live Mac delivery (item 1 is done by a person; see SKILL.md)"
}

case "${1:-drive}" in
  doctor) cmd_doctor ;;
  path) cmd_path ;;
  drive) cmd_drive ;;
  *) echo "usage: verify.sh doctor|path|drive" >&2; exit 2 ;;
esac
