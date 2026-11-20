# Implementer task (feat/sticky-mode)

Wire sticky delivery. Decision logic already exists and must stay behavior-identical.

## Do not change

- `Sources/voice-switch/StickyMode.swift` transitions, `stickyMayPaste`, `stickyMayEnter`, `stickyAdvanceAge`, `stickyEnabled`. A zero diff is the goal. State count stays 300.
- `.claude/skills/verify-sticky-mode/*`, `tests/macos/StickyMode/*`, `plan/2026-11-20-sticky-mode/chart.mmd`.
- `sendKeysToPane` signature and behavior. Add a new helper if Enter must be sent without `-l`.
- `config.example.json` and `config.example.windows.json`. Do not add `stickyTarget` there.
- `DictationConfig`. Do not add the key there (parity compares those fields).
- Windows C# behavior. One sentence in `docs/windows-feature-parity.md` only.
- `.audit/`. Do not commit it. No machine-local absolute paths, emails, real names, or tokens.
- No push. No merge. No PR. Stay on `feat/sticky-mode`.

## Flag

`MacOSActions` in `Sources/voice-switch/Config.swift` gains `var stickyTarget: Bool?`. JSON key `macOS.stickyTarget`. Absent or null is off. Synthesized `Decodable` is enough.

Effective flag: `stickyEnabled(configFlag:override:)`. UserDefaults key `stickyTarget` wins when `object(forKey:)` is non-nil; otherwise the config bool (`true` only). Menu item title `スティッキー貼り付け`, checkmark follows the effective flag. Toggle writes the bool into UserDefaults (after that, the override exists and wins). Place it next to `効果音`. `refresh()` updates the checkmark. Before the listener exists, missing override means off.

## Capture (main thread, at dictation start)

Only a wake-word dictation: `action == nil` and `!origin.isAutomatic` (`WakeOrigin.isAutomatic` is `rule != .strict`). Not actions, not automatic / relaxed / followup. Four call sites in `Listener.consume` construct that dictation (early wake with and without samples, and the normal wake with and without samples). Capture on the main actor immediately before `Dictation(...)` and pass it in. Other call sites keep the default nil.

`Dictation` gains `var sticky: StickySession? = nil`. `StickySession` is a class in `MacApp.swift` (AppKit stays here). It holds `state: StickyState` plus pid, bundle id, optional `AXUIElement` for the element and the window, optional pane id, and `capturedAt: Date`.

When the flag is off, sticky stays nil. Do not call AX.

When the flag is on:

1. Frontmost `NSRunningApplication` pid and bundle id. If there is no frontmost app, kind is `noField`.
2. Secure if `IsSecureEventInputEnabled()` or the focused element's subrole is `AXSecureTextField`.
3. Else if role is `AXTextField` or `AXTextArea`, kind is `field`, even when the subrole read fails. Also keep the window (`kAXWindowAttribute`).
4. Else kind is `noField`. If Accessibility is not trusted, kind is `noField` and log that (no dictated text).
5. If the bundle is `com.googlecode.iterm2`, `com.apple.Terminal`, `dev.warp.Warp-Stable`, or `com.mitchellh.ghostty`, try the tty with a 400 ms cap (`osascript` for iTerm2 and Terminal; others may fail). Match `tmux list-panes -a -F '#{pane_id} #{pane_tty}'`. The pane id must pass `isPaneID`. On a match, kind is `tmux` and the element is unused. If tty or tmux fails, keep `field` (paste path). That failure is not a failed dictation.
6. `state = stickyStep(StickyState.initial(on: true), .start(kind))`.

## Handoff must not auto-paste a sticky session

`handoff` gains `sticky: StickySession? = nil` at the end. Existing call sites that omit it stay byte-for-byte on the old path. The wake `submit` passes `d.sticky`. The automatic `submit` does not.

When `sticky == nil`: do not change which mode is chosen, do not skip `selectPasteMode`, do not skip `routeDictation`.

When `sticky != nil`:

- Do not call `selectPasteMode`.
- Open Superwhisper only when `dictation.superwhisperMode` (`cfg.superwhisperMode`) is confirmed `superwhisperAutoPasteDisabled`. Use that mode as `wantedMode`.
- If the mode is missing, has auto-paste on, or `enterSuperwhisperMode` fails: recover any switch, do not `open` Superwhisper, return `.notDispatched` so the caller falls through to local `transcribeToHistory`.
- On a non-empty result: do not call `routeDictation`. Call the delivery adapter. Return `.delivered`.
- `transcribeToHistory`: when `d.sticky != nil`, do not call `pasteDictation`. Call the same delivery adapter. History still stores the text. The non-sticky branch still calls `pasteDictation`.

