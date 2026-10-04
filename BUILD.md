# Build notes

## `$PC` switch

| Environment | Default `make` | What you get |
|---|---|---|
| unset / anything except `wsl` | `make relaunch` | macOS `VoiceSwitch.app`, quit, reinstalled, and reopened |
| `PC=wsl` or `PC=WSL` | `make win` | Windows `.NET` build, tests, and publish |

Case-insensitive `PC=wsl` and `PC=WSL` both select Windows. The Windows default release directory is `$(CURDIR)/release`. Pass `RELEASE_DIR` when you need a Windows-local folder from WSL.

## macOS

Requirements: macOS 26+, Swift 6 toolchain, Xcode.

```sh
make
make install
```

Config example: `config.example.json` with `open -g superwhisper://record`.

Useful flags: `--check`, `--simulate`, `--fire`, `--vad-selftest`.

## Windows from WSL

Requirements: .NET SDK with Windows targeting support. The Makefile assumes a POSIX shell. Override `DOTNET=/path/to/dotnet` if `dotnet` is not on `PATH`.

```sh
export PC=wsl
make win-verify
make win-build
make win-publish
make parity-test
```

For native Windows validation without touching an installed copy, publish to an explicit Windows-local folder:

```sh
RELEASE_DIR=/mnt/c/temp/voice-switch-validation make win-publish
cp config.example.windows.json /mnt/c/temp/voice-switch-validation/config.dictation.json
```

`win-publish` creates `config.json` only if it is missing, from `config.example.windows.json`, which is dictation mode. An existing `config.json` is never overwritten, so an older command-mode copy stays command mode until the `dictation` block is added.

## Windows configuration

`config.example.windows.json` is dictation mode, like the macOS sample. With the `dictation` block present, Windows selects the PCM dictation runtime and does not fall back to the command toggle on dictation errors. Removing the block selects command mode, which runs `command` (the `superwhisper://record` deep link through `rundll32 url.dll,FileProtocolHandler`; `cmd /c start` returns access denied for this URL). The sample omits the unsupported process microphone-use guard.

The app first uses `config.json` next to `voice-switch.exe`. If that file is absent, it falls back to `%USERPROFILE%\.config\voice-switch\config.json` or `VOICE_SWITCH_CONFIG`.

Optional noise reduction defaults to `Off`. `ConservativeWiener` affects only the local SAPI analysis/control lane. Original PCM remains the emitted body WAV and handoff source. Current native noise evidence does not justify changing the default.

## CLI diagnostics

```text
voice-switch.exe --self-test
voice-switch.exe --recognizers
voice-switch.exe --check-device
voice-switch.exe --fire
voice-switch.exe --config config.dictation.json --input-wav PATH
voice-switch.exe --config config.dictation.json --input-wav PATH --input-wav-fast --output-dir PATH
voice-switch.exe --tray-command status|start|pause|reload|quit
```

`--self-test`, `--input-wav`, and record-only `--output-dir` runs do not require a live microphone. `--check-device`, `--dry-run --listen-seconds`, bounded resident runs, and the tray app (which listens on launch unless `--paused`) can open the live microphone. Treat those as separate native checks, not synthetic validation.

## Synthetic fixtures and harnesses

Generate synthetic fixtures using the checked-in generator on Windows:

```powershell
pwsh -File tests\windows\compose-fixtures.ps1 -OutputDir C:\temp\voice-switch-fixtures
```

Run the production-script synthetic command probe with the command config:

```powershell
dotnet run --project tests/windows/ProductionScriptSyntheticProbe/ProductionScriptSyntheticProbe.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --phrase 音声入力
dotnet run --project tests/windows/ProductionScriptSyntheticProbe/ProductionScriptSyntheticProbe.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --phrase 音声入力 --simulate-bug
```

Run native dictation recognizer probes with the dictation config:

