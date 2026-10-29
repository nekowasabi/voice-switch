---
name: verify-meet-pause-windows
description: Model-check (TLA+) the Windows port of the Meet pause — the tray stays off while chrome.exe or msedge.exe holds the microphone, no queued Start / Reload / device retry reopens it, a manual pause survives the call, and the hold resumes after the call. Use before and while implementing the port of AppDelegate.followMeet on Windows, and when a change touches TrayRuntimeSupervisor.StartAsync / PauseAsync / ReloadAsync / ObserveCompletionAsync, ShouldRetryStart, or VoiceSwitchTrayContext.FollowDevice / TogglePause / RunCommandAsync / ApplySnapshot in dotnet/VoiceSwitch.Windows/Tray/.
---

# Verify the Meet pause on Windows (TLA+)

The macOS app pauses while Chrome or Edge holds the mic (commits `fc7144b`, `a2c5975`; model in `../verify-meet-pause/`). The Windows tray has it since the `win-port` branch, behind `pauseDuringMeet`. Its ordering problem is different from the Mac one: every tray action is `_ = RunCommandAsync(...)`, so the command waits on `TrayRuntimeSupervisor.gate` and runs later against a newer state, and the menu reads a snapshot that can lag. `MeetPauseWindows.tla` checks the port's design against every interleaving of the existing tray code paths that are in the model. Acceptance items on the real tray are in `../verify-voice-switch/features/meet-pause.md`.

A pass means the design is safe against the tray code paths in the coverage table. It does not cover the real tray: MPW0 (whether Meet's capture shows under `chrome` / `msedge`) and MPW1–MPW8 still need a live call.

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `HeldMeansOff` (MP-F1) | While the Meet hold is on, the capture is closed. | voice-switch hears the meeting and pastes or runs commands from it. |
| `NoAutoStartInCall` (MP-F1, action) | A launch start or a device retry never opens the mic during a call the user did not resume in. | The mic is open from launch until the first `FollowMeet` tick, 2 s later. |
| `MicOffInCall` (MP-F1) | Once a Hold command has run during this call, nothing reopens the mic unless the user resumed during the call. | Same, through a device retry, a reload, or a stale resume. |
| `ManualPauseKept` (MP-F2) | After the user's 一時停止 has run, nothing reopens the mic (a device retry, a reload, or the end of the call). | A user who turned voice-switch off finds it listening. |
| `HoldTakesEffect` (MP-F1, liveness) | Listening during a call without a user resume eventually stops. | A dropped or lost Hold leaves the mic open for the whole call. |
| `HeldResumes` (MP-F3, liveness) | A hold whose call has ended eventually listens again, unless another call started, the user ran a command, or the runtime finished on its own. | voice-switch stays silently off after every call. |

## The proposed design (what the port must implement)

These rules come out of the model. Each one has a broken variant below that fails without it.

1. `FollowMeet` runs on the 2 s `FollowDevice` tick and only enqueues: `Hold` while `MicInUse` sees chrome/msedge (unless a Hold is already queued), and `Resume` when the call is gone and the supervisor is held or overridden (unless a Resume is already queued). Re-enqueue on every tick, so a command dropped by the 20 s timeout is retried.
2. One `TrayHold` value (`None`, `User`, `Meet`, `MeetOverride`) lives in `TrayRuntimeSupervisor` and changes only under `gate`. In the model, `held` is `Meet`, `override` is `MeetOverride`, and `~userOn` is `User`.
3. `Hold` (under the gate): reads `MicInUse` again and does nothing when the call is gone. Otherwise, from `None` or `Meet`, set `Meet` and do what `PauseAsync` does. It must hold from any state, including `Error` and `Finished`, not only `Listening`.
4. `StartAsync(StartOrigin.DeviceRetry)` (`ShouldRetryStart`) and `StartAsync(StartOrigin.Launch)` refuse while `Meet` or `User`. From `None` during a call they hold as in step 3 instead of starting (Mac runs `followMeet` right after its launch start for the same reason).
5. `StartAsync(StartOrigin.User)` (menu 開始, `TogglePause`, IPC) does nothing while already `Listening`. Otherwise it sets `MeetOverride` if `MicInUse(...)` sees a call, else `None`.
6. `PauseAsync` from the user sets `User`.
7. `Resume` (under the gate) does nothing unless `Meet` or `MeetOverride`, then reads `MicInUse` again. If a call is on, it does nothing. Otherwise it sets `None` and, if it was `Meet`, starts.
8. Keep `pauseDuringMeet` off by default, read at launch (as on Mac). When it is off, steps 1, 3 and the hold in 4 do nothing without querying `MicInUse`, and step 7 never sees `Meet` or `MeetOverride`.

## Run

```sh
sh .claude/skills/verify-meet-pause-windows/check.sh
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked by the script, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` on first run). Needs Java 11+ (on this WSL: `mise use -g java@temurin-21`). About 2 s. A pass ends with `Model checking completed. No error has been found.` (35160 distinct states at the sync commit). `sh check.sh <other.tla>` checks a copy, which is how broken variants are tried.

