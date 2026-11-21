#!/usr/bin/env bash
# verify-journal-append. Usage: run.sh check [length] | mutants | observe [journal-dir]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(git -C "$HERE" rev-parse --show-toplevel)"
WORK="${TMPDIR:-/tmp}/verify-journal-append"
mkdir -p "$WORK"

build() { # build <sources-dir> <binary>
  local src="$1" out="$2" f files=()
  for f in SecretLog Config Platform DictationJournal WakeSafety Transcript; do files+=("$src/$f.swift"); done
  swiftc -module-name voice_switch -module-cache-path "$WORK/mc" "${files[@]}" "$HERE/main.swift" -o "$out"
}

check() {
  build "$ROOT/Sources/voice-switch" "$WORK/harness"
  "$WORK/harness" "$WORK/run-$$" "${1:-4}"
}

# Each mutant breaks one guard in a copy of the sources and must fail the named invariant.
mutants() {
  local bad=0 i=0 name old new want out copy
  while IFS='|' read -r name want old new; do
    i=$((i + 1))
    copy="$WORK/mut-$i"
    rm -rf "$copy"; cp -R "$ROOT/Sources/voice-switch" "$copy"
    if ! OLD="$old" NEW="$new" perl -0pi -e 's/\Q$ENV{OLD}\E/$ENV{NEW}/ or die "anchor missing\n"' "$copy/DictationJournal.swift"; then
      echo "MUTANT $name: anchor missing (update run.sh)"; bad=1; continue
    fi
    build "$copy" "$WORK/mut-$i.bin"
    out="$("$WORK/mut-$i.bin" "$WORK/mutrun-$i" 4 || true)"
    if grep -q "^FAIL $want " <<<"$out"; then echo "CAUGHT $name by $want"
    else echo "MISSED $name (want FAIL $want)"; echo "$out"; bad=1; fi
  done <<'EOF'
write-only-handle|J1|FileHandle(forUpdating: URL(fileURLWithPath: file))|FileHandle(forWritingTo: URL(fileURLWithPath: file))
no-newline-repair|J3|if last?.first != 10 { try handle.write(contentsOf: Data([10])) }|_ = last
no-fallback-write|J1|if write(next, line: stamped.text, day: stamped.day) {|if false {
silent-fallback|J4|log("dictation journal: \(error); using default \(fallback)")|_ = error
EOF
  return $bad
}

# Line counts only, never the text. One line per day on a day with several dictations is the symptom.
observe() {
  local dir="${1:-$HOME/Library/Application Support/voice-switch/journal}" f
  for f in "$dir"/*.jsonl; do [[ -e "$f" ]] || continue; printf '%s lines=%s\n' "$(basename "$f")" "$(wc -l <"$f" | tr -d ' ')"; done
}

case "${1:-check}" in
  check) shift || true; check "$@" ;;
  mutants) mutants ;;
  observe) shift || true; observe "$@" ;;
  *) echo "usage: run.sh check [length] | mutants | observe [dir]" >&2; exit 2 ;;
esac
