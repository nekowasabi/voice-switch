#!/bin/sh
# Keeps formal checks small (2026-11-22, after one fix took an hour of verification).
# A new TLA+ model gets at most 150 lines; an existing one may not grow past its size on 2026-11-22.
# Skill docs may not add more "at the sync commit" counts: each one forces a docs commit on every change.
set -eu
cd "$(dirname "$0")/.."
fail=0
limit() {
  case "$1" in
    */TriageExec.tla) echo 210 ;; */MicFallback.tla) echo 176 ;; */MeetPauseWindows.tla) echo 173 ;;
    */StickyDelivery.tla) echo 161 ;; *) echo 150 ;;
  esac
}
for f in $(git ls-files "*.tla"; git ls-files --others --exclude-standard "*.tla"); do
  n=$(wc -l < "$f" | tr -d " "); max=$(limit "$f")
  if [ "$n" -gt "$max" ]; then
    echo "budget: $f has $n lines, over $max. Cut the model to the rule the user asked to protect; put other rules in a reducer test (tests/triage/main.swift)." >&2
    fail=1
  fi
done
sync=$(cat .claude/skills/*/SKILL.md | grep -c "sync commit" || true)
if [ "$sync" -gt 16 ]; then
  echo "budget: .claude/skills/*/SKILL.md mention the sync commit $sync times, over 16. Do not write state counts or mutant counts pinned to a commit." >&2
  fail=1
fi
exit $fail
