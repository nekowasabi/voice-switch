# voice-switch

On macOS, a menu-bar app. A wake word on its own runs a command through `/bin/sh`, or sends a configured macOS URL action without a shell. Speech after a wake word is recorded and opened in superwhisper only when `dictation` is set. Without it, that longer utterance is ignored. When dictation returns a string, one tmux pane receives it only if the pane catalog, optionally checked by Jev, points at exactly one pane. Otherwise nothing is sent.

[日本語](README_ja.md)

The bundle identifier is `local.voice-switch`. `Info.plist` sets `LSMinimumSystemVersion` to 26.0. A successful Mac build is not claimed here.

## Platforms

| | macOS | Windows |
|---|---|---|
| Menu / tray UI | Menu-bar app | Console resident and WinForms tray host |
| Dictation HUD, finish and cancel keys | HUD and event tap | Top-center HUD and `WH_KEYBOARD_LL` hook, hand test pending |
| Wake-word recognition | Apple SpeechTranscriber over segmented utterances | Windows SAPI constrained grammar for configured words |
| Superwhisper hook | `open -g superwhisper://record` | `rundll32 url.dll,FileProtocolHandler superwhisper://record` |
| Build | `make` | `PC=wsl make` |

See [BUILD.md](./BUILD.md) for `$PC` details and verification.

## Install (macOS)

`make install` builds a release binary, wraps it as `VoiceSwitch.app`, ad-hoc signs it as `local.voice-switch`, copies the app to `~/Applications`, copies `config.example.json` to `~/.config/voice-switch/config.json` when that file is missing, and opens the app.

`make uninstall` quits the app and removes that copy. `make logs` follows `~/Library/Logs/voice-switch.log`.

## Permissions

The app asks for the microphone and for on-device speech recognition. While a dictation is recording it also installs a keyboard tap for superwhisper's record and cancel shortcuts, which needs Accessibility. Until that is granted, the log says `hotkey: Accessibility not granted yet`.

## Config

`$VOICE_SWITCH_CONFIG`, or `~/.config/voice-switch/config.json` when that variable is unset. The file is re-read between utterances when its modification time changes.

`command` and `stopCommand` are run with `/bin/sh -c`. The sample file is `config.example.json`.

For direct macOS URL actions, set `macOS.wakeURL` / `macOS.stopURL` (for example `superwhisper://record/start` and `superwhisper://record/stop`). Each overrides its corresponding command; keep `command` for compatibility. Wake actions require command mode (`dictation` absent); `--fire` tests the wake action directly. See the [complete URL example](config.example.macos-url.json) and [settings, errors, and Mac verification steps](docs/macos-url-actions.md). Windows .NET continues to use its existing commands.

A wake word has to be the whole utterance. The sample words are `音声入力`, `音声入る`, `おんせい`, `音声に入るよ`, `音声に入る`, and `音声によって`, with locale `ja_JP`.

If `dictation` is absent, a lone wake word runs `command`. In the sample, that command is `open -g superwhisper://record`.

If `dictation` is present:

- A lone wake word starts a dictation and waits for speech. The sample wait is `startTimeoutMs` 3000. Silence before any speech cancels it.
- An utterance that starts with a wake word and continues is recorded until about `endSilenceMs` of silence (sample 1200), a stop word, superwhisper's record shortcut, or `dictation.maxSeconds` (sample 60). The wake-word audio is cut.
- The wav is opened in superwhisper. The app polls `dictation.recordingsDir` (sample `~/Documents/superwhisper/recordings`) for a new `meta.json`, reads `llmResult` or else `result`, and logs the length. That string then goes through the pane routing below. The code activates the app that was frontmost when the wake word was heard, while superwhisper is frontmost, so superwhisper does not skip its paste.

`stopWords` said on their own end an in-progress dictation. If an app in `skipWhileMicInUseBy` is using the microphone instead, the same word runs `stopCommand`. The sample word is `入力ストップ`. The sample `stopCommand` is the same superwhisper record URL, which toggles.

A wake word is ignored while an app in `skipWhileMicInUseBy` has the microphone open. Dictation is skipped when the frontmost bundle id is in `dictation.excludeBundleIDs`.

