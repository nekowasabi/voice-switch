#!/bin/sh
# Finding F1: when stickyTmuxPane misses in a terminal that runs tmux, the field path pastes into the active pane.
# Exit 0 while the finding stands (TLC shows NeverWrongPaste violated). Exit 1 once the code and model no longer allow it.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
out=$(mktemp)
if sh "$dir/check.sh" "$dir/FindingTmuxMiss.cfg" >"$out" 2>&1; then
  echo "F1 no longer reproduces: FindingTmuxMiss.cfg passes. Update SKILL.md (Findings) and drop this step."
  rm -f "$out"; exit 1
fi
if grep -q "Invariant NeverWrongPaste is violated" "$out"; then
  echo "F1 stands: wake in a tmux pane, tmux capture misses, switch pane, Cmd+V lands in the other pane"
  rm -f "$out"; exit 0
fi
echo "F1 check failed for another reason"; tail -n 20 "$out"; rm -f "$out"; exit 1
