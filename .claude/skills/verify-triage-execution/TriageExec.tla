---- MODULE TriageExec ----
\* Journal triage from classification to execution, as designed in docs/journal-triage-design.md and
\* docs/journal-triage-execution.md. No code exists yet: every action names the design sentence it models.
EXTENDS Naturals

CONSTANTS U,               \* utterances
          Kinds,           \* action kinds: look_up, self_task, write_text, computer_change
          Routes,          \* kinds with a configured route (dictation.triage.routes)
          AutoRoutes,      \* routes with auto: true
          PreRecord,       \* TRUE: "executing" is written to proposals.jsonl before the route runs
          MenuShowsUnsure, \* TRUE: unjudged and unrouted-action rows appear in the proposal window
          MaxCrashes

Verdicts == Kinds \cup {"thought", "filler", "unjudged"}
Intents  == Kinds \cup {"thought", "filler"}

VARIABLES intent,     \* what the speaker meant (the truth the user knows)
          verdict,    \* what the classifier decided; Jev may be wrong, so any verdict is possible
          stage,      \* in-memory state in the app
          persisted,  \* last state in proposals.jsonl (survives a restart)
          runs,       \* times the route actually ran
          runKind,    \* kind of the last run
          viaAuto,    \* the run was started by an auto route, not by the user
          retries,    \* explicit user retries after an unknown outcome
          dismissed, dRuns,
          alive, crashes

vars == <<intent, verdict, stage, persisted, runs, runKind, viaAuto, retries, dismissed, dRuns, alive, crashes>>

\* Where a verdict lands. execution.md: an action kind without a route "is not proposed and stays in the list only";
\* the list is the CLI output, which a menu-only user never sees.
Shown(v) == IF v \in Routes THEN "proposed"
            ELSE IF v = "filler" THEN "hidden"
            ELSE IF v = "thought" THEN "listed"
            ELSE IF MenuShowsUnsure THEN "review" ELSE "listed"

Set(f, u, x) == [f EXCEPT ![u] = x]

Init == /\ intent \in [U -> Intents]
        /\ verdict = [u \in U |-> "none"]
        /\ stage = [u \in U |-> "said"]
        /\ persisted = [u \in U |-> "said"]
        /\ runs = [u \in U |-> 0]
        /\ runKind = [u \in U |-> "none"]
        /\ viaAuto = [u \in U |-> FALSE]
        /\ retries = [u \in U |-> 0]
        /\ dismissed = [u \in U |-> FALSE]
        /\ dRuns = [u \in U |-> 0]
        /\ alive = TRUE
        /\ crashes = 0

\* Flow step 2-3: the app classifies a listed row after DictationHistory.add.
Classify(u) == /\ alive /\ stage[u] = "said"
               /\ \E v \in Verdicts:
                    /\ verdict' = Set(verdict, u, v)
                    /\ stage' = Set(stage, u, Shown(v))
                    /\ persisted' = Set(persisted, u, Shown(v))
               /\ UNCHANGED <<intent, runs, runKind, viaAuto, retries, dismissed, dRuns, alive, crashes>>

\* Flow step 5: the user approves only a proposal whose kind is what they meant.
Approve(u) == /\ alive /\ stage[u] \in {"proposed", "review"}
              /\ verdict[u] \in Routes /\ verdict[u] = intent[u]
              /\ stage' = Set(stage, u, "approved")
              /\ UNCHANGED <<intent, verdict, persisted, runs, runKind, viaAuto, retries, dismissed, dRuns, alive, crashes>>

\* Flow step 5: "種類を直す". The corrected kind is shown again like a fresh verdict.
Fix(u) == /\ alive /\ stage[u] \in {"proposed", "review"}
          /\ intent[u] \in Kinds /\ verdict[u] # intent[u]
          /\ verdict' = Set(verdict, u, intent[u])
          /\ stage' = Set(stage, u, Shown(intent[u]))
          /\ persisted' = Set(persisted, u, Shown(intent[u]))
          /\ UNCHANGED <<intent, runs, runKind, viaAuto, retries, dismissed, dRuns, alive, crashes>>

Dismiss(u) == /\ alive /\ stage[u] \in {"proposed", "review", "unknown"}
              /\ stage' = Set(stage, u, "dismissed")
              /\ persisted' = Set(persisted, u, "dismissed")
              /\ dismissed' = Set(dismissed, u, TRUE)
              /\ dRuns' = Set(dRuns, u, runs[u])
              /\ UNCHANGED <<intent, verdict, runs, runKind, viaAuto, retries, alive, crashes>>

