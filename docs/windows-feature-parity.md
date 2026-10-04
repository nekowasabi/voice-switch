# Windows feature parity audit

This document summarizes the repository-visible Windows parity review for the current branch. It cites committed source, tests, and contracts rather than uncommitted execution notes.

Required dictation experience means one spoken wake prefix plus body, removal of only the wake prefix and a standalone terminal stop, automatic transcription and paste into the original target, and safe continuation. The current Windows implementation has every step in code and tests, but live microphone, real Superwhisper paste, and focus behavior are not yet hand-tested on Windows. A passing parity gate accepts 23 declared differences. It does not prove full user-experience parity.

Evidence terms:

- Implemented means current source contains the behavior.
- Tested core or synthetic means controlled input or regression coverage, not personal microphone or external application validation.
- Unsupported means no current Windows adapter implements the feature.
- HOLD means external behavior or safety is still unverified.

Command mode and dictation mode are separate. The default Windows sample `config.example.windows.json` is dictation mode, like macOS; removing its `dictation` block selects command mode.

## Experience matrix

Repository-relative source names:

- `Mac`: `Sources/voice-switch/MacApp.swift`
- `Win runtime`: `dotnet/VoiceSwitch.Windows/DictationRuntime.cs`
- `Win core`: `dotnet/VoiceSwitch.Windows.Core/DictationCore.cs`
- `Win CLI`: `dotnet/VoiceSwitch.Windows/Program.cs`
- `Tray`: `dotnet/VoiceSwitch.Windows/Tray/` (same `voice-switch.exe`)

