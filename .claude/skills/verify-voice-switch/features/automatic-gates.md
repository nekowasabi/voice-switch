# Automatic-recording gates (issue #35)

An automatic recording (`noWakeWord.autoIdle`) passes three checks before Superwhisper opens: at least `minHandoffMs` long, Silero VAD hears a voice, and two speech.cpp recognizers (parakeet-tdt_ctc-0.6b-ja, qwen3-asr-0.6b with `--language=ja`) share at least 3 characters. The HUD shows recording only once the recognizers agree. One ended by the stop word is handed off like a wake-word dictation, so Superwhisper pastes it. A recognizer that is missing or fails lets the recording through.

## Sub-features

- `AG1` noise with no voice is dropped by Silero: `dictation dropped: automatic audio had no speech`.
- `AG2` speech is confirmed while it is recorded, then shows the HUD: `automatic confirmed by agreed words` before `dictation phase: Recording`, and no `dictation agreement:` line at the end.
- `AG3` a cough the recognizers disagree on is dropped: `dictation agreement: shared=<n>` with n < 3, then `dictation dropped: automatic audio had no agreed words`.
- `AG4` without speech.cpp, the recording goes through: stdout starts with `word agreement: <model> unavailable`, and the confirm line says `unavailable word agreement`.
- `AG5` an automatic recording ended by the stop word goes out as a wake-word dictation: `automatic ended by stop word, pasting like a wake-word dictation`.

## How to get to it (user POV)

- Turn on `autoIdle`, then cough, knock, or speak without a wake word. Say `入力ストップ` at the end to paste into the window in front.

## Driving it with verify.sh

Preconditions: baseline from the index. `$V fixtures` writes `auto-hiss`, `auto-backchannel`, `auto-speech`, `auto-stop` and the config `auto.json` into the fixtures folder. Run each with `VS_RUN_ID=<name> $V wav <fixture> /mnt/c/temp/voice-switch-verify/fixtures/auto.json`.

- `AG1` `auto-hiss`: `automatic rule=auto_b` → `end reason=Silence` → `had no speech`, `handoff_wavs=0`.
- `AG2` and `AG4` `auto-speech`: `automatic rule=auto_b` → `automatic confirmed by ...` → `dictation phase: Recording` → `end reason=Silence` → one handoff. With speech.cpp installed the line says `agreed words`. Without it you get AG4.
- `AG5` `auto-stop`: `standalone-stop trimExcluded=<a>..<b>` → `end reason=StandaloneStop range=<x>..<a>` → `automatic ended by stop word, pasting like a wake-word dictation` → one handoff. The handoff range ends where the stop word starts.
- `AG3` `auto-backchannel` (needs speech.cpp): `automatic rule=auto_b` → `end reason=Silence` → `dictation agreement: shared=2` → `had no agreed words`, `handoff_wavs=0`, and no `dictation phase: Recording` line. A real cough is not in the repo because it is the user's voice. Check one with `voice-switch.exe --check <wav>` from the build folder. stderr has `<file>\tvoiced=<n>` and `<file>\tagreed=<n>\tparakeet=<text>\tqwen=<text>`.

## Gotchas

- speech.cpp is detected at `%LOCALAPPDATA%\Programs\speech.cpp\current\speech.exe`, then `PATH`. Check whether stdout has `word agreement: ... ready` or `unavailable` before you read AG2 as proof of agreement.
- The first worker start downloads about 1.3 GB of models. Until then every recording goes through as AG4. Wait for `ready` in a warm-up run first. Once the models are cached, both workers report `ready` within about 2 s of launch, before the fixtures' first second of speech is checked.
- Qwen3-ASR writes 咳 for a cough and shares nothing with parakeet's laughter-like text for it. If a cough gets through, run `--check` on its WAV (Superwhisper keeps it as `recordings/<id>/Output.wav`) and on prefixes of it: the runtime confirms on the first second that agrees, so a prefix can leak where the whole file does not.
- TTS speech is clean, so it proves agreement works, not that the 3-character threshold is right for real voices.
- `--output-dir` replaces Superwhisper, so the paste in AG5 and the HUD itself are not exercised. The `dictation phase:` lines are the closest evidence. The paste needs a live tray run.
- `inputIdleSeconds` can be under 1 and still open `auto_b`. That is the current behavior, not a fault of the run.
