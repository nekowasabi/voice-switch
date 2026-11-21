---- MODULE StickyDelivery ----
\* Where sticky delivery puts the text, compared with where the user was typing when the wake word fired.
\* Code: Sources/voice-switch/MacApp.swift, functions captureStickyTarget, stickyTmuxPane, deliverSticky,
\* stickyPasteToPane, stickyFrontIs, stickyFocusIs, and the local stillTarget inside deliverSticky.
\* Two apps: "term" (a terminal whose one AX text area "ta" shows tmux panes p1/p2) and "notes"
\* (a native app with text fields f1, f2 and a secure field "sec"). The user may switch app, pane, or field,
\* or close a pane, at any step after the wake word.
EXTENDS Naturals

CONSTANTS
  MISS,         \* TRUE: stickyTmuxPane may return nil in a terminal that runs tmux (tmux not on the app's PATH,
                \*       list-clients over its 400 ms cap, or a client that is not a descendant of the terminal).
  CHECK_FRONT,  \* stickyFrontIs and the frontmost-pid half of stillTarget. FALSE only in BreakFront.cfg.
  CHECK_FOCUS,  \* stickyFocusIs in the guard and in stillTarget. FALSE only in BreakFocus.cfg.
  ENTER_GATE,   \* session.enter (macOS.stickyEnterApps) gates Return. FALSE only in BreakEnter.cfg.
  ENTER_APPS    \* the apps listed in macOS.stickyEnterApps

None == "none"
Panes == {"p1", "p2"}
NotesEls == {"f1", "f2", "sec"}

VARIABLES front, notesFocus, pane, closed, pc, kind, cap, intent, pastes, landed, entered
vars == <<front, notesFocus, pane, closed, pc, kind, cap, intent, pastes, landed, entered>>

Loc(a, e, p) == [app |-> a, el |-> e, pane |-> p]
FocusOf(a) == IF a = "term" THEN "ta" ELSE notesFocus
\* Where a Cmd+V or Return posted to .cghidEventTap lands: the frontmost app's focused element, and in the
\* terminal, whichever tmux pane is active.
Here == IF front = "term" THEN Loc("term", "ta", pane) ELSE Loc("notes", notesFocus, None)
\* IsSecureEventInputEnabled() is on while the frontmost app focuses a secure field.
SecureNow == front = "notes" /\ notesFocus = "sec"
\* stillTarget(): frontmost pid, no secure input, same focused element.
StillTarget ==
  /\ (~CHECK_FRONT \/ front = cap.app)
  /\ ~SecureNow
  /\ (~CHECK_FOCUS \/ FocusOf(cap.app) = cap.el)

Init ==
  /\ front \in {"term", "notes"}
  /\ notesFocus \in NotesEls
  /\ pane \in Panes
  /\ closed = {}
  /\ pc = "idle"
  /\ kind = None
  /\ cap = Loc(None, None, None)
  /\ intent = Loc(None, None, None)
  /\ pastes = 0
  /\ landed = {}
  /\ entered = {}

\* ---- the user, at any time after the wake word ----
Busy == pc \in {"recording", "check", "activate", "wait", "post", "enter", "tmuxEnter"}
User == <<pc, kind, cap, intent, pastes, landed, entered>>

SwitchApp ==
  /\ Busy /\ front' = IF front = "term" THEN "notes" ELSE "term"
  /\ UNCHANGED <<notesFocus, pane, closed>> /\ UNCHANGED User
FocusField ==
  /\ Busy /\ \E e \in NotesEls \ {notesFocus} : notesFocus' = e
  /\ UNCHANGED <<front, pane, closed>> /\ UNCHANGED User
SwitchPane ==
  /\ Busy /\ \E p \in Panes \ (closed \cup {pane}) : pane' = p
  /\ UNCHANGED <<front, notesFocus, closed>> /\ UNCHANGED User
ClosePane ==
  /\ Busy /\ closed = {}
  /\ \E p \in Panes :
       /\ closed' = {p}
       /\ pane' = IF pane = p THEN CHOOSE q \in Panes : q # p ELSE pane
  /\ UNCHANGED <<front, notesFocus>> /\ UNCHANGED User

\* ---- the code ----
\* captureStickyTarget: secure first, then a tmux client under the frontmost app, else the AX text field.
Wake ==
  /\ pc = "idle"
  /\ intent' = Here
  /\ \E k \in (IF SecureNow THEN {"secure"}
               ELSE IF front = "term" THEN (IF MISS THEN {"tmux", "field"} ELSE {"tmux"})
               ELSE {"field"}) :
       /\ kind' = k
       /\ cap' = Loc(front, FocusOf(front), IF k = "tmux" THEN pane ELSE None)
  /\ pc' = "recording"
  /\ UNCHANGED <<front, notesFocus, pane, closed, pastes, landed, entered>>

