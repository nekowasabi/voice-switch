---- MODULE MicLive ----
\* Which mic the macOS listener records from when a plugged-in mic sends only exact zeros (DJI Rx with
\* its transmitter off, a muted input). Each action is one main-thread step in MacApp.swift.
\* DESIGN = FALSE: the code before the zero check. DESIGN = TRUE: with Listener.silent.
EXTENDS Naturals, Sequences, FiniteSets, TLC

CONSTANT DESIGN

Mics    == {"rx", "cam", "builtin"}     \* inputDeviceIDs()
Builtin == "builtin"                    \* builtInInputDeviceID()
None    == "none"
Default == "default"                    \* InputRoute.systemDefault
Lists   == {<<"rx", "cam">>, <<"cam", "rx">>, <<>>}   \* Listener.candidates (saved mic + 予備マイク)
VARIABLES
    conn,       \* world: plugged-in mics
    send,       \* world: mics that send real samples (the rest of conn sends only exact zeros)
    dflt,       \* world: the system default input
    cands,      \* Listener.candidates
    wanted,     \* Listener.wanted
    running,    \* Listener.running
    route,      \* Listener.activeRoute: a mic, Default, or None (nil)
    active,     \* ghost: the device the open tap reads
    silent,     \* Listener.silent: mics demoted for sending only zeros
    watch,      \* ghost: mics with an open zero watcher (Listener.watchers)
    devPending, \* a device-list notice (or a candidate edit) is queued for Listener.reroute
    stale       \* ghost: something changed since the 1 s timer and the watchers last looked

vars == <<conn, send, dflt, cands, wanted, running, route, active, silent, watch, devPending, stale>>

First(cs, s) == cs[CHOOSE i \in 1..Len(cs) : cs[i] \in s /\ \A j \in 1..(i - 1) : cs[j] \notin s]
Hit(cs, s)   == \E i \in 1..Len(cs) : cs[i] \in s

\* InputRoute.choose: first plugged candidate, then the built-in mic, then (avoiding a silent default) any mic.
Choose(cs, pl, bi, skipD) ==
    IF cs = <<>> /\ ~skipD THEN Default
    ELSE IF Hit(cs, pl) THEN First(cs, pl)
    ELSE IF bi # None THEN bi
    ELSE IF skipD /\ pl # {} THEN CHOOSE m \in pl : TRUE
    ELSE Default

\* InputRoute.resolve(candidates:silent:): skip silent mics; when every mic is silent, today's choice.
Resolve(s) ==
    LET skipD == dflt \in s
        live  == Choose(cands, conn \ s, IF Builtin \in conn \ s THEN Builtin ELSE None, skipD)
    IN IF live # Default \/ ~skipD THEN live
       ELSE Choose(cands, conn, IF Builtin \in conn THEN Builtin ELSE None, FALSE)

