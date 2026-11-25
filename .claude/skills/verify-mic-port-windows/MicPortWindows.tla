---- MODULE MicPortWindows ----
\* The Windows port of the macOS mic priority (verify-mic-fallback) and zero skip (verify-mic-live), against the
\* tray's real ordering: FollowDevice polls every 2 s and only queues; commands run later, one at a time, under
\* TrayRuntimeSupervisor.gate, against the state at that moment. DESIGN = FALSE is the Windows code at the sync
\* commit (one pinned WinMM name, else the default). DESIGN = TRUE is the proposed port (SKILL.md, "The port").
EXTENDS Naturals, Sequences, FiniteSets, TLC
CONSTANT DESIGN

Mics    == {"rx", "cam", "builtin"}     \* WinMmCapture.InputDevices()
Builtin == "builtin"                    \* proposed: the endpoint the port treats as built-in
None    == "none"
Default == "default"
Lists   == IF DESIGN THEN {<<"rx", "cam">>, <<"cam", "rx">>, <<>>}       \* microphones / pinned + 予備
           ELSE {<<"rx">>, <<"cam">>, <<>>}                              \* TraySettings.MicDevice or null
Cmds    == {"Reload", "Start", "UPause", "UStart"}
VARIABLES
    conn, send, dflt,   \* world: plugged-in mics, mics that send real samples, Windows' default recording device
    cands,              \* the candidate list
    st,                 \* supervisor snapshot.State: "Listening", "Error", "Paused"
    snap,               \* VoiceSwitchTrayContext.snapshot (ApplySnapshot, 500 ms)
    userOn,             \* hold # TrayHold.User
    cur,                \* WinMmCapture.CurrentDevice (None when no capture is open)
    silent, watch,      \* DESIGN: mics marked for sending only zeros / mics with an open zero watcher
    q                   \* commands waiting on gate (SemaphoreSlim, FIFO assumed)
vars == <<conn, send, dflt, cands, st, snap, userOn, cur, silent, watch, q>>

First(cs, s) == cs[CHOOSE i \in 1..Len(cs) : cs[i] \in s /\ \A j \in 1..(i - 1) : cs[j] \notin s]
Hit(cs, s)   == \E i \in 1..Len(cs) : cs[i] \in s
\* Copied from MicLive.tla (InputRoute.choose / resolve on macOS): the port must compute the same route.
Choose(cs, pl, bi, skipD) ==
    IF cs = <<>> /\ ~skipD THEN Default
    ELSE IF Hit(cs, pl) THEN First(cs, pl)
    ELSE IF bi # None THEN bi
    ELSE IF skipD /\ pl # {} THEN CHOOSE m \in pl : TRUE
    ELSE Default
Resolve(s) ==
    LET skipD == dflt \in s
        live  == Choose(cands, conn \ s, IF Builtin \in conn \ s THEN Builtin ELSE None, skipD)
    IN IF live # Default \/ ~skipD THEN live
       ELSE Choose(cands, conn, IF Builtin \in conn THEN Builtin ELSE None, FALSE)
Device(r) == IF r = Default THEN dflt ELSE r
\* WinMmCapture.EffectiveDevice: today the pinned name when listed, else the default.
Eff(s) == IF DESIGN THEN Device(Resolve(s))
          ELSE IF cands # <<>> /\ cands[1] \in conn THEN cands[1] ELSE dflt

\* factory.StartAsync -> WinMmCapture.Open(Eff). Assumption: waveInOpen fails on a device that is not listed.
Open(s) == IF Eff(s) \in conn
           THEN st' = "Listening" /\ cur' = Eff(s) /\ watch' = (IF DESIGN THEN s \cap conn ELSE {})
           ELSE st' = "Error" /\ cur' = None /\ watch' = {}
Close(to) == st' = to /\ cur' = None /\ watch' = {}   \* the run and every watcher it owns are disposed
Enq(c) == IF Len(q) < 2 /\ c \notin {q[i] : i \in 1..Len(q)} THEN q' = Append(q, c) ELSE q' = q

Init ==
    /\ conn \in SUBSET Mics /\ send \in SUBSET Mics /\ dflt \in (IF conn = {} THEN {None} ELSE conn)
    /\ cands \in Lists /\ userOn \in BOOLEAN
    /\ st = "Paused" /\ snap = "Paused" /\ cur = None /\ silent = {} /\ watch = {}
    /\ q = IF userOn THEN <<"Start">> ELSE <<>>        \* TrayHost: StartAsync(StartOrigin.Launch)

\* --- the world --- (WinMM never tells an open capture that its device left: cur stays until a Reload)
Plug(d) == /\ d \notin conn /\ conn' = conn \cup {d}
           /\ dflt' \in (IF dflt = None THEN {d} ELSE {dflt, d})
           /\ UNCHANGED <<send, cands, st, snap, userOn, cur, silent, watch, q>>
Unplug(d) == /\ d \in conn /\ conn' = conn \ {d} /\ watch' = watch \ {d}
             /\ dflt' \in (IF dflt # d THEN {dflt} ELSE IF conn' = {} THEN {None} ELSE conn')
             /\ UNCHANGED <<send, cands, st, snap, userOn, cur, silent, q>>
Toggle(d) == /\ send' = IF d \in send THEN send \ {d} ELSE send \cup {d}
             /\ UNCHANGED <<conn, dflt, cands, st, snap, userOn, cur, silent, watch, q>>
