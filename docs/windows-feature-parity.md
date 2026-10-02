# Windows feature parity audit

This document summarizes the repository-visible Windows parity review for the current branch. It cites committed source, tests, and contracts rather than uncommitted execution notes.

Required dictation experience means one spoken wake prefix plus body, removal of only the wake prefix and a standalone terminal stop, automatic transcription and paste into the original target, and safe continuation. The current Windows implementation does not yet establish that complete experience. A passing parity gate accepts 29 declared differences. It does not prove full user-experience parity.

Evidence terms:

- Implemented means current source contains the behavior.
- Tested core or synthetic means controlled input or regression coverage, not personal microphone or external application validation.
- Unsupported means no current Windows adapter implements the feature.
- HOLD means external behavior or safety is still unverified.

Command mode and dictation mode are separate. The default Windows sample remains command mode. `config.example.windows-dictation.json` is the explicit optional dictation sample.

## Experience matrix

Repository-relative source names:

- `Mac`: `Sources/voice-switch/MacApp.swift`
- `Win runtime`: `dotnet/VoiceSwitch.Windows/DictationRuntime.cs`
- `Win core`: `dotnet/VoiceSwitch.Windows.Core/DictationCore.cs`
- `Win CLI`: `dotnet/VoiceSwitch.Windows/Program.cs`
- `Tray`: `dotnet/VoiceSwitch.Windows.Tray/`

| Required experience | macOS source behavior | Windows command, dictation, or tray behavior | Verdict |
|---|---|---|---|
| Wake toggles configured recorder in command mode | Whole normalized wake runs the configured command. | Legacy command recognizer dispatches the configured wake command. | Implemented matching and dispatch. Live recognition equivalence is not proven. |
| Wake followed by arbitrary body in one breath | Lexical timestamps determine the body cut. A lone wake waits for speech. | Dictation SAPI recognition feeds absolute source sample ranges. Wake prefix starts the body. A lone wake waits. Legacy constrained command grammar is not this experience. | Implemented in core and runtime. Real microphone accuracy and latency are HOLD. |
| Preserve body exactly and trim prefix | Original PCM is sliced using the recognized cut. | Separate original and analysis lanes. The body WAV is rendered from original absolute samples. | Implemented and tested for exact PCM boundaries and WAV bytes. Acoustic boundary accuracy is recognizer-dependent. |
| Only standalone terminal stop ends dictation | A closed whole-utterance stop removes its own audio tail. | `ClosedUtterance` plus `StandaloneStopRange`; embedded, prefix-head, rejected, or late stops are retained. | Implemented and tested in core plus synthetic runtime. Live accuracy is HOLD. |
| Stop while recorder idle does nothing | Command stop checks configured process microphone use. | Windows command mode suppresses missing stop commands and the known Superwhisper record toggle. A custom `stopCommand` remains an operator-supplied idempotent stop contract. Dictation session stop is gated. | Safe default implemented. Observed recorder-state parity is still unsupported. This is allowed difference 23, not full parity. |
| Silence, maximum duration, and empty start timeout | Defaults are 1200 ms, 60 s, and 3000 ms. Trailing silence is stripped and empty sessions cancel. | Dictation timers use the source clock and handle delayed recognition/open stop. Tray uses the same runtime. | Implemented and tested synthetically. Live timing proof is HOLD. |
| Finish and cancel keyboard shortcuts | Global `CGEvent` tap borrows Superwhisper shortcuts while dictating. | Core finish/cancel semantics exist, but no global keyboard adapter exists. Console and tray cancellation stop the runtime. | Required shortcut user experience is unsupported. This is allowed difference 25. |
| Automatic transcription through installed Superwhisper | UUID WAV is opened with `open -g -a`; the active mode processes the file. | Registered `superwhisper://file//` intake dispatch is implemented for ordinary raw paths. Paths with unverified whitespace, reserved URI characters, or non-ASCII are retained as `DeferredUnsent` instead of launched. | Dispatch is implemented only for the raw-path contract. Actual intake and transcription are HOLD. Record-only output is not Superwhisper. |
| Paste into intended original application | Superwhisper owns paste. macOS captures frontmost target and retries activation. | No Windows target capture, restoration, or paste confinement adapter exists. | Target restoration is unsupported. Actual paste is HOLD. This is allowed difference 24. |
| Automatic completion and safe cleanup | Polls recent timestamp-named recording metadata for `llmResult` or `result` for up to 30 s. Own WAV is removed with `defer`. | UUID WAV plus manifest stays `SubmittedUnconfirmed`. A blocked body can be retained as `DeferredUnsent`. Only `--complete-handoff <id>` completes owned state after external completion or manual UNSENT recovery/discard. | Automatic completion is unsupported. Association is HOLD. This is allowed difference 26. |
| Seamless consecutive dictation | Capture continues. One `handoffBusy` flag prevents overlap and can drop a new submission while busy. | Record-only consecutive sessions were tested. Registered sink refuses startup while any owned state is pending. A raced completed body is retained once as `DeferredUnsent`, then the runtime stops visibly. | Local record-only pipeline works. Real repeated no-touch external workflow is blocked by manual completion. |
| Exclude sensitive or frontmost applications | Frontmost bundle ID exclusion runs before dictation starts. | Field parses and warns, but is not enforced. | Unsupported. This is allowed difference 5. |
| Skip wake while a designated app uses the microphone | CoreAudio process input-state check. | Field parses and warns only. No process microphone guard exists. | Unsupported. This is allowed differences 7 and 22. |
| Default mic and device selection/recovery | Nil follows default. Chosen UID is persisted. Menu picker exists. Engine is recreated for pinned change or config notifications. | WinMM opens the WaveMapper default at capture start. No device picker, pinning, hotplug, or default-change recovery exists. | Default-open is implemented. Device behavior is HOLD. Picker and recovery are unsupported. |
| Permissions and speech readiness | Apple model is prepared. Microphone and Accessibility permissions are surfaced. | SAPI diagnostics and capture errors exist. No equivalent first-run microphone/privacy onboarding or shortcut permission flow exists. | Error paths are implemented. Actual microphone permission and readiness are HOLD. |
| Resident status, start, pause, settings, error, quit | Menu icon, pause/resume, device/config/log/login/quit menu. Starts listening after permission on launch. | Optional tray starts Paused. It supports Start/Pause/Reload/config/error/status/Quit with serialized owned runtime and bounded IPC. | Tray is implemented. Paused launch is intentional. There is no device or login menu. |
| Safe duplicate ownership and stop/exit | The macOS app lifecycle owns the listener. | Tray uses canonical-config plus SID owner lease. Commands identify without acquiring the lease. Failed cleanup keeps live ownership. | Implemented and tested by `tests/windows/run-tray-host.ps1`. This proves lifecycle, not dictation or paste. |
| Reload without losing last valid config | Reloads at utterance boundary. Invalid config keeps old config. | Legacy command resident auto-reloads. Dictation console holds read-only CLI config. Tray manual Reload validates first, then cancels/restarts the running session. Invalid config retains current config/run. | Differentiated behavior is implemented. Automatic dictation reload and preserving an active dictation session across reload are not equivalent. |
| Login/autostart | `SMAppService` user toggle. | No Windows login registration. | Intentional scoped omission, not achieved parity. This is allowed difference 27. |
| Optional low-latency noise suppression | No matching required adapter was established by this audit. | Off by default. Conservative Wiener DSP applies only to the SAPI analysis/control lane. Original PCM, including its noise, is sent to handoff. | Supplementary control-recognition feature. BODY CER measures local SAPI hypothesis, not Superwhisper returned text. There is no noise-removed handoff claim. |

