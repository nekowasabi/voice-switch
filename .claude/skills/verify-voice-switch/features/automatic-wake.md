# Automatic no-wake recording

With `dictation.noWakeWord.autoIdle` on, speech opens an automatic recording without a wake word. It ends on silence or a stop word. One ended on silence goes to history only; one ended by the stop word is handed off like a wake-word dictation. If the user says a wake word while an automatic recording is open, the automatic recording is discarded and a normal wake-word dictation takes over.

## Sub-features

- `AW1` idle speech without a wake word opens `dictation session: automatic rule=auto_b`.
- `AW2` a wake word spoken during an automatic recording replaces it with a wake-word dictation.
- `AW3` the automatic recording does not arm the Superwhisper hotkeys (Escape stays with the foreground app).
- `AW4` (target, issue "自動ウェイクを操作中でも発火させる") speech while the user is typing or moving the mouse also opens `automatic rule=auto_b` with `inputIdleSeconds` below 1. Today `NoWakePolicy.OnSpeech` (`dotnet/VoiceSwitch.Windows.Core/NoWakePolicy.cs:64`) returns null there.
- `AW5` the recording starts at the phrase head. `DictationRuntime.cs:579-581` no longer cuts at `analysisStore.Next - idle*Rate`; the handoff WAV duration is close to the spoken phrase.
- `AW6` the recording is handed to Superwhisper while operating, the same as an idle one, and the foreground window returns to the app that was in front.

## How to get to it (user POV)

- Turn on `autoIdle` in `config.json`, stop touching the keyboard and mouse for 3 s, then speak.

## Driving it with verify.sh

Preconditions: baseline from the index. Write a config variant with `"autoIdle": true` (for example `sed 's/"autoIdle": false/"autoIdle": true/' config.example.windows.json > <scratchpad>/auto.json`). Do not touch the Windows keyboard or mouse for 3 s before and during the run.

- `AW2`: `$V wav natural-one-breath-clean <scratchpad>/auto.json`. Expect `dictation session: automatic rule=auto_b` → `recognition: complete ... leadingWake=True ... rule=auto_b` → `dictation session: automatic replaced by wake word` → `body-start` (range starts after the wake word) → one `handoff`, `handoff_wavs=1`.
- `AW1`: needs a fixture with speech but no wake word. None ships yet. Add one to `tests/windows/compose-fixtures.ps1`, then expect `automatic rule=auto_b` → `handoff ... reason=Silence` with no `replaced` line.
- `AW4` to `AW6`: need busy input on the Windows desktop during `$V wav`. No helper ships yet. The Windows equivalent of the Mac `busy-input.swift` is a PowerShell loop that calls `SetCursorPos` to the current position (or `SendInput` with `MOUSEEVENTF_MOVE` and dx=dy=0) every 200 ms and logs `GetForegroundWindow` changes. Add it beside `run-wav.ps1`, then expect `automatic rule=auto_b inputIdleSeconds=0.<n>`, one handoff WAV of about the phrase length. `--output-dir` replaces Superwhisper, so the focus return of AW6 needs a live tray run.
- Gates before handoff (issue #35). Compose the fixtures into the fixtures folder: `auto-hiss` (1 s silence, 3 s amplitude-modulated white noise, 2.5 s silence), `auto-speech` (`scripts/windows-say.ps1` saying a sentence with no wake word, padded with silence), `auto-stop` (the same sentence, 0.3 s, `入力ストップ`). Then `VS_RUN_ID=<name> $V wav <fixture> <scratchpad>/auto.json`.
  - `auto-hiss`: `automatic rule=auto_b` → `end reason=Silence` → `dictation dropped: automatic audio had no speech`, `handoff_wavs=0`. Plain keystroke or burst fixtures (`tests/parity/fixtures/speech_gate/noise-*.wav`) usually never open a session on Windows, so they do not reach the gate.
  - `auto-speech`: `automatic rule=auto_b` → `automatic confirmed by agreed words` → `dictation phase: Recording` → `end reason=Silence` → one handoff. Without speech.cpp the confirm line says `unavailable word agreement` and the stdout starts with `word agreement: <model> unavailable`.
  - `auto-stop`: `standalone-stop` → `end reason=StandaloneStop` → `automatic ended by stop word, pasting like a wake-word dictation` → one handoff whose range ends before the stop word. The paste itself needs a live tray run.
  - A cough dropped by `dictation agreement: shared=<n>` → `had no agreed words` needs speech.cpp installed and a real cough recording. `voice-switch.exe --check <wav>` prints `agreed=` and both transcripts on stderr.
- `AW3`: not provable with this harness. The hotkey hook needs a real dictation window and keyboard input. Report it as not verified and cite the absence of `hotkey: keyboard hook installed` after `automatic` in a live tray log as the closest evidence.

## Gotchas

- `inputIdleSeconds` is the real Windows desktop idle time. If the user is typing, `autoIdle` never fires and the run silently behaves like the default config. Check `inputIdleSeconds=` on the `automatic` line.
- With a `noWakeWord` switch on, handoff lines say `details suppressed`. Prove the source by the session lines, not the handoff message.
- `--output-dir` replaces Superwhisper, so the automatic transcription path (mode `superwhisperMode`, history only) is not exercised here.
