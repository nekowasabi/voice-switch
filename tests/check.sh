#!/bin/sh
# The check to run after every change to triage or the journal (about 70 s). Stops at the first failure.
# Heavier checks (mutants, the real binary in --simulate) run before a PR only: see .claude/skills/verify-triage-execution.
set -eu
cd "$(dirname "$0")/.."
start=$(date +%s); slow=""; slowest=0
step() {
  printf '== %s\n' "$1"; t=$(date +%s); name=$1; shift; "$@"
  d=$(( $(date +%s) - t )); if [ "$d" -gt "$slowest" ]; then slowest=$d; slow=$name; fi
}
step "budget (model size)" sh tests/budget.sh
step "verification coverage" sh tests/coverage_gate.sh
step "swift core boundary" python3 tests/core_sources.py check
step "python tests" python3 -m unittest discover -s tests -p 'test_*.py'
step "swift triage (truth table, reducer, request, routes)" sh tests/triage/run.sh
step "tla+ send path" sh .claude/skills/verify-triage-execution/check.sh design
# Why: each model takes 1-5 s, so every change runs them all instead of choosing. Run one at a time:
# verify-meet-pause-windows failed (exit 150) when started beside the other TLC runs on 2026-11-23.
step "tla+ hud blip (mac)" sh .claude/skills/verify-hud-blip/check.sh mac
step "tla+ hud blip (windows)" sh .claude/skills/verify-hud-blip/check.sh windows
step "tla+ hud noise" sh .claude/skills/verify-hud-noise/check.sh
step "tla+ meet pause (mac)" sh .claude/skills/verify-meet-pause/check.sh
step "tla+ meet pause (windows)" sh .claude/skills/verify-meet-pause-windows/check.sh
step "tla+ mic fallback" sh .claude/skills/verify-mic-fallback/check.sh
step "tla+ mic live (zeros)" sh .claude/skills/verify-mic-live/check.sh .claude/skills/verify-mic-live/MicLive.tla Design
step "tla+ mic port (windows)" sh .claude/skills/verify-mic-port-windows/check.sh .claude/skills/verify-mic-port-windows/MicPortWindows.tla Design
step "tla+ sticky delivery" sh .claude/skills/verify-sticky-mode/check.sh
step "wake allowances" sh .claude/skills/verify-wake-allowances/check.sh
step "journal append" bash .claude/skills/verify-journal-append/run.sh check
step "swift macOS dictation session" sh tests/macos/MacDictationSession/run.sh
step "swift listening policy" sh tests/macos/ListeningPolicy/run.sh
step "swift recognition slot" sh tests/macos/RecognitionSlot/run.sh
step "swift superwhisper result" sh tests/macos/SuperwhisperResult/run.sh
step "url actions" make url-test
step "build" make build
total=$(( $(date +%s) - start ))
if [ "$total" -gt "${CHECK_BUDGET_SECONDS:-120}" ]; then
  echo "FAIL quick checks took ${total} s, over ${CHECK_BUDGET_SECONDS:-120} s. Slowest: $slow (${slowest} s). Move it to the pre-PR list in .claude/skills/verify-triage-execution/SKILL.md instead of adding time here." >&2
  exit 1
fi
echo "PASS all quick checks (${total} s)"
