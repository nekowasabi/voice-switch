---
name: verify-voice-switch-mac
description: Drive the real macOS voice-switch binary with synthetic Japanese speech through its --simulate mode and keep the runtime log plus command side effects as proof. Use when a change to the Mac listener (wake word, stop word, normalization, early wake, dictation, automatic no-wake sessions) must be shown working in the built Swift binary, not just in parity checks. The Windows exe has its own skill, verify-voice-switch.
---

# verify-voice-switch-mac

The primary surface is the menu bar app `VoiceSwitch.app` (`Sources/voice-switch`). It listens to the microphone, recognizes with Apple Speech, and either runs `command` or hands a recording to Superwhisper. The same binary has a `--simulate <audio>` mode. That mode feeds an audio file through the real `Listener` (segmenter, recognizer, wake/stop matching, dictation state machine) without opening the microphone, and logs to stdout. The config comes from `VOICE_SWITCH_CONFIG`.

Out of scope here are the menu bar UI, the hotkeys (F9 to F11, Ctrl+Shift+F8), and the microphone device path. The Windows exe is covered by `verify-voice-switch`.

`verify.sh` in this folder wraps every step. Run it by absolute path.

```sh
V=.claude/skills/verify-voice-switch-mac/verify.sh   # from the repo root; verify.sh finds the repo from its own path
```

## Launch

```sh
$V build                                   # swift build -c release into ~/Library/Caches/voice-switch-verify/build (about 20 s warm)
$V say <fixture> "<Japanese text>"         # Kyoko speech into .../fixtures/<fixture>.aiff; [[slnc 800]] inserts 800 ms of silence
```

There is no long-lived server. Each `$V run` starts one `--simulate` process. The process holds for 40 s and then exits by itself. Ready means `$V doctor` passes.

Never use `make` for verification. It installs into `~/Applications`, quits the user's running VoiceSwitch.app, and relaunches it on the live microphone. `$V build` uses its own build path and touches none of that.

Override the root with `VS_VERIFY_ROOT=<dir>`.

## Doctor

```sh
$V doctor
```

It checks that the binary exists, that the Japanese voice `Kyoko` is installed, and that `--vad-selftest` exits 0. The self-test also fails when the bundled Silero model is missing or the Whisper model is not at `~/Library/Application Support/voice-switch/models/ggml-large-v3-turbo-q5_0.bin`. `make model` downloads the Whisper model (550 MB) without installing the app. The FAIL line quotes the self-test's reason. Doctor also prints the commit and dirty-file count of the build next to the repo's current values. If they differ from what you are proving, rebuild. It also warns about runs that never finished.

## Drive

```sh
VS_RUN_ID=<name> $V run <fixture> [config.json]
```

- `config.json` defaults to `config.safe.json` in this folder. It has no `dictation` key, so nothing reaches Superwhisper. `command` and `stopCommand` only `touch` a marker file in the evidence folder (`__EVIDENCE__` is replaced with that folder).
- To prove a config-dependent feature, write a variant into your scratchpad and pass it. It is copied into the evidence folder.
- `VS_BUSY_INPUT=1` simulates a user who keeps operating the Mac. It runs `swift busy-input.swift 45` beside the process, which posts an in-place mouse move every 200 ms (nothing is typed, the cursor stays put) and logs every frontmost-app change into `busy-input.txt`. Use it with `SIMULATE_REALTIME=1` so audio time matches wall time.
- A config with a `dictation` key drives the user's real Superwhisper. It writes a WAV, runs `open -a superwhisper`, may switch Superwhisper modes, and may paste into the frontmost app. Wake-word dictations and automatic recordings ended by the stop word paste. Tell the user before running one.
- To run automatic sessions without Superwhisper, set `dictation.superwhisperMode` to a mode that does not exist (e.g. `vs-verify-missing`). The handoff then returns before `open`, and the recording goes to local history (`dictation recognition: complete id=<n> rule=auto_b`). This does not help wake-word dictations or stop-word endings. They hand off without a mode check and still open Superwhisper.

HUD glyphs have their own drive, `VS_RUN_ID=<name> $V hud`. It runs `voice-switch --hud-labels <evidence>/hud`, which shows every HUD glyph on the real `HUD` class and checks it, then compares the Mac and Windows glyph names. See [features/hud-labels.md](features/hud-labels.md).

The `run` command prints the exit code, the evidence folder, any marker files, and the `heard` / `dictation` / `phase` / `whisper` lines (the last covers `whisper: ready` and `superwhisper mode:`). The fixture text and expected lines for each feature are in [features/README.md](features/README.md).

## Evidence

Each run leaves `~/Library/Caches/voice-switch-verify/evidence/<run>/<fixture>/`.

| File | Contents |
|---|---|
| `config.json` | the exact config the run used |
| `input.aiff` | the audio that was fed |
| `stdout.txt` | the full runtime log (`heard:`, `dictation recognition: complete id=<n> rule=`, `dictation session:`, `phase:`) |
| `wake.marker`, `stop.marker` | present only when `command` / `stopCommand` actually ran |
| `exitcode.txt`, `pid.txt`, `stderr.txt` | process outcome |

Proof standards:

- Drive the built binary through `--simulate`. A parity check or a standalone `normalize` call shows a function, not app behavior.
- Quote the log lines in order, including the `heard:` text the recognizer produced. The recognizer decides the spelling (it returns `st` for Kyoko's エスティー but `ST` for a live voice), so a synthetic pass does not prove every spelling. Say which spelling the run exercised.
- Check the side effect. A wake proof needs `wake.marker`, not just a `-> wake` log line.
- `--simulate` uses the real keyboard and mouse idle time (`inputIdleSeconds`). Automatic sessions depend on it, so do not type during those runs.
- It shows a HUD window on screen and does not write to `~/Library/Logs/voice-switch.log`.

## Cleanup

```sh
$V cleanup
```

It kills a recorded PID only when its run never wrote `exitcode.txt` and that PID is still this skill's `--simulate` binary. Then it deletes the build folder. Evidence and fixtures are kept. Delete `~/Library/Caches/voice-switch-verify/evidence/<run>` yourself when it is no longer needed.

## Isolation

Runs never open the microphone and do not share config with the user's app. Two runs can go in parallel. With the safe config the only shared thing is the Apple Speech recognizer. With a `dictation` config, Superwhisper, its mode, and the local mode-owner lock in `~/Library/Application Support/voice-switch` are shared with the user's running app. Do not run those in parallel or while the user is dictating.