\* Flow step 4: a route marked auto runs without approval.
Auto(u) == /\ alive /\ stage[u] = "proposed" /\ verdict[u] \in AutoRoutes
           /\ stage' = Set(stage, u, "approved")
           /\ viaAuto' = Set(viaAuto, u, TRUE)
           /\ UNCHANGED <<intent, verdict, persisted, runs, runKind, retries, dismissed, dRuns, alive, crashes>>

Start(u) == /\ alive /\ stage[u] = "approved"
            /\ stage' = Set(stage, u, "executing")
            /\ persisted' = IF PreRecord THEN Set(persisted, u, "executing") ELSE persisted
            /\ UNCHANGED <<intent, verdict, runs, runKind, viaAuto, retries, dismissed, dRuns, alive, crashes>>

\* The route's URL, POST, or command runs (the WakeAction delivery). It is outside the app's state, so it can
\* complete even if the app dies before recording it.
Run(u) == /\ alive /\ stage[u] = "executing"
          /\ runs' = Set(runs, u, runs[u] + 1)
          /\ runKind' = Set(runKind, u, verdict[u])
          /\ stage' = Set(stage, u, "ran")
          /\ UNCHANGED <<intent, verdict, persisted, viaAuto, retries, dismissed, dRuns, alive, crashes>>

\* Flow step 6: the result is appended to proposals.jsonl.
Record(u) == /\ alive /\ stage[u] = "ran"
             /\ stage' = Set(stage, u, "done")
             /\ persisted' = Set(persisted, u, "done")
             /\ UNCHANGED <<intent, verdict, runs, runKind, viaAuto, retries, dismissed, dRuns, alive, crashes>>

\* After a restart with PreRecord, an "executing" row is shown as "実行されたか不明": the user retries or confirms.
Retry(u) == /\ alive /\ stage[u] = "unknown" /\ verdict[u] = intent[u]
            /\ retries' = Set(retries, u, retries[u] + 1)
            /\ stage' = Set(stage, u, "approved")
            /\ UNCHANGED <<intent, verdict, persisted, runs, runKind, viaAuto, dismissed, dRuns, alive, crashes>>

ConfirmDone(u) == /\ alive /\ stage[u] = "unknown"
                  /\ stage' = Set(stage, u, "done")
                  /\ persisted' = Set(persisted, u, "done")
                  /\ UNCHANGED <<intent, verdict, runs, runKind, viaAuto, retries, dismissed, dRuns, alive, crashes>>

Crash == /\ alive /\ crashes < MaxCrashes
         /\ alive' = FALSE /\ crashes' = crashes + 1
         /\ UNCHANGED <<intent, verdict, stage, persisted, runs, runKind, viaAuto, retries, dismissed, dRuns>>

\* The app reloads proposals.jsonl. In-memory approvals are lost; a persisted "executing" becomes "unknown".
Restore(p) == IF p = "executing" THEN "unknown" ELSE p
Restart == /\ ~alive /\ alive' = TRUE
           /\ stage' = [u \in U |-> Restore(persisted[u])]
           /\ UNCHANGED <<intent, verdict, persisted, runs, runKind, viaAuto, retries, dismissed, dRuns, crashes>>

Done == /\ alive /\ \A u \in U: stage[u] \in {"done", "dismissed", "listed", "hidden"}
        /\ UNCHANGED vars

Next == \/ \E u \in U: Classify(u) \/ Approve(u) \/ Fix(u) \/ Dismiss(u) \/ Auto(u) \/ Start(u)
                       \/ Run(u) \/ Record(u) \/ Retry(u) \/ ConfirmDone(u)
        \/ Crash \/ Restart \/ Done

Spec == Init /\ [][Next]_vars

\* Keeps a broken variant that runs a route without end from exploring forever. Correct designs stay far below it.
Bounded == \A u \in U: runs[u] <= 3

\* Worst case 2: a route runs for something the speaker did not mean, except by an auto route the user opted into.
NoWrongExecution == \A u \in U: runs[u] > 0 => (runKind[u] = intent[u] \/ viaAuto[u])
\* The same without the auto exception: what an auto route costs.
StrictNoWrongExecution == \A u \in U: runs[u] > 0 => runKind[u] = intent[u]
\* Worst case 2 by repetition: a route runs again without the user asking for it again.
AtMostOnce == \A u \in U: runs[u] <= 1 + retries[u]
DismissedNeverRuns == \A u \in U: dismissed[u] => runs[u] = dRuns[u]
\* Worst case 1 without a trace: an unsure or unrouted action ends where a menu user cannot see it.
\* (A wrong "thought" or "filler" verdict is classifier accuracy, measured by eval, not by this model.)
NoSilentDrop == \A u \in U: stage[u] = "listed" => verdict[u] = "thought"
====
