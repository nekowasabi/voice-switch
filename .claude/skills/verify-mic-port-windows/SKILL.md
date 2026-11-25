---
name: verify-mic-port-windows
description: Model-check (TLA+) that the Windows port of the macOS mic priority (`microphones` / 予備マイク, verify-mic-fallback) and zero skip (verify-mic-live) records from the same mic macOS would, once the tray has caught up, and closes every zero watcher on pause, across every order of plug, unplug, a mic sending only zeros, a run failing, menu mic changes, pause and start, with the tray's queued commands running later under TrayRuntimeSupervisor.gate. Use before and while porting `microphones` or the zero skip to Windows, when a change touches WinMmCapture.Open / EffectiveDevice / CurrentDevice, VoiceSwitchTrayContext.FollowDevice / FillMicMenu / RunAsync, TrayRuntimeSupervisor.ReloadAsync / StartAsync / ShouldRetryStart, and when InputRoute.resolve / choose change on macOS (this model copies them as the spec).
---

# Verify the Windows port of the mic priority (TLA+)

The risk this guards: a feature ported from macOS behaves differently on Windows. The Mac promises are LM-1 and LM-2 of `../verify-mic-live/`. Windows reaches them through a different machine. WinMM tells an open capture nothing, so `FollowDevice` polls every 2 s and only queues a command. The command runs later, one at a time under `gate`, against whatever the state is by then. WinMM also names a device by a 31-character name, where macOS uses a per-plug AudioDeviceID. `MicPortWindows.tla` copies macOS's `Choose` / `Resolve` / `Expected` from `MicLive.tla` unchanged and checks the Windows ordering against them.

`Current.cfg` is the Windows code today: one pinned WinMM name (`TraySettings.MicDevice`), else the system default. It is expected to FAIL, because the port is not written yet. `Design.cfg` is the port described below.

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `SameMicAsMac` (LM-1) | Once the tray has caught up, while the user has not paused and some plugged-in mic sends real samples, Windows records from the mic macOS would record from. | The same config and the same mics give a different mic on Windows: wake words and dictation silently hear nothing, or the wrong mic. |
| `WatchOff` (LM-2) | No zero watcher holds a mic open after the user's pause has run. | The mic stays open during a pause or a Meet call. |

"Caught up" (`Settled`) means nothing is queued, the menu shows the state, and the code's own poll, zero check and watchers have nothing left to do. `Settled` uses the same operators as those actions (`PollCmd`, `PollWatch`, `ZeroDue`, `ReturnDue`). Removing a step from the code therefore makes the property fail instead of making it vacuous. Keep it that way when you edit the model.

## The port (what Windows must implement)

Each rule has a broken variant below that fails without it.

1. Resolve exactly as macOS `InputRoute.resolve(candidates:silent:)`: the first plugged, non-silent candidate, then the built-in mic, then the default, and the all-silent fallback. Put it in `VoiceSwitch.Windows.Core` and call it from `WinMmCapture.EffectiveDevice`, so `Open` and `FollowDevice` use one function.
2. The capture marks the device it reads after `zeroSeconds` of exact zeros, as `Listener.checkSilence` does. The next `FollowDevice` then sees `CurrentDevice != EffectiveDevice` and queues the Reload.
3. Every `FollowDevice` tick, while listening, reopens a zero watcher on every marked mic that is listed. A WinMM name unplugged and replugged between two polls keeps its mark, so only a watcher can clear it. Dropping the marks of unlisted names is not needed (variant B6 passes without it).
4. The watchers belong to the run and are disposed with it (`PauseAsync`, `ReloadAsync`, a failed run).
5. `ReloadAsync` from `Error` leaves `Error` and does not end `Paused`, so `ShouldRetryStart` still retries. See the finding.
6. Each demotion and return writes one log line, as on macOS (`input <name> sends only exact zeros ...`, `input <name> sends sound again`). A real run must be able to tell "Rx off, on the webcam" from "Rx chosen" without the menu.

## Check items

Run them in this order.

