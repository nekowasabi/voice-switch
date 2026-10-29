---
name: verify-voice-switch
description: Drive the real Windows voice-switch.exe from WSL with synthetic speech WAVs and capture logs plus recorded handoff WAVs as proof. Use when a change to the Windows dictation runtime (wake word, stop word, silence end, automatic no-wake sessions, handoff) must be shown working in the built app, not just in unit tests.
---

# verify-voice-switch

The primary surface is the Windows tray/CLI app `voice-switch.exe` (`dotnet/VoiceSwitch.Windows`). It listens to the microphone, recognizes a wake word with Windows SAPI, records the body, and hands the WAV to Superwhisper. For verification the same exe reads a WAV instead of the microphone (`--input-wav`) and writes each handoff WAV to a folder instead of Superwhisper (`--output-dir`). So the real runtime, recognizer, and session logic run, while Superwhisper, paste, and the user's mode are never touched.

Other surfaces exist and are out of scope here. The Mac app (`Sources/voice-switch`, needs Darwin). The tray IPC harness `tests/windows/run-tray-host.ps1` (needs PowerShell 7, which this machine lacks). The pure tests `dotnet/VoiceSwitch.Windows.Tests` (Windows-only runner).

Everything runs from WSL. `verify.sh` in this folder wraps every step; run it by absolute path.

```sh
V=/home/takets/repos/voice-switch/.claude/skills/verify-voice-switch/verify.sh
```

## Launch

```sh
$V build      # dotnet publish of the working tree to /mnt/c/temp/voice-switch-verify/build (about 30 s)
$V fixtures   # once: synthesizes speech WAVs with Windows System.Speech into .../fixtures (cached afterwards)
              # also writes auto-hiss/auto-speech/auto-stop and auto.json (make-auto-fixtures.py) for features/automatic-gates.md
```

There is no long-lived server. Each `$V wav ...` starts one exe, feeds the WAV in real time, and the exe exits by itself at the end of the WAV. Ready means `$V doctor` passes.

The build always goes to its own folder with a copy of `config.example.windows.json` as `config.json`. Never use `make win-publish` for verification. It runs `taskkill /IM voice-switch.exe /F`, which kills the user's running tray app, and then launches a tray that opens the live microphone.

Override the root with `VS_VERIFY_ROOT=/mnt/c/...` (it must be on a Windows drive, because the exe writes there).

## Doctor

```sh
$V doctor
```

It checks that the exe and its config exist, that the Windows Desktop runtime 8.x is installed, that the fixtures exist, and that `voice-switch.exe --self-test` exits 0. It prints the commit the build came from and the dirty-file count next to the repo's current HEAD. If they differ from what you are proving, rebuild.

## Drive

```sh
$V wav <fixture> [config.json]
```

- `<fixture>` is a file name in the fixtures folder without `.wav`. `*-clean` has silent gaps and `*-silence-noise2` adds noise to the gaps.
- `config.json` defaults to the build's copy of `config.example.windows.json` (wake words include `音声入力`, stop word `入力ストップ`, every `noWakeWord` switch off). To prove a config-dependent feature, write a variant into your scratchpad and pass it. The variant is copied into the evidence folder.
- Set `VS_RUN_ID=<name>` to group several runs under one evidence folder.

The command prints the exit code, the number of handoff WAVs, the evidence folder, and the session lines from the log. The fixtures and the expected session lines for each feature are in [features/README.md](features/README.md).

`run-wav.ps1` is the Windows side of `wav`. It starts the exe with `--config --input-wav --output-dir`, writes its PID, waits up to 120 s, and kills only that PID on timeout.

## Live microphone probe

`$V aec <name> <seconds> [play.wav]` records the live microphone raw and through the OS echo canceller, plus the speaker loopback, and turns the three into fixtures for `$V wav`. It pauses the user's installed tray while it records and starts it again afterwards. Details in [features/playback-silence.md](features/playback-silence.md).

## Transcription recall KPI

Capture is only half the path. `$V phrases` builds a synthetic multi-phrase corpus, and `$V recall [cases.json] [padMs,...]` sends each WAV through the real Superwhisper and counts expected phrases missing from `rawResult`. Exit 0 means zero missing. It drives the user's Superwhisper (mode switch and file intake), so say so before running it. On a Mac, run `superwhisper_recall_mac.py` in this folder instead (Python 3 standard library, `say`, `open`). Details in [features/transcription-recall.md](features/transcription-recall.md).

## Evidence

Each run leaves `/mnt/c/temp/voice-switch-verify/evidence/<run>/<fixture>/`.

| File | Contents |
|---|---|
| `config.json` | the exact config the run used |
| `stdout.txt` | the full runtime log, including `dictation session:`, `dictation recognition: complete`, and `dictation handoff:` lines |
| `handoffs/*.wav` + `*.json` | what the app would have sent to Superwhisper |
| `exitcode.txt`, `pid.txt`, `stderr.txt` | process outcome |

Proof standards:

- Drive the real exe through `--input-wav`. A unit test or a direct `DictationSession` call is not proof of app behavior.
- Prove the transition, not only the end state. Quote the session lines in order (for example `automatic ... -> automatic replaced by wake word -> body-start -> handoff`).
- Check the side effect. Count the handoff WAVs, and check their `range=` against the log.
- `--output-dir` is a real production boundary (`LocalRecordingHandoff`). Superwhisper, mode switching, paste, and local history are not exercised, so this skill cannot prove them. Say so instead of implying it.
- The exe also appends every line to the user's shared log `%LOCALAPPDATA%\voice-switch\voice-switch.log`. That is a known side effect. Read evidence from `stdout.txt`, never from the shared log.

## Cleanup

```sh
$V cleanup
```

It kills a recorded PID only when its run never wrote `exitcode.txt` (the run was interrupted) and the PID is still a `voice-switch` process. A finished run's PID may since have been reused by the user's own tray, so it is never touched. Then it deletes the build folder. Evidence and the fixture cache are kept. Delete `/mnt/c/temp/voice-switch-verify/evidence/<run>` yourself when it is no longer needed.

## Isolation

Runs do not share state with each other or with the user's tray app. Each run has its own config path and output folder, and `--input-wav` never opens the microphone. Two `wav` runs can go in parallel. The only shared things are the log file above and the SAPI recognizer engine.