## Delivery adapter

Run on the main actor. Never log the text. Log `chars=` and a reason word only.

1. Empty text: `stickyStep(.stop(false))`. Do not paste. Do not touch the clipboard.
2. `state = stickyAdvanceAge(state, seconds since capturedAt)` then, if the app `isTerminated` or the AX element returns `kAXErrorInvalidUIElement` (or the window is gone), `stickyStep(.targetGone)`.
3. Snapshot the frontmost app before any `activate`. If its pid differs from the captured pid, `stickyStep(.switchApp)`.
4. Re-check secure (`IsSecureEventInputEnabled` or subrole `AXSecureTextField`). If secure now, do not paste; `stickyStep(.pasteFail)` and fallback.
5. `stickyStep(.stop(true))`. Paste only when `stickyMayPaste` is true. Otherwise fallback.
6. tmux: do not activate. `send-keys -l` for `ESC [200~`, the body, `ESC [201~`, then `send-keys` Enter without `-l`. All four must succeed. Then `pasteOk` and, only if `stickyMayEnter`, `enterOk`. Any failure is `pasteFail` and fallback. Do not Cmd+V into the frontmost app.
7. Field: `activate` the target, `kAXRaiseAction` on the window, set `kAXFocusedAttribute` on the element, then `CFEqual` the newly focused element to the captured one. If it is not the same element, `pasteFail` and fallback. Do not Cmd+V. If it is the same: clipboard snapshot (same 8 MB cap as `pasteDictation`), set the string, Cmd+V (virtual key `0x09`, `maskCommand`, `cghidEventTap`), about 80 ms, then if `stickyMayEnter` still holds, Return virtual key `0x24`. `pasteOk` before Enter. Enter posted means `enterOk`. If the element dies after the paste, `enterFail` and do not post Return. Restore the clipboard about 1 s later only after a successful paste, and only if `changeCount` is still ours (same as `pasteDictation`).
8. After a successful Enter, if `state.focus == .user` (they had switched), activate the app snapshotted in step 3 when it is still alive. If that activate fails, leave focus on the target and log. tmux does not activate anyone.
9. Fallback: put the text on the general pasteboard and do not restore the previous clipboard. Log the reason and `chars=`. The HUD must show failure, not success. `submit` currently sets `.done` unless the outcome is `.noResult`. Because `StickySession` is a class, `submit` can read `d.sticky?.state.phase == .fellBack` after handoff and after `storeLocal`, and pass `.failed` in that case. A nil session must still report exactly as today.

Do not call `pasteDictation` for a sticky session. It requires 1 s idle and a keyboard-layout input source, which rejects a Japanese IME. Do not use that composition check here. IME mid-composition is not detected. Log that limit once per delivery only as a static reason if you must; do not block the paste for it.

## Docs

Short section in `README_ja.md` and the parallel place in `README.md`: opt-in `macOS.stickyTarget`, default off, wake-word dictation only, remembers the field or tmux pane, pastes and presses Enter there even if the user switched, returns to the app that was frontmost when delivery started, refuses secure fields, drops the target after 3 minutes, leaves the text on the clipboard when it cannot paste. One line under Known Windows deviations in `docs/windows-feature-parity.md`: sticky is macOS-only; Windows ignores a `macOS` object.

## Checks before you commit

- `swift-format` on every Swift file you touch (`~/swift/usr/bin` is on PATH). `swiftlint` is not installed; say so. `swiftc -parse` cannot parse `MacApp.swift` on Linux (AppKit). Parse is for `StickyMode.swift` only, via the existing test.
- `python3 -m py_compile` on any Python you touch.
- `sh .claude/skills/verify-sticky-mode/check.sh` still prints `Model checking completed. No error has been found.`
- `swiftc Sources/voice-switch/StickyMode.swift tests/macos/StickyMode/main.swift -o /tmp/sticky-mode && /tmp/sticky-mode` still prints `PASS sticky logic 300 states`.
- `python3 tests/macos/StickyMode/test_sticky.py` still prints `PASS python sticky 300 states`.
- Do not run the dotnet suite unless you touch C# (you should not).

Commit on `feat/sticky-mode`. No push. Reply with `~/.agents/skills/agmsg/scripts/send.sh vs-sticky implementer orchestrator "<report>"`: files changed, config key, tests you ran with exit codes, anything you did not wire.