Optional `dictation.superwhisperMode` (Mac + Windows; key or display name) selects a Superwhisper mode that turns auto-paste off for voice-switch dictations. Unset keeps the old behavior (Superwhisper may auto-paste). When set, voice-switch switches to that mode before handoff and restores the previous mode after; if the pane route did not send, voice-switch pastes once into the wake-time app (SendFailed pastes the same resolved body send-keys tried, quoted or body-Jev, never the full dictation wrapper). A pane that matched but had no extractable send body does not paste the full dictation. Mac mode switch / paste are wired in source; live Mac runtime is unverified on this box (no swiftc / Apple Speech).

The top-level `maxSeconds` (sample 2.5) caps a short wake-word utterance. It is not the dictation cap.

Optional `earlyWakeMs` (Mac + Windows dictation mode; try 300) recognizes the utterance while it is still open: at 60 ms and 150 ms of silence, and every `earlyWakeMs` of speech. A wake word then fires without waiting for `hangoverMs`. A wake word that also starts a longer one (`音声` beside `音声入力`) fires only after 150 ms of silence, and starts a one-breath dictation only when a 150 ms pause follows it, so `音声認識…` is not a wake. Absent keeps the old timing. Synthetic-speech results are in `.claude/hillclimb/wake-latency/`; a live hand test is still pending on both platforms.

The menu items are 一時停止 (releases the microphone), マイク, 設定ファイルを開く, ログを開く, ログイン時に起動, and 終了.

## Pane routing (tmux and Jev)

The dictation string is matched to a closed catalog from `tmux list-panes -a -F '#{pane_id}\t#{window_name}\t#{pane_title}\t#{pane_current_command}'`. On macOS the command runs through `/usr/bin/env`. On Windows it runs as `wsl.exe -e tmux ...`, so the target is tmux inside WSL. The app does not start a tmux server. If tmux fails or there is no server, nothing is sent and the failure is logged. A pane hits when any non-empty label is a case-insensitive substring of the text. Labels are the pane title, the window name, and the current command. On Linux, an allowlisted agent name found for that pane through `/proc` (`claude`, `aider`, `gemini`, `copilot`, `codex`, `devin`, `hermes`, `opencode`, `pi`, `grok`, `cursor-agent`) is a label too. Mac and Windows have no `/proc` walk, so there only title, window, and command match.

If `TYPESAFE_API_KEY` (or else `JEV_API_KEY`) is set in the app's environment, the dictation text and the pane labels (id, window, title, command, agent names) are sent to TypeSafe at `https://api.typesafe.ai/v1/systemone` (model `jev-latest`) as one choice question: which listed pane does the speaker address, or none. A Finder-launched app and the Windows tray do not see shell exports, so the variable has to be set where the app starts. The answer counts only at confidence 0.8 or higher, and only for panes already in the catalog:

- One label hit: it is sent, unless Jev confidently names a different pane or none. Then nothing is sent (`jev rejected`).
- Two or more hits: the pane Jev names is sent if it is one of the hits (`jev narrowed`). Otherwise nothing is sent.
- No hit: nothing is sent. A confident pick is logged as a suggestion only.

Without a key, after the 5 s timeout, on an HTTP error, or on an answer that is not a catalog choice, the rule is the one without Jev: send iff exactly one label hit. Jev never adds a pane the catalog does not have. The text goes out as `tmux send-keys -t <pane-id> -l -- <text>`, as argv and never through a shell, and only to an id of the form `%N`. Each dictation writes one log line like `tmux: hits=2 jev=%2@0.87 -> send %2 (jev narrowed)`. `jev=off` means no key, `jev=error` means the request or the answer failed. The key and the dictation text are never logged. The next handoff waits while the route runs.

The 0.8 floor comes from `scripts/pane_jev_probe.py`, 8 utterances against a fixed catalog: the two wrong answers had confidence 0.49 and 0.52, the right ones 0.80 to 1.00. Rerun it and read the `tmux:` log lines to recalibrate. `scripts/pane_route_proof.py` proves the rule without Jev against a private tmux server on Linux. The policy table, the label match, and the answer parser are shared between Swift and C# through `tests/parity/fixtures/pane_route.json`.

