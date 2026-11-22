#!/bin/sh
# Each mutant breaks one guard in a copy of TriageExec.tla; under design.cfg it must fail the named invariant.
# <NL> in a field is a newline (\n would clash with TLA+'s \notin).
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
work="${TMPDIR:-/tmp}/verify-triage-execution/mutants"
rm -rf "$work"; mkdir -p "$work"
bad=0
while IFS='|' read -r name want old new; do
  copy="$work/$name.tla"
  if ! OLD="$old" NEW="$new" perl -0pe 'BEGIN { ($o, $n) = map { s/<NL>/\n/gr } @ENV{qw(OLD NEW)} } s/\Q$o\E/$n/ or die "anchor missing\n"' "$dir/TriageExec.tla" > "$copy"; then
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
approve-any-kind|NoWrongExecution|/\ Candidate(verdict[u], intentRoute[u]) /\ verdict[u] = intent[u]|/\ Candidate(verdict[u], intentRoute[u])
failed-off-screen|NoSilentDrop|/\ stage' = Set(stage, u, "failed")|/\ stage' = Set(stage, u, "done")
restore-hides-failed|NoSilentDrop|Restore(p) == IF p = "executing" THEN "unknown" ELSE p|Restore(p) == IF p = "executing" THEN "unknown" ELSE IF p = "failed" THEN "listed" ELSE p
failed-resends-on-its-own|AtMostOnce|/\ stage' = Set(stage, u, "failed")|/\ stage' = Set(stage, u, "approved")
retry-uncounted|AtMostOnce|/\ retries' = Set(retries, u, retries[u] + 1)|/\ retries' = retries
run-sends-default|NoWrongDestination|          /\ runRoute' = Set(runRoute, u, route[u])<NL>          /\ stage' = Set(stage, u, "ran")|          /\ runRoute' = Set(runRoute, u, 0)<NL>          /\ stage' = Set(stage, u, "ran")
grace-sends-default|NoWrongDestination|          /\ runRoute' = Set(runRoute, u, route[u])<NL>          /\ stage' = Set(stage, u, "ran")|          /\ runRoute' = Set(runRoute, u, IF graced[u] THEN 0 ELSE route[u])<NL>          /\ stage' = Set(stage, u, "ran")
send-keeps-shown-route|NoWrongDestination|              /\ route' = Set(route, u, intentRoute[u])|              /\ route' = route
fix-takes-default|NoWrongDestination|          /\ verdict' = Set(verdict, u, intent[u])<NL>          /\ route' = Set(route, u, intentRoute[u])|          /\ verdict' = Set(verdict, u, intent[u])<NL>          /\ route' = Set(route, u, 0)
switch-ignored|NoWrongDestination|    /\ route' = Set(route, u, i)|    /\ route' = route
restart-forgets-route|NoWrongDestination|           /\ UNCHANGED <<intent, intentRoute, picked, route, runRoute,|           /\ route' = [x \in U |-> IF route[x] = -1 THEN -1 ELSE 0]<NL>           /\ UNCHANGED <<intent, intentRoute, picked, runRoute,
EOF
exit $bad
