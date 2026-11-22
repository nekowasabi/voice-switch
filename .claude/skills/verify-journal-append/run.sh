#!/usr/bin/env bash
# verify-journal-append. Usage: run.sh check [length] | mutants | observe [journal-dir]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(git -C "$HERE" rev-parse --show-toplevel)"
WORK="${TMPDIR:-/tmp}/verify-journal-append"
TRIAGE_TEST="$ROOT/tests/test_journal_triage.py"
mkdir -p "$WORK"

build() { # build <sources-dir> <binary>
  local src="$1" out="$2"
  python3 "$ROOT/tests/core_sources.py" journal "$src" swiftc -module-name voice_switch \
    -module-cache-path "$WORK/mc" "$HERE/main.swift" -o "$out"
}

# T1, T6, C1-C10: scripts/journal_triage.py over every combination, HTTP replaced. <script> defaults to the real one.
triage() { # triage [script]
  local out rc=0 id
  out="$(JOURNAL_TRIAGE="${1:-$ROOT/scripts/journal_triage.py}" python3 "$TRIAGE_TEST" 2>&1)" || rc=$?
  if [[ $rc -eq 0 ]]; then
    for id in T1 T6 C1 C2 C3 C4 C5 C6 C7 C8 C9 C10; do echo "PASS $id"; done
  else
    local found=0
    for id in T1 T6 C1 C2 C3 C4 C5 C6 C7 C8 C9 C10; do
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
  local bad=0 i=0 name want file old new out copy target
  while IFS='|' read -r name want file old new; do
    i=$((i + 1))
    copy="$WORK/mut-$i"
    rm -rf "$copy"; mkdir -p "$copy"
    # The script reads Resources/triage_questions.json next to its own folder, so the copy keeps that layout.
    if [[ $file == *.py ]]; then mkdir -p "$copy/scripts" "$copy/Resources"; cp "$ROOT/$file" "$copy/scripts/"
      cp "$ROOT/Resources/triage_questions.json" "$copy/Resources/"; target="$copy/scripts/$(basename "$file")"
    else cp -R "$ROOT/Sources/voice-switch/." "$copy/"; target="$copy/$(basename "$file")"; fi
    if ! OLD="$old" NEW="$new" perl -0pi -e 's/\Q$ENV{OLD}\E/$ENV{NEW}/ or die "anchor missing\n"' "$target"; then
      echo "MUTANT $name: anchor missing (update run.sh)"; bad=1; continue
    fi
    if [[ $file == *.py ]]; then
      out="$(triage "$target" || true)"
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
silent-fallback|J4|Sources/voice-switch/DictationJournal.swift|logger("dictation journal: \(error); using default \(fallback)")|_ = error
delivery-dropped|J3|Sources/voice-switch/DictationJournal.swift|"\"delivery\":\(delivery.map { jsonString($0.rawValue) } ?? "null")",|"\"delivery\":null",
decide-hole|C1|scripts/journal_triage.py|return actions or ["unjudged:kind"]|return actions
tune-tie-takes-smallest|C8|scripts/journal_triage.py|return weight, sum(abs(candidate[q][b] - thresholds[q][b]) for q in QUESTION_IDS for b in ("no", "yes"))|return weight, 0
old-labels-unscored|C9|scripts/journal_triage.py|actual = decide(label["answers"], thresholds, legacy_label=True)|actual = decide(label["answers"], thresholds)
python-no-delegate|C1|scripts/journal_triage.py|KINDS = (*LOCAL_MARKERS, "delegate")|KINDS = tuple(LOCAL_MARKERS)
unsure-edge-at-no|C1|scripts/journal_triage.py|if intent > intent_band["no"]:|if intent >= intent_band["no"]:
error-becomes-filler|C2|scripts/journal_triage.py|return ["unjudged:error"]|return ["filler"]
unsure-filler-is-filler|C2|scripts/journal_triage.py|if filler >= filler_band["yes"]:|if filler >= filler_band["yes"] or filler > filler_band["no"]:
markers-say-filler|C2|scripts/journal_triage.py|else ["unjudged:no-jev"]|else ["filler"]
context-despite-no-context|C3|scripts/journal_triage.py|if not include_context:|if False:
send-id|C3|scripts/journal_triage.py|{"text": prior.text, "seconds_before"|{"text": prior.text, "id": prior.id, "seconds_before"
cache-ignored|C4|scripts/journal_triage.py|if saved.get("question_hash") == digest and valid_answers(saved.get("answers")):|if False:
skip-automatic|C4|scripts/journal_triage.py|if not key:|if not key or entry.origin == "自動":
markers-before-jev|C4|scripts/journal_triage.py|if not key:|if not key or local_category(entry.text) != ["unjudged:no-jev"]:
hash-ignored|C4|scripts/journal_triage.py|if saved.get("question_hash") == digest and|if True and
error-cached|C5|scripts/journal_triage.py|if answers is not None and entry.id is not None:|if entry.id is not None:
no-prune|C5|scripts/journal_triage.py|if entry_id in ids or row.get("journal_hash") != journal_hash}|if True}
log-text|C6|scripts/journal_triage.py|answers = ask_jev(key, state, questions)|answers = ask_jev(key, state, questions); print(entry.text, file=sys.stderr)
window-61|C7|scripts/journal_triage.py|entry.instant - 60|entry.instant - 61
three-previous|C7|scripts/journal_triage.py|max(0, end - 2)|max(0, end - 3)
split-by-order|C8|scripts/journal_triage.py|bucket = int(hashlib.sha256(label["id"].encode("utf-8")).hexdigest(), 16) % 10|bucket = (len(training) + len(holdout)) % 10
no-filler-guard|C8|scripts/journal_triage.py| and adjusted["filler_misses"] <= baseline["filler_misses"]|
tune-below-30|C8|scripts/journal_triage.py|if len(labels) < 30 or|if len(labels) < 3 or
eval-ignores-thresholds|C9|scripts/journal_triage.py|actual = decide(label["answers"], thresholds, legacy_label=True)|actual = decide(label["answers"], {}, legacy_label=True)
label-to-row-device|C10|scripts/journal_triage.py|path = directory / f"{local_device}-cli.jsonl"|path = directory / f"{device}-cli.jsonl"
jev-despite-no-jev|T1|scripts/journal_triage.py|key = (os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY")) if use_jev else None|key = (os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY"))
list-pasted|T6|scripts/journal_triage.py|row.get("delivery") in (None, "history")|row.get("delivery") in (None, "history", "paste")
read-dot-folders|T6|scripts/journal_triage.py|if not child.name.startswith(".") and child.is_dir()|if child.is_dir()
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