Not hand-tested: the Swift side was not compiled here, so the Mac build and a live Mac dictation are not claimed. On Windows, `wsl.exe` reaching tmux, Japanese text surviving the `wsl.exe` round trip in both directions, and a live dictation landing in a WSL pane were not run. `make win-test` covers the C# logic on WSL; the proof script covers the rule without Jev.

## macrowhisper integration

[macrowhisper](https://github.com/ognistik/macrowhisper) watches Superwhisper recordings and runs insert/URL/Shortcut/shell/AppleScript actions. voice-switch does **not** reimplement that; it calls the same public CLI the [Alfred workflow](https://github.com/ognistik/macrowhisper/tree/main/alfred) uses before starting Superwhisper.

Documented CLI used here ([cli-reference](https://github.com/ognistik/macrowhisper/blob/main/docs/cli-reference.md)):

| Config field | CLI |
| --- | --- |
| `scheduleAction` | `macrowhisper --schedule-action <name>` (one-shot for the next / active recording) |
| `autoReturn` | `macrowhisper --auto-return true` (one-shot Return; mutually exclusive with schedule) |
| `activeAction` | `macrowhisper --action <name>` (persistent fallback) |
| `modeKey` | `open -g superwhisper://mode?key=…` |
| `bin` | Absolute path or name on `PATH` (default `macrowhisper`) |
| `onDictationHandoff` | Also prepare before one-breath WAV handoff (default `true` when any hook is set) |

Priority when more than one is set: `scheduleAction` > `autoReturn` > `activeAction` (macrowhisper treats schedule and auto-return as mutually exclusive).

Example: `config.example.macrowhisper.json`

```json
"macrowhisper": {
  "bin": "macrowhisper",
  "scheduleAction": "autoPaste",
  "onDictationHandoff": true
}
```

Requirements on the Mac:

1. Install macrowhisper (`brew install ognistik/formulae/macrowhisper`) and start the service (`macrowhisper --start-service`).
2. Follow macrowhisper’s Superwhisper text-input checklist (paste off, etc.).
3. Ensure `scheduleAction` / `activeAction` names exist in `~/.config/macrowhisper/macrowhisper.json` (`macrowhisper --list-actions`).

Wake-word path: prepare CLI hooks → run `command` (record toggle).  
One-breath dictation: optional prepare → open WAV in Superwhisper (macrowhisper’s watcher handles the result when configured).

## CLI

- `voice-switch --check a.wav` feeds files through the VAD and transcriber and prints a verdict per utterance. Windows has the same mode with SAPI; it reads PCM16 mono 16 kHz WAV only.
- `voice-switch --simulate a.wav` feeds one file through the live path instead of the microphone.

## Not on main

[PR #3](https://github.com/nekowasabi/voice-switch/pull/3) is open and not merged. This section is not current `main` behavior.

On that branch, a non-empty `llmResult` (otherwise `result`) is passed as argv, not through a shell:

```
/usr/bin/env computer-use-jev -goal <text>
```

Whitespace-only text does not start it. The app does not wait for it to exit. A non-zero status is logged. The branch does not pass `-key`. `TYPESAFE_API_KEY` is not written in source or in config. If the CLI reads a key, it reads that environment variable. This repo does not contain a key value.

A successful Mac build of that branch is not claimed here. It is also not claimed that superwhisper stops pasting after the focus change on that branch.

## Quick start (Windows / WSL)

```sh
export PC=wsl
make win-verify   # no SDK required
make              # build, test, publish to ./release
```

For a Windows-local validation folder from WSL, pass the destination explicitly:

```sh
RELEASE_DIR=/mnt/c/temp/voice-switch-validation make win-publish
cp config.example.windows.json /mnt/c/temp/voice-switch-validation/config.dictation.json
```

`win-publish` creates `config.json` only when it is missing, from the dictation-mode sample. An existing `config.json` is never overwritten.

Windows diagnostics that do not require external app launch:

```text
voice-switch.exe --self-test
voice-switch.exe --recognizers
voice-switch.exe --check-device
voice-switch.exe --check a.wav b.wav
voice-switch.exe --config config.dictation.json --input-wav fixture.wav
voice-switch.exe --config config.dictation.json --input-wav fixture.wav --output-dir out --input-wav-fast
```

`--check-device`, `--dry-run --listen-seconds`, and resident runs can open the live microphone. They are not part of the synthetic validation path below.

Windows SAPI root cause fixed: PowerShell 5 was treating the configured phrase JSON array as one object, which built one concatenated grammar phrase. Native verification recognized `音声入力` through the compiled production script and matched `run-command` / `wake`; the simulated old parsing bug produced no recognized phrase before timeout. The restart-key, diagnostic-drain, double-dispose, and `ExpandPath` review findings are separate follow-ups.

Windows dictation is selected by the `dictation` block, which `config.example.windows.json` includes. The synthetic WAV path is validated without a microphone. The live microphone capture and Superwhisper file-intake handoff remain user-validation-held. When the handoff path is used: it writes `<id>.wav` under `%LOCALAPPDATA%\voice-switch\dictation-handoffs` and launches `Superwhisper.exe superwhisper://file//<path>`. Like macOS it then polls `dictation.recordingsDir` (default `%LOCALAPPDATA%\com.superwhisper.app\recordings`) for up to 30 s for a run whose `meta.json` has `llmResult` or `result`, logs the length, and deletes the WAV. With no result it logs the path and keeps the WAV. Files in that directory older than 10 minutes are deleted when the handoff is created. One handoff runs at a time: a dictation that ends while one is in flight is dropped with `dictation dropped: previous one still in flight`, and the runtime keeps listening. Superwhisper only takes plain ASCII paths, so with a non-ASCII or spaced profile name the WAV folder falls back to its 8.3 short name, then to a per-user folder under `%ProgramData%\voice-switch` that only that user can open. While a dictation is open, the tray also borrows Superwhisper's finish and cancel keys through a low-level keyboard hook. It reads `toggleRecordingShortcut` and `cancelRecordingShortcut` from `%LOCALAPPDATA%\com.superwhisper.app\preferences.json` and falls back to Ctrl+Space and Esc. A top-center HUD shows 🎙 どうぞ, ● 録音中, and ■ 録音終了 (1.5 s, only after a stop word or the finish key). The phase also goes to the log as `dictation phase: X`. The tray menu item 効果音 toggles a sound on a lone wake (registry `HKCU\Software\voice-switch` `ConfirmationSound`, default off) and the VAD ignores audio for 600 ms after it. After handoff, for 2 s and only while Superwhisper is in front, the window that was in front at wake is brought back. Hand test on Windows is still needed for: live microphone wake, Ctrl+Space finish and Esc cancel, focus restore, the 効果音 sound, and real Superwhisper intake and paste. Those are covered only by compile and tests with fakes. Known deviations: keys typed into an elevated (admin) window bypass the hook (UIPI), the WAV is kept on `NoResult` where macOS deletes it, and Alt or Win chords are not specially handled.

HUD は前面アプリの画面上中央に表示します。前面ウィンドウを取得できない場合はカーソルの画面に表示します。Windows の2画面で配置・フォーカス維持・非表示を検証済みです。検証には `dotnet run --project tests/windows/HUDScreenProbe -c Release` を使います。Windows と2画面が必要です。

Synthetic Windows dictation uses `--input-wav PATH` with strict PCM16 mono 16 kHz WAV input and no microphone fallback. The default pace is 480 samples every 30 ms; add `--input-wav-fast` for structural tests. Without `--output-dir`, synthetic input is a dry-run and never launches an external app. With `--output-dir PATH`, voice-switch records body WAV files and JSON source range/hash metadata locally through the production encoder; this is a record-only validation adapter, not a Superwhisper integration. DSP, when enabled, affects only the local SAPI analysis/control lane. The original PCM, including its noise, is still used for emitted body WAV and handoff.

Stop-boundary validation passed 20/20 expanded clean strict standalone-stop checks, 20/20 body-tail checks, and 2/2 independent full-body checks. The known low-onset limit remains: 16/20 very-low stop-onset cases retain 0.375-15 ms of source audio so quiet body audio is not over-trimmed. The default endpoint stays lexical and does not move to `Source.Start`.

`voice-switch.exe` is one app. Launched with no arguments, or with only `--config PATH`, it starts as a tray app and listens right away in command mode or dictation mode, whichever the config selects. `--paused` starts the tray without opening the microphone. Diagnostic flags such as `--self-test` run in the terminal instead. The tray supports record-only synthetic `--input-wav PATH --record-only DIR` and prevents duplicate tray instances for the same Windows user plus canonical config path. The tray and a terminal `--listen-seconds` run own their capture lifecycles separately, so do not run both against the same capture at the same time. Logs go to `%LOCALAPPDATA%\voice-switch\voice-switch.log`.

The tray follows the macOS menu bar app. Its icon is a microphone while listening and a struck-through one otherwise. The menu is 状態, 再開, 一時停止, マイク (the dictation input device, saved by name; the run restarts on a change, an unplug, or a new Windows default), 設定ファイルを開く, 設定を再読み込み, ログを開く, 効果音, ログイン時に起動 (`HKCU\...\CurrentVersion\Run`), 直近のエラー, and 終了. Dictation mode reloads the config file between utterances like macOS. A wake word is ignored while a process named in `skipWhileMicInUseBy` (sample `Superwhisper`) has an active capture session, and dictation is skipped while the window in front belongs to an executable in `dictation.excludeProcessNames`. A wake word that SAPI hears as a near-homophone (音声 as 温泉 or 温水) still opens the wait, because kana readings are compared as well as text; `BUILD.md` has the rule and the log lines. Every PowerShell recognizer child dies with `voice-switch.exe`.

Generate synthetic fixtures using the checked-in generator, then run record-only dictation validation:

```powershell
pwsh -File tests\windows\compose-fixtures.ps1 -OutputDir C:\temp\voice-switch-fixtures
dotnet run --project tests/windows/SyntheticDictationRuntimeHarness/SyntheticDictationRuntimeHarness.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --wav C:\temp\voice-switch-fixtures\wake-body-separate-stop-clean.wav --output-dir C:\temp\voice-switch-runtime-out --evidence C:\temp\voice-switch-runtime-out\evidence.json
```

Native tray validation after an isolated publish requires PowerShell 7+:

```powershell
pwsh -File tests\windows\run-tray-host.ps1 -ReleaseDir C:\temp\voice-switch-validation -OutputRoot C:\temp\voice-switch-tray-runs
```

Manual tray smoke commands use the same record-only path. Start the host in one PowerShell window:

```powershell
$build='C:\temp\voice-switch-validation'
$fixtures='C:\temp\voice-switch-fixtures'
$out='C:\temp\voice-switch-tray-record-only'
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --paused --input-wav "$fixtures\consecutive-sessions-clean.wav" --record-only $out
```

Then run IPC commands in another PowerShell window. `--paused` keeps the host Paused, then `start`, `pause`, `start`, and `quit` exercise Start, Pause, Resume, and Quit without external app handoff:

```powershell
$build='C:\temp\voice-switch-validation'
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command status
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command start
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command pause
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command start
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command quit
```

Windows command mode dispatches configured custom stop commands only when `stopCommand` is an operator-supplied idempotent stop command. Missing `stopCommand` and the older Superwhisper record toggle sample are suppressed. That is distinct from the dictation runtime, which has session-gated stop handling. See [Windows feature parity audit](./docs/windows-feature-parity.md), [Windows DSP evaluation method](./docs/windows-dsp-evaluation-method.md), and [Windows DSP evaluation results 2026-10-03](./docs/windows-dsp-evaluation-results-20261003.md) for the current limits.

Do not run CLI and tray concurrent capture. The default sample now runs live microphone capture and Superwhisper file intake, but neither has been confirmed by a hand test on Windows yet. The external app path may involve clipboard, selected text, active-application context, and focus behavior, so future validation must check those boundaries directly. A `Transcribed` handoff means Superwhisper wrote a result, not that the paste landed. A `NoResult` WAV is kept for manual recovery until the 10 minute sweep.
