---
name: verify-mic-fallback
description: Model-check (TLA+) that the macOS listener always records from a plugged-in mic when at least one is plugged in (MF-1), and from the highest-priority plugged-in candidate of the saved mic plus the 予備マイク fallback list (MF-2, the adopted option 2), across every order of plug, unplug, default-device change, pause, resume, mic selection, fallback-list edit, and every listener restart path. Use when a change touches InputRoute.resolve, inputDeviceIDs / builtInInputDeviceID, Listener.start / stop / startRetrying / restart / attemptStart / checkStall or the device / configuration-change observers in Listener.init, AppDelegate.selectDevice / menuNeedsUpdate, or adds the inputFallbackUIDs list, in Sources/voice-switch/MacApp.swift.
---

# Verify the mic priority and fallback (TLA+)

The user unplugs the DJI Wireless Mic Rx often. Today one saved mic (`inputDeviceUID`) is kept and, when it is missing, `InputRoute.resolve` falls back to the built-in mic only. The adopted design (option 2) adds an ordered fallback list (`inputFallbackUIDs`, edited in a 予備マイク submenu) and shows which mic is in use. `MicFallback.tla` explores every order of the events below for the code as it is (`Current.cfg`) and for option 2 (`Design.cfg`).

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `ValidMic` (MF-1, the universal condition) | When at least one mic is plugged in and no device or engine notification is still queued, a listening voice-switch records from a mic that is plugged in. | Wake words and dictation silently stop working after the Rx is unplugged, with the menu still showing 待ち受け中. |
| `TopCandidate` (MF-2) | In the same situation it records from the first plugged-in mic of [saved mic, fallback 1, fallback 2]; with none plugged in, the built-in mic, then the system default; with "システムのデフォルト" chosen, the system default. | The webcam the user ranked second is skipped for the built-in mic, or a fallback stays in use after the Rx comes back. |

"Settled" (no queued notification) is the moment the app has heard about the change. A pending 10 s retry does not count as unsettled: a retry that keeps failing while a mic is plugged in breaks MF-1.

## Check items

Run them in this order. Item 1 comes first because the symptom is something the user hears and sees; a model pass does not replace it.

1. **Real Mac, symptom (manual).** With the Rx chosen in the マイク menu and the webcam plugged in: unplug the Rx receiver, say the wake word, plug it back, say it again. Expected today: `~/Library/Logs/voice-switch.log` shows `input device <Rx UID> not found, falling back to MacBook Proのマイク` and the wake word works on the built-in mic. Expected with option 2 and the webcam as 予備 1: the fallback line names `Mic-j5 WebCam JVCU435`, and after re-plugging the next `input devices changed, restarting input` returns to the Rx. Record whether the wake word was recognized each time.
2. **Fallback is visible in the log.** Every start on a device other than the saved mic must write one line naming the device it chose (today: the `falling back to` line in `Listener.start`). Option 2 must keep one such line per start that names the device and its rank, so a real run tells "Rx" from "fallback 1" from "built-in" without the menu.
3. **Model, current code:** `sh .claude/skills/verify-mic-fallback/check.sh` → `== Current ==` ends with `Model checking completed. No error has been found.` (3784 distinct states at the sync commit).
4. **Model, option 2:** the same run → `== Design ==` passes (39868 distinct states at the sync commit).
5. **Implementation matches the model:** once option 2 is coded, re-map each Design-only part (the list in `Resolve`, `SetFallbacks`) to the new code and rerun. In particular, saving the fallback list must re-resolve through the same path as the device listener (`SetFallbacks` sets `devPending`); a list edit that waits for the next plug event fails MF-2.

## Run

```sh
sh .claude/skills/verify-mic-fallback/check.sh                      # both configs
sh .claude/skills/verify-mic-fallback/check.sh <copy.tla> Design    # a broken variant, one config
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` on first run; the same jar as `verify-meet-pause`). Needs Java 11+. About 15 s. Exit status is non-zero when either config fails.

Read every result, pass or fail, with the `/statechart` skill: a violation as its shortest path in plain words, a pass as a chart of the states.

## Coverage record

Sync commit: `c930389` (the model was last confirmed to describe the code at this commit, with option 2 implemented: the fallback list, config.json `microphones`, and the 1 s coalescing of device notices before `Listener.reroute`, which only lengthens `devPending`).

