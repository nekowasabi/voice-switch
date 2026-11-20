---- MODULE StickyMode ----
\* Sticky delivery for one wake-word dictation.
\* Each action is one call of stickyStep in Sources/voice-switch/StickyMode.swift.
\* AX, the pasteboard, and CGEvent are not steps here. The adapter may paste only
\* when stickyMayPaste is true, and may press Enter only when stickyMayEnter is true.
\* BROKEN = TRUE adds the two deliveries the prototype must not have.
EXTENDS Integers

CONSTANT MaxAge, BROKEN

VARIABLES
  on, phase, kind, alive, switched, deliveries, entered, age, focus, bad

vars == <<on, phase, kind, alive, switched, deliveries, entered, age, focus, bad>>

Phases == {"idle", "armed", "recording", "delivering", "delivered", "fellback"}
Kinds == {"none", "field", "tmux", "secure", "nofield"}

Capturable(k) == k \in {"field", "tmux"}

TypeOK ==
  /\ on \in BOOLEAN /\ bad \in BOOLEAN
  /\ phase \in Phases /\ kind \in Kinds
  /\ alive \in BOOLEAN /\ switched \in BOOLEAN /\ entered \in BOOLEAN
  /\ deliveries \in 0..1 /\ age \in 0..MaxAge
  /\ focus \in {"user", "target"}

Init ==
  /\ on \in BOOLEAN
  /\ phase = "idle" /\ kind = "none" /\ alive = FALSE /\ switched = FALSE
  /\ deliveries = 0 /\ entered = FALSE /\ age = 0 /\ focus = "user" /\ bad = FALSE

begin(k, a) ==
  /\ kind' = k /\ alive' = a /\ switched' = FALSE
  /\ deliveries' = 0 /\ entered' = FALSE /\ age' = 0 /\ focus' = "user"
  /\ on' = on /\ bad' = bad

ToggleOn ==
  /\ ~on /\ phase \in {"idle", "delivered", "fellback"}
  /\ on' = TRUE
  /\ UNCHANGED <<phase, kind, alive, switched, deliveries, entered, age, focus, bad>>

ToggleOff ==
  /\ on
  /\ IF phase \in {"armed", "recording", "delivering"}
     THEN /\ on' = FALSE /\ phase' = "fellback" /\ entered' = FALSE
          /\ UNCHANGED <<kind, alive, switched, deliveries, age, focus, bad>>
     ELSE /\ on' = FALSE
          /\ UNCHANGED <<phase, kind, alive, switched, deliveries, entered, age, focus, bad>>

Start(k) ==
  /\ phase = "idle" /\ on /\ k \in {"field", "tmux", "secure", "nofield"}
  /\ begin(k, k \in {"field", "tmux", "secure"})
  /\ phase' = IF Capturable(k) THEN "armed" ELSE "recording"

Speech ==
  /\ on /\ phase = "armed"
  /\ phase' = "recording"
  /\ UNCHANGED <<on, kind, alive, switched, deliveries, entered, age, focus, bad>>

SwitchApp ==
  /\ on /\ phase \in {"armed", "recording", "delivering"}
  /\ switched' = TRUE
  /\ UNCHANGED <<on, phase, kind, alive, deliveries, entered, age, focus, bad>>

TargetGone ==
  /\ on /\ alive /\ phase \in {"armed", "recording", "delivering"}
  /\ alive' = FALSE
  /\ UNCHANGED <<on, phase, kind, switched, deliveries, entered, age, focus, bad>>

\* stickyStep(.tick). Reaching MaxAge drops an armed or recording dictation.
Tick ==
  /\ on /\ phase \in {"armed", "recording"} /\ age < MaxAge
  /\ age' = age + 1
  /\ phase' = IF age + 1 = MaxAge THEN "fellback" ELSE phase
  /\ UNCHANGED <<on, kind, alive, switched, deliveries, entered, focus, bad>>