| Required experience | macOS source behavior | Windows command, dictation, or tray behavior | Verdict |
|---|---|---|---|
| Wake toggles configured recorder in command mode | Whole normalized wake runs the configured command. | Legacy command recognizer dispatches the configured wake command. | Implemented matching and dispatch. Live recognition equivalence is not proven. |
| Wake followed by arbitrary body in one breath | Lexical timestamps determine the body cut. A lone wake waits for speech. | Dictation SAPI recognition feeds absolute source sample ranges. Wake prefix starts the body. A lone wake waits. Legacy constrained command grammar is not this experience. | Implemented in core and runtime. Real microphone accuracy and latency are HOLD. |
| Preserve body exactly and trim prefix | Original PCM is sliced using the recognized cut. | Separate original and analysis lanes. The body WAV is rendered from original absolute samples. | Implemented and tested for exact PCM boundaries and WAV bytes. Acoustic boundary accuracy is recognizer-dependent. |
| Only standalone terminal stop ends dictation | A closed whole-utterance stop removes its own audio tail. | `ClosedUtterance` plus `StandaloneStopRange`; embedded, prefix-head, rejected, or late stops are retained. | Implemented and tested in core plus synthetic runtime. Live accuracy is HOLD. |
| Stop while recorder idle does nothing | Command stop checks configured process microphone use. | Windows command mode suppresses missing stop commands and the known Superwhisper record toggle. A custom `stopCommand` remains an operator-supplied idempotent stop contract. Dictation session stop is gated. | Safe default implemented. Observed recorder-state parity is still unsupported. This is the allowed gap `stop_word_session_gating`, not full parity. |
| Silence, maximum duration, and empty start timeout | Defaults are 1200 ms, 60 s, and 3000 ms. Trailing silence is stripped and empty sessions cancel. | Dictation timers use the source clock and handle delayed recognition/open stop. Tray uses the same runtime. | Implemented and tested synthetically. Live timing proof is HOLD. |
| Finish and cancel keyboard shortcuts | Global `CGEvent` tap borrows Superwhisper shortcuts while dictating. | While a dictation is open (Waiting or Recording), the tray installs a `WH_KEYBOARD_LL` hook and borrows Superwhisper's finish and cancel keys. Keys come from `%LOCALAPPDATA%\com.superwhisper.app\preferences.json` (`toggleRecordingShortcut`, `cancelRecordingShortcut`), with `Control+Space` and `Escape` as fallback. The key and its keyUp are swallowed, finish sends the body and cancel sends nothing. The hook is removed when the phase is Idle or Ended. Console runs have no hook. | Implemented and tested in core and runtime with fakes. Hand test on Windows is HOLD for real Ctrl+Space and Esc. Known deviations: UIPI means keys typed into an elevated (admin) window bypass the hook, and Alt or Win chords are not specially handled. |
| Recording HUD and phase feedback | A HUD shows the phase. Phases are Idle, Waiting, Recording, Ended. | The runtime derives the phase each loop and logs `dictation phase: X` on change. The tray shows a 160x40 rounded top-center HUD (`WS_EX_NOACTIVATE`, `TOOLWINDOW`, `TRANSPARENT`, `TOPMOST`): 🎙 どうぞ, ● 録音中 (red), ■ 録音終了 for 1.5 s. Ended shows only for a stop word or the finish key. | Implemented. The phase log and the HUD state are tested. HUD placement on a multi-monitor setup is HOLD for a hand test. |
| Confirmation sound | Optional Tink sound on a lone wake, toggled from the menu. | Tray menu item 効果音 toggles `HKCU\Software\voice-switch` `ConfirmationSound`. Default is off. It plays on a lone wake only. For 600 ms after it, the VAD gets zeros while the store keeps real audio. | Implemented and tested with fakes. Audible result and no self-trigger on real hardware are HOLD. |
| Automatic transcription through installed Superwhisper | UUID WAV is opened with `open -g -a`; the active mode processes the file. | Registered `superwhisper://file//` intake dispatch is implemented for ordinary raw paths. Paths with whitespace, reserved URI characters, or non-ASCII fail before anything is written or launched. | Dispatch is implemented only for the raw-path contract. Actual intake and transcription are HOLD. Record-only output is not Superwhisper. |
| Paste into intended original application | Superwhisper owns paste. macOS captures frontmost target and retries activation. | At wake the frontmost window is recorded. For 2 s after handoff, while Superwhisper is the foreground window, the tray restores that window with `AttachThreadInput` and `SetForegroundWindow`. It does not use `AllowSetForegroundWindow` or a synthetic Alt. Each attempt logs the foreground process, the target, and the result. There is no paste confinement. | Implemented. Compile and unit tests only. Focus restore and the real paste are HOLD until a hand test on Windows. |
| Automatic completion and safe cleanup | Polls recent timestamp-named recording metadata for `llmResult` or `result` for up to 30 s. Own WAV is removed with `defer`. | Same poll of `dictation.recordingsDir` (default `%LOCALAPPDATA%\com.superwhisper.app\recordings`) for 30 s, then the UUID WAV is deleted. With no result the WAV is kept and logged. Files older than 10 minutes in the owned directory are swept when the handoff is created. | Implemented and tested with a fake process and recordings folder. Live association is HOLD. |
| Seamless consecutive dictation | Capture continues. One `handoffBusy` flag prevents overlap and can drop a new submission while busy. | Capture continues while the handoff runs off the loop. One in-flight handoff at a time; a dictation that ends meanwhile is dropped with `dictation dropped: previous one still in flight`. | Implemented and tested with fakes for every handoff status. Real repeated external workflow is HOLD. |
| Exclude sensitive or frontmost applications | Frontmost bundle ID exclusion runs before dictation starts. | Field parses and warns, but is not enforced. | Unsupported. This is the allowed sample gap `dictation.excludeBundleIDs`. |
| Skip wake while a designated app uses the microphone | CoreAudio process input-state check. | Field parses and warns only. No process microphone guard exists. | Unsupported. These are the allowed gaps `skipWhileMicInUseBy` and `skip_while_mic_in_use_guard`. |
| Default mic and device selection/recovery | Nil follows default. Chosen UID is persisted. Menu picker exists. Engine is recreated for pinned change or config notifications. | WinMM opens the WaveMapper default at capture start. No device picker, pinning, hotplug, or default-change recovery exists. | Default-open is implemented. Device behavior is HOLD. Picker and recovery are unsupported. |
| Permissions and speech readiness | Apple model is prepared. Microphone and Accessibility permissions are surfaced. | SAPI diagnostics and capture errors exist. No equivalent first-run microphone/privacy onboarding or shortcut permission flow exists. | Error paths are implemented. Actual microphone permission and readiness are HOLD. |
| Resident status, start, pause, settings, error, quit | Menu icon, pause/resume, device/config/log/login/quit menu. Starts listening after permission on launch. | `voice-switch.exe` launched without diagnostic flags is the tray app and starts listening, in command or dictation mode. `--paused` opts out. It supports Start/Pause/Reload/config/error/status/Quit with serialized owned runtime and bounded IPC. | Tray is implemented and matches the macOS listen-on-launch default. There is no device or login menu. |
| Safe duplicate ownership and stop/exit | The macOS app lifecycle owns the listener. | Tray uses canonical-config plus SID owner lease. Commands identify without acquiring the lease. Failed cleanup keeps live ownership. Every PowerShell recognizer child (the warm dictation SAPI child and the command-mode recognizer) is assigned to a kill-on-close Job Object at spawn, so it dies with `voice-switch.exe` even after a crash or a forced kill. | Implemented and tested by `tests/windows/run-tray-host.ps1`. This proves lifecycle, not dictation or paste. |
| Reload without losing last valid config | Reloads at utterance boundary. Invalid config keeps old config. | Legacy command resident auto-reloads. Dictation console holds read-only CLI config. Tray manual Reload validates first, then cancels/restarts the running session. Invalid config retains current config/run. | Differentiated behavior is implemented. Automatic dictation reload and preserving an active dictation session across reload are not equivalent. |
| Login/autostart | `SMAppService` user toggle. | No Windows login registration. | Intentional scoped omission, not achieved parity. This is the allowed gap `menu_bar_device_and_login_items`. |
| Optional low-latency noise suppression | No matching required adapter was established by this audit. | Off by default. Conservative Wiener DSP applies only to the SAPI analysis/control lane. Original PCM, including its noise, is sent to handoff. | Supplementary control-recognition feature. BODY CER measures local SAPI hypothesis, not Superwhisper returned text. There is no noise-removed handoff claim. |

