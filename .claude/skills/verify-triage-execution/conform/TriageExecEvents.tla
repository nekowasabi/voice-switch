---- MODULE TriageExecEvents ----
\* Prints every transition of TriageExec under design.cfg as one line: the event with its arguments,
\* and the state before and after, without intent (what the speaker meant is not app state).
\* The app cannot know intent, so each row action is also printed as if the speaker of that row had meant
\* any other value and any other candidate (A(u, k, r)); those lines are tagged "A", the real ones "T".
\* picked is left out with intent: it only records for NoWrongDestination that the user chose a candidate.
\* Probe prints and then fails (FALSE), so EvSpec reaches exactly the states and steps of Spec.
\* gen.py turns the lines into transitions.json and checks that the "T" lines are exactly Next's steps.
EXTENDS TriageExec, TLC

A(u, k, r) == INSTANCE TriageExec WITH intent <- [intent EXCEPT ![u] = k], intentRoute <- [intentRoute EXCEPT ![u] = r]

Row(u)  == <<stage[u], persisted[u], verdict[u], verified[u], runs[u], runKind[u], graced[u], userOK[u], retries[u], dismissed[u], dRuns[u], route[u], runRoute[u]>>
RowP(u) == <<stage'[u], persisted'[u], verdict'[u], verified'[u], runs'[u], runKind'[u], graced'[u], userOK'[u], retries'[u], dismissed'[u], dRuns'[u], route'[u], runRoute'[u]>>
St  == <<alive, crashes, [u \in U |-> Row(u)]>>
StP == <<alive', crashes', [u \in U |-> RowP(u)]>>
Out(tag, e) == PrintT(ToString(<<tag, e, St, StP>>))
OutA(u, k, r, e) == Out(IF intent[u] = k /\ intentRoute[u] = r THEN "T" ELSE "A", e)

EvInit == Init /\ PrintT(ToString(<<"I", St, <<U, Kinds, Routes, Heavy, Verdicts, MaxCrashes, AlternateRoutes>>>>))

Probe ==
  /\ intent' = intent
  /\ intentRoute' = intentRoute
  /\ \/ \E u \in U:
          \* These actions do not read intent or intentRoute, so the real step is the only one.
          \/ \E v \in Verdicts, ok \in BOOLEAN, flagged \in BOOLEAN:
                Classify(u, v, ok, flagged) /\ Out("T", <<"classify", u, v, ok, flagged>>)
          \/ GraceExpire(u) /\ Out("T", <<"graceExpire", u>>)
          \/ Dismiss(u)     /\ Out("T", <<"dismiss", u>>)
          \/ Start(u)       /\ Out("T", <<"start", u>>)
          \/ Run(u)         /\ Out("T", <<"run", u>>)
          \/ Fail(u)        /\ Out("T", <<"fail", u>>)
          \/ Record(u)      /\ Out("T", <<"record", u>>)
          \/ ConfirmDone(u) /\ Out("T", <<"confirmDone", u>>)
          \* These read what the speaker meant: the user's choice, which the app takes as given.
          \/ \E k \in Intents, r \in RouteIndices:
                \/ \E i \in RouteIndices, ok \in BOOLEAN, flagged \in BOOLEAN:
                      A(u, k, r)!SwitchDestination(u, i, ok, flagged) /\ OutA(u, k, r, <<"switchDestination", u, i, ok, flagged>>)
                \/ A(u, k, r)!SendNow(u) /\ OutA(u, k, r, <<"sendNow", u, verdict[u], route'[u]>>)
                \/ A(u, k, r)!Fix(u)     /\ OutA(u, k, r, <<"fix", u, verdict'[u], route'[u]>>)
                \/ A(u, k, r)!Retry(u)   /\ OutA(u, k, r, <<"retry", u, verdict[u]>>)
     \/ Crash   /\ Out("T", <<"crash">>)
     \/ Restart /\ Out("T", <<"restart">>)
     \/ Done    /\ Out("T", <<"done">>)
  /\ FALSE

EvSpec == EvInit /\ [][Next \/ Probe]_vars
====
