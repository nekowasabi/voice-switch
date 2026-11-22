---- MODULE TriageExecEvents ----
\* Prints every transition of TriageExec under design.cfg as one line: the event with its arguments,
\* and the state before and after, without intent (what the speaker meant is not app state).
\* The app cannot know intent, so each row action is also printed as if the speaker of that row had meant
\* any other value (A(u, k)); those lines are tagged "A", the real ones "T".
\* Probe prints and then fails (FALSE), so EvSpec reaches exactly the states and steps of Spec.
\* gen.py turns the lines into transitions.json and checks that the "T" lines are exactly Next's steps.
EXTENDS TriageExec, TLC

A(u, k) == INSTANCE TriageExec WITH intent <- [intent EXCEPT ![u] = k]

Row(u)  == <<stage[u], persisted[u], verdict[u], verified[u], runs[u], runKind[u], graced[u], userOK[u], retries[u], dismissed[u], dRuns[u]>>
RowP(u) == <<stage'[u], persisted'[u], verdict'[u], verified'[u], runs'[u], runKind'[u], graced'[u], userOK'[u], retries'[u], dismissed'[u], dRuns'[u]>>
St  == <<alive, crashes, [u \in U |-> Row(u)]>>
StP == <<alive', crashes', [u \in U |-> RowP(u)]>>
Out(tag, e) == PrintT(ToString(<<tag, e, St, StP>>))
OutA(u, k, e) == Out(IF intent[u] = k THEN "T" ELSE "A", e)

EvInit == Init /\ PrintT(ToString(<<"I", St, <<U, Kinds, Routes, Heavy, Verdicts, MaxCrashes>>>>))

Probe ==
  /\ intent' = intent
  /\ \/ \E u \in U, k \in Intents:
          \/ \E v \in Verdicts, ok \in BOOLEAN, flagged \in BOOLEAN:
                A(u, k)!Classify(u, v, ok, flagged) /\ OutA(u, k, <<"classify", u, v, ok, flagged>>)
          \/ A(u, k)!GraceExpire(u) /\ OutA(u, k, <<"graceExpire", u>>)
          \/ A(u, k)!SendNow(u)     /\ OutA(u, k, <<"sendNow", u, verdict[u]>>)
          \/ A(u, k)!Fix(u)         /\ OutA(u, k, <<"fix", u, verdict'[u]>>)
          \/ A(u, k)!Dismiss(u)     /\ OutA(u, k, <<"dismiss", u>>)
          \/ A(u, k)!Start(u)       /\ OutA(u, k, <<"start", u>>)
          \/ A(u, k)!Run(u)         /\ OutA(u, k, <<"run", u>>)
          \/ A(u, k)!Fail(u)        /\ OutA(u, k, <<"fail", u>>)
          \/ A(u, k)!Record(u)      /\ OutA(u, k, <<"record", u>>)
          \/ A(u, k)!Retry(u)       /\ OutA(u, k, <<"retry", u, verdict[u]>>)
          \/ A(u, k)!ConfirmDone(u) /\ OutA(u, k, <<"confirmDone", u>>)
     \/ Crash   /\ Out("T", <<"crash">>)
     \/ Restart /\ Out("T", <<"restart">>)
     \/ Done    /\ Out("T", <<"done">>)
  /\ FALSE

EvSpec == EvInit /\ [][Next \/ Probe]_vars
====
