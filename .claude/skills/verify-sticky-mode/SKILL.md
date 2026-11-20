---
name: verify-sticky-mode
description: Model-check (TLA+) that macOS sticky dictation pastes at most once into the field or tmux pane captured at dictation start, never into a secure field or a different app, presses Enter only after that paste, and does nothing sticky while the flag is off. Use when a change touches stickyStep, stickyMayPaste, stickyMayEnter, stickyAdvanceAge, or stickyEnabled in Sources/voice-switch/StickyMode.swift, or the sticky capture and delivery calls in Sources/voice-switch/MacApp.swift.
---

# Verify sticky delivery (TLA+)

Sticky mode remembers the focused text field, or the tmux pane, when a wake-word dictation starts. When the text is ready it pastes there and presses Enter, even if the user has switched apps. `StickyMode.tla` is that decision. The Accessibility and pasteboard calls are outside the model. They may paste only when `stickyMayPaste` is true, and they may press Enter only when `stickyMayEnter` is true.

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `NeverWrongPaste` | A delivery never pastes into an app that is not the captured target. The good model has no such paste (`bad` stays false). | The sentence lands in whatever the user switched to. |
| `NeverSecurePaste` | A secure field is never pasted and never gets Enter. | A password field receives the dictation. |
| `AtMostOne` | One dictation pastes at most once. | A retry duplicates the sentence. |
| `EnterAfterPaste` | Enter happens only after one successful paste into a text field or a tmux pane. | Enter submits an empty or wrong field. |
| `OffQuiet` | While sticky is off, the machine never sits in armed, recording, or delivering. | The flag does not actually turn the feature off. |
| `FocusRestored` | After a clean delivery, focus is the app the user was in if they had switched, and the target if they had not. | The user is left in the dictation target after they had moved on. |

`TypeOK` is checked as an invariant. `bad` is the witness a broken paste sets. The good config keeps `BROKEN = FALSE`, so that action is disabled.

## Check items

1. **Model.** `sh .claude/skills/verify-sticky-mode/check.sh` ends with `Model checking completed. No error has been found.`
2. **The check can fail.** `sh .claude/skills/verify-sticky-mode/break.sh` exits 0 because `Broken.cfg` (`BROKEN = TRUE`) violates `NeverWrongPaste` or `NeverSecurePaste`.
3. **Swift logic.** `swiftc Sources/voice-switch/StickyMode.swift tests/macos/StickyMode/main.swift -o /tmp/sticky-mode && /tmp/sticky-mode` prints `PASS` lines and no `FAIL`. The BFS state count should match a reading of the same events as the TLA model. The Swift names are `fellBack` and `.stop(true)`. The TLA names are `fellback` and `StopText`.
4. **Python mirror.** `python3 tests/macos/StickyMode/test_sticky.py` prints `PASS`. If its state count differs from the Swift BFS, one of the two drifted from `stickyStep`.
5. **Flag off.** With `macOS.stickyTarget` absent or false, and no UserDefaults override, `handoff` is called as before. A diff that passes a sticky session in that case is a bug even if the model passes.
6. **Live Mac.** Capture, raise, paste, Enter, and focus restore on a real Mac are `MAC_UNVERIFIED` here. Do not treat a model pass as that proof.

## Run

```sh
sh .claude/skills/verify-sticky-mode/verify.sh doctor
sh .claude/skills/verify-sticky-mode/verify.sh drive
```

`drive` runs items 1 to 4. Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked, fetched with `gh` into `~/.cache/tla2tools-v1.7.4/` on first run, the same jar as `verify-mic-fallback`). Needs Java 11+. Item 6 stays `SKIP` with `MAC_UNVERIFIED`.

Read every TLC result, pass or fail, with the `/statechart` skill. A violation is its shortest path in plain words. A pass is a chart of the states.

## Coverage record

Sync commit: `pending` until the commit that contains this model and `StickyMode.swift` is recorded in `RESULT.md` and this line is updated.

| Model part | Code |
|---|---|
| `Start` | `stickyStep(.start)` |
| `Speech` | `stickyStep(.speech)` |
| `SwitchApp` | `stickyStep(.switchApp)` |
| `TargetGone` | `stickyStep(.targetGone)` |
| `Tick` | `stickyStep(.tick)`, also `stickyAdvanceAge` (one tick per 60 seconds, drop at 3) |
| `StopEmpty` / `StopText` | `stickyStep(.stop(false))` / `stickyStep(.stop(true))` |
| `PasteOk` / `PasteFail` | `stickyMayPaste` then `stickyStep(.pasteOk)` or `.pasteFail` |
| `EnterOk` / `EnterFail` | `stickyMayEnter` then `stickyStep(.enterOk)` or `.enterFail` |
| `ToggleOn` / `ToggleOff` | `stickyStep` of those events. The menu and `macOS.stickyTarget` meet in `stickyEnabled` |
| `Reset` | `stickyStep(.reset)` |
| `PasteFrontmost` / `PasteSecure` | not in the Swift file. `BROKEN = TRUE` only |

The adapter in `MacApp.swift` is one step per call above. A change that pastes without `stickyMayPaste`, or presses Enter without `stickyMayEnter`, is outside what a passing model says.

Bounds. `MaxAge = 3` matches `StickyState.maxAge`. The model does not count milliseconds, AX error codes, or clipboard bytes.

Assumptions. The adapter classifies the focused element before `start`: text field, tmux pane, secure field, or no field. It sets `targetGone` when the element or the app is gone, including `kAXErrorInvalidUIElement`. It does not paste on fallback. It logs the character count only.

## Does my change touch this?

Run `git diff <sync> -- Sources/voice-switch/StickyMode.swift Sources/voice-switch/MacApp.swift` and look for `stickyStep`, `stickyMayPaste`, `stickyMayEnter`, `stickyAdvanceAge`, `stickyEnabled`, or a new paste into the frontmost app on the sticky path. Any of those needs this check before trusting a pass.

## When it fails

1. Read the trace with `/statechart` and retell it in the words of the table above.
2. If every step is something `stickyStep` can do, it is a real bug. Do not weaken the invariant or delete `PasteFrontmost` from the broken config to make `break.sh` pass.
3. If a step is something the Swift function cannot do, the model is wrong. Fix the model and say what you changed.

## Updating the model after an intended change

Mirror the change in the matching action, run `check.sh` and `break.sh`, and run the Swift BFS. Move the sync commit only after the model describes `stickyStep` at the new commit.

Broken variants already seen failing:

- `BROKEN = TRUE` enables `PasteFrontmost` and `PasteSecure`. `break.sh` expects a non-zero TLC status and an invariant violation.

## Not covered

- Whether Chrome, Safari, Electron, or iTerm actually accepts Cmd+V or `kAXFocusedAttribute`. That is the live Mac item.
- Bracketed paste bytes and the Return virtual key. The model only says Enter comes after a successful paste.
- Superwhisper mode switching. The adapter must not open an auto-paste mode for a sticky session. The model starts at the decision, after the text exists.
- Windows. Sticky is not implemented there. A `macOS` object in a shared config is ignored by `VoiceSwitchConfig`.
- Two dictations at once. The machine is one session. `Reset` starts the next.
