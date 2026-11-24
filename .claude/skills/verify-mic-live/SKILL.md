---
name: verify-mic-live
description: Model-check (TLA+) that the macOS listener leaves a plugged-in mic that sends only exact zeros (the DJI Wireless Mic Rx receiver with its transmitter off, a muted input) for the next mic in the priority list, comes back to it when it sends again, never leaves a mic that is merely quiet, and closes every zero watcher on pause (LM-1, LM-2). Use when a change touches InputRoute.resolve / InputRoute.choose, Listener.silent / checkSilence / watchSilent / zeroSeconds, the tap's zero test in Listener.start, Listener.reroute / stop / startRetrying / attemptStart, or the device-list listener in Listener.init, in Sources/voice-switch/MacApp.swift.
---

# Verify that a mic sending only zeros is skipped (TLA+)

The DJI Wireless Mic Rx receiver stays plugged in by USB. With its transmitter off, Core Audio still lists it, every property stays the same (`isAlive=1`, 48 kHz, no mute; USB registry unchanged), and the tap keeps getting buffers, so `checkStall` never fires. The only difference is the samples: exactly 0.0, every one. Measured on 2026-11-24 with a throwaway Core Audio probe (90 s, 0.5 s windows): transmitter on and talking or quiet, no window was all zeros (at most 0.2 % zeros); transmitter off, every window was all zeros within about 1 s; switched back on, sound returned about 7 s later. The user asked for: switch to the next mic, switch even mid-dictation, and apply it to every mic.

`MicLive.tla` explores every order of the events below for the code without the zero check (`Current.cfg`, expected to FAIL) and with it (`Design.cfg`).

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `LiveMic` (LM-1) | When at least one plugged-in mic sends real samples and the app has caught up, a listening voice-switch records from the first sending mic of the candidate list; with no list, the sending system default; then the sending built-in mic; else any sending mic. | Wake words and dictation silently do nothing while the Rx transmitter is off, although the webcam or built-in mic would hear the user. |
| `WatchOff` (LM-2) | No zero watcher holds a mic open while the listener is stopped. | The mic indicator stays on during a Google Meet call or a manual pause (breaks verify-meet-pause). |

"Caught up" (`Settled`) means no queued device notice, and the 1 s timer and the zero watchers have looked since the last change. A transmitter left off forever with every other mic also silent is outside LM-1 (nothing sends).

## Check items

Run them in this order. Item 1 comes first: the symptom is something the user hears.

1. **Real Mac, symptom (manual).** Rx first in the マイク menu, webcam as 予備 1, voice-switch running. (a) Transmitter on, stay quiet 30 s: no `silent` line in `~/Library/Logs/voice-switch.log`, wake word works on the Rx. (b) Switch the transmitter off: within about 3 s one line `input Wireless Mic Rx sends only exact zeros for 2 s, skipping it`, then `input device <Rx UID> skipped (only zeros), falling back to <予備 1> (fallback 1)` and `listening on <予備 1>`; the wake word works. (c) Switch it on: one line `input Wireless Mic Rx sends sound again` and `listening on Wireless Mic Rx`. (d) Pause from the menu while on the webcam: the menu-bar mic indicator goes off. Record whether the wake word was recognized each time.
2. **Each demotion and return is one log line** (item 1 names them). A run must tell "Rx off, on the webcam" from "Rx chosen" without the menu.
3. **Model:** `sh .claude/skills/verify-mic-live/check.sh`. Expected: `== Current ==` reports `Invariant LiveMic is violated` (the finding below), `== Design ==` ends with `Model checking completed. No error has been found.` The script exits non-zero because Current fails; read the two blocks, not the exit status. `tests/check.sh` runs Design only.
4. **Implementation matches the model:** re-map every row of the coverage table after a change and rerun.

## Run

```sh
sh .claude/skills/verify-mic-live/check.sh                      # Current (fails) and Design
sh .claude/skills/verify-mic-live/check.sh .claude/skills/verify-mic-live/MicLive.tla Design   # what tests/check.sh runs
sh .claude/skills/verify-mic-live/check.sh <copy.tla> Design    # a broken variant
```

