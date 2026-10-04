# voice-switch

On macOS, a menu-bar app. A wake word on its own runs a command through `/bin/sh`. Speech after a wake word is recorded and opened in superwhisper only when `dictation` is set. Without it, that longer utterance is ignored. On `main`, the transcribed text is not passed to another program.

[日本語](README_ja.md)

The bundle identifier is `local.voice-switch`. `Info.plist` sets `LSMinimumSystemVersion` to 26.0. A successful Mac build is not claimed here.

## Platforms

| | macOS | Windows |
|---|---|---|
| Menu / tray UI | Menu-bar app | Console resident and WinForms tray host |
| Wake-word recognition | Apple SpeechTranscriber over segmented utterances | Windows SAPI constrained grammar for configured words |
| Superwhisper hook | `open -g superwhisper://record` | `cmd /c start "" superwhisper://record` |
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

A wake word has to be the whole utterance. The sample words are `音声入力`, `音声入る`, `おんせい`, `音声に入るよ`, `音声に入る`, and `音声によって`, with locale `ja_JP`.

If `dictation` is absent, a lone wake word runs `command`. In the sample, that command is `open -g superwhisper://record`.

If `dictation` is present:

- A lone wake word starts a dictation and waits for speech. The sample wait is `startTimeoutMs` 3000. Silence before any speech cancels it.
- An utterance that starts with a wake word and continues is recorded until about `endSilenceMs` of silence (sample 1200), a stop word, superwhisper's record shortcut, or `dictation.maxSeconds` (sample 60). The wake-word audio is cut.
- The wav is opened in superwhisper. The app polls `dictation.recordingsDir` (sample `~/Documents/superwhisper/recordings`) for a new `meta.json`, reads `llmResult` or else `result`, and logs the length. On `main` that string is not passed on. The code activates the app that was frontmost when the wake word was heard, while superwhisper is frontmost, so superwhisper does not skip its paste.

`stopWords` said on their own end an in-progress dictation. If an app in `skipWhileMicInUseBy` is using the microphone instead, the same word runs `stopCommand`. The sample word is `入力ストップ`. The sample `stopCommand` is the same superwhisper record URL, which toggles.

A wake word is ignored while an app in `skipWhileMicInUseBy` has the microphone open. Dictation is skipped when the frontmost bundle id is in `dictation.excludeBundleIDs`.

The top-level `maxSeconds` (sample 2.5) caps a short wake-word utterance. It is not the dictation cap.

The menu items are 一時停止 (releases the microphone), マイク, 設定ファイルを開く, ログを開く, ログイン時に起動, and 終了.

## CLI

- `voice-switch --check a.wav` feeds files through the VAD and transcriber and prints a verdict per utterance.
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
make              # build, test, publish to ./artifacts/windows
```

For a Windows-local validation folder from WSL, pass the destination explicitly:

```sh
RELEASE_DIR=/mnt/c/temp/voice-switch-validation make win-publish
cp config.example.windows.json /mnt/c/temp/voice-switch-validation/config.command.json
cp config.example.windows-dictation.json /mnt/c/temp/voice-switch-validation/config.dictation.json
```

`win-publish` creates `config.json` only when it is missing. That default file is command mode. Use `config.dictation.json` for dictation probes and record-only runs so the command-mode sample is not mistaken for the dictation runtime.

Windows diagnostics that do not require external app launch:

```text
voice-switch.exe --self-test
voice-switch.exe --recognizers
voice-switch.exe --check-device
voice-switch.exe --config config.dictation.json --input-wav fixture.wav
voice-switch.exe --config config.dictation.json --input-wav fixture.wav --output-dir out --input-wav-fast
voice-switch.exe --complete-handoff <id>
```

`--check-device`, `--dry-run --listen-seconds`, and resident runs can open the live microphone. They are not part of the synthetic validation path below.

Windows SAPI root cause fixed: PowerShell 5 was treating the configured phrase JSON array as one object, which built one concatenated grammar phrase. Native verification recognized `音声入力` through the compiled production script and matched `run-command` / `wake`; the simulated old parsing bug produced no recognized phrase before timeout. The restart-key, diagnostic-drain, double-dispose, and `ExpandPath` review findings are separate follow-ups.

Windows dictation is selected by adding the `dictation` block from `config.example.windows-dictation.json`. The synthetic WAV path is validated without a microphone. The live microphone capture and Superwhisper file-intake handoff remain user-validation-held. When the handoff path is used, a launched body is `SubmittedUnconfirmed`; a body that cannot be safely launched is retained as `DeferredUnsent`. Startup and `--help` show pending manifest paths and age. Use `--complete-handoff <id>` after confirming Superwhisper has finished or cancelled, or after manually copying or discarding an UNSENT WAV, so voice-switch can clean its owned source file. Automatic external transcription, paste, target restoration, completion, and no-touch consecutive external dictation are not yet proven.

Synthetic Windows dictation uses `--input-wav PATH` with strict PCM16 mono 16 kHz WAV input and no microphone fallback. The default pace is 480 samples every 30 ms; add `--input-wav-fast` for structural tests. Without `--output-dir`, synthetic input is a dry-run and never launches an external app. With `--output-dir PATH`, voice-switch records body WAV files and JSON source range/hash metadata locally through the production encoder; this is a record-only validation adapter, not a Superwhisper integration. DSP, when enabled, affects only the local SAPI analysis/control lane. The original PCM, including its noise, is still used for emitted body WAV and handoff.

Stop-boundary validation passed 20/20 expanded clean strict standalone-stop checks, 20/20 body-tail checks, and 2/2 independent full-body checks. The known low-onset limit remains: 16/20 very-low stop-onset cases retain 0.375-15 ms of source audio so quiet body audio is not over-trimmed. The default endpoint stays lexical and does not move to `Source.Start`.

The Windows tray host is a separate `voice-switch-tray.exe`. It starts Paused, opens no microphone until Start/Resume, supports record-only synthetic `--input-wav PATH --record-only DIR`, and prevents duplicate tray instances for the same Windows user plus canonical config path. The tray and CLI resident modes own their capture lifecycles separately, so do not run both against the same capture at the same time. It does not install login/autostart registration.

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
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --input-wav "$fixtures\consecutive-sessions-clean.wav" --record-only $out
```

Then run IPC commands in another PowerShell window. The host starts Paused, then `start`, `pause`, `start`, and `quit` exercise Start, Pause, Resume, and Quit without external app handoff:

```powershell
$build='C:\temp\voice-switch-validation'
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command status
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command start
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command pause
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command start
& "$build\voice-switch-tray.exe" --config "$build\config.dictation.json" --tray-command quit
```

Legacy Windows command mode dispatches configured custom stop commands only when `stopCommand` is an operator-supplied idempotent stop command. Missing `stopCommand` and the older Superwhisper record toggle sample are suppressed. That is distinct from the dictation runtime, which has session-gated stop handling. See [Windows feature parity audit](./docs/windows-feature-parity.md), [Windows DSP evaluation method](./docs/windows-dsp-evaluation-method.md), and [Windows DSP evaluation results 2026-10-03](./docs/windows-dsp-evaluation-results-20261003.md) for the current limits.

Do not run CLI and tray concurrent capture. Live microphone capture and external app intake/paste remain on HOLD until narrowly authorized safe boundary verification. The external app path may involve clipboard, selected text, active-application context, and focus behavior, so future validation must check those boundaries directly. Pending handoff completion remains manual: `SubmittedUnconfirmed` is not proof of paste, `DeferredUnsent` is not externally consumed, a 24-hour overdue warning is shown, and there is no automatic timed delete or retry.