1. **Real Windows, symptom (manual, after the port lands).** Put the DJI Rx first in `microphones` and the webcam second, with voice-switch.exe listening. (a) Transmitter on, quiet for 30 s: no zero line in the log, and the wake word works. (b) Transmitter off: within about 4 s, one zero line and `dictation capture: <webcam>`, and the wake word works. (c) Transmitter on: one "sends sound again" line, then `dictation capture: <Rx>`. (d) Unplug and replug the Rx within 2 s while it is marked, then switch it on: the capture returns to the Rx. (e) Pause: the Windows mic indicator goes off. Drive what you can with `../verify-voice-switch/`. A live mic stays unverified until a person does this.
2. **Model (every change):** `sh .claude/skills/verify-mic-port-windows/check.sh .claude/skills/verify-mic-port-windows/MicPortWindows.tla Design` must end with `Model checking completed. No error has been found.`
3. **Before a PR:** `sh .claude/skills/verify-mic-port-windows/check.sh` runs both configs. `Current` must report `Invariant SameMicAsMac is violated` until the port lands, and `Design` must pass. The script exits non-zero while Current fails, so read the two blocks, not the exit status. Then try the broken variants below.
4. **After the port lands:** re-map every row of the coverage record against the C# code, set `Current.cfg` to describe the shipped code (or delete it once it equals Design), and break the real code once as in "Broken variants" to see the check fail.

## Run

```sh
sh .claude/skills/verify-mic-port-windows/check.sh                     # Current (fails until the port) and Design
sh .claude/skills/verify-mic-port-windows/check.sh <copy.tla> Design   # a broken variant
```

Pinned TLC v1.7.4, the same jar and checksum as verify-mic-fallback, cached in `~/.cache/tla2tools-v1.7.4/`. Needs Java 11+ on `PATH`. On this WSL no global java is set, so prefix the command with `mise exec java@temurin-21 --`. Design takes about 10 s.

Read every result, pass or fail, with the `/statechart` skill. It shows a violation as its shortest path in plain words, and a pass as a chart of the states.

## Coverage record

Sync commit: `56738fb`. Every row below was mapped against the C# and Swift code at this commit. The Design rows marked "proposed" have no code yet. Move the commit only after you re-map every row against the new commit.

| Model part | Code | Status |
|---|---|---|
| `Mics` | `WinMmCapture.InputDevices()` (names cut at 31 characters) | exists |
| `Builtin` | none: WinMM does not say which input is built in | proposed (needs a source, for example the MMDevice form factor) |
| `Choose` / `Resolve` / `Expected` | copied from `MicLive.tla` = `InputRoute.choose` / `InputRoute.resolve` in `Sources/voice-switch/MacApp.swift` | macOS spec |
| `Eff` (Current) | `WinMmCapture.EffectiveDevice(pinned, devices, defaultName)` | exists |
| `Eff` (Design) | rule 1 | proposed |
| `Open` | `TrayRuntimeSupervisor.StartRunAsync` / `ReloadAsync` → `factory.StartAsync` → `WinMmCapture.Open(pinned)`, which sets `CurrentDevice` | exists (Design: rule 1) |
| `Close` | `PauseRunAsync` / `ReloadAsync` / `ObserveCompletionAsync`, then `WinMmCapture.DisposeAsync` (`CurrentDevice = null`) | exists (Design: rule 4) |
| `Init` | `TrayHost`: `StartAsync(..., StartOrigin.Launch)` unless `options.Paused` | exists |
| `Plug` / `Unplug` / `Toggle` | the world. WinMM does not notify an open capture, so `cur` stays put | world |
| `Fail` | `ObserveCompletionAsync` (exit code ≠ 0 → `Error`) | exists |
| `Apply` | the 500 ms `diagnosticsTimer` → `ApplySnapshot(supervisor.Snapshot)` | exists |
| `Poll` / `PollCmd` | `VoiceSwitchTrayContext.FollowDevice` every 4th tick: Reload when `Listening` and `CurrentDevice != effective`, else `ShouldRetryStart` (Error with a device; its 40 s fallback tick is folded into "eventually") | exists |
| `PollWatch` | rule 3 | proposed |
| `Zero` / `ZeroDue` | rule 2 | proposed |
| `Return` / `ReturnDue` | the watcher hears a non-zero sample (rule 3) | proposed |
| `SetCands` | `FillMicMenu` click → `TraySettings.MicDevice` → `RunCommandAsync(Reload)` (Design: `microphones` or the 予備 list) | exists (Design: proposed) |
| `UserPause` / `UserStart` | 一時停止 / 開始, `TogglePause`, `RunCommandAsync` → `PauseAsync` (`TrayHold.User`) / `StartAsync(StartOrigin.User)` | exists |
| `Exec` Reload | `ReloadAsync`: running → stop and start; `Error` stays `Error`; otherwise → `Paused` (rule 5) | exists |
| `Exec` Start | `StartAsync(DeviceRetry/Launch)`: refused under `TrayHold.User`; `StartRunAsync` no-ops while `Listening` | exists |
| `q` | commands waiting on `gate` (`SemaphoreSlim`, FIFO assumed), at most 2, with no duplicate queued | exists (bounded) |