## Allowed difference accounting

The inventory source is `tests/parity/contracts/platform_parity.json`. "Allowed" means the gate tolerates an absence or difference. It is not a user waiver for missing required user experience. Several rows describe the same feature from different gate dimensions.

| # | Contract allowance | Feature meaning |
|---|---|---|
| 1 | sample `dictation` missing from Windows default sample | Default sample remains command mode. Explicit Windows dictation sample exists. This is not absent dictation engine. |
| 2 | sample `dictation.recordingsDir` missing from Windows default sample | Windows uses an owned lease directory instead of Mac metadata polling. Automatic completion remains missing. |
| 3 | sample `dictation.endSilenceMs` missing from Windows default sample | Available in explicit dictation sample/runtime. Sample-only difference. |
| 4 | sample `dictation.maxSeconds` missing from Windows default sample | Available in explicit dictation sample/runtime. Sample-only difference. |
| 5 | sample `dictation.excludeBundleIDs` missing from Windows default sample | Real unsupported foreground exclusion, despite parsed config. |
| 6 | sample `dictation.startTimeoutMs` missing from Windows default sample | Available in explicit dictation sample/runtime. Sample-only difference. |
| 7 | sample `skipWhileMicInUseBy` missing from Windows default sample | Real unsupported mic-use guard. Overlaps 22. |
| 8 | CLI `--help` missing from macOS | Windows help versus Mac menu first-run. Diagnostic surface difference. |
| 9 | CLI `--self-test` missing from macOS | Windows core harness versus older shared VAD self-test. Diagnostic difference. |
| 10 | CLI `--recognizers` missing from macOS | Windows System.Speech diagnostic. OS-specific surface. |
| 11 | CLI `--check-device` missing from macOS | Windows speech/default-input diagnostic. Does not implement a device picker. |
| 12 | CLI `--complete-handoff` missing from macOS | Manual Windows lease cleanup exists because automatic completion is absent. Required user-experience gap. |
| 13 | CLI `--listen-seconds` missing from macOS | Windows bounded live/diagnostic run. |
| 14 | CLI `--dry-run` missing from macOS | Suppresses dispatch. Live dry-run can still open the microphone. Synthetic ingress avoids microphone use. |
| 15 | CLI `--config` missing from macOS | Explicit Windows path versus Mac environment/default. Configuration entry difference. |
| 16 | CLI `--input-wav` missing from macOS | Windows public synthetic WAV ingress for dictation validation. Diagnostic difference. |
| 17 | CLI `--input-wav-fast` missing from macOS | Accelerated structural ingress. Does not prove real-time behavior. |
| 18 | CLI `--output-dir` missing from macOS | Windows owned record-only validation sink. Does not prove external intake/paste. |
| 19 | CLI `--check` missing from Windows | Mac Apple Speech file diagnostic absent by this name. Current Windows `--input-wav` and probes provide alternative coverage. Old "only probes" wording is stale. |
| 20 | CLI `--simulate` missing from Windows | Mac AVFoundation simulation absent by this name. Current Windows `--input-wav` and probes are analogous for file ingress. Old "only probes" wording is stale. |
| 21 | CLI `--hotkey-test` missing from Windows | Mac global hotkey adapter/test. Windows global shortcut implementation is absent. Overlaps 25. |
| 22 | capability `skip_while_mic_in_use_guard` missing from Windows | Real required guard gap. Overlaps 7. |
| 23 | capability `stop_word_session_gating` missing from Windows | Command mode has safe defaults but no observed recorder-state guard for arbitrary custom stop commands. Dictation stop is session-gated. Real mode-specific difference. |
| 24 | capability `dictation_target_restoration` missing from Windows | Real missing foreground restoration. External focus behavior is HOLD. |
| 25 | capability `dictation_finish_cancel_shortcuts` missing from Windows | Real missing global shortcut user experience. Overlaps 21. |
| 26 | capability `dictation_automatic_completion` missing from Windows | Real missing automatic correlation/cleanup. Manual pending blocks consecutive external sessions. |
| 27 | capability `menu_bar_device_and_login_items` missing from Windows | Broad name bundles implemented tray with absent device picker/login. Tray existence does not close picker/login gaps. |
| 28 | capability `windows_speech_diagnostics` missing from macOS | Intentional System.Speech-specific diagnostics. Overlaps 10 and 11. |
| 29 | sample `stopCommand` missing from Windows default sample | Intentional safety difference: Windows cannot observe external recording state and suppresses known recording toggles. Operators may provide an idempotent custom stop. |

