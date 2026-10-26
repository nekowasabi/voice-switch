# voice-switch verification map

This directory is the maintained source for verifying the user-facing behavior of the Windows `voice-switch.exe`. Read this index before driving the app, then use the matching feature file as the recipe.

## Baseline preconditions

- `V=/home/takets/repos/voice-switch/.claude/skills/verify-voice-switch/verify.sh`
- `$V build` from the working tree you want to prove, then `$V fixtures` once.
- `$V doctor` passes and its build commit matches what you are proving.
- Never drive the user's installed tray (`/mnt/c/takeda/tools/voice-switch`) or anything started with `make win-publish`.

## Driving conventions

- Every recipe is `$V wav <fixture> [config]`. Fixtures speak through Windows TTS. Only the voice changes the timing, so compare session lines, not timestamps.
- Write config variants into your scratchpad. Start from `config.example.windows.json` and change only the field the feature needs.
- Group a proof under one `VS_RUN_ID` so the evidence sits together.

## Proof and skip reporting

- Quote the session lines in order from `stdout.txt`, plus `handoff_wavs=` and `exit=`.
- A feature that needs Superwhisper, the live microphone, paste, the HUD, or the history window cannot be proven here. Report it as not verified with the reason. Do not report it as verified because a neighboring path passed.
- Record the fixture and the config used. Both are saved in the evidence folder.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph describing the user-visible behavior. It then uses exactly four H2 sections in this order: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify.sh`, `Gotchas`.

## Features

- [Wake-word dictation](./wake-dictation.md) covers wake, body, standalone stop, late stop, and silence end.
- [Consecutive sessions](./consecutive-sessions.md) covers two dictations in one listening run.
- [Silence produces nothing](./no-speech.md) covers audio with no speech.
- [Automatic no-wake recording](./automatic-wake.md) covers `noWakeWord.autoIdle` and the spoken wake word taking priority over it.
- [Automatic-recording gates](./automatic-gates.md) covers the Silero and speech.cpp checks before handoff, the recording HUD waiting for agreement, and the stop-word paste.
- [Transcription recall](./transcription-recall.md) counts phrases Superwhisper drops from a captured WAV. The KPI is zero.
- [Speaker gate](./speaker-gate.md) drops automatic recordings of another voice and cuts 2 s+ stretches of another voice from handed-off audio (SG9, SG4, built only); the rest is the acceptance list, with the playback front end.
- [Local transcription](./local-transcription.md) transcribes dictation without Superwhisper (Cohere GGUF on transcribe.cpp). It is not drivable yet; the engine baseline runs from the Mac skill.
- [HUD glyphs](./hud-labels.md) covers the vector HUD glyphs. Source checks run from the Mac skill; rendering needs a Windows desktop.
- [Stop sound](./stop-sound.md) tells apart one stop ring, the chime's two notes, and Superwhisper's own sound when a stop sounds doubled. It needs the live tray.
- [Tray host and IPC](./tray-ipc.md) covers start, pause, reload, and quit of the tray app. It is not drivable on this machine yet.