`covers.txt` lists the same functions for `tests/affected.sh`.

## Does my change touch this?

Run `sh tests/affected.sh 56738fb`. If `verify-mic-port-windows` is listed, find the functions you changed in the coverage record. A new caller of `RunAsync`, `RunCommandAsync`, `supervisor.*Async` or `WinMmCapture.Open` is a new action: add it to the model before you trust a pass. A change to `InputRoute.resolve` / `choose` on macOS changes the spec: copy it into `Choose` / `Resolve` here and in `MicLive.tla`.

## When it fails

1. Read the trace with `/statechart` and retell it in code words. Example (Current): "the pinned webcam sends only zeros → the launch Start opens it → the Rx is plugged in and sends → nothing reroutes, so voice-switch records zeros while macOS would record from the Rx."
2. Check each step against the code. If the code can take every step, it is a real bug. Report it with that story. Do not weaken `SameMicAsMac` or `Expected`, and do not remove an action.
3. If the code cannot take a step, the model is wrong. Fix the model to match the code, and say what you changed.

## Broken variants already seen failing

Copy the model, change the one line, and run `check.sh <copy> Design`.

- B1, Reload from `Error` ends `Paused` (`ELSE IF FALSE` for `ELSE IF st = "Error"`), which was the code until the fix: `SameMicAsMac` fails. See the finding.
- B2, keep today's rule of the first plugged candidate else the default, with no built-in step: `SameMicAsMac` fails.
- B3, no watcher reopen on poll (`PollWatch(s) == watch`): `SameMicAsMac` fails. The mark is set on the webcam, the webcam is unplugged, Reload moves to the Rx, the webcam is replugged and sends, and no watcher ever hears it.
- B4, the user's pause keeps the watchers: `WatchOff` fails.
- B5, no zero check (`ZeroDue == FALSE /\ ...`): `SameMicAsMac` fails.
- Real-code break: in `FollowDevice`, `current != effective` changed to `current == null`, so no Reload is ever queued. `tests/affected.sh` lists this skill, and the mirror (`PollCmd` without the Reload branch) fails `SameMicAsMac`: launch opens the default webcam, the preferred Rx is plugged in, and nothing moves to it.

## Finding (fixed 2026-11-25; reproduced in the supervisor, not on the tray)

Fixed on 2026-11-25 (rule 5 in `ReloadAsync`; the test `tray supervisor reload from Error keeps the device retry` replays the path below). Variant B1 was the code until then, so this applied before the port too. At launch no mic can be opened yet (a USB mic is listed a moment before it opens), so `StartAsync(Launch)` ends in `Error`. The user picks a mic in the menu, which queues a Reload. The mic comes up. The Reload runs: `wasRunning` is false, so it ends `Paused` (`TrayRuntimeSupervisor.cs`, `ReloadAsync`, `if (!wasRunning) SetSnapshot(TrayState.Paused, ...)`). `ShouldRetryStart` retries only from `Error`, so the tray stays off although the user never paused. The menu does show 停止中. Rule 5 closes it.

Replayed on 2026-11-25 with a throwaway test in `dotnet/VoiceSwitch.Windows.Tests` (`FakeTrayRuntimeFactory { ThrowOnStart = true }`, `StartAsync(Launch)`, then `ThrowOnStart = false` and `ReloadAsync`): the state was `Error`, then `Paused` with no start, and `ShouldRetryStart(Paused, ...)` was false. The real tray with a USB mic has not been tried.

## Not covered

- Real WinMM: whether `waveInOpen` on an unlisted name fails, how fast a replugged USB mic opens, and whether a sending mic ever gives `zeroSeconds` of exact zeros on Windows (macOS measured none, 2026-11-24).
- Which input is built in on Windows (`Builtin` is assumed known).
- Two devices with the same cut 31-character name.
- Meet hold, `StopCurrentRunAsync` timing out, Quit, and config-load failure: see `../verify-meet-pause-windows/`.
- Queue bound 2, three mics, and three candidate lists. Timing (2 s, 500 ms, 40 s, `zeroSeconds`) is only an ordering here.
- The dictation runtime (`DictationRuntime`) and the HUD.