```powershell
dotnet run --project tests/windows/NativeDictationRecognizerProbe/NativeDictationRecognizerProbe.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --phrase "音声入力、今日は晴れです" --expect 音声入力 --expect-leading-wake
dotnet run --project tests/windows/NativeDictationRecognizerProbe/NativeDictationRecognizerProbe.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --phrase "入力ストップ" --expect 入力ストップ --expect-standalone-stop
```

Run the record-only dictation runtime harness with the dictation config:

```powershell
dotnet run --project tests/windows/SyntheticDictationRuntimeHarness/SyntheticDictationRuntimeHarness.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --wav C:\temp\voice-switch-fixtures\wake-body-separate-stop-clean.wav --output-dir C:\temp\voice-switch-runtime-out --evidence C:\temp\voice-switch-runtime-out\evidence.json
dotnet run --project tests/windows/SyntheticDictationRuntimeHarness/SyntheticDictationRuntimeHarness.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --wav C:\temp\voice-switch-fixtures\embedded-stop-clean.wav --output-dir C:\temp\voice-switch-runtime-out --evidence C:\temp\voice-switch-runtime-out\embedded-stop-evidence.json
dotnet run --project tests/windows/SyntheticDictationRuntimeHarness/SyntheticDictationRuntimeHarness.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --wav C:\temp\voice-switch-fixtures\natural-one-breath-clean.wav --output-dir C:\temp\voice-switch-runtime-out --evidence C:\temp\voice-switch-runtime-out\natural-one-breath-evidence.json
```

Native Windows noise comparison uses the same published folder and generated fixtures. Requires PowerShell 7+:

```powershell
pwsh -File tests\windows\run-noise-matrix.ps1 -ReleaseDir C:\temp\voice-switch-validation -OutputRoot C:\temp\voice-switch-noise-runs -PerCaseTimeoutSeconds 45 -OverallTimeoutMinutes 90
```

Tray validation launches with `--paused`, then drives Start, Pause, Resume, and Quit through IPC. Requires PowerShell 7+:

```powershell
pwsh -File tests\windows\run-tray-host.ps1 -ReleaseDir C:\temp\voice-switch-validation -OutputRoot C:\temp\voice-switch-tray-runs
```

For a small manual record-only tray check, start the host in one PowerShell window and run the IPC commands in another:

```powershell
$build='C:\temp\voice-switch-validation'
$fixtures='C:\temp\voice-switch-fixtures'
$out='C:\temp\voice-switch-tray-record-only'
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --paused --input-wav "$fixtures\consecutive-sessions-clean.wav" --record-only $out
```

```powershell
$build='C:\temp\voice-switch-validation'
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command status
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command start
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command pause
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command start
& "$build\voice-switch.exe" --config "$build\config.dictation.json" --tray-command quit
```

## Verified Windows coverage

- Config load and reload, platform command hook, and path expansion such as `%LOCALAPPDATA%`.
- Shared wake-word normalization and matching behavior.
- Package-free behavior tests for wake, stop word, path expansion, config validation, CLI parsing, child recognizer lifecycle, portable VAD fixtures, dictation core, and tray lifecycle.
- Resident wake-word recognition through Windows SAPI with a constrained grammar of configured wake and stop words.
- `--fire` dispatches the configured command. The sample command is `superwhisper://record`; it requires Superwhisper for Windows to be installed if you actually run it.
- `voice-switch.exe` with no diagnostic flag is a WinForms tray app. It listens on launch in command or dictation mode (`--paused` opts out), supports Status, Start/Resume, Pause, Reload, Settings, Recent error, and Quit, prevents duplicate instances per Windows user plus canonical config path, and does not register login/autostart.

## Limits