Fallback == pc' = "fallback" /\ UNCHANGED <<front, notesFocus, pane, closed, kind, cap, intent, pastes, landed, entered>>

\* The text is ready (Superwhisper result or local transcript) and deliverSticky starts.
TextReady ==
  /\ pc = "recording" /\ pc' = "check"
  /\ UNCHANGED <<front, notesFocus, pane, closed, kind, cap, intent, pastes, landed, entered>>

\* deliverSticky: secureNow refuses before either path.
Check ==
  /\ pc = "check"
  /\ IF kind = "secure" \/ SecureNow THEN Fallback
     ELSE /\ pc' = IF kind = "tmux" THEN "tmuxPaste" ELSE "activate"
          /\ UNCHANGED <<front, notesFocus, pane, closed, kind, cap, intent, pastes, landed, entered>>

\* stickyPasteToPane: load-buffer, then paste-buffer -p -t <captured pane>. Fails if the pane is gone.
TmuxPaste ==
  /\ pc = "tmuxPaste"
  /\ IF cap.pane \in closed THEN Fallback
     ELSE /\ landed' = landed \cup {Loc("term", "ta", cap.pane)}
          /\ pastes' = pastes + 1
          /\ pc' = "tmuxEnter"
          /\ UNCHANGED <<front, notesFocus, pane, closed, kind, cap, intent, entered>>

\* send-keys -t <captured pane> Enter, only when session.enter.
TmuxEnter ==
  /\ pc = "tmuxEnter"
  /\ entered' = IF ~ENTER_GATE \/ cap.app \in ENTER_APPS THEN entered \cup {Loc("term", "ta", cap.pane)} ELSE entered
  /\ pc' = "done"
  /\ UNCHANGED <<front, notesFocus, pane, closed, kind, cap, intent, pastes, landed>>

\* target.activate(): it may or may not bring the app to the front.
Activate ==
  /\ pc = "activate"
  /\ front' \in {front, cap.app}
  /\ pc' = "wait"
  /\ UNCHANGED <<notesFocus, pane, closed, kind, cap, intent, pastes, landed, entered>>

\* guard await stickyFrontIs(session.pid), stickyFocusIs(session.pid, element)
Wait ==
  /\ pc = "wait"
  /\ IF (~CHECK_FRONT \/ front = cap.app) /\ (~CHECK_FOCUS \/ FocusOf(cap.app) = cap.el)
     THEN pc' = "post" /\ UNCHANGED <<front, notesFocus, pane, closed, kind, cap, intent, pastes, landed, entered>>
     ELSE Fallback

\* guard stillTarget(), postKey(0x09, flags: .maskCommand). One step: assumption A1 in SKILL.md.
Post ==
  /\ pc = "post"
  /\ IF StillTarget
     THEN /\ landed' = landed \cup {Here}
          /\ pastes' = pastes + 1
          /\ pc' = "enter"
          /\ UNCHANGED <<front, notesFocus, pane, closed, kind, cap, intent, entered>>
     ELSE Fallback

\* if !session.enter { no key } else if stillTarget(), postKey(0x24) { Return } else { enterFail }
Enter ==
  /\ pc = "enter"
  /\ entered' = IF (~ENTER_GATE \/ cap.app \in ENTER_APPS) /\ StillTarget THEN entered \cup {Here} ELSE entered
  /\ pc' = "done"
  /\ UNCHANGED <<front, notesFocus, pane, closed, kind, cap, intent, pastes, landed>>

Finished == pc \in {"done", "fallback"} /\ UNCHANGED vars

Next ==
  \/ SwitchApp \/ FocusField \/ SwitchPane \/ ClosePane
  \/ Wake \/ TextReady \/ Check \/ TmuxPaste \/ TmuxEnter \/ Activate \/ Wait \/ Post \/ Enter \/ Finished

Spec == Init /\ [][Next]_vars

\* The text lands only where the user was typing at the wake word (pane included).
NeverWrongPaste == landed \subseteq {intent}
\* A secure field never receives the text.
NeverSecurePaste == \A l \in landed : l.el # "sec"
\* Return goes only where the text went, and only in an app listed in macOS.stickyEnterApps.
EnterOnlyAllowed == entered \subseteq landed /\ \A l \in entered : l.app \in ENTER_APPS
\* One dictation pastes at most once.
AtMostOnePaste == pastes <= 1
====
