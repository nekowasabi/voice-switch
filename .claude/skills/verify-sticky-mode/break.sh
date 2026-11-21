#!/bin/sh
# Each broken config removes one guard the code has. Exit 0 only when TLC rejects every one with the named invariant.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
fail=0
for pair in "BreakFront:NeverWrongPaste" "BreakFocus:NeverWrongPaste" "BreakEnter:EnterOnlyAllowed"; do
  cfg=${pair%%:*}; inv=${pair#*:}
  out=$(mktemp)
  if sh "$dir/check.sh" "$dir/$cfg.cfg" >"$out" 2>&1; then
    echo "FAIL break $cfg passed; $inv does not catch the missing guard"; fail=1
  elif grep -q "Invariant $inv is violated" "$out"; then
    echo "ok   break $cfg rejected by $inv"
  else
    echo "FAIL break $cfg failed for another reason"; tail -n 20 "$out"; fail=1
  fi
  rm -f "$out"
done
exit "$fail"