- `--recognizers` reports installed recognizers. If `ja-JP` is missing, resident mode exits with a direct diagnostic.
- `--check-device` opens the default speech input once and reports device or recognizer errors without entering an indefinite microphone loop.
- `--input-wav PATH` uses the Windows dictation runtime, segmenter, SAPI recognizer, session trimming, and WAV encoder, but reads strict PCM16 mono 16 kHz WAV instead of WinMM.
- `--input-wav PATH` without `--output-dir` is a dry run and does not launch an external app. With `--output-dir`, it writes record-only body WAV files and JSON source range/hash metadata. It is not an actual Superwhisper integration.
- Expanded clean stop-boundary validation passed 20/20 strict standalone-stop checks, 20/20 body-tail checks, and 2/2 independent full-body checks. The known low-onset limit remains: 16/20 very-low stop-onset cases retain 0.375-15 ms of source audio to avoid deleting possible quiet body audio.
- With `dictation` configured, the intended live path captures mono PCM16 16 kHz audio in the .NET parent, trims wake/stop by absolute SAPI lexical ranges, writes a UUID WAV, and launches the registered Superwhisper file URI once. Synthetic WAV and record-only pieces are validated. Live microphone capture and Superwhisper handoff remain manual-validation items.
- Dictation handoff follows the macOS poll-then-delete model. It writes `<id>.wav` under `%LOCALAPPDATA%\voice-switch\dictation-handoffs` and launches `Superwhisper.exe superwhisper://file//<path>`. Like macOS it then polls `dictation.recordingsDir` (default `%LOCALAPPDATA%\com.superwhisper.app\recordings`) for up to 30 s for a run whose `meta.json` has `llmResult` or `result`, logs the length, and deletes the WAV. With no result it logs the path and keeps the WAV. Files in that directory older than 10 minutes are deleted when the handoff is created. One handoff runs at a time: a dictation that ends while one is in flight is dropped with `dictation dropped: previous one still in flight`, and the runtime keeps listening.
- `Transcribed` means Superwhisper wrote a result, not that the paste landed. `NoResult` keeps the WAV for manual recovery until the 10 minute sweep. Paths that are not plain ASCII fail before anything is written or launched.
- Windows PowerShell 5 previously wrapped `ConvertFrom-Json` output as one array object, so SAPI received one concatenated grammar phrase instead of distinct configured phrases. The production parser fix is verified by the synthetic native probe.
- Command mode recognizes only configured wake and stop words. Windows dispatches custom `stopCommand` only as an operator-supplied idempotent stop command; missing stop commands and the older Superwhisper record toggle sample are suppressed. Dictation mode uses finite SAPI recognition over PCM and has separate session-gated stop handling.
- `skipWhileMicInUseBy` is parsed for config compatibility but logged as unsupported on Windows when present.
- The tray host and CLI resident modes own capture lifecycles separately. Do not run both against the same capture at the same time.
- Mic-in-use detection and device selection/change recovery remain unimplemented or user-validation-held.
- Dictation mode reloads the config file like macOS: when the VAD closes an utterance while no dictation is open and no recognition is pending, a changed modification time triggers a reload, logged as `config reloaded:` and a fresh `dictation timing:` line. An invalid file logs `config reload failed, keeping previous:`. Wake and stop words, timings, and VAD settings apply from that utterance on; `locale` and `noiseReduction` need the tray Reload or a restart. Start after Pause reads the file again and falls back to the last valid config.
- The tray adds a dictation HUD, a `WH_KEYBOARD_LL` hook for Superwhisper's finish and cancel keys (read from `preferences.json`, fallback Control+Space and Escape, active only while a dictation is open), an optional 効果音 toggle (`HKCU\Software\voice-switch` `ConfirmationSound`, default off), and focus restore to the window in front at wake for 2 s after handoff. These are verified by Release compile and tests with fakes only. A hand test on Windows is still needed for live mic wake, Ctrl+Space and Esc, HUD placement on multiple monitors, focus restore, and real Superwhisper paste.
- Known deviations from macOS: keys typed into an elevated (admin) window bypass the hook (UIPI), `NoResult` keeps the WAV where macOS deletes it, and Alt or Win chords are not specially handled.
- WSL interop must work to execute generated Windows `.exe` files from WSL. If it fails, run the same commands from Windows in the published folder.

The Swift Windows files remain source-level compatibility stubs. The functional Windows runtime is the .NET implementation under `dotnet/`.