Read every result, pass or fail, with the `/statechart` skill. It shows a violation as its shortest path in plain words, and a pass as a chart of the states.

## Coverage record

Sync commit: `cf9d18b` for every row (`win-port-fix-tray`: the launch-in-call hold, the `TrayHold` enum and `StartOrigin`; the model passed and a model copy without the launch-in-call hold failed `NoAutoStartInCall` at this commit).

| Model action | Code | Status |
|---|---|---|
| `Init` | `TrayHost`: `if (!options.Paused) _ = supervisor.StartAsync(..., StartOrigin.Launch)`, ctor `userPaused: options.Paused` (→ `TrayHold.User`) | exists |
| `Exec` Start / Pause / Reload | `TrayRuntimeSupervisor.StartAsync(origin)` / `PauseAsync` / `ReloadAsync`, each under `gate` (Reload from a non-running state ends `Paused`); `by = "user"` is `StartOrigin.User` (`RunCommandAsync`, `TrayIpcServer`), `"device"` is `StartOrigin.DeviceRetry` (`FollowDevice`), `"launch"` is `StartOrigin.Launch`; the non-user hold during a call is `HoldRunAsync` | exists |
| `Complete` | `TrayRuntimeSupervisor.ObserveCompletionAsync` (exit 0 → `Finished`, else `Error`) | exists |
| `Tick` | `diagnosticsTimer` (500 ms) `ApplySnapshot(supervisor.Snapshot)` and `OnSnapshotChanged` | exists |
| `FollowDevice` | `VoiceSwitchTrayContext.FollowDevice` + `ShouldRetryStart` (reads the snapshot just applied) | exists |
| `UserStart` / `UserPause` | 開始 / 一時停止 menu items, `TogglePause` (pause hotkey), `TrayIpcServer` Start/Pause, enabled from `ApplySnapshot` | exists |
| `UserReload` | mic menu item click (`FillMicMenu`) and 再読み込み → `RunCommandAsync(Reload)` | exists |
| `Drop` | `RunAsync`'s 20 s `CancellationTokenSource` cancelling `gate.WaitAsync` (every tray command goes through it) | exists |
| `q` (FIFO, assumed) | `SemaphoreSlim` async waiters | exists |
| `Poll` | `VoiceSwitchTrayContext.FollowMeet` on the 2 s `FollowDevice` tick; `holdPending` / `resumePending` cleared by the `done` callback in `RunAsync`'s `finally`; reads `supervisor.MeetBrowser()` and `MeetResumeOwed` | exists |
| `Exec` Hold / Resume | `TrayRuntimeSupervisor.HoldForMeetAsync` / `ResumeAfterMeetAsync`, each one gate step (`HoldRunAsync` → `PauseRunAsync` / `StartRunAsync` run inside it); both read `MeetBrowser()` again under the gate | exists |
| `inCall` | `TrayRuntimeSupervisor.MeetBrowser()`: injected `micInUseBy` (default `MicInUse.By`, `IAudioSessionManager2`) over `MeetBrowsers` = `chrome` / `msedge` | exists |
| `held` / `override` / `userOn` | `TrayRuntimeSupervisor.hold` = `Meet` / `MeetOverride` / not `User`, written only under `gate`. The model's reachable combinations are exactly the four `TrayHold` values: `held` and `override` never hold together, `held` implies `userOn`, and `Pause` clears `override` | exists |

