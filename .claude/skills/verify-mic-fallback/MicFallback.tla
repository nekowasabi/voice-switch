---- MODULE MicFallback ----
\* Which microphone the macOS listener records from, and how it follows plug / unplug.
\* Every action is one main-thread step in Sources/voice-switch/MacApp.swift; the Swift name is next to it.
\* DESIGN = FALSE is the code at the sync commit (one saved mic, built-in fallback).
\* DESIGN = TRUE is the adopted "option 2": an ordered fallback list after the saved mic.
EXTENDS Sequences, FiniteSets, Naturals, TLC

CONSTANT DESIGN

Mics    == {"rx", "cam", "builtin"}   \* what inputDeviceIDs() lists (Wireless Mic Rx, webcam, built-in)
Builtin == "builtin"                  \* builtInInputDeviceID()
None    == "none"
Default == "default"                  \* InputRoute.systemDefault

VARIABLES
    conn,       \* the world: listed input devices that are plugged in
    dflt,       \* the world: kAudioHardwarePropertyDefaultInputDevice (None when nothing is plugged in)
    first,      \* Listener.deviceUID (UserDefaults inputDeviceUID); None = "システムのデフォルト"
    fbs,        \* DESIGN only: the ordered fallback list (inputFallbackUIDs); always <<>> in the current code
    wanted,     \* Listener.wanted
    running,    \* Listener.running
    route,      \* Listener.activeRoute: a mic, Default, or None (nil)
    active,     \* ghost: the device the open tap reads
    devPending, \* a device-list / default-device property listener block is queued on main (Listener.init)
    cfgPending, \* an AVAudioEngineConfigurationChange notification is queued on main (Listener.init)
    retry       \* Listener.retryScheduled: the 10 s retry closure is pending

vars == <<conn, dflt, first, fbs, wanted, running, route, active, devPending, cfgPending, retry>>

Lists == {<<>>} \cup {<<a>> : a \in Mics} \cup {<<a, b>> \in Mics \X Mics : a # b}

Cands(f, fb) == IF f = None THEN <<>> ELSE <<f>> \o fb

\* InputRoute.resolve(savedUID:) — with DESIGN the saved mic is followed by the fallback list.
Resolve(f, fb, c) ==
    LET cs   == Cands(f, fb)
        hits == {i \in 1..Len(cs) : cs[i] \in c}
    IN IF f = None THEN Default
       ELSE IF hits # {} THEN cs[CHOOSE i \in hits : \A j \in hits : i <= j]
       ELSE IF Builtin \in c THEN Builtin
       ELSE Default

Device(r) == IF r = Default THEN dflt ELSE r

TypeOK ==
    /\ conn \subseteq Mics /\ dflt \in Mics \cup {None}
    /\ first \in Mics \cup {None} /\ fbs \in Lists
    /\ route \in Mics \cup {Default, None} /\ active \in Mics \cup {None}
    /\ \A v \in {wanted, running, devPending, cfgPending, retry} : v \in BOOLEAN

Init ==
    /\ conn \in SUBSET Mics
    /\ dflt \in (IF conn = {} THEN {None} ELSE conn)
    /\ first \in Mics \cup {None}
    /\ fbs \in (IF DESIGN THEN Lists ELSE {<<>>})
    /\ wanted = FALSE /\ running = FALSE /\ route = None /\ active = None
    /\ devPending = FALSE /\ cfgPending = FALSE /\ retry = FALSE

\* Listener.attemptStart() -> start() with saved mic f.
\* Assumption: start() succeeds on a device that is plugged in and throws on one that is not
\* (pinning a missing device fails in AudioUnitSetProperty). On failure activeRoute = nil and the retry is scheduled.
AttemptStart(f, w0) ==
    LET r == Resolve(f, fbs, conn)
        d == Device(r)
    IN IF d \in conn
       THEN running' = TRUE /\ wanted' = TRUE /\ route' = r /\ active' = d /\ retry' = retry
       ELSE running' = FALSE /\ wanted' = w0 /\ route' = None /\ active' = None /\ retry' = TRUE

\* Listener.restart(): halt(); attemptStart()
Restart == AttemptStart(first, wanted)

\* A notification the engine posts when the device it reads disappears or the default it follows moves.
\* Assumption: posted for the Default route; for a pinned route it may or may not come (comment at the listener block).
CfgAfter(d, newDflt) ==
    \/ cfgPending' = TRUE
    \/ /\ cfgPending' = cfgPending
       /\ ~(route = Default /\ running /\ (newDflt # dflt \/ active = d))

\* --- the world ---------------------------------------------------------------
Plug(d) ==
    /\ d \notin conn /\ conn' = conn \cup {d}
    /\ dflt' \in (IF dflt = None THEN {d} ELSE {dflt, d})
    /\ devPending' = TRUE /\ CfgAfter(d, dflt')
    /\ UNCHANGED <<first, fbs, wanted, running, route, active, retry>>

Unplug(d) ==
    /\ d \in conn /\ conn' = conn \ {d}
    /\ dflt' \in (IF dflt # d THEN {dflt} ELSE IF conn' = {} THEN {None} ELSE conn')
    /\ devPending' = TRUE /\ CfgAfter(d, dflt')
    /\ UNCHANGED <<first, fbs, wanted, running, route, active, retry>>

\* The user changes the default input in System Settings.
DefaultMoves ==
    /\ \E d \in conn \ {dflt} : dflt' = d
    /\ devPending' = TRUE /\ CfgAfter(None, dflt')
    /\ UNCHANGED <<conn, first, fbs, wanted, running, route, active, retry>>

