# Build notes

## `$PC` switch

| Environment | Default `make` | What you get |
|---|---|---|
| unset / anything except `wsl` | `make app` | macOS `VoiceSwitch.app` |
| `PC=wsl` or `PC=WSL` | `make win` | Windows `.NET` build, tests, and publish |

Case-insensitive `PC=wsl` and `PC=WSL` both select Windows. The Windows default release directory is `$(CURDIR)/artifacts/windows`. Pass `RELEASE_DIR` when you need a Windows-local folder from WSL.

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
cp config.example.windows.json /mnt/c/temp/voice-switch-validation/config.command.json
cp config.example.windows-dictation.json /mnt/c/temp/voice-switch-validation/config.dictation.json
```

`win-publish` creates `config.json` only if it is missing. That default file is the command-mode sample. Use `config.dictation.json` for dictation probes and record-only validation. Do not use the command-mode sample as evidence for dictation behavior.

## Windows configuration

`config.example.windows.json` keeps command mode and uses the `superwhisper://record` deep link through `cmd /c start`. Optional Windows dictation is configured separately with `config.example.windows-dictation.json`. The command-mode sample omits the unsupported process microphone-use guard.

`config.example.windows-dictation.json` adds the `dictation` block. With that block present, Windows selects the PCM dictation runtime and does not fall back to the command toggle on dictation errors.

The app first uses `config.json` next to `voice-switch.exe`. If that file is absent, it falls back to `%USERPROFILE%\.config\voice-switch\config.json` or `VOICE_SWITCH_CONFIG`.

Optional noise reduction defaults to `Off`. `ConservativeWiener` affects only the local SAPI analysis/control lane. Original PCM remains the emitted body WAV and handoff source. Current native noise evidence does not justify changing the default.

## CLI diagnostics

```text
voice-switch.exe --self-test
voice-switch.exe --recognizers
voice-switch.exe --check-device
voice-switch.exe --fire
voice-switch.exe --complete-handoff ID
voice-switch.exe --config config.dictation.json --input-wav PATH
voice-switch.exe --config config.dictation.json --input-wav PATH --input-wav-fast --output-dir PATH
voice-switch-tray.exe --config PATH
```

`--self-test`, `--input-wav`, and record-only `--output-dir` runs do not require a live microphone. `--check-device`, `--dry-run --listen-seconds`, bounded resident runs, and the tray after Start/Resume can open the live microphone. Treat those as separate native checks, not synthetic validation.

## Synthetic fixtures and harnesses

Generate synthetic fixtures using the checked-in generator on Windows:

```powershell
pwsh -File tests\windows\compose-fixtures.ps1 -OutputDir C:\temp\voice-switch-fixtures
```

Run the production-script synthetic command probe with the command config:

```powershell
dotnet run --project tests/windows/ProductionScriptSyntheticProbe/ProductionScriptSyntheticProbe.csproj -c Release -- --config C:\temp\voice-switch-validation\config.command.json --phrase 音声入力
dotnet run --project tests/windows/ProductionScriptSyntheticProbe/ProductionScriptSyntheticProbe.csproj -c Release -- --config C:\temp\voice-switch-validation\config.command.json --phrase 音声入力 --simulate-bug
```

Run native dictation recognizer probes with the dictation config:

```powershell
dotnet run --project tests/windows/NativeDictationRecognizerProbe/NativeDictationRecognizerProbe.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --phrase "音声入力、今日は晴れです" --expect 音声入力
dotnet run --project tests/windows/NativeDictationRecognizerProbe/NativeDictationRecognizerProbe.csproj -c Release -- --config C:\temp\voice-switch-validation\config.dictation.json --phrase "入力ストップ" --expect 入力ストップ
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

Tray validation starts Paused, then drives Start, Pause, Resume, and Quit through IPC. Requires PowerShell 7+:

```powershell
pwsh -File tests\windows\run-tray-host.ps1 -ReleaseDir C:\temp\voice-switch-validation -OutputRoot C:\temp\voice-switch-tray-runs
```

For a small manual record-only tray check, start the host in one PowerShell window and run the IPC commands in another:

```powershell
$build='C:\temp\voice-switch-validation'
$fixtures='C:\temp\voice-switch-fixtures'
$out='C:\temp\voice-switch-tray-record-only'
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --input-wav "$fixtures\consecutive-sessions-clean.wav" --record-only $out
```

```powershell
$build='C:\temp\voice-switch-validation'
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command status
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command start
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command pause
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command start
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command quit
```

## Verified Windows coverage

- Config load and reload, platform command hook, and path expansion such as `%LOCALAPPDATA%`.
- Shared wake-word normalization and matching behavior.
- Package-free behavior tests for wake, stop word, path expansion, config validation, CLI parsing, child recognizer lifecycle, portable VAD fixtures, dictation core, and tray lifecycle.
- Resident wake-word recognition through Windows SAPI with a constrained grammar of configured wake and stop words.
- `--fire` dispatches the configured command. The sample command is `superwhisper://record`; it requires Superwhisper for Windows to be installed if you actually run it.
- `voice-switch-tray.exe` is a WinForms tray host. It starts Paused, supports Status, Start/Resume, Pause, Reload, Settings, Recent error, and Quit, prevents duplicate instances per Windows user plus canonical config path, and does not register login/autostart.

## Limits

- `--recognizers` reports installed recognizers. If `ja-JP` is missing, resident mode exits with a direct diagnostic.
- `--check-device` opens the default speech input once and reports device or recognizer errors without entering an indefinite microphone loop.
- `--input-wav PATH` uses the Windows dictation runtime, segmenter, SAPI recognizer, session trimming, and WAV encoder, but reads strict PCM16 mono 16 kHz WAV instead of WinMM.
- `--input-wav PATH` without `--output-dir` is a dry run and does not launch an external app. With `--output-dir`, it writes record-only body WAV files and JSON source range/hash metadata. It is not an actual Superwhisper integration.
- Expanded clean stop-boundary validation passed 20/20 strict standalone-stop checks, 20/20 body-tail checks, and 2/2 independent full-body checks. The known low-onset limit remains: 16/20 very-low stop-onset cases retain 0.375-15 ms of source audio to avoid deleting possible quiet body audio.
- With `dictation` configured, the intended live path captures mono PCM16 16 kHz audio in the .NET parent, trims wake/stop by absolute SAPI lexical ranges, writes a UUID WAV, and launches the registered Superwhisper file URI once. Synthetic WAV and record-only pieces are validated. Live microphone capture and Superwhisper handoff remain manual-validation items.
- Dictation handoff is manual lifecycle on Windows. `SubmittedUnconfirmed` is not proof that transcription or paste succeeded. The owned WAV and manifest stay under `%LOCALAPPDATA%\voice-switch\dictation-handoffs` until `--complete-handoff ID` runs after the external reader is done or cancelled.
- A second dictation handoff is refused while one owned manifest is pending. Startup and `--help` show pending path, age, and a 24-hour overdue warning.
- Windows PowerShell 5 previously wrapped `ConvertFrom-Json` output as one array object, so SAPI received one concatenated grammar phrase instead of distinct configured phrases. The production parser fix is verified by the synthetic native probe.
- Command mode recognizes only configured wake and stop words. Legacy command-mode stop words dispatch directly without the macOS session/mic-in-use guard. Dictation mode uses finite SAPI recognition over PCM and has separate session-gated stop handling.
- `skipWhileMicInUseBy` is parsed for config compatibility but logged as unsupported on Windows when present.
- The tray host and CLI resident modes own capture lifecycles separately. Do not run both against the same capture at the same time.
- Automatic Superwhisper completion correlation, target focus restoration, global finish/cancel shortcuts, mic-in-use detection, device selection/change recovery, and automatic dictation reload remain unimplemented or user-validation-held.
- WSL interop must work to execute generated Windows `.exe` files from WSL. If it fails, run the same commands from Windows in the published folder.

The Swift Windows files remain source-level compatibility stubs. The functional Windows runtime is the .NET implementation under `dotnet/`.