`Starting`, `Pausing`, `Stopping`, and `Reloading` happen inside one gate step, so the model folds them into the step. `StopCurrentRunAsync`'s `TimeoutException` (the run ignores cancellation, so `run` is kept and the capture stays open) is not modeled. See Not covered.

## Does my change touch this?

From the repo root, run `git diff cf9d18b -- dotnet/VoiceSwitch.Windows/Tray/ dotnet/VoiceSwitch.Windows/MicInUse.cs`. Look for any function in the coverage table, a new `RunCommandAsync` / `RunAsync` / `supervisor.*Async` caller, or anything that writes `hold`. A new caller is a new action: add it to the model before you trust a pass.

## After the port lands

1. Rename the `proposed` actions to the real C# names, and check each action against its code. Each gate-held method must still be one step: no `gate.Release()` and re-acquire inside it.
2. Run the check. Then break the real code once, for example by removing the `TrayHold.Meet` refusal from the device-retry start, mirror that break in a copy of the model, and confirm that the copy fails.
3. Set the sync commit for every row to the new commit only after steps 1 and 2.

## When it fails

1. Read the trace with `/statechart` and retell it in code words. Example: "call starts → FollowMeet queues Hold → the start from launch fails into Error → FollowDevice queues a retry Start → Hold pauses → the retry Start runs → mic open during the call."
2. Check each step against the code. If the code can do every step, it is a real bug. Report it with that story. Do not weaken the property or remove the action.
3. If the code cannot do a step, the model is wrong. Fix the model to match the code, and say what you changed.

## Broken variants already seen failing

Try one of these after you edit the model.

- Device or launch Start starts during a call instead of holding (`ELSE IF FALSE` for `ELSE IF inCall /\ ~override`): `NoAutoStartInCall` fails in two steps (launch during a call, the launch Start listens). No other property catches it, because the first Hold closes the mic again.
- Device or launch Start ignores the `Meet` hold (`IF ~userOn THEN`): `HeldMeansOff` fails. The path is a launch start that fails into `Error`, FollowDevice queueing a retry, Hold, then the retry reopening the mic.
- User Pause leaves the `Meet` hold set (`held' = held`): `ManualPauseKept` fails. The user pauses from a stale 待ち受け中 menu while held, and the call ending resumes.
- Hold only from `Listening` (`... /\ userOn /\ Mic`): `MicOffInCall` fails. A Hold that lands in `Error` does nothing, and the device retry then listens through the call.
- Device or launch Start ignores the `User` pause (`IF held THEN`), which is how `StartAsync` behaved before the port: `ManualPauseKept` fails. See the finding below.
- Resume without re-reading `MicInUse` (`IF FALSE`): `MicOffInCall` fails. The user resumes in call A, a Resume is queued when A ends, call B starts, and the old Resume clears the override and keeps the mic open in B.

## Finding on the current code (not reproduced on the tray)

The finding is the `IF held THEN` variant with no Meet involved. The runtime exits with an error, so the state becomes `Error` while the menu still shows 待ち受け中. The user presses 一時停止, but the Pause waits on the gate. The next `FollowDevice` tick sees `Error` and queues a retry `StartAsync`. The Pause runs, and then the retry runs and listens again. The window is small: the Pause must still be waiting on the gate when the tick fires, for example behind `ObserveCompletionAsync`. Rule 4 closes it.

## Not covered

- `StopCurrentRunAsync` timing out: the run is kept and its capture may stay open until it exits, during a call too. No design here closes that gap. It needs the child to be killed.
- Whether `MicInUse` sees Meet's capture under `chrome` / `msedge`, or under some other process name (acceptance item MPW0).
- `MicInUse` failing. `MicInUse.By` returns null, which reads as "no call".
- Waiters on the gate are assumed to run in FIFO order. That is how .NET's `SemaphoreSlim` is implemented today, but it is not documented. The current-code finding depends on that order.
- Stopped and Quitting states, Quit, and `ReloadAsync`'s config-load failure.
- Queue bound: at most 3 waiting commands, at most 1 timeout drop, and duplicate UI clicks are capped by the bound.
- The ordering of `SnapshotChanged` versus `Tick`: both are folded into `Tick`.
- An open dictation when the hold lands (the run is stopped, so the dictation ends with it). HUD and tray icon text.
- Timing: 2 s, 500 ms, 10 s and 20 s are only orderings here.
