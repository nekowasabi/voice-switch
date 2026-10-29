---- MODULE MeetPause ----
\* Pause during Google Meet (macOS). Every action is one main-thread step in
\* Sources/voice-switch/MacApp.swift; the name of the Swift function is next to it.
EXTENDS TLC

VARIABLES
    inCall,    \* a browser in AppDelegate.meetBrowsers holds mic input (the world, not our view of it)
    seen,      \* AppDelegate.meetInCall: what the last followMeet / togglePause read
    wanted,    \* Listener.wanted
    running,   \* Listener.running (the engine tap is open)
    held,      \* AppDelegate.meetHeld
    override,  \* AppDelegate.meetOverride
    retry,     \* Listener.retryScheduled: the 10 s retry timer is pending
    launching, \* applicationDidFinishLaunching has not reached listener.start() yet
    userOn,    \* ghost: the user's last menu/hotkey choice (starts on, as launch listens)
    alerted    \* ghost: an NSAlert told the user that start() failed (AppDelegate.fail)

vars == <<inCall, seen, wanted, running, held, override, retry, launching, userOn, alerted>>

TypeOK == \A v \in {inCall, seen, wanted, running, held, override, retry, launching, userOn, alerted} : v \in BOOLEAN

Init ==
    /\ inCall \in BOOLEAN
    /\ seen = FALSE /\ wanted = FALSE /\ running = FALSE
    /\ held = FALSE /\ override = FALSE /\ retry = FALSE
    /\ launching = TRUE /\ userOn = TRUE /\ alerted = FALSE

\* Listener.attemptStart(): start() either opens the tap and sets wanted, or
\* leaves wanted as it was (w0) and schedules the retry.
AttemptStart(w0) ==
    \/ running' = TRUE /\ wanted' = TRUE /\ retry' = retry
    \/ running' = FALSE /\ wanted' = w0 /\ retry' = TRUE

\* --- the world -------------------------------------------------------------
MeetStarts == ~inCall /\ inCall' = TRUE
              /\ UNCHANGED <<seen, wanted, running, held, override, retry, launching, userOn, alerted>>
MeetEnds   == inCall /\ inCall' = FALSE
              /\ UNCHANGED <<seen, wanted, running, held, override, retry, launching, userOn, alerted>>

\* --- AppDelegate.followMeet (2 s timer) -----------------------------------
Poll ==
    /\ seen' = inCall
    /\ IF inCall
       THEN IF wanted /\ ~override
            THEN /\ wanted' = FALSE /\ running' = FALSE /\ held' = TRUE   \* listener.stop()
                 /\ UNCHANGED <<override, retry>>
            ELSE UNCHANGED <<wanted, running, held, override, retry>>
       ELSE /\ override' = FALSE
            /\ IF held
               THEN held' = FALSE /\ AttemptStart(TRUE)                  \* listener.startRetrying()
               ELSE UNCHANGED <<held, wanted, running, retry>>
    /\ UNCHANGED <<inCall, launching, userOn, alerted>>

\* --- applicationDidFinishLaunching: try listener.start(); followMeet() ----
Launch ==
    /\ launching /\ launching' = FALSE
    /\ \/ alerted' = TRUE /\ UNCHANGED <<seen, wanted, running, held>>    \* start() threw: alert only
       \/ /\ seen' = inCall /\ alerted' = alerted
          /\ IF inCall /\ ~override
             THEN wanted' = FALSE /\ running' = FALSE /\ held' = TRUE
             ELSE wanted' = TRUE /\ running' = TRUE /\ held' = held
    /\ UNCHANGED <<inCall, override, retry, userOn>>

\* --- AppDelegate.togglePause (menu item and pause hotkey) ------------------
\* Assumption: not pressed before launch reaches listener.start().
Toggle ==
    /\ ~launching
    /\ IF wanted
       THEN /\ wanted' = FALSE /\ running' = FALSE /\ userOn' = FALSE
            /\ UNCHANGED <<seen, held, override, retry, alerted>>
       ELSE /\ userOn' = TRUE /\ seen' = inCall /\ held' = FALSE /\ override' = inCall
            /\ \/ running' = TRUE /\ wanted' = TRUE /\ alerted' = alerted \* start()
               \/ alerted' = TRUE /\ UNCHANGED <<running, wanted>>       \* start() threw: alert
            /\ UNCHANGED retry
    /\ UNCHANGED <<inCall, launching>>

\* --- AppDelegate.selectDevice: stop(); start() when wanted ----------------
SelectDevice ==
    /\ wanted
    /\ \/ running' = TRUE /\ wanted' = TRUE /\ alerted' = alerted
       \/ running' = FALSE /\ wanted' = FALSE /\ alerted' = TRUE          \* start() threw: alert
    /\ UNCHANGED <<inCall, seen, held, override, retry, launching, userOn>>

\* --- Listener restart paths -----------------------------------------------
\* AVAudioEngineConfigurationChange and the device-list listener: guard wanted, restart().
EngineRestart ==
    /\ wanted
    /\ AttemptStart(wanted)
    /\ UNCHANGED <<inCall, seen, held, override, launching, userOn, alerted>>

\* checkStall: guard running, halt(); attemptStart().
Stall ==
    /\ running
    /\ AttemptStart(wanted)
    /\ UNCHANGED <<inCall, seen, held, override, launching, userOn, alerted>>

\* The 10 s retry closure: retryScheduled = false; if wanted, !running { attemptStart() }.
RetryOK   == retry /\ wanted /\ ~running /\ running' = TRUE /\ wanted' = TRUE /\ retry' = FALSE
             /\ UNCHANGED <<inCall, seen, held, override, launching, userOn, alerted>>
RetryFail == retry /\ wanted /\ ~running /\ UNCHANGED vars
RetryNoop == retry /\ ~(wanted /\ ~running) /\ retry' = FALSE
             /\ UNCHANGED <<inCall, seen, wanted, running, held, override, launching, userOn, alerted>>

Next ==
    \/ MeetStarts \/ MeetEnds \/ Poll \/ Launch \/ Toggle \/ SelectDevice
    \/ EngineRestart \/ Stall \/ RetryOK \/ RetryFail \/ RetryNoop

\* The 2 s timer keeps firing, launch finishes, and a retry that keeps being tried eventually works.
Spec == Init /\ [][Next]_vars /\ WF_vars(Poll) /\ WF_vars(Launch) /\ SF_vars(RetryOK) /\ WF_vars(RetryNoop)

\* MP-F1: once the poll has seen the call, the mic stays closed unless the user resumed during it.
MicOffInCall == (inCall /\ seen /\ ~override) => ~running
\* MP-F1, the hold itself: while held, no restart path has reopened the mic.
HeldMeansOff == held => ~running
\* MP-F2: after the user pauses, nothing (call end included) reopens the mic.
ManualPauseKept == ~userOn => ~running
\* MP-F3: a hold whose call has ended turns back into listening, unless another call
\* started, the user paused, or an alert told the user that start() failed.
HeldResumes == (held /\ ~inCall) ~> (running \/ inCall \/ ~userOn \/ alerted)
====
