# Windows feature parity audit

This document summarizes the repository-visible Windows parity review for the current branch. It cites committed source, tests, and contracts rather than uncommitted execution notes.

Required dictation experience means one spoken wake prefix plus body, removal of only the wake prefix and a standalone terminal stop, automatic transcription and paste into the original target, and safe continuation. The current Windows implementation has every step in code and tests, but live microphone, real Superwhisper paste, and focus behavior are not yet hand-tested on Windows. A passing parity gate accepts 20 declared differences. It does not prove full user-experience parity.

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
| Wake followed by arbitrary body in one breath | Lexical timestamps determine the body cut. A lone wake waits for speech. | Dictation SAPI recognition feeds absolute source sample ranges. Wake prefix starts the body. A lone wake waits. Legacy constrained command grammar is not this experience. Beyond Mac: a SAPI word that fuses the wake word's tail with the first body characters ("音声" + "入力今日") is split by character count, and an utterance SAPI rejects whose best guess is exactly a wake word (confidence at least 0.2) opens a wait, never a body, logged as `wake-only ... via=rejected`. A wake word is also matched by kana reading (SAPI lexical forms against MS-IME readings of the wake words) within edit distance 1: a distance-1 match opens a wait only, logged `via=reading d=1`, and a reading of 4 kana or fewer must be the whole utterance. | Implemented in core and runtime. Real microphone accuracy and latency are HOLD. On 84 TTS fixtures (clean, three speaking rates, white noise at 30/20/15 dB SNR) neither addition changed a result: 8 of 30 wake fixtures opened a session before and after, 0 of 54 non-wake fixtures did. SAPI never rejected there; its wake misses are substitutions such as 温泉有力 or 5200. The reading match leaves the TTS result at 8 of 30 and 0 of 54, and on the 2026-10-04 live log it accepts 7 missed wake attempts (温水 x5, 温泉, 温泉に入る) and no other utterance. |
| Preserve body exactly and trim prefix | Original PCM is sliced using the recognized cut. | Separate original and analysis lanes. The body WAV is rendered from original absolute samples. | Implemented and tested for exact PCM boundaries and WAV bytes. Acoustic boundary accuracy is recognizer-dependent. |
| Only standalone terminal stop ends dictation | A closed whole-utterance stop removes its own audio tail. | `ClosedUtterance` plus `StandaloneStopRange`; embedded, prefix-head, rejected, or late stops are retained. | Implemented and tested in core plus synthetic runtime. Live accuracy is HOLD. |
| Stop while recorder idle does nothing | Command stop checks configured process microphone use. | Windows command mode suppresses missing stop commands and the known Superwhisper record toggle. A custom `stopCommand` remains an operator-supplied idempotent stop contract. Dictation session stop is gated. | Safe default implemented. Observed recorder-state parity is still unsupported. This is the allowed gap `stop_word_session_gating`, not full parity. |
| Silence, maximum duration, and empty start timeout | Defaults are 1200 ms, 60 s, and 3000 ms. Trailing silence is stripped and empty sessions cancel. | Dictation timers use the source clock and handle delayed recognition/open stop. Tray uses the same runtime. | Implemented and tested synthetically. Live timing proof is HOLD. |
| Finish and cancel keyboard shortcuts | Global `CGEvent` tap borrows Superwhisper shortcuts while dictating. | While a dictation is open (Waiting or Recording), the tray installs a `WH_KEYBOARD_LL` hook and borrows Superwhisper's finish and cancel keys. Keys come from `%LOCALAPPDATA%\com.superwhisper.app\preferences.json` (`toggleRecordingShortcut`, `cancelRecordingShortcut`), with `Control+Space` and `Escape` as fallback. The key and its keyUp are swallowed, finish sends the body and cancel sends nothing. Finish after a lone wake word, once speech followed it, hands off at once like Mac: the body runs from the first VAD onset after the wake (less the preroll, never into the wake word) to the last live speech, and the SAPI recognition still in flight is dropped as stale. The hook is removed when the phase is Idle or Ended. Console runs have no hook. | Implemented and tested in core and runtime with fakes. Hand test on Windows is HOLD for real Ctrl+Space and Esc. Known deviations: UIPI means keys typed into an elevated (admin) window bypass the hook, and Alt or Win chords are not specially handled. |
| Recording HUD and phase feedback | A HUD shows the phase. Phases are Idle, Waiting, Recording, Ended. | The runtime derives the phase each loop and logs `dictation phase: X` on change. The tray shows a 160x40 rounded top-center HUD (`WS_EX_NOACTIVATE`, `TOOLWINDOW`, `TRANSPARENT`, `TOPMOST`): 🎙 どうぞ, ● 録音中 (red), ■ 録音終了 for 1.5 s. Ended shows only for a stop word or the finish key. | Implemented. The phase log and the HUD state are tested. HUD placement on a multi-monitor setup is HOLD for a hand test. |
| Confirmation sound | Optional Tink sound on a lone wake, toggled from the menu. | Tray menu item 効果音 toggles `HKCU\Software\voice-switch` `ConfirmationSound`. Default is off. It plays on a lone wake only. For 600 ms after it, the VAD gets zeros while the store keeps real audio. | Implemented and tested with fakes. Audible result and no self-trigger on real hardware are HOLD. |
| Automatic transcription through installed Superwhisper | UUID WAV is opened with `open -g -a`; the active mode processes the file. | Registered `superwhisper://file//` intake dispatch is implemented for ordinary raw paths. The WAV folder is chosen once per run start: `%LOCALAPPDATA%\voice-switch\dictation-handoffs` when it is plain ASCII, else its 8.3 short name, else `%ProgramData%\voice-switch\dictation-handoffs\<user SID>` with an owner-only ACL. A path that is still unsafe fails before anything is written or launched. | Dispatch is implemented only for the raw-path contract. Actual intake and transcription are HOLD. Record-only output is not Superwhisper. |
| Paste into intended original application | Superwhisper owns paste. macOS captures frontmost target and retries activation. | At wake the frontmost window is recorded. For 2 s after handoff, while Superwhisper is the foreground window, the tray restores that window with `AttachThreadInput` and `SetForegroundWindow`. It does not use `AllowSetForegroundWindow` or a synthetic Alt. Each attempt logs the foreground process, the target, and the result. There is no paste confinement. | Implemented. Compile and unit tests only. Focus restore and the real paste are HOLD until a hand test on Windows. |
| Automatic completion and safe cleanup | Polls recent timestamp-named recording metadata for `llmResult` or `result` for up to 30 s. Own WAV is removed with `defer`. | Same poll of `dictation.recordingsDir` (default `%LOCALAPPDATA%\com.superwhisper.app\recordings`) for 30 s, then the UUID WAV is deleted. With no result the WAV is kept and logged. Files older than 10 minutes in the owned directory are swept when the handoff is created. | Implemented and tested with a fake process and recordings folder. Live association is HOLD. |
| Seamless consecutive dictation | Capture continues. One `handoffBusy` flag prevents overlap and can drop a new submission while busy. | Capture continues while the handoff runs off the loop. One in-flight handoff at a time; a dictation that ends meanwhile is dropped with `dictation dropped: previous one still in flight`. | Implemented and tested with fakes for every handoff status. Real repeated external workflow is HOLD. |
| Exclude sensitive or frontmost applications | Frontmost bundle ID exclusion runs before dictation starts. | `dictation.excludeProcessNames` lists executables. When a wake word opens a dictation and the window in front belongs to one of them, the dictation is dropped and `dictation skipped: <name> is excluded` is logged. `dictation.excludeBundleIDs` is ignored with a warning. | Implemented and tested with fakes. The key differs by platform: `dictation.excludeBundleIDs` and `dictation.excludeProcessNames` are each allowed sample gaps on the other platform. |
| Skip wake while a designated app uses the microphone | CoreAudio process input-state check. | `skipWhileMicInUseBy` lists executable names. At a wake word the runtime enumerates the audio sessions of every active capture endpoint (`IAudioSessionManager2`) and skips if a listed process has an Active one: `skipped: Superwhisper is using the microphone`. Command mode skips the wake command the same way. A failed query lets the wake through. | Implemented. The session query is tested natively against the test process's own WinMM capture (Active while open, gone after). Superwhisper was observed Inactive while idle; Active while it records is HOLD for a hand test. The shared capability `skip_while_mic_in_use_guard` replaces the gap. |
| Default mic and device selection/recovery | Nil follows default. Chosen UID is persisted. Menu picker exists. Engine is recreated for pinned change or config notifications. | The tray's マイク submenu lists システムのデフォルト and every WinMM input device, rebuilt on each open. The choice is saved by device name in `HKCU\Software\voice-switch` `MicDevice` and restarts a running dictation onto it. A pinned device that is not connected falls back to the default. Every 2 s the tray compares the device a fresh open would pick with the one in use and restarts the run when they differ (unplug, replug, or a new Windows default), and starts it again from an error state when a device appears. | Implemented in dictation mode. Device resolution is tested; enumeration and the default lookup are tested natively. WinMM cuts names at 31 characters, so two devices with the same cut name are indistinguishable. Command mode (SAPI) always uses the default device. Unplug and default-change recovery are HOLD for a hand test with a second device. |
| Permissions and speech readiness | Apple model is prepared. Microphone and Accessibility permissions are surfaced. | SAPI diagnostics and capture errors exist. No equivalent first-run microphone/privacy onboarding or shortcut permission flow exists. | Error paths are implemented. Actual microphone permission and readiness are HOLD. |
| Resident status, start, pause, settings, error, quit | Menu icon, pause/resume, device/config/log/login/quit menu. Starts listening after permission on launch. | `voice-switch.exe` launched without diagnostic flags is the tray app and starts listening, in command or dictation mode. `--paused` opts out. The tray icon is a microphone while listening and a struck-through microphone otherwise (drawn at startup in the taskbar's light or dark ink). The menu is Japanese like Mac: 状態, 再開, 一時停止, マイク, 設定ファイルを開く, 設定を再読み込み, ログを開く, 効果音, ログイン時に起動, 直近のエラー, 終了, with serialized owned runtime and bounded IPC. Double-clicking the icon shows the config path and instance. | Tray is implemented and matches the macOS listen-on-launch default. `--tray-command status` reports the icon (`mic` or `mic-slash`) and each item's check state. |
| Safe duplicate ownership and stop/exit | The macOS app lifecycle owns the listener. | Tray uses canonical-config plus SID owner lease. Commands identify without acquiring the lease. Failed cleanup keeps live ownership. Every PowerShell recognizer child (the warm dictation SAPI child and the command-mode recognizer) is assigned to a kill-on-close Job Object at spawn, so it dies with `voice-switch.exe` even after a crash or a forced kill. | Implemented and tested by `tests/windows/run-tray-host.ps1`. This proves lifecycle, not dictation or paste. |
| Reload without losing last valid config | Reloads at utterance boundary. Invalid config keeps old config. | Legacy command resident auto-reloads. Dictation mode (console and tray) checks the file's modification time like Mac, at an utterance boundary while no dictation is open and no recognition is pending, and applies the new config to the VAD and the next session; an invalid file keeps the previous config. `locale` and `noiseReduction` changes need the tray Reload, which validates first and restarts the run. | Implemented and tested with a fake reload source and a real file. Unlike Mac, a locale change does not reach the warm SAPI child until Reload. |
| Login/autostart | `SMAppService` user toggle. | ログイン時に起動 toggles `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` `voice-switch` = `"<exe>" --config "<config>"`. The check mark is read from the registry each time the menu opens, and an entry left by another copy reads as off. | Implemented; the registry round trip is tested natively. An exe under `\\wsl.localhost` may not be reachable at sign-in before WSL starts; enabling it from such a path shows that warning. |
| Optional low-latency noise suppression | No matching required adapter was established by this audit. | Off by default. Conservative Wiener DSP applies only to the SAPI analysis/control lane. Original PCM, including its noise, is sent to handoff. | Supplementary control-recognition feature. BODY CER measures local SAPI hypothesis, not Superwhisper returned text. There is no noise-removed handoff claim. |

## Allowed difference accounting

The inventory source is `tests/parity/contracts/platform_parity.json`. "Allowed" means the gate tolerates an absence or difference. It is not a user waiver for missing required user experience. The default Windows sample is now dictation mode, so the old sample-only rows for `dictation`, `recordingsDir`, `endSilenceMs`, `maxSeconds`, and `startTimeoutMs` are gone. Finish and cancel shortcuts and target restoration are now shared capabilities with Windows markers, not gaps.

| Group | Entries | Feature meaning |
|---|---|---|
| Sample config gaps (3) | `stopCommand` | Intentional safety difference: the default sample omits the record toggle as a stop command. Operators may provide an idempotent custom stop. |
| | `dictation.excludeBundleIDs` | macOS key; Windows uses `dictation.excludeProcessNames`. |
| | `dictation.excludeProcessNames` | Windows key; macOS parses and ignores it. |
| CLI gaps (15) | `--paused`, `--record-only`, `--tray-command` | Windows tray surfaces with no macOS counterpart. |
| | `--help`, `--self-test`, `--recognizers`, `--check-device`, `--listen-seconds`, `--dry-run`, `--config` | Windows diagnostic and configuration entry points. `--check-device` does not implement a device picker. `--dry-run` can still open the microphone. |
| | `--input-wav`, `--input-wav-fast`, `--output-dir` | Synthetic WAV ingress and the record-only sink. They do not prove real-time behavior or external intake and paste. |
| | `--simulate` | Mac live-path file driver. Windows `--input-wav` covers it under another name. `--check` is now shared. |
| | `--hotkey-test` | Mac hotkey test tool. Windows has the hook itself but no standalone test mode. |
| Capability gaps (2) | `stop_word_session_gating` | Command mode has safe defaults but does not yet gate custom stop commands on the mic-in-use check that now exists. Dictation stop is session-gated. |
| | `windows_speech_diagnostics` | Intentional System.Speech-specific diagnostics. |

Total: 3 + 15 + 2 = 20 entries, not unique missing features. `menu_bar_device_and_login_items` and `skip_while_mic_in_use_guard` are now shared capabilities.

## Known Windows deviations

- UIPI: the low-level hook does not see keys typed into an elevated (admin) window from a non-elevated voice-switch. Finish and cancel then reach the app as normal keys.
- `NoResult` keeps the WAV for manual recovery until the 10 minute sweep. macOS deletes it.
- Alt and Win chords are not specially handled. Only the configured finish and cancel keys are matched.
- Focus restore runs only for 2 s after handoff and only while Superwhisper is the foreground window.
- Beyond Mac: SAPI words that fuse the wake word with the body are split by character count, and a rejected hypothesis that is exactly a wake word opens a wait. Neither changed a result on the 84 TTS fixtures. A wake word heard as a near-homophone (音声 as 温泉 or 温水) is matched by kana reading within edit distance 1 and opens a wait only.
- A config reload in dictation mode does not reach the warm SAPI child's locale or the noise processor; those need the tray Reload.
- The マイク picker and device recovery cover dictation mode only; command mode (SAPI) always records from the default device. Device names are WinMM's, cut at 31 characters.
- `dictation.excludeProcessNames` replaces `dictation.excludeBundleIDs`, and `skipWhileMicInUseBy` lists executable names instead of bundle IDs.
- `--check` reads PCM16 mono 16 kHz WAV only, where macOS converts any audio file.

## Verified by tests or compile only

Everything above is covered by Release compile, package-free unit tests, and the synthetic runtime harness with fakes. Some of it was also run natively on Windows: the job object (a hard-killed tray leaves no PowerShell child), the dictation config reload, the handoff folder log, the tray icon and Japanese menu through `--tray-command status`, the microphone pinning and fallback, the mic-in-use guard between two voice-switch processes, and `--check` on TTS fixtures. These still need a hand test on Windows:

- Live microphone wake and stop.
- Ctrl+Space right after speaking following a lone wake word hands off at once.
- The wake is ignored while Superwhisper itself records (its session turning Active).
- Unplugging the chosen microphone or changing the Windows default device restarts capture on the new device.
- The tray icon in the real taskbar, and ログイン時に起動 at an actual sign-in.
- Real Ctrl+Space finish and Esc cancel through the hook.
- HUD placement on a multi-monitor setup.
- Focus restore to the original window.
- Real Superwhisper intake, result, and paste.
- The 効果音 sound on real hardware.

## What the gates prove

The parity runner compares normalized fixtures, decisions, and segmenter outputs. It checks source markers and mutation behavior. Its shared `dictation_handoff_lifecycle` and `dictation_one_handoff_at_a_time` markers compare the Mac and Windows result polls and in-flight drop. Its `config_reload_keeps_previous_on_error` Windows marker targets `Program.cs`, where the `ConfigFile` reload now serves both command mode and the dictation runtime.

Therefore a shared marker pass does not establish equivalent end-to-end handoff or reload user experience. Core tests cover exact source ranges, delayed recognition, embedded stops, empty/cancel paths, and ownership assertions. Synthetic recognition is not live SAPI acoustic accuracy. Record-only sinks are not external transcription.

Highest-priority missing required user experience is safe automatic external transcription and paste into the original target, with trustworthy job identity. The result poll attributes the newest run within 2 s of launch, like macOS. Noise treatment improves only the local SAPI analysis/control lane. Original noisy PCM still goes to handoff. BODY CER is a local SAPI hypothesis metric, not external returned text or delivered-transcription accuracy.

Target exclusion, the recorder mic-use guard, device selection and recovery, dictation reload, and login registration now exist on Windows. The remaining declared differences are OS-specific diagnostics, the default stop command, and command-mode stop gating.

Actual external validation remains HOLD. Future validation must treat clipboard, selected text, active-application context, focus movement, and automatic paste as risk conditions to verify directly. An empty owned text control alone does not isolate clipboard/context or prove the paste destination. Keyboard hooks do not confine all input paths. A new desktop does not isolate the window-station clipboard or prove singleton routing.