Entry totals, with each numbered row counted once:

- 4 sample-only rows: 1, 3, 4, 6.
- 13 diagnostic or configuration-entry rows: 8 through 11, 13 through 20, and 28.
- 11 rows exposing required user-experience gaps or changed behavior: 2, 5, 7, 12, 21 through 26, and 29.
- 1 mixed tray/device/login grouping: 27.

Total: 29. These are entry totals, not unique missing features. Rows 7 and 22 overlap. Rows 21 and 25 overlap. Rows 2, 12, and 26 concern the same completion boundary.

## What the gates prove

The parity runner compares normalized fixtures, decisions, and segmenter outputs. It checks source markers and mutation behavior. Its shared `dictation_manual_handoff_lifecycle` marker explicitly compares Mac automatic polling/defer with Windows manual leases. Its `config_reload_keeps_previous_on_error` Windows marker targets legacy `Program` reload, not immutable dictation-console configuration.

Therefore a shared marker pass does not establish equivalent end-to-end handoff or reload user experience. Core tests cover exact source ranges, delayed recognition, embedded stops, empty/cancel paths, and ownership assertions. Synthetic recognition is not live SAPI acoustic accuracy. Record-only sinks are not external transcription.

Highest-priority missing required user experience is safe automatic external transcription and paste into the original target, with trustworthy job identity and automatic lease completion. Manual pending currently prevents seamless repeated external dictation. Noise treatment improves only the local SAPI analysis/control lane. Original noisy PCM still goes to handoff. BODY CER is a local SAPI hypothesis metric, not external returned text or delivered-transcription accuracy.

Next missing pieces are global finish/cancel, target exclusion and recorder mic-use safeguards, device selection/change recovery, and the dictation reload experience. These are separate from OS-specific diagnostics and intentional Paused/no-autostart choices.

Actual external validation remains HOLD. Future validation must treat clipboard, selected text, active-application context, focus movement, and automatic paste as risk conditions to verify directly. An empty owned text control alone does not isolate clipboard/context or prove the paste destination. Keyboard hooks do not confine all input paths. A new desktop does not isolate the window-station clipboard or prove singleton routing.
