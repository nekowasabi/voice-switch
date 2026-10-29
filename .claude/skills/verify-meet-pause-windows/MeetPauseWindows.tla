---- MODULE MeetPauseWindows ----
\* Pause during Google Meet, ported to the Windows tray. Each supervisor command is one
\* step under TrayRuntimeSupervisor.gate (SemaphoreSlim: async waiters assumed FIFO, as implemented, not documented).
\* UI actions run on the WinForms thread in VoiceSwitchTrayContext and only enqueue a
\* command (`_ = RunCommandAsync(...)`), so the command runs later against a newer state.
\* "port:" marks the parts added by the port of AppDelegate.followMeet (HoldForMeetAsync, ResumeAfterMeetAsync, FollowMeet).
EXTENDS Sequences, Naturals, TLC

States == {"Paused", "Listening", "Error", "Finished"}
Kinds  == {"Start", "Pause", "Reload", "Hold", "Resume"}
Bys    == {"user", "device", "launch", "meet"}
C(k, by) == [k |-> k, by |-> by]

VARIABLES
    inCall,   \* chrome.exe / msedge.exe has an Active capture session (MicInUse; the world)
    state,    \* TrayRuntimeSupervisor.snapshot.State (Starting/Pausing/Stopping/Reloading happen inside one step)
    ui,       \* VoiceSwitchTrayContext.snapshot: what the menu items and TogglePause read
    devDiff,  \* WinMmCapture.CurrentDevice != EffectiveDevice (a mic was plugged, pulled, or the default moved)
    q,        \* commands waiting on the gate
    held,     \* port:supervisor meetHeld
    override, \* port:supervisor meetOverride (the user resumed during this call)
    userOn,   \* port:supervisor !userPaused (last user command was not 一時停止)
    honored,  \* ghost: a Hold has run during the current call
    drops,    \* ghost: commands dropped by RunCommandAsync's 20 s timeout
    touched   \* ghost: a user command has run since the hold last paused

vars == <<inCall, state, ui, devDiff, q, held, override, userOn, honored, drops, touched>>

Mic == state = "Listening"   \* a run exists, so the capture is open
Pending(k) == \E i \in 1..Len(q) : q[i].k = k
\* UI and device commands leave one slot so the 2 s poll can always enqueue (a bound of the model).
Enq(c, room) == Len(q) < room /\ q' = Append(q, c)

TypeOK ==
    /\ inCall \in BOOLEAN /\ devDiff \in BOOLEAN /\ held \in BOOLEAN
    /\ override \in BOOLEAN /\ userOn \in BOOLEAN /\ honored \in BOOLEAN
    /\ state \in States /\ ui \in States /\ drops \in 0..1 /\ touched \in BOOLEAN
    /\ q \in Seq([k : Kinds, by : Bys]) /\ Len(q) <= 3

\* TrayHost: `if (!options.Paused) _ = supervisor.StartAsync(...)`.
Init ==
    /\ inCall \in BOOLEAN
    /\ state = "Paused" /\ ui = "Paused" /\ devDiff = FALSE
    /\ held = FALSE /\ override = FALSE /\ honored = FALSE /\ drops = 0 /\ touched = FALSE
    /\ \E paused \in BOOLEAN :
         /\ q = IF paused THEN <<>> ELSE <<C("Start", "launch")>>
         /\ userOn = ~paused

\* factory.StartAsync succeeds, or throws and the catch sets Error.
Open(ok) == /\ state' = IF ok THEN "Listening" ELSE "Error"
            /\ devDiff' = IF ok THEN FALSE ELSE devDiff
StartRun(ok) == IF Mic THEN UNCHANGED <<state, devDiff>> ELSE Open(ok)

\* --- the world -------------------------------------------------------------
MeetStarts == ~inCall /\ inCall' = TRUE
              /\ UNCHANGED <<state, ui, devDiff, q, held, override, userOn, honored, drops, touched>>
MeetEnds   == inCall /\ inCall' = FALSE /\ honored' = FALSE
              /\ UNCHANGED <<state, ui, devDiff, q, held, override, userOn, drops, touched>>
DevChange  == ~devDiff /\ devDiff' = TRUE
              /\ UNCHANGED <<inCall, state, ui, q, held, override, userOn, honored, drops, touched>>

\* --- WinForms thread ---------------------------------------------------------
\* diagnosticsTimer (500 ms) ApplySnapshot(supervisor.Snapshot), and SnapshotChanged via BeginInvoke.
Tick == ui # state /\ ui' = state
        /\ UNCHANGED <<inCall, state, devDiff, q, held, override, userOn, honored, drops, touched>>

\* FollowDevice (every 4th tick, right after ApplySnapshot, so it reads the fresh state).
FollowDevice ==
    /\ \/ state = "Listening" /\ devDiff /\ Enq(C("Reload", "device"), 2)
       \/ state = "Error" /\ Enq(C("Start", "device"), 2)   \* ShouldRetryStart
    /\ UNCHANGED <<inCall, state, ui, devDiff, held, override, userOn, honored, drops, touched>>

\* The 開始 / 一時停止 menu items and TogglePause (pause hotkey), enabled from `ui`; IPC Start/Pause too.
UserStart == ui \in {"Paused", "Finished", "Error"} /\ Enq(C("Start", "user"), 2)
             /\ UNCHANGED <<inCall, state, ui, devDiff, held, override, userOn, honored, drops, touched>>
UserPause == ui = "Listening" /\ Enq(C("Pause", "user"), 2)
             /\ UNCHANGED <<inCall, state, ui, devDiff, held, override, userOn, honored, drops, touched>>
