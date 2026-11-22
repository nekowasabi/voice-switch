#!/bin/sh
# Each mutant breaks one guard in a copy of TriageExec.tla; under design.cfg it must fail the named invariant.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
work="${TMPDIR:-/tmp}/verify-triage-execution/mutants"
rm -rf "$work"; mkdir -p "$work"
bad=0
while IFS='|' read -r name want old new; do
  copy="$work/$name.tla"
  if ! OLD="$old" NEW="$new" perl -0pe 's/\Q$ENV{OLD}\E/$ENV{NEW}/ or die "anchor missing\n"' "$dir/TriageExec.tla" > "$copy"; then
    echo "MUTANT $name: anchor missing (update mutants.sh)"; bad=1; continue
  fi
  out=$(sh "$dir/check.sh" design "$copy" || true)
  if printf '%s\n' "$out" | grep -q "^FAIL $want "; then echo "CAUGHT $name by $want"
  else echo "MISSED $name (want FAIL $want)"; printf '%s\n' "$out"; bad=1; fi
done <<'EOF'
grace-skipped|NoSendWithoutChance|ELSE IF Exception(v, ok, flagged) THEN "review" ELSE "pending"|ELSE IF Exception(v, ok, flagged) THEN "review" ELSE "approved"
heavy-no-verify|HeavyNeedsVerify|v \notin Routes \/ (v \in Heavy /\ ~ok) \/ flagged|v \notin Routes \/ flagged
cancel-ignored|DismissedNeverRuns|GraceExpire(u) == /\ alive /\ stage[u] = "pending"|GraceExpire(u) == /\ alive /\ stage[u] \in {"pending", "dismissed"}
start-after-dismiss|DismissedNeverRuns|Start(u) == /\ alive /\ stage[u] = "approved"|Start(u) == /\ alive /\ stage[u] \in {"approved", "dismissed"}
restart-reruns-executing|AtMostOnce|IF p = "executing" THEN "unknown" ELSE p|IF p = "executing" THEN "approved" ELSE p
unsure-to-cli-list|NoSilentDrop|(IF MenuShowsUnsure THEN "review" ELSE "listed")|"listed"
approve-any-kind|NoWrongExecution|/\ verdict[u] \in Routes /\ verdict[u] = intent[u]|/\ verdict[u] \in Routes
EOF
exit $bad