Pinned TLC v1.7.4 (the same jar and checksum as verify-mic-fallback, cached in `~/.cache/tla2tools-v1.7.4/`). Java 11+. A few seconds.

Read every result, pass or fail, with the `/statechart` skill: a violation as its shortest path in plain words, a pass as a chart of the states.

## Finding (Current)

Shortest trace: Rx and webcam plugged in, no saved mic (system default = webcam), the webcam sends only zeros and the Rx sends → `startRetrying` opens the webcam → the timer looks and does nothing → the listener keeps recording zeros while the Rx would hear the user. The same story with the Rx off and first in the list is the user's report.

## Coverage record

Sync commit: `76ff65c` (`Design` re-mapped row by row against the zero check at this commit; `Current` is the code before it, `d77f7fd`). Move it only after re-mapping every row below against the new commit.

| Model part | Code (Sources/voice-switch/MacApp.swift) |
|---|---|
| `Mics` / `Builtin` | `inputDeviceIDs()` / `builtInInputDeviceID()` |
| `Choose` / `Resolve` | `InputRoute.choose` / `InputRoute.resolve(candidates:silent:)`: skip `silent`; if that would leave a silent default, choose again with every mic |
| `send` | the world: whether the device's raw samples are all exactly 0.0 |
| `AttemptStart` | `Listener.attemptStart` → `start()`, which ends with `watchSilent()` |
| `Reroute` | `Listener.reroute` / `Listener.applySilent`: restart when the route changes, else resync the watchers |
| `DeviceEvent` | the coalesced device-list listener in `Listener.init` → `Listener.reroute`, which first drops marks of unplugged devices |
| `Tick` (first branch) | `Listener.checkSilence` on the 1 s timer: buffers arrived within 1 s (no buffers at all is `checkStall`'s case) and `Date() - lastSound >= Listener.zeroSeconds` on the active device |
| `Tick` (second branch) | a watcher's IOProc in `Listener.watchSilent` seeing a non-zero sample → main queue |
| `Stop` / `Resume` | `Listener.stop()` (closes every watcher) / `Listener.startRetrying()` |
| `SetCands` | `AppDelegate.selectDevice`, `AppDelegate.toggleFallback`, config.json `microphones` reload |
| `Follow` | a Default route's engine following the default (verify-mic-fallback's `ConfigChange`) |

Assumptions: only a mic that does not send gives `Listener.zeroSeconds` of exact zeros (one 90 s run, one room, 2026-11-24); `start()` succeeds on a plugged-in device; a replugged device gets a new `AudioDeviceID`, so its mark does not follow it.

## Does my change touch this?

`git diff 76ff65c -- Sources/voice-switch/MacApp.swift`, then look for any function in the coverage table, anything that writes `silent`, `lastSound` or the watcher table, or a new caller of `start()`. `python3 tests/verification_coverage.py affected <ref>` lists this skill when `covers.txt` matches.

## When it fails

1. Read the trace with `/statechart` and retell it in code words.
2. If every step is something the code can do, it is a real bug: report it with that story. Do not weaken `LiveMic` / `WatchOff` or remove an action.
3. If a step is something the code cannot do, fix the model and say what changed.

Broken variants seen failing on Design: `resolve` ignoring `silent` (LiveMic); no demotion in the timer (LiveMic); watcher never reviving (LiveMic); `start()` not opening watchers (LiveMic); `stop()` leaving watchers open (WatchOff).

## Not covered

- Startup: whether a transmitter that is on gives zeros for the first 2 s after the engine starts (not measured).
- A quieter room, a low transmitter battery, or radio dropouts shorter than 2 s.
- Whether the dictation open at the switch is kept: `restart()` → `halt()` bumps the capture generation, so the open dictation is dropped and the user says the wake word again.
- The menu labels, the HUD, Windows (`WinMmCapture`).
- Timing beyond order: the 2 s threshold, the 1 s timer, the 7 s transmitter start.
