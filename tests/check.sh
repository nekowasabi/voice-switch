#!/bin/sh
# The check to run after every change to triage or the journal (about 70 s). Stops at the first failure.
# Heavier checks (mutants, the real binary in --simulate) run before a PR only: see .claude/skills/verify-triage-execution.
set -eu
cd "$(dirname "$0")/.."
step() { printf '== %s\n' "$1"; shift; "$@"; }
step "python tests" python3 -m unittest discover -s tests -p 'test_*.py'
step "swift triage (truth table, reducer, request, routes)" sh tests/triage/run.sh
step "tla+ send path" sh .claude/skills/verify-triage-execution/check.sh design
step "journal append" bash .claude/skills/verify-journal-append/run.sh check
step "url actions" make url-test
step "build" make build
echo "PASS all quick checks"