\* ObserveCompletionAsync: the run exits with an error (a lost device, a crash) at any moment.
Fail == /\ st = "Listening" /\ Close("Error")
        /\ UNCHANGED <<conn, send, dflt, cands, snap, userOn, silent, q>>

\* --- the tray ---
Apply == /\ snap # st /\ snap' = st
         /\ UNCHANGED <<conn, send, dflt, cands, st, userOn, cur, silent, watch, q>>
\* FollowDevice (+ ShouldRetryStart, whose 40 s fallback tick makes "changed" eventually true).
\* DESIGN: it reopens a watcher on every marked mic that is listed. A WinMM name unplugged and replugged between
\* two polls keeps its mark (macOS gets a new AudioDeviceID instead), so only a watcher can clear it.
PollCmd(s) == IF snap = "Listening" /\ cur # None /\ cur # Eff(s) THEN "Reload"
              ELSE IF snap = "Error" /\ Eff(s) # None THEN "Start" ELSE None
PollWatch(s) == IF DESIGN /\ st = "Listening" THEN s \cap conn ELSE watch
Poll == /\ watch' = PollWatch(silent)
        /\ IF PollCmd(silent) # None THEN Enq(PollCmd(silent)) ELSE q' = q
        /\ UNCHANGED <<conn, send, dflt, cands, st, snap, userOn, cur, silent>>
\* DESIGN: the capture's zero check (macOS Listener.checkSilence) marks the device it reads.
ZeroDue == DESIGN /\ st = "Listening" /\ cur \in conn \ send /\ cur \notin silent
Zero == /\ ZeroDue /\ silent' = silent \cup {cur}
        /\ UNCHANGED <<conn, send, dflt, cands, st, snap, userOn, cur, watch, q>>
\* DESIGN: a zero watcher hears a non-zero sample on a marked mic.
ReturnDue == DESIGN /\ watch \cap send \cap silent # {}
Return == /\ ReturnDue /\ \E d \in watch \cap send \cap silent : silent' = silent \ {d}
          /\ UNCHANGED <<conn, send, dflt, cands, st, snap, userOn, cur, watch, q>>
UserPause == /\ snap # "Paused" /\ Enq("UPause")
             /\ UNCHANGED <<conn, send, dflt, cands, st, snap, userOn, cur, silent, watch>>
UserStart == /\ snap # "Listening" /\ Enq("UStart")
             /\ UNCHANGED <<conn, send, dflt, cands, st, snap, userOn, cur, silent, watch>>
\* FillMicMenu click (or the 予備 list): save, then RunCommandAsync(Reload).
SetCands == /\ \E l \in Lists \ {cands} : cands' = l
            /\ Enq("Reload")
            /\ UNCHANGED <<conn, send, dflt, st, snap, userOn, cur, silent, watch>>

\* One gate step. ReloadAsync: from a non-running state it ends Paused, but an Error stays Error, so the
\* device retry still runs. StartAsync(DeviceRetry/Launch): refused under TrayHold.User; StartRunAsync no-ops
\* while Listening. PauseAsync from the user sets TrayHold.User.
Exec == /\ q # <<>> /\ q' = Tail(q)
        /\ CASE q[1] = "Reload" ->
                  IF st = "Listening" THEN Open(silent)
                  ELSE IF st = "Error" THEN UNCHANGED <<st, cur, watch>> ELSE Close("Paused")
             [] q[1] = "Start" ->
                  IF ~userOn \/ st = "Listening" THEN UNCHANGED <<st, cur, watch>> ELSE Open(silent)
             [] q[1] = "UPause" -> Close("Paused")
             [] q[1] = "UStart" ->
                  IF st = "Listening" THEN UNCHANGED <<st, cur, watch>> ELSE Open(silent)
        /\ userOn' = CASE q[1] = "UPause" -> FALSE [] q[1] = "UStart" -> TRUE [] OTHER -> userOn
        /\ UNCHANGED <<conn, send, dflt, cands, snap, silent>>

Next == \/ \E d \in Mics : Plug(d) \/ Unplug(d) \/ Toggle(d)
        \/ Fail \/ Apply \/ Poll \/ Zero \/ Return \/ UserPause \/ UserStart \/ SetCands \/ Exec
Spec == Init /\ [][Next]_vars

\* Caught up: nothing queued, the menu shows the state, and the code's own poll, zero check and watchers have
\* nothing left to do (the same operators the actions use, so removing a step from the code cannot hide a bug).
Settled == q = <<>> /\ snap = st /\ ~ZeroDue /\ ~ReturnDue
           /\ PollCmd(silent) = None /\ watch = PollWatch(silent)
Live == conn \cap send
\* Copied from MicLive.tla: what macOS records from.
Expected ==
    IF Hit(cands, Live) THEN First(cands, Live)
    ELSE IF cands = <<>> /\ dflt \in Live THEN dflt
    ELSE IF Builtin \in Live THEN Builtin
    ELSE "any"
\* LM-1 on Windows: once caught up, with the user not paused and some mic sending, the tray records from the
\* mic macOS would record from.
SameMicAsMac == (Settled /\ userOn /\ Live # {}) =>
    (st = "Listening" /\ cur \in Live /\ (Expected = "any" \/ cur = Expected))
\* LM-2 on Windows: no zero watcher holds a mic open while the user has paused.
WatchOff == ~userOn /\ st # "Listening" => watch = {}
====
