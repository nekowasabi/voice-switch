#!/bin/sh
# doctor: tools for the sticky-mode checks. drive: run them. Live Mac UI is MAC_UNVERIFIED.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$dir/../../.." && pwd)
export PATH="$HOME/.dotnet:$HOME/swift/usr/bin:$HOME/.local/bin:$PATH"

cmd_doctor() {
  ok=0
  if command -v java >/dev/null 2>&1; then echo "ok   java $(java -version 2>&1 | head -n 1)"; else echo "FAIL java missing"; ok=1; fi
  if command -v swiftc >/dev/null 2>&1; then echo "ok   swiftc"; else echo "FAIL swiftc missing"; ok=1; fi
  if command -v python3 >/dev/null 2>&1; then echo "ok   python3"; else echo "FAIL python3 missing"; ok=1; fi
  if command -v gh >/dev/null 2>&1; then echo "ok   gh"; else echo "FAIL gh missing (TLC download)"; ok=1; fi
  echo "SKIP live Mac AX / CGEvent / tmux paste (MAC_UNVERIFIED)"
  exit "$ok"
}

cmd_drive() {
  echo "== tlc =="
  sh "$dir/check.sh"
  echo "== tlc-break =="
  sh "$dir/break.sh"
  echo "== python =="
  python3 "$repo/tests/macos/StickyMode/test_sticky.py"
  echo "== swift =="
  out=$(mktemp)
  swiftc -parse "$repo/Sources/voice-switch/StickyMode.swift"
  swiftc "$repo/Sources/voice-switch/StickyMode.swift" "$repo/tests/macos/StickyMode/main.swift" -o "$out"
  "$out"
  rm -f "$out"
  echo "SKIP live Mac capture, paste, and Enter (MAC_UNVERIFIED)"
}

case "${1:-drive}" in
  doctor) cmd_doctor ;;
  drive) cmd_drive ;;
  *) echo "usage: verify.sh doctor|drive" >&2; exit 2 ;;
esac
