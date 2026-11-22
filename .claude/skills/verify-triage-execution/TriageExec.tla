---- MODULE TriageExec ----
\* Journal triage from classification to sending, as designed in docs/journal-triage-execution.md (plan A, 2026-11-22):
\* a sure request waits a grace period and then goes out on its own; an exception waits in 確認が要るもの for the user.
\* No code exists yet: every action names the design sentence it models.
EXTENDS Naturals

CONSTANTS U,               \* utterances
          Kinds,           \* action kinds
          Routes,          \* kinds with a configured route (dictation.triage.routes)
          Heavy,           \* routes that are hard to take back (heavy: true); they need the extra Jev check
          PreRecord,       \* TRUE: "送信中" is written to proposals.jsonl before the route runs
          MenuShowsUnsure, \* TRUE: unjudged and unrouted rows go to 確認が要るもの
          MaxCrashes

Verdicts == Kinds \cup {"thought", "filler", "unjudged"}
Intents  == Kinds \cup {"thought", "filler"}

VARIABLES intent,     \* what the speaker meant
          verdict,    \* what the classifier decided; Jev may be wrong, so any verdict is possible
          verified,   \* the extra Jev check for a heavy route said the send matches the wish (may also be wrong)
          stage,      \* in-memory state in the app
          persisted,  \* last state in proposals.jsonl
          runs, runKind,
          graced,     \* the row waited out its grace period in まもなく送る
          userOK,     \* the user pressed 送る or すぐ送る for this kind
          retries, dismissed, dRuns,
          alive, crashes

vars == <<intent, verdict, verified, stage, persisted, runs, runKind, graced, userOK, retries, dismissed, dRuns, alive, crashes>>

\* 例外になる条件 1-2 (3-4, the warm-up and the repeat check, only add exceptions; flagged covers them).
Exception(v, ok, flagged) == v \notin Routes \/ (v \in Heavy /\ ~ok) \/ flagged

\* Where a verdict lands. Without MenuShowsUnsure an unrouted or unjudged row would stay in the CLI list only.
Shown(v, ok, flagged) == IF v = "filler" THEN "hidden"
                         ELSE IF v = "thought" THEN "listed"
                         ELSE IF v \notin Routes THEN (IF MenuShowsUnsure THEN "review" ELSE "listed")
                         ELSE IF Exception(v, ok, flagged) THEN "review" ELSE "pending"

Set(f, u, x) == [f EXCEPT ![u] = x]

Init == /\ intent \in [U -> Intents]
        /\ verdict = [u \in U |-> "none"]
        /\ verified = [u \in U |-> FALSE]
        /\ stage = [u \in U |-> "said"]
        /\ persisted = [u \in U |-> "said"]
        /\ runs = [u \in U |-> 0]
        /\ runKind = [u \in U |-> "none"]
        /\ graced = [u \in U |-> FALSE]
        /\ userOK = [u \in U |-> FALSE]
        /\ retries = [u \in U |-> 0]
        /\ dismissed = [u \in U |-> FALSE]
        /\ dRuns = [u \in U |-> 0]
        /\ alive = TRUE
        /\ crashes = 0

\* 流れ 1-3: the app classifies after DictationHistory.add, asks the extra check for a heavy route, and files the row.
Classify(u) == /\ alive /\ stage[u] = "said"
               /\ \E v \in Verdicts, ok \in BOOLEAN, flagged \in BOOLEAN:
                    /\ verdict' = Set(verdict, u, v)
                    /\ verified' = Set(verified, u, v \in Heavy /\ ok)
                    /\ stage' = Set(stage, u, Shown(v, ok, flagged))
                    /\ persisted' = Set(persisted, u, Shown(v, ok, flagged))
               /\ UNCHANGED <<intent, runs, runKind, graced, userOK, retries, dismissed, dRuns, alive, crashes>>

\* 流れ 2: the grace period runs out. Only a row in まもなく送る can go out this way.
GraceExpire(u) == /\ alive /\ stage[u] = "pending"
                  /\ stage' = Set(stage, u, "approved")
                  /\ graced' = Set(graced, u, TRUE)
                  /\ UNCHANGED <<intent, verdict, verified, persisted, runs, runKind, userOK, retries, dismissed, dRuns, alive, crashes>>

\* [すぐ送る] on まもなく送る, or [送る] / a kind button on 確認が要るもの. The user picks only what they meant.
SendNow(u) == /\ alive /\ stage[u] \in {"pending", "review"}
              /\ verdict[u] \in Routes /\ verdict[u] = intent[u]
              /\ stage' = Set(stage, u, "approved")
              /\ userOK' = Set(userOK, u, TRUE)
              /\ UNCHANGED <<intent, verdict, verified, persisted, runs, runKind, graced, retries, dismissed, dRuns, alive, crashes>>

\* A kind button on 確認が要るもの when the verdict was wrong: the row takes the kind the user chose.
Fix(u) == /\ alive /\ stage[u] = "review"
          /\ intent[u] \in Routes /\ verdict[u] # intent[u]
          /\ verdict' = Set(verdict, u, intent[u])
          /\ stage' = Set(stage, u, "approved")
          /\ userOK' = Set(userOK, u, TRUE)
          /\ UNCHANGED <<intent, verified, persisted, runs, runKind, graced, retries, dismissed, dRuns, alive, crashes>>