StopEmpty ==
  /\ on /\ phase \in {"armed", "recording"}
  /\ phase' = "idle" /\ kind' = "none" /\ alive' = FALSE /\ switched' = FALSE
  /\ deliveries' = 0 /\ entered' = FALSE /\ age' = 0 /\ focus' = "user"
  /\ on' = on /\ bad' = bad

StopText ==
  /\ on /\ phase \in {"armed", "recording"}
  /\ phase' = IF alive /\ Capturable(kind) /\ age < MaxAge THEN "delivering" ELSE "fellback"
  /\ UNCHANGED <<on, kind, alive, switched, deliveries, entered, age, focus, bad>>

PasteOk ==
  /\ on /\ phase = "delivering" /\ deliveries = 0 /\ ~entered
  /\ alive /\ Capturable(kind) /\ age < MaxAge
  /\ deliveries' = 1 /\ focus' = "target"
  /\ UNCHANGED <<on, phase, kind, alive, switched, entered, age, bad>>

PasteFail ==
  /\ on /\ phase = "delivering" /\ deliveries = 0 /\ ~entered
  /\ phase' = "fellback"
  /\ UNCHANGED <<on, kind, alive, switched, deliveries, entered, age, focus, bad>>

EnterOk ==
  /\ on /\ phase = "delivering" /\ deliveries = 1 /\ ~entered /\ alive /\ Capturable(kind)
  /\ entered' = TRUE /\ phase' = "delivered"
  /\ focus' = IF switched THEN "user" ELSE "target"
  /\ UNCHANGED <<on, kind, alive, switched, deliveries, age, bad>>

EnterFail ==
  /\ on /\ phase = "delivering" /\ deliveries = 1 /\ ~entered
  /\ phase' = "fellback"
  /\ UNCHANGED <<on, kind, alive, switched, deliveries, entered, age, focus, bad>>

\* Broken build: paste into whatever is frontmost, not the captured field.
PasteFrontmost ==
  /\ BROKEN /\ on /\ phase = "delivering" /\ deliveries = 0 /\ ~entered
  /\ bad' = TRUE /\ deliveries' = 1 /\ entered' = TRUE /\ phase' = "delivered"
  /\ UNCHANGED <<on, kind, alive, switched, age, focus>>

\* Broken build: paste into a secure field.
PasteSecure ==
  /\ BROKEN /\ on /\ phase \in {"armed", "recording"} /\ kind = "secure"
  /\ bad' = TRUE /\ deliveries' = 1 /\ entered' = TRUE /\ phase' = "delivered"
  /\ UNCHANGED <<on, kind, alive, switched, age, focus>>

Reset ==
  /\ phase \in {"delivered", "fellback"}
  /\ phase' = "idle" /\ kind' = "none" /\ alive' = FALSE /\ switched' = FALSE
  /\ deliveries' = 0 /\ entered' = FALSE /\ age' = 0 /\ focus' = "user"
  /\ UNCHANGED <<on, bad>>

Done ==
  /\ phase \in {"idle", "delivered", "fellback"}
  /\ UNCHANGED vars

Next ==
  \/ ToggleOn \/ ToggleOff
  \/ \E k \in {"field", "tmux", "secure", "nofield"} : Start(k)
  \/ Speech \/ SwitchApp \/ TargetGone \/ Tick
  \/ StopEmpty \/ StopText
  \/ PasteOk \/ PasteFail \/ EnterOk \/ EnterFail
  \/ PasteFrontmost \/ PasteSecure
  \/ Reset \/ Done

Spec == Init /\ [][Next]_vars

NeverWrongPaste == bad = FALSE
NeverSecurePaste == ~(entered /\ kind = "secure") /\ ~(deliveries > 0 /\ kind = "secure")
AtMostOne == deliveries <= 1
EnterAfterPaste == entered => deliveries = 1 /\ Capturable(kind)
OffQuiet == phase \in {"armed", "recording", "delivering"} => on
FocusRestored ==
  (phase = "delivered" /\ entered /\ ~bad) =>
    (switched => focus = "user") /\ (~switched => focus = "target")

====
