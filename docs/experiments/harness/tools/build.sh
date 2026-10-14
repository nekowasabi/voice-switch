#!/usr/bin/env bash
# Build the small Swift tools that call the app's real NoWakePolicy.
# Usage: tools/build.sh [repo root] [output dir]   (SWIFTC overrides the compiler)
# Tools that need functions missing from the checked-out code are skipped.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=${1:-$here/../../../..}
out=${2:-$here/bin}
swiftc=${SWIFTC:-swiftc}
srcs=(); for f in Config WakeSafety Transcript Segmenter Platform SuperwhisperModes PaneRoute; do srcs+=("$repo/Sources/voice-switch/$f.swift"); done  # same set as tests/parity/run_parity.py
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
mkdir -p "$out"
for t in agree_same_empty agree_pair_main agree_pair hallucination_filter; do
  mkdir -p "$tmp/$t"; cp "$here/$t.swift" "$tmp/$t/main.swift"  # top-level code must be in main.swift
  if "$swiftc" -O -module-cache-path "$tmp/mc" "${srcs[@]}" "$tmp/$t/main.swift" -o "$out/$t" 2>/dev/null; then echo "built $out/$t"
  else echo "skipped $t (functions not in this checkout)"; fi
done