\* [取消] on まもなく送る, [送らない] on 確認が要るもの, or dismissing an "unknown" row.
Dismiss(u) == /\ alive /\ stage[u] \in {"pending", "review", "unknown"}
              /\ stage' = Set(stage, u, "dismissed")
              /\ persisted' = Set(persisted, u, "dismissed")
              /\ dismissed' = Set(dismissed, u, TRUE)
              /\ dRuns' = Set(dRuns, u, runs[u])
              /\ UNCHANGED <<intent, verdict, verified, runs, runKind, graced, userOK, retries, alive, crashes>>

\* 流れ 6: "送信中" is appended before the route runs.
Start(u) == /\ alive /\ stage[u] = "approved"
            /\ stage' = Set(stage, u, "executing")
            /\ persisted' = IF PreRecord THEN Set(persisted, u, "executing") ELSE persisted
            /\ UNCHANGED <<intent, verdict, verified, runs, runKind, graced, userOK, retries, dismissed, dRuns, alive, crashes>>

\* The route (WakeAction open / post / command) runs outside the app's state.
Run(u) == /\ alive /\ stage[u] = "executing"
          /\ runs' = Set(runs, u, runs[u] + 1)
          /\ runKind' = Set(runKind, u, verdict[u])
          /\ stage' = Set(stage, u, "ran")
          /\ UNCHANGED <<intent, verdict, verified, persisted, graced, userOK, retries, dismissed, dRuns, alive, crashes>>

Record(u) == /\ alive /\ stage[u] = "ran"
             /\ stage' = Set(stage, u, "done")
             /\ persisted' = Set(persisted, u, "done")
             /\ UNCHANGED <<intent, verdict, verified, runs, runKind, graced, userOK, retries, dismissed, dRuns, alive, crashes>>

\* 流れ 7: after a restart a "送信中" row shows as "送ったか不明"; the user retries or confirms.
Retry(u) == /\ alive /\ stage[u] = "unknown" /\ verdict[u] = intent[u]
            /\ retries' = Set(retries, u, retries[u] + 1)
            /\ stage' = Set(stage, u, "approved")
            /\ UNCHANGED <<intent, verdict, verified, persisted, runs, runKind, graced, userOK, dismissed, dRuns, alive, crashes>>

ConfirmDone(u) == /\ alive /\ stage[u] = "unknown"
                  /\ stage' = Set(stage, u, "done")
                  /\ persisted' = Set(persisted, u, "done")
                  /\ UNCHANGED <<intent, verdict, verified, runs, runKind, graced, userOK, retries, dismissed, dRuns, alive, crashes>>

Crash == /\ alive /\ crashes < MaxCrashes
         /\ alive' = FALSE /\ crashes' = crashes + 1
         /\ UNCHANGED <<intent, verdict, verified, stage, persisted, runs, runKind, graced, userOK, retries, dismissed, dRuns>>

\* The app reloads proposals.jsonl. A pending row keeps its deadline (it is persisted), an in-memory approval is lost.
Restore(p) == IF p = "executing" THEN "unknown" ELSE p
Restart == /\ ~alive /\ alive' = TRUE
           /\ stage' = [u \in U |-> Restore(persisted[u])]
           /\ UNCHANGED <<intent, verdict, verified, persisted, runs, runKind, graced, userOK, retries, dismissed, dRuns, crashes>>

Done == /\ alive /\ \A u \in U: stage[u] \in {"done", "dismissed", "listed", "hidden"}
        /\ UNCHANGED vars

Next == \/ \E u \in U: Classify(u) \/ GraceExpire(u) \/ SendNow(u) \/ Fix(u) \/ Dismiss(u) \/ Start(u)
                       \/ Run(u) \/ Record(u) \/ Retry(u) \/ ConfirmDone(u)
        \/ Crash \/ Restart \/ Done

Spec == Init /\ [][Next]_vars

\* Keeps a broken variant that runs a route without end from exploring forever. Correct designs stay far below it.
Bounded == \A u \in U: runs[u] <= 3

\* Nothing goes out unless the user said so or it sat in まもなく送る for the whole grace period.
NoSendWithoutChance == \A u \in U: runs[u] > 0 => (userOK[u] \/ graced[u])
\* Worst case 2: a route runs for something the speaker did not mean only after a grace period nobody stopped.
NoWrongExecution == \A u \in U: runs[u] > 0 => (runKind[u] = intent[u] \/ graced[u])
\* The same without the grace exception: what plan A costs (risk.cfg expects this to fail).
StrictNoWrongExecution == \A u \in U: runs[u] > 0 => runKind[u] = intent[u]
\* A heavy route goes out on its own only after the extra Jev check agreed.
HeavyNeedsVerify == \A u \in U: (runs[u] > 0 /\ ~userOK[u] /\ runKind[u] \in Heavy) => verified[u]
AtMostOnce == \A u \in U: runs[u] <= 1 + retries[u]
DismissedNeverRuns == \A u \in U: dismissed[u] => runs[u] = dRuns[u]
\* Worst case 1 without a trace: an unsure or unrouted request ends where a menu user cannot see it.
NoSilentDrop == \A u \in U: stage[u] = "listed" => verdict[u] = "thought"
====
