---
name: verify-sticky-mode
description: Check that macOS sticky dictation (macOS.stickyWakeWords) pastes only into the field or tmux pane the user was typing in when the wake word fired, never into another app, field, pane, or a secure field, and presses Return only where it pasted and only in apps listed in macOS.stickyEnterApps. TLA+ model of the delivery adapter plus anchors that tie each model step to a guard in MacApp.swift. Use when a change touches captureStickyTarget, stickyTmuxPane, deliverSticky, stickyPasteToPane, stickyFrontIs, stickyFocusIs, StickySession.enter, or StickyMode.swift.
---

# Verify sticky delivery

Sticky mode remembers where the user was typing when a sticky wake word fired and delivers the text there later, even after the user moved on. The harm it must never do is put the sentence (and maybe Return) into whatever the user is looking at now.

The previous model (StickyMode.tla, removed) checked only `stickyStep`. Its wrong-paste property could fail only in a deliberately broken config, and it never modelled the AX, tmux, and CGEvent steps, so it passed while the app pasted into the wrong tmux pane. This model is about those steps.

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `NeverWrongPaste` | The text lands only in the app, field, and tmux pane that were active at the wake word. | The sentence goes to another app, field, or pane (seen on 2026-11-21: Ghostty, pane switch, text in the other pane). |
| `NeverSecurePaste` | A secure field never receives the text. | A password field gets the dictation. |
| `EnterOnlyAllowed` | Return goes only where the text went, and only in an app listed in `macOS.stickyEnterApps`. | A chat sends a message, or a shell runs a line, the user did not mean to submit. |
| `AtMostOnePaste` | One dictation pastes at most once. | The sentence appears twice. |

## Check items, in this order

1. **Observe on the real Mac (a person does this).** Build and run the app (`make relaunch`), then: in a Ghostty or WezTerm tmux pane say a sticky wake word and a sentence, say the stop word, switch to another tmux pane. Expected: the text appears in the first pane, and the log (`make logs`) has `sticky: captured kind=tmux app=<bundle> pane=%N` then `sticky: tmux chars=… enter=…`. If the log says `kind=field` for a terminal, the capture fell back silently (finding F1) and the result of the next items does not describe this run. Repeat once in a native app (Notes): speak, switch to another app, expect the text in Notes or on the clipboard, never in the other app. Until a person has done this on the current commit, report the change as model-checked only.
2. **Anchors.** `sh .claude/skills/verify-sticky-mode/anchors.sh` prints `ok` for every line of `anchors.tsv`. Each line is one guard in `Sources/voice-switch/MacApp.swift` or `PaneRoute.swift` that a model step stands for. A FAIL means the code no longer matches the model: read the change, update the model action and the anchor together, or treat the removed guard as a bug.
3. **Model.** `sh .claude/skills/verify-sticky-mode/check.sh` ends with `Model checking completed. No error has been found.` (710 distinct states at the sync commit).
4. **The model can fail.** `sh .claude/skills/verify-sticky-mode/break.sh` prints `ok` three times: without the frontmost-app check (`BreakFront.cfg`) and without the focused-element check (`BreakFocus.cfg`) TLC finds a wrong paste; without the `stickyEnterApps` gate (`BreakEnter.cfg`) it finds a Return in a non-listed app.
5. **Finding F1 still stands.** `sh .claude/skills/verify-sticky-mode/finding.sh` exits 0 and prints `F1 stands`. When it exits 1, F1 is fixed: update the Findings section and drop this item.
6. **PATH of the running app.** `sh .claude/skills/verify-sticky-mode/verify.sh path` prints `ok` when the running app can run tmux, `WARN` when it cannot (then F1 happens on every sticky wake in a tmux pane).
7. **stickyStep logic.** The Swift harness `tests/macos/StickyMode/main.swift` prints `PASS sticky logic 300 states`. It covers the pure state machine in `StickyMode.swift`, not where the text lands.

`sh .claude/skills/verify-sticky-mode/verify.sh drive` runs items 2 to 7. `verify.sh doctor` checks java, swiftc, and gh. TLC is pinned to v1.7.4 (`tla2tools.jar`, sha256 checked, fetched with `gh` into `~/.cache/tla2tools-v1.7.4/`, the same jar as `verify-mic-fallback`). A full drive takes about 10 seconds.

Read every TLC result, pass or fail, with the `/statechart` skill: a violation as its shortest path in plain words, a pass as a chart of the states.

## What the model is

`StickyDelivery.tla`. Two apps: `term`, a terminal whose single AX text area `ta` shows tmux panes `p1`/`p2`, and `notes`, a native app with fields `f1`, `f2` and a secure field `sec`. After the wake word the user may switch app, switch pane, move focus between fields, or close a pane, between any two code steps. `intent` is where the user was typing at the wake word; `landed` and `entered` are where Cmd+V / paste-buffer and Return actually arrived.

