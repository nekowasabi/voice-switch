---
name: verify-meet-pause
description: Model-check (TLA+) that the macOS listener stays off during a Google Meet call in Chrome or Edge, never reopens the mic through a restart path, keeps a manual pause, and resumes after the call. Use when a change touches AppDelegate.followMeet / togglePause / selectDevice / launch, or Listener.start / stop / startRetrying / restart / attemptStart / checkStall in Sources/voice-switch/MacApp.swift.
---

# Verify the Meet pause (TLA+)

voice-switch on macOS pauses while Chrome or Edge holds microphone input (a Google Meet call) and resumes when the browser lets go. `MeetPause.tla` explores every order of call start/end, the 2 s poll, launch, the menu toggle and pause hotkey, mic selection, and every listener restart path, and checks the properties below. Real-app acceptance items (which bundle ID Chrome reports, 🔕/🔔 timing) live in `../verify-voice-switch-mac/features/meet-pause.md`; this skill covers only the ordering logic.

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `MicOffInCall` (MP-F1) | Once the poll has seen the call, the mic is closed unless the user resumed during that call. | voice-switch hears the meeting and pastes or runs commands from it. |
| `HeldMeansOff` (MP-F1) | While the Meet hold is on, nothing has reopened the mic. | Same, through a restart path. |
| `ManualPauseKept` (MP-F2) | After the user pauses, nothing reopens the mic, the call ending included. | A user who turned voice-switch off finds it listening. |
| `HeldResumes` (MP-F3, liveness) | A hold whose call has ended eventually listens again, unless another call started, the user paused, or an alert reported a failed start. | voice-switch stays silently off after every call. |

## Run

```sh
sh .claude/skills/verify-meet-pause/check.sh
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked by the script, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` on first run). Needs Java 11+. About 5 s. A pass ends with `Model checking completed. No error has been found.` (72 distinct states at the sync commit). `sh check.sh <other.tla>` checks a copy, which is how broken variants are tried.

Read every result, pass or fail, with the `/statechart` skill: a violation as its shortest path in plain words, a pass as a chart of the states.

## Coverage record

Sync commit: `a2c5975` (the model was last confirmed to describe the code at this commit).

| Model action | Code (Sources/voice-switch/MacApp.swift) |
|---|---|
| `Poll` | `AppDelegate.followMeet` (2 s timer) |
| `Launch` | `applicationDidFinishLaunching`: `try listener.start()` then `followMeet()` in one main-actor step |
| `Toggle` | `AppDelegate.togglePause` (menu item and `MacWakeShortcuts` pause hotkey) |
| `SelectDevice` | `AppDelegate.selectDevice` |
| `EngineRestart` | `Listener.init` observers: `AVAudioEngineConfigurationChange` and the device-list listener, both `guard wanted` then `restart()` |
| `Stall` | `Listener.checkStall` |
| `RetryOK` / `RetryFail` / `RetryNoop` | the 10 s closure in `Listener.attemptStart` |
| `AttemptStart` | `Listener.attemptStart` / `start()` (start sets `wanted = true` on success) |
| `inCall` | `micInUse(by: AppDelegate.meetBrowsers)` |

The model assumes config `pauseDuringMeet` is true. When it is false (`meetEnabled`), `followMeet` returns at once and `togglePause` never sets `meetOverride`, so only the pre-feature actions remain.

Each action is one main-thread step in the code. If a change moves any of these onto another thread or splits one across an `await`, the model no longer describes it.

## Does my change touch this?

Run `git diff a2c5975 -- Sources/voice-switch/MacApp.swift Sources/voice-switch/Config.swift` from the repo root and look for any function in the coverage table, anything that writes `Listener.wanted`, or a new caller of `listener.start()` / `attemptStart()`. A new caller of `start()` is a new action: add it to the model before trusting a pass.

## When it fails

1. Read the trace with `/statechart` and retell it as steps in code words (e.g. "call starts → poll stops the listener → user picks a mic → selectDevice starts it → mic open during the call").
2. Check each step against the code it names. If every step is something the code can do, it is a real bug: report it with that story. Do not weaken the property or remove the action.
3. If a step is something the code cannot do, the model is wrong: fix the model to match the code, and say what you changed.

## Updating the model after an intended change

Mirror the code change in the matching action, run the check, then try one broken variant of your new code (below) and confirm it fails. Move the sync commit only after the model describes the code at the new commit.

Broken variants already seen failing (try one after editing the model):

- Hold with `halt()` instead of `stop()` (in `Poll`, `wanted' = wanted`): `MicOffInCall` fails; `SelectDevice` reopens the mic during the call. The same final state violates `HeldMeansOff`.
- Call end resumes whenever not listening (`IF held \/ ~wanted`): `ManualPauseKept` fails.
- Resume with `start()` instead of `startRetrying()` (one failed attempt drops `wanted`): `HeldResumes` fails.

## Not covered

- Whether the bundle IDs in `meetBrowsers` are the ones Chrome/Edge report (feature file MP0).
- `micInUse` returning nil on a CoreAudio error (read as "call ended"), and a browser briefly releasing the mic mid-call; the model treats both as a call ending.
- Any Chrome/Edge mic use counts as a call; Meet itself is not identified.
- The launch-time `followMeet()` only removes a window of up to one poll (2 s); removing it does not fail the model, because `MicOffInCall` starts after the poll has seen the call.
- A Superwhisper recording already open when the call starts; HUD text; `refresh()` titles.
- `togglePause` pressed before launch reaches `listener.start()` (assumed not to happen).
- Timing: 2 s and 10 s are only orderings here.