\* --- Listener ------------------------------------------------------------------
\* Listener.init: property listener for kAudioHardwarePropertyDevices / DefaultInputDevice.
DeviceEvent ==
    /\ devPending /\ devPending' = FALSE
    /\ IF wanted /\ Resolve(first, fbs, conn) # route
       THEN Restart
       ELSE UNCHANGED <<wanted, running, route, active, retry>>
    /\ UNCHANGED <<conn, dflt, first, fbs, cfgPending>>

\* Listener.init: AVAudioEngineConfigurationChange observer.
ConfigChange ==
    /\ cfgPending /\ cfgPending' = FALSE
    /\ IF wanted THEN Restart ELSE UNCHANGED <<wanted, running, route, active, retry>>
    /\ UNCHANGED <<conn, dflt, first, fbs, devPending>>

\* The 10 s closure in Listener.attemptStart.
Retry ==
    /\ retry
    /\ IF wanted /\ ~running
       THEN AttemptStart(first, wanted)
       ELSE retry' = FALSE /\ UNCHANGED <<wanted, running, route, active>>
    /\ UNCHANGED <<conn, dflt, first, fbs, devPending, cfgPending>>

\* Listener.checkStall: no audio from the device the tap reads.
Stall ==
    /\ running /\ active \notin conn
    /\ AttemptStart(first, wanted)
    /\ UNCHANGED <<conn, dflt, first, fbs, devPending, cfgPending>>

\* Listener.stop() (pause menu, pause hotkey, Meet hold, pauseWhenMicrophoneConnected).
Stop ==
    /\ wanted /\ wanted' = FALSE /\ running' = FALSE
    /\ UNCHANGED <<conn, dflt, first, fbs, route, active, devPending, cfgPending, retry>>

\* Listener.startRetrying() (resume).
Resume ==
    /\ ~wanted /\ AttemptStart(first, TRUE)
    /\ UNCHANGED <<conn, dflt, first, fbs, devPending, cfgPending>>

\* AppDelegate.selectDevice: stop, set deviceUID, start again if it was listening.
SelectDevice ==
    /\ \E f \in (Mics \cup {None}) \ {first} :
        /\ first' = f
        /\ IF wanted THEN AttemptStart(f, TRUE)
           ELSE UNCHANGED <<wanted, running, route, active, retry>>
    /\ UNCHANGED <<conn, dflt, fbs, devPending, cfgPending>>

\* DESIGN: editing the 予備マイク submenu saves the list and re-resolves through the device-event path.
SetFallbacks ==
    /\ DESIGN
    /\ \E s \in Lists \ {fbs} : fbs' = s
    /\ devPending' = TRUE
    /\ UNCHANGED <<conn, dflt, first, wanted, running, route, active, cfgPending, retry>>

Next ==
    \/ \E d \in Mics : Plug(d) \/ Unplug(d)
    \/ DefaultMoves \/ DeviceEvent \/ ConfigChange \/ Retry \/ Stall
    \/ Stop \/ Resume \/ SelectDevice \/ SetFallbacks

Spec == Init /\ [][Next]_vars

\* No queued device or engine notification: the app has heard about every change in the world.
Settled == ~devPending /\ ~cfgPending

\* MF-1 (the user's universal condition): when at least one mic is plugged in and the app has caught up,
\* a listening voice-switch records from a mic that is plugged in.
ValidMic == (Settled /\ wanted /\ conn # {}) => (running /\ active \in conn)

\* MF-2 (option 2): it records from the highest-priority candidate that is plugged in
\* (built-in, then the system default, when none is). Written apart from Resolve so a wrong Resolve fails it.
Expected ==
    IF first = None THEN dflt
    ELSE IF \E i \in 1..Len(Cands(first, fbs)) : Cands(first, fbs)[i] \in conn
         THEN Cands(first, fbs)[CHOOSE i \in 1..Len(Cands(first, fbs)) :
                  /\ Cands(first, fbs)[i] \in conn
                  /\ \A j \in 1..(i - 1) : Cands(first, fbs)[j] \notin conn]
    ELSE IF Builtin \in conn THEN Builtin ELSE dflt
TopCandidate == (Settled /\ wanted /\ conn # {}) => (running /\ active = Expected)
====