| Model part | Code (Sources/voice-switch/MacApp.swift) |
|---|---|
| `Mics` | `inputDeviceIDs()` (aggregate and speaker-terminal streams excluded) |
| `Builtin` | `builtInInputDeviceID()` |
| `Resolve` | `InputRoute.resolve(candidates:)` with `Listener.candidates`: config.json `microphones` (names or UIDs) when set, else the saved mic then `Listener.fallbackUIDs` (UserDefaults `inputFallbackUIDs`); first plugged-in one, then the built-in mic, then the system default |
| `Device` | `InputRoute.deviceID` (`defaultInputDeviceID()` for `.systemDefault`) |
| `AttemptStart` | `Listener.attemptStart` → `start()`; failure sets `activeRoute = nil` and schedules the 10 s retry |
| `Restart` | `Listener.restart()` (`halt(); attemptStart()`) |
| `DeviceEvent` | the `kAudioHardwarePropertyDevices` / `DefaultInputDevice` listener block in `Listener.init` → `Listener.reroute` (`guard wanted, resolve != activeRoute`) |
| `ConfigChange` | the `AVAudioEngineConfigurationChange` observer in `Listener.init` |
| `Retry` | the 10 s closure in `Listener.attemptStart` (`if wanted, !running`) |
| `Stall` | `Listener.checkStall` |
| `Stop` / `Resume` | `Listener.stop()` / `Listener.startRetrying()` (pause menu and hotkey, Meet hold, `pauseWhenMicrophoneConnected`) |
| `SelectDevice` | `AppDelegate.selectDevice` |
| `SetFallbacks` | `AppDelegate.toggleFallback` (the 予備マイク submenu built in `menuNeedsUpdate`): saves the list, then `Listener.reroute`, the same path as `DeviceEvent` |

Each action is one main-thread step. If a change moves one of them off the main queue or across an `await`, the model no longer describes it.

Assumptions: `start()` succeeds on a plugged-in device and throws on a missing one (transient CoreAudio errors such as -10851 are left out); the engine posts a configuration change when the default device it follows moves or disappears, while for a pinned device it may or may not (the comment above the listener block); the OS default input is always a listed, plugged-in device or none.

## Does my change touch this?

Run `git diff c930389 -- Sources/voice-switch/MacApp.swift` from the repo root and look for any function in the coverage table, anything that writes `Listener.wanted`, `running` or `activeRoute`, a new caller of `start()` / `attemptStart()`, or the new fallback list. A new caller of `start()` is a new action: add it to the model before trusting a pass.

## When it fails

1. Read the trace with `/statechart` and retell it in code words (e.g. "Rx unplugged → no configuration change comes → device listener sees resolve == activeRoute → tap stays on the missing Rx").
2. Check each step against the code it names. If every step is something the code can do, it is a real bug: report it with that story. Do not weaken `ValidMic` / `TopCandidate` or remove an action.
3. If a step is something the code cannot do, the model is wrong: fix the model, and say what you changed.

## Updating the model after an intended change

Mirror the code change in the matching action, keep `Expected` (the plain statement of the priority) separate from `Resolve` (the mirror of the code), run both configs, then try one broken variant of the new code and confirm it fails. Move the sync commit only after the model describes the code at the new commit.

Broken variants already seen failing:

- `resolve` pins the saved mic without checking `inputDeviceIDs().contains` (`hits` includes index 1 unconditionally): `ValidMic` fails in Current — start throws on the missing Rx and retries forever while the built-in mic is there.
- Device listener never restarts (`IF FALSE THEN Restart` in `DeviceEvent`): `ValidMic` fails in Current — Rx unplugged, no configuration change, tap left on the missing device.
- Saving the fallback list without re-resolving (`SetFallbacks` leaves `devPending`): `TopCandidate` fails in Design.
- Built-in tried before the fallback list: `TopCandidate` fails in Design.

## Not covered

- The Rx still listed while its transmitter is off ("plugged in but silent"): the model treats listed as usable. Check item 1 is the only place this shows up.
- Whether the built-in mic is listed with the lid closed; whether the OS default can be an aggregate or other unlisted device.
- The menu labels (使用中 / 未接続 / 停止中) and the HUD line on a change of device; only which device the tap reads is checked.
- Windows (`WinMmCapture.EffectiveDevice`, `TraySettings.MicDevice`, `VoiceSwitchTrayContext.FollowDevice`).
- Timing: the 10 s retry, the stall backoff up to 320 s, and how long "unsettled" lasts are orderings only.
- Two devices with the same UID, and fallback lists longer than two.
