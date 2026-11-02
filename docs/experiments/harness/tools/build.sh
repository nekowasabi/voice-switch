#!/usr/bin/env bash
# Build the small Swift tools that call the app's real NoWakePolicy.
# Usage: tools/build.sh [repo root] [output dir]   (SWIFTC overrides the compiler)
# A tool is skipped only when the function it needs is not in the checkout.
# Any other build failure stops with exit 1.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=${1:-$here/../../../..}
out=${2:-$here/bin}
swiftc=${SWIFTC:-swiftc}
policy=$repo/Sources/voice-switch/WakeSafety.swift
srcs=(); for f in SecretLog Config DictationJournal WakeSafety Transcript Segmenter Platform SuperwhisperModes PaneRoute; do srcs+=("$repo/Sources/voice-switch/$f.swift"); done  # same set as tests/parity/run_parity.py
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
mkdir -p "$out"
needs() { case $1 in agree_pair) echo agreedWithWhisper;; hallucination_filter) echo whisperTextForAgreement;; *) echo agreedCharacters;; esac; }
tools=(agree_same_empty agree_pair_main agree_pair hallucination_filter)
for t in "${tools[@]}"; do rm -f "$out/$t"; done
for t in "${tools[@]}"; do
  # Loose on purpose: a hit inside a comment just makes the build fail with exit 1.
  if ! grep -Eq "\bfunc[[:space:]]+$(needs $t)\b" "$policy"; then echo "skipped $t (func $(needs $t) is not in this checkout)"; continue; fi
  mkdir -p "$tmp/$t"; cp "$here/$t.swift" "$tmp/$t/main.swift"  # top-level code must be in main.swift
  if ! "$swiftc" -O -module-cache-path "$tmp/mc" "${srcs[@]}" "$tmp/$t/main.swift" -o "$out/$t"; then echo "build failed: $t" >&2; exit 1; fi
  echo "built $out/$t"
done
