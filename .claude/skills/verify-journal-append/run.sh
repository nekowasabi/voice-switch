#!/usr/bin/env bash
# verify-journal-append. Usage: run.sh check [length] | mutants | observe [journal-dir]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(git -C "$HERE" rev-parse --show-toplevel)"
WORK="${TMPDIR:-/tmp}/verify-journal-append"
TRIAGE_TEST="$ROOT/tests/test_journal_triage.py"
mkdir -p "$WORK"

build() { # build <sources-dir> <binary>
  local src="$1" out="$2" f files=()
  for f in SecretLog Config Platform DictationJournal WakeSafety Transcript; do files+=("$src/$f.swift"); done
  swiftc -module-name voice_switch -module-cache-path "$WORK/mc" "${files[@]}" "$HERE/main.swift" -o "$out"
}

# T1-T6: scripts/journal_triage.py over every combination, HTTP replaced. <script> defaults to the real one.
triage() { # triage [script]
  local out rc=0 id
  out="$(JOURNAL_TRIAGE="${1:-$ROOT/scripts/journal_triage.py}" python3 "$TRIAGE_TEST" 2>&1)" || rc=$?
  if [[ $rc -eq 0 ]]; then
    for id in T1 T2 T3 T4 T5 T6; do echo "PASS $id"; done
  else
    local found=0
    for id in T1 T2 T3 T4 T5 T6; do
      if grep -q " $id " <<<"$out"; then echo "FAIL $id $(grep -m1 " $id " <<<"$out")"; found=1; fi
    done
    [[ $found -eq 1 ]] || { echo "FAIL HARNESS"; echo "$out"; }
  fi
  return $rc
}

check() {
  local rc=0
  build "$ROOT/Sources/voice-switch" "$WORK/harness"
  "$WORK/harness" "$WORK/run-$$" "${1:-4}" || rc=1
  triage || rc=1
  return $rc
}

# Each mutant breaks one guard in a copy of one source and must fail the named check.
mutants() {
  local bad=0 i=0 name want file old new out copy
  while IFS='|' read -r name want file old new; do
    i=$((i + 1))
    copy="$WORK/mut-$i"
    rm -rf "$copy"; mkdir -p "$copy"
    if [[ $file == *.py ]]; then cp "$ROOT/$file" "$copy/"; else cp -R "$ROOT/Sources/voice-switch/." "$copy/"; fi
    if ! OLD="$old" NEW="$new" perl -0pi -e 's/\Q$ENV{OLD}\E/$ENV{NEW}/ or die "anchor missing\n"' "$copy/$(basename "$file")"; then
      echo "MUTANT $name: anchor missing (update run.sh)"; bad=1; continue
    fi
    if [[ $file == *.py ]]; then
      out="$(triage "$copy/$(basename "$file")" || true)"
    else
      build "$copy" "$WORK/mut-$i.bin"
      out="$("$WORK/mut-$i.bin" "$WORK/mutrun-$i" 4 || true)"
    fi
    if grep -q "^FAIL $want " <<<"$out"; then echo "CAUGHT $name by $want"
    else echo "MISSED $name (want FAIL $want)"; echo "$out"; bad=1; fi
  done <<'EOF'
write-only-handle|J1|Sources/voice-switch/DictationJournal.swift|FileHandle(forUpdating: URL(fileURLWithPath: file))|FileHandle(forWritingTo: URL(fileURLWithPath: file))
no-newline-repair|J3|Sources/voice-switch/DictationJournal.swift|if last?.first != 10 { try handle.write(contentsOf: Data([10])) }|_ = last
no-fallback-write|J1|Sources/voice-switch/DictationJournal.swift|if write(next, line: stamped.text, day: stamped.day) {|if false {
silent-fallback|J4|Sources/voice-switch/DictationJournal.swift|log("dictation journal: \(error); using default \(fallback)")|_ = error
delivery-dropped|J3|Sources/voice-switch/DictationJournal.swift|"\"delivery\":\(delivery.map { jsonString($0.rawValue) } ?? "null")",|"\"delivery\":null",
jev-without-flag|T1|scripts/journal_triage.py|if not use_jev or entry.origin|if entry.origin
key-is-consent|T1|scripts/journal_triage.py|if not use_jev or entry.origin|if not (use_jev or os.environ.get("TYPESAFE_API_KEY")) or entry.origin
send-automatic|T2|scripts/journal_triage.py|entry.origin not in ("手動", "ウェイク")|entry.origin not in ("手動", "ウェイク", "自動")
send-context|T3|scripts/journal_triage.py|ask_jev(key, entry.text)|ask_jev(key, f"{entry.text} {entry.focus_app} {entry.task_name}")
pane-floor|T4|scripts/journal_triage.py|confidence >= min_confidence|confidence >= 0.8
two-hits-pick-first|T4|scripts/journal_triage.py|if len(matches) == 1:|if len(matches) >= 1:
log-text|T5|scripts/journal_triage.py|confidence={score}", file=sys.stderr)|confidence={score} text={entry.text}", file=sys.stderr)
list-pasted|T6|scripts/journal_triage.py|row.get("delivery") in (None, "history")|row.get("delivery") in (None, "history", "paste")
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
