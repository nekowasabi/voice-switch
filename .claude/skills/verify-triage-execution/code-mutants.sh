#!/bin/sh
# Mutants for check items 6 and 7 of SKILL.md: each breaks one guard in a copy of the app sources or of
# scripts/changelogmemo-add (or takes the script from before the fix); the named check must FAIL on the copy.
# Usage: sh .claude/skills/verify-triage-execution/code-mutants.sh   (about 3 min)
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$dir/../../.." && pwd)
work="${TMPDIR:-/tmp}/verify-triage-execution/code-mutants-$$"
mkdir -p "$work"
trap 'rm -rf "$work"' EXIT
i=0
while IFS='|' read -r name want file old new; do
  i=$((i + 1))
  copy="$work/$i"
  mkdir -p "$copy"
  case $file in
  *@*) git -C "$repo" show "${file#*@}:${file%@*}" > "$copy/target" ;;
  scripts/*) cp "$repo/$file" "$copy/target" ;;
  *) cp -R "$repo/Sources/voice-switch/." "$copy/"; mv "$copy/$(basename "$file")" "$copy/target" ;;
  esac
  if [ -n "$old" ] && ! OLD="$old" NEW="$new" perl -0pi -e 's/\Q$ENV{OLD}\E/$ENV{NEW}/ or die "anchor missing\n"' "$copy/target"; then
    echo "MUTANT $name: anchor missing (update code-mutants.sh)"; continue
  fi
  case $file in
  scripts/*) out=$(cd "$repo" && CHANGELOGMEMO_ADD="$copy/target" python3 -m unittest tests.test_changelogmemo_add 2>&1 || true) ;;
  *) mv "$copy/target" "$copy/$(basename "$file")"
     out=$(TRIAGE_SOURCES="$copy" sh "$repo/tests/triage/run.sh" 2>&1 || true) ;;
  esac
  if printf '%s\n' "$out" | grep -Eq "^(FAIL|ERROR):? $want"; then echo "CAUGHT $name by $want"
  else echo "MISSED $name (want FAIL $want)"; printf '%s\n' "$out" | tail -n 20; fi
done <<'EOF' | tee "$work/out"
changelogmemo-before-fix|test_a_writer_that_edits_now_and_then_loses_nothing|scripts/changelogmemo-add@1b4c0f5||
changelogmemo-no-recheck|test_a_writer_that_edits_now_and_then_loses_nothing|scripts/changelogmemo-add|current = after_publish(path, published, raced)|current = snapshot(path)
previous-window-61|request|Sources/voice-switch/TriageRequest.swift|age <= 60|age <= 61
previous-three|request|Sources/voice-switch/TriageRequest.swift|.suffix(2)|.suffix(3)
previous-other-device|request|Sources/voice-switch/TriageRequest.swift|row.device == device && |
previous-pasted|request|Sources/voice-switch/TriageRequest.swift|row.entry.eligibleForTriage && |
send-bundle-id|request|Sources/voice-switch/TriageRequest.swift|app: note.focus?.name.flatMap|app: note.focus?.bundleId.flatMap
verify-route-in-batch|request|Sources/voice-switch/TriageRequest.swift|let batch = questions.filter { TriageClassifier.questionIDs.contains($0.key) }|let batch = questions
verify-without-route|request|Sources/voice-switch/TriageRequest.swift|state.route = route|_ = route
hash-escapes-slash|question hash|Sources/voice-switch/TriageFiles.swift|encoder.outputFormatting = .withoutEscapingSlashes|encoder.outputFormatting = []
routes-any-key|routes|Sources/voice-switch/Config.swift|guard routes.keys.allSatisfy(kinds.contains) else {|guard true else {
EOF
! grep -qv '^CAUGHT ' "$work/out"