Device(r) == IF r = Default THEN dflt ELSE r
\* A Default route follows the default at once (its restart path is verify-mic-fallback's ConfigChange).
Follow == active' = IF route = Default /\ running /\ dflt' # None THEN dflt' ELSE active

\* Listener.attemptStart -> start(); a failed start leaves the watchers as they were.
AttemptStart(s, w0) ==
    LET r == Resolve(s) IN
    IF Device(r) \in conn
    THEN running' = TRUE /\ wanted' = TRUE /\ route' = r /\ active' = Device(r) /\ watch' = s \cap conn
    ELSE running' = FALSE /\ wanted' = w0 /\ route' = None /\ active' = None /\ watch' = watch

\* Listener.reroute after silent changed: restart only for another route, else just resync the watchers.
Reroute(s) ==
    /\ silent' = s
    /\ IF wanted /\ Resolve(s) # route THEN AttemptStart(s, wanted)
       ELSE /\ watch' = IF wanted /\ running THEN s \cap conn ELSE {}
            /\ UNCHANGED <<wanted, running, route, active>>

Init ==
    /\ conn \in SUBSET Mics /\ send \in SUBSET Mics
    /\ dflt \in (IF conn = {} THEN {None} ELSE conn)
    /\ cands \in Lists
    /\ wanted = FALSE /\ running = FALSE /\ route = None /\ active = None
    /\ silent = {} /\ watch = {} /\ devPending = FALSE /\ stale = FALSE

Plug(d) ==
    /\ d \notin conn /\ conn' = conn \cup {d}
    /\ dflt' \in (IF dflt = None THEN {d} ELSE {dflt, d})
    /\ devPending' = TRUE /\ Follow /\ stale' = TRUE
    /\ UNCHANGED <<send, cands, wanted, running, route, silent, watch>>

\* A replugged device gets a new AudioDeviceID, so its mark and watcher go with the old one.
Unplug(d) ==
    /\ d \in conn /\ conn' = conn \ {d}
    /\ dflt' \in (IF dflt # d THEN {dflt} ELSE IF conn' = {} THEN {None} ELSE conn')
    /\ silent' = silent \ {d} /\ watch' = watch \ {d}
    /\ devPending' = TRUE /\ Follow /\ stale' = TRUE
    /\ UNCHANGED <<send, cands, wanted, running, route>>

\* A transmitter is switched off or on, or an input is muted or unmuted.
Toggle(d) ==
    /\ send' = IF d \in send THEN send \ {d} ELSE send \cup {d}
    /\ stale' = TRUE
    /\ UNCHANGED <<conn, dflt, cands, wanted, running, route, active, silent, watch, devPending>>

\* AppDelegate.selectDevice / toggleFallback / config reload: the list changes, then Listener.reroute.
SetCands ==
    /\ \E l \in Lists \ {cands} : cands' = l
    /\ devPending' = TRUE /\ stale' = TRUE
    /\ UNCHANGED <<conn, send, dflt, wanted, running, route, active, silent, watch>>

\* The coalesced device-list listener in Listener.init -> Listener.reroute (drops marks of unplugged mics).
DeviceEvent ==
    /\ devPending /\ devPending' = FALSE /\ stale' = TRUE
    /\ Reroute(silent \cap conn)
    /\ UNCHANGED <<conn, send, dflt, cands>>

\* One look by the 1 s timer (Listener.checkSilence: only exact zeros for Listener.zeroSeconds, which a
\* sending mic never gives: measured 2026-11-24) or by a zero watcher (non-zero sample on a demoted mic).
Tick ==
    /\ stale
    /\ IF DESIGN /\ running /\ active \in conn \ send /\ active \notin silent
       THEN Reroute(silent \cup {active})
       ELSE IF DESIGN /\ watch \cap send \cap silent # {}
       THEN Reroute(silent \ {CHOOSE d \in watch \cap send \cap silent : TRUE})
       ELSE UNCHANGED <<wanted, running, route, active, silent, watch>>
    /\ stale' = (silent' # silent)
    /\ UNCHANGED <<conn, send, dflt, cands, devPending>>

\* Listener.stop() (pause, hotkey, Meet hold, pauseWhenMicrophoneConnected) / startRetrying() (resume).
Stop ==
    /\ wanted /\ wanted' = FALSE /\ running' = FALSE /\ watch' = {} /\ stale' = TRUE
    /\ UNCHANGED <<conn, send, dflt, cands, route, active, silent, devPending>>

Resume ==
    /\ ~wanted /\ AttemptStart(silent, TRUE) /\ stale' = TRUE
    /\ UNCHANGED <<conn, send, dflt, cands, silent, devPending>>

Next ==
    \/ \E d \in Mics : Plug(d) \/ Unplug(d) \/ Toggle(d)
    \/ SetCands \/ DeviceEvent \/ Tick \/ Stop \/ Resume

Spec == Init /\ [][Next]_vars

\* The app has caught up: no queued notice, and the timer and watchers have looked since the last change.
Settled == ~devPending /\ ~stale
Live == conn \cap send

\* The user's priority, written apart from Resolve: first sending candidate; with no list a sending
\* default; then the sending built-in mic; else any sending mic.
Expected ==
    IF Hit(cands, Live) THEN First(cands, Live)
    ELSE IF cands = <<>> /\ dflt \in Live THEN dflt
    ELSE IF Builtin \in Live THEN Builtin
    ELSE "any"

\* LM-1: when some mic sends and the app has caught up, it records from the top mic that sends.
LiveMic == (Settled /\ wanted /\ Live # {}) =>
    (running /\ active \in Live /\ (Expected = "any" \/ active = Expected))

\* LM-2 (the Meet-pause promise of verify-meet-pause): no zero watcher keeps a mic open while paused.
WatchOff == ~wanted => watch = {}
====