| Model step | Code (Sources/voice-switch/MacApp.swift) |
|---|---|
| `Wake` | `captureStickyTarget`: secure (`IsSecureEventInputEnabled` or subrole `AXSecureTextField`) first, then `stickyTmuxPane(terminalPID:)` (tmux `list-clients` + `descends`), else the AX focused text field. `MISS` allows the tmux lookup to return nil |
| `TextReady` | `deliverSticky` is called from `handoff` or `storeLocal` |
| `Check` | `if secureNow { … stickyFallback(reason: "secure") }`, then the tmux or field path |
| `TmuxPaste` / `TmuxEnter` | `stickyPasteToPane` (`load-buffer`, `paste-buffer -p -d -t <pane>`), then `send-keys -t <pane> Enter` only if `session.enter` |
| `Activate` | `_ = target.activate()` (may or may not bring the app forward) |
| `Wait` | `guard await stickyFrontIs(session.pid), stickyFocusIs(session.pid, element)` |
| `Post` | `guard stillTarget(), postKey(0x09, flags: .maskCommand)` |
| `Enter` | `if !session.enter { no key } else if stillTarget(), postKey(0x24, flags: [])` |
| `Here` | CGEvent goes to `.cghidEventTap`, so it lands in the frontmost app's focused element, and in a terminal in the active tmux pane |

## Assumptions and scope

- **A1.** `stillTarget()` and the `postKey` after it are one step. The real gap is microseconds plus HID queue latency; a switch inside it is not modelled.
- **A2.** `StickyDelivery.cfg` sets `MISS = FALSE`: when the frontmost terminal runs tmux, `stickyTmuxPane` finds the pane. F1 below is what happens when it does not.
- **A3.** tmux pane ids are not reused within one tmux server (`%N` only grows). A server restart between wake and delivery, which can reuse `%N`, is out of scope.
- **A4.** A terminal's native splits or tabs without tmux are out of scope: the model's terminal has one AX text area and panes only through tmux.
- Not modelled: the clipboard snapshot and restore, focus return after delivery, the 3-minute expiry (`stickyAdvanceAge`, covered by item 7), Superwhisper mode switching.

## Findings

**F1: a missed tmux lookup pastes into the wrong pane.** Counterexample (`finding.sh`, `FindingTmuxMiss.cfg`): the wake word fires in tmux pane p1 of the terminal; `stickyTmuxPane` returns nil, so `captureStickyTarget` records the terminal's AX text area as a field; the user switches to pane p2; the text is ready; `activate` keeps the terminal in front; `stickyFrontIs` and `stickyFocusIs` pass, because the AX element is the same for every pane; Cmd+V lands in p2. Observed on the real Mac on 2026-11-21 (log `sticky: field chars=27`). The lookup missed on 2026-11-21 because an app opened by Finder or `open` has `PATH=/usr/bin:/bin:/usr/sbin:/sbin`, so `/usr/bin/env tmux` failed (log `env: tmux: No such file or directory`, 00:33Z). That trigger is fixed: every tmux call now goes through `tmuxExecutable` (PaneRoute.swift), which also looks in `/opt/homebrew/bin`, `/usr/local/bin`, and `/opt/local/bin` (anchor `TmuxPath`). F1 still stands for the other misses: `list-clients` over its 400 ms cap, or a tmux client that is not a descendant of the terminal (for example tmux inside ssh). `verify.sh path` shows whether the running app can resolve tmux. The same miss also lets Return go astray: the text is pasted into p1, the user switches to p2 within the 80 ms before Return, and Return lands in p2 (`EnterOnlyAllowed`, seen in the model only; it needs the terminal in `macOS.stickyEnterApps`). A fix for the remaining misses would refuse a field capture in an app that has any tmux client; not done.

## When a check fails

- **anchors FAIL**: the guard text changed. If the guard still does the same thing (a rename, swift-format), update `anchors.tsv` and the table above. If it was removed or weakened, that is the bug; do not edit the anchor to make it pass.
- **check.sh violation**: read the trace with `/statechart`, then follow each step in MacApp.swift. If the code really allows those steps, it is a bug. If a step is impossible in the code (the model is more generous than the code), fix the model action and cite the code line that rules it out.
- **break.sh says a break passed**: an invariant no longer looks at what the code does; fix the invariant before trusting item 3.

## Coverage record

| Check | Covers |
|---|---|
| `StickyDelivery.tla` + `anchors.tsv` | `tmuxExecutable` in PaneRoute.swift, `captureStickyTarget`, `stickyTmuxPane`, `descends`, `deliverSticky`, `stickyPasteToPane`, `stickyTmux`, `stickyFrontIs`, `stickyFocusIs`, `StickySession.enter` in Sources/voice-switch/MacApp.swift; `stickyWakeWords`/`stickyEnterApps` in Config.swift |
| `tests/macos/StickyMode/main.swift` | `stickyStep`, `stickyMayPaste`, `stickyMayEnter`, `stickyAdvanceAge`, `stickyEnabled` in Sources/voice-switch/StickyMode.swift |
| `verify.sh path` | the PATH the running VoiceSwitch.app inherited |

Sync: `464db40` on `feat/sticky-mode` (verify.sh drive passed there on 2026-11-21). Move this forward only after confirming that the model table and `anchors.tsv` still describe the code at the new commit.