\* FillMicMenu item click and the 再読み込み item: RunCommandAsync(Reload).
UserReload == Enq(C("Reload", "user"), 2)
              /\ UNCHANGED <<inCall, state, ui, devDiff, held, override, userOn, honored, drops, touched>>

\* port:FollowMeet, a 2 s tick beside FollowDevice. Reads MicInUse and the supervisor's flags.
Poll ==
    /\ IF inCall
       THEN IF ~Pending("Hold") THEN Enq(C("Hold", "meet"), 3) ELSE UNCHANGED q
       ELSE IF (held \/ override) /\ ~Pending("Resume") THEN Enq(C("Resume", "meet"), 3) ELSE UNCHANGED q
    /\ UNCHANGED <<inCall, state, ui, devDiff, held, override, userOn, honored, drops, touched>>

\* RunCommandAsync's 20 s timeout cancels gate.WaitAsync; the command is gone.
Drop == drops < 1 /\ q # <<>> /\ drops' = drops + 1
        /\ \E i \in 1..Len(q) : q' = SubSeq(q, 1, i - 1) \o SubSeq(q, i + 1, Len(q))
        /\ UNCHANGED <<inCall, state, ui, devDiff, held, override, userOn, honored, touched>>

\* --- TrayRuntimeSupervisor, one command under the gate -------------------------
Exec(ok) ==
    /\ q # <<>> /\ q' = Tail(q)
    /\ LET c == Head(q) IN
       CASE c.k = "Start" /\ c.by = "user" ->
              \* StartAsync from 開始 / hotkey / IPC. port:a user resume clears the hold and wins until the call ends.
              /\ userOn' = TRUE /\ held' = FALSE /\ override' = inCall
              /\ StartRun(ok) /\ UNCHANGED honored
         [] c.k = "Start" /\ c.by # "user" ->
              \* StartAsync from FollowDevice or launch. port:never over a hold or a user pause.
              /\ IF held \/ ~userOn THEN UNCHANGED <<state, devDiff>> ELSE StartRun(ok)
              /\ UNCHANGED <<held, override, userOn, honored>>
         [] c.k = "Pause" ->
              \* PauseAsync (user only). port:records the user pause and drops the hold.
              /\ state' = "Paused" /\ userOn' = FALSE /\ held' = FALSE
              /\ UNCHANGED <<devDiff, override, honored>>
         [] c.k = "Reload" ->
              \* ReloadAsync: restarts only when it was running, otherwise ends Paused.
              /\ IF Mic THEN Open(ok) ELSE state' = "Paused" /\ UNCHANGED devDiff
              /\ UNCHANGED <<held, override, userOn, honored>>
         [] c.k = "Hold" ->
              \* port:PauseAsync for the call, unless the user resumed during it or had paused.
              /\ honored' = (honored \/ inCall)
              /\ IF ~override /\ userOn
                 THEN held' = TRUE /\ state' = "Paused"
                 ELSE UNCHANGED <<held, state>>
              /\ UNCHANGED <<devDiff, override, userOn>>
         [] c.k = "Resume" ->
              \* port:the call ended; start again if the hold paused it. Reads MicInUse again under
              \* the gate: a Resume queued at the end of one call can run during the next one.
              /\ IF inCall
                 THEN UNCHANGED <<override, held, state, devDiff>>
                 ELSE /\ override' = FALSE
                      /\ IF held THEN held' = FALSE /\ StartRun(ok) ELSE UNCHANGED <<held, state, devDiff>>
              /\ UNCHANGED <<userOn, honored>>
    /\ touched' = IF Head(q).by = "user" THEN TRUE ELSE IF held' /\ ~held THEN FALSE ELSE touched
    /\ UNCHANGED <<inCall, ui, drops>>

\* ObserveCompletionAsync: the run exits on its own (code 0 -> Finished, else Error).
Complete == Mic /\ state' \in {"Finished", "Error"}
            /\ UNCHANGED <<inCall, ui, devDiff, q, held, override, userOn, honored, drops, touched>>

Next ==
    \/ MeetStarts \/ MeetEnds \/ DevChange
    \/ Tick \/ FollowDevice \/ UserStart \/ UserPause \/ UserReload \/ Poll
    \/ Drop \/ Complete \/ \E ok \in BOOLEAN : Exec(ok)

\* Timers keep firing, the gate keeps serving, and a start that keeps being tried eventually works.
Spec == Init /\ [][Next]_vars
        /\ WF_vars(Tick) /\ WF_vars(Poll) /\ WF_vars(FollowDevice)
        /\ WF_vars(\E ok \in BOOLEAN : Exec(ok)) /\ SF_vars(Exec(TRUE))

\* MP-F1: while the hold is on, the capture is closed.
HeldMeansOff == held => ~Mic
\* MP-F1: once a Hold has run in this call, nothing reopens the mic unless the user resumed during it.
MicOffInCall == (inCall /\ honored /\ ~override) => ~Mic
\* MP-F2: after the user pauses, nothing (a retry, a reload, the call ending) reopens the mic.
ManualPauseKept == ~userOn => ~Mic
\* MP-F1, liveness: listening in a call without a user resume eventually stops.
HoldTakesEffect == (inCall /\ Mic /\ ~override) ~> (~Mic \/ ~inCall \/ override)
\* MP-F3: a hold whose call has ended eventually listens again, unless another call
\* started, the user ran a command (a pause, or a start that failed and a mic pick that
\* ReloadAsync turns from Error into Paused), or the runtime finished on its own.
HeldResumes == (held /\ ~inCall) ~> (Mic \/ inCall \/ touched \/ state = "Finished")
====