## Allowed difference accounting

The inventory source is `tests/parity/contracts/platform_parity.json`. "Allowed" means the gate tolerates an absence or difference. It is not a user waiver for missing required user experience. The default Windows sample is now dictation mode, so the old sample-only rows for `dictation`, `recordingsDir`, `endSilenceMs`, `maxSeconds`, and `startTimeoutMs` are gone. Finish and cancel shortcuts and target restoration are now shared capabilities with Windows markers, not gaps.

| Group | Entries | Feature meaning |
|---|---|---|
| Sample config gaps (3) | `stopCommand` | Intentional safety difference: Windows cannot observe external recording state and suppresses known recording toggles. Operators may provide an idempotent custom stop. |
| | `dictation.excludeBundleIDs` | Real unsupported foreground exclusion, despite parsed config. |
| | `skipWhileMicInUseBy` | Real unsupported mic-use guard. Overlaps `skip_while_mic_in_use_guard`. |
| CLI gaps (16) | `--paused`, `--record-only`, `--tray-command` | Windows tray surfaces with no macOS counterpart. |
| | `--help`, `--self-test`, `--recognizers`, `--check-device`, `--listen-seconds`, `--dry-run`, `--config` | Windows diagnostic and configuration entry points. `--check-device` does not implement a device picker. `--dry-run` can still open the microphone. |
| | `--input-wav`, `--input-wav-fast`, `--output-dir` | Synthetic WAV ingress and the record-only sink. They do not prove real-time behavior or external intake and paste. |
| | `--check`, `--simulate` | Mac file diagnostics absent by this name. Windows `--input-wav` and probes cover file ingress. |
| | `--hotkey-test` | Mac hotkey test tool. Windows has the hook itself but no standalone test mode. |
| Capability gaps (4) | `skip_while_mic_in_use_guard` | Real required guard gap. |
| | `stop_word_session_gating` | Command mode has safe defaults but no observed recorder-state guard for arbitrary custom stop commands. Dictation stop is session-gated. |
| | `menu_bar_device_and_login_items` | Tray existence does not close the picker and login gaps. |
| | `windows_speech_diagnostics` | Intentional System.Speech-specific diagnostics. |

Total: 3 + 16 + 4 = 23 entries, not unique missing features.

## Known Windows deviations

- UIPI: the low-level hook does not see keys typed into an elevated (admin) window from a non-elevated voice-switch. Finish and cancel then reach the app as normal keys.
- `NoResult` keeps the WAV for manual recovery until the 10 minute sweep. macOS deletes it.
- Alt and Win chords are not specially handled. Only the configured finish and cancel keys are matched.
- Focus restore runs only for 2 s after handoff and only while Superwhisper is the foreground window.

## Verified by tests or compile only

Everything above is covered by Release compile, package-free unit tests, and the synthetic runtime harness with fakes. None of it has run on a live Windows desktop. These need a hand test on Windows:

- Live microphone wake and stop.
- Real Ctrl+Space finish and Esc cancel through the hook.
- HUD placement on a multi-monitor setup.
- Focus restore to the original window.
- Real Superwhisper intake, result, and paste.
- The 効果音 sound on real hardware.

## What the gates prove

The parity runner compares normalized fixtures, decisions, and segmenter outputs. It checks source markers and mutation behavior. Its shared `dictation_handoff_lifecycle` and `dictation_one_handoff_at_a_time` markers compare the Mac and Windows result polls and in-flight drop. Its `config_reload_keeps_previous_on_error` Windows marker targets legacy `Program` reload, not immutable dictation-console configuration.

Therefore a shared marker pass does not establish equivalent end-to-end handoff or reload user experience. Core tests cover exact source ranges, delayed recognition, embedded stops, empty/cancel paths, and ownership assertions. Synthetic recognition is not live SAPI acoustic accuracy. Record-only sinks are not external transcription.

Highest-priority missing required user experience is safe automatic external transcription and paste into the original target, with trustworthy job identity. The result poll attributes the newest run within 2 s of launch, like macOS. Noise treatment improves only the local SAPI analysis/control lane. Original noisy PCM still goes to handoff. BODY CER is a local SAPI hypothesis metric, not external returned text or delivered-transcription accuracy.

Next missing pieces are target exclusion and recorder mic-use safeguards, device selection/change recovery, and the dictation reload experience. These are separate from OS-specific diagnostics and the intentional no-autostart choice.

Actual external validation remains HOLD. Future validation must treat clipboard, selected text, active-application context, focus movement, and automatic paste as risk conditions to verify directly. An empty owned text control alone does not isolate clipboard/context or prove the paste destination. Keyboard hooks do not confine all input paths. A new desktop does not isolate the window-station clipboard or prove singleton routing.
