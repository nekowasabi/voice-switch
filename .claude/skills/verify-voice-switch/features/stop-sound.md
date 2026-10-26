# Stop sound

When the stop word or the finish key ends a recording and the tray's 効果音 is on, `voice-switch.exe` rings one stop chime just before the Superwhisper handoff, and the VAD ignores the next 600 ms. Superwhisper may then play sounds of its own while it transcribes and pastes. A user who hears "the sound twice" can be hearing any of three things, and this file tells them apart.

## Sub-features

- `SS1` one ring per stop: `Submit` calls `PlayStopSound` once, only for `StandaloneStop` or `FinishCommand` (`DictationRuntime.cs` `Submit`). Each session logs exactly one `dictation session: end reason=` line, so two lines with the same `sid=` would mean two rings.
- `SS2` the stop chime is two notes by design: `TraySettings.StopChime` is A5 (880 Hz) then E5 (659.25 Hz), each 120 ms with its own 5 ms attack and a cubic decay to zero (`Synthesize`). It reads as two pops 120 ms apart, not one tone (commit 70dd440).
- `SS3` Superwhisper's own sound: an automatic recording ended by the stop word is pasted like a wake-word dictation, so Superwhisper opens the file and transcribes it between `automatic ended by StandaloneStop` and `dictation: <n> chars`. Any sound in that window that is not the A5→E5 pair comes from Superwhisper, not from voice-switch.

## How to get to it (user POV)

- Turn on 効果音 in the tray menu. Dictate, then say `入力ストップ`. Note how many sounds you hear, their pitch, and how far apart they are.

## Driving it with verify.sh

`verify.sh wav` cannot prove this. The `--input-wav` path has no tray observer, so `PlayStopSound` returns false and nothing rings. Use these instead, and report a live result as not verified by this skill.

- `SS1` from the code: `make win-test` must print `PASS dictation stop sound rings once on stop and not on a lone wake`. From a live run, count `end reason=` lines per `sid=` in `%LOCALAPPDATA%\voice-switch\voice-switch.log` around the stop. One line per sid means one ring.
- `SS2` by listening: with Superwhisper closed, the tray still rings on a stop word (the ring comes before the handoff). Two short falling pops 120 ms apart is the chime working as designed.
- `SS3` by elimination, one switch at a time, each with one stop word:
  - 効果音 off, Superwhisper sounds on: what you still hear is Superwhisper's.
  - 効果音 on, Superwhisper sounds off: what you hear is the voice-switch chime only. Two pops here is `SS2`.
  - Both on: expected is the chime, then Superwhisper's sound somewhere between `automatic ended by StandaloneStop` and `dictation: <n> chars`. That gap was 2.3 s in the 2026-10-26 18:11:03 run.

## Gotchas

- The chime has no log line. The log proves how many sessions ended, not how many sounds played. Count sounds by ear or with a loopback recording, never from the log alone.
- `deafUntil` hides 600 ms of microphone audio after the ring. A `vad: speech-onset` right after that window can be the tail of Superwhisper's sound picked up by the microphone, not the user.
- A stop word and the finish key in the same breath end one session. The second one arrives after the session closed, so it rings nothing. Two `end reason=` lines with different `sid=` values are two recordings, not a double ring.
- Mac rings its own stop sound from `playStopSound()` in `MacApp.swift`. This file covers Windows only.
