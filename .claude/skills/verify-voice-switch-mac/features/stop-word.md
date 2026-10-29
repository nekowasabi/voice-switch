# Stop word

When the user says a configured stop word, voice-switch ends the current dictation. When nothing is recording, it only stops Superwhisper if a listed app holds the microphone, and otherwise just logs it.

## Sub-features

- `stop-idle` logs a stop word heard while nothing is recording.
- `stop-latin-case` matches a Latin stop word regardless of the case the recognizer returns.
- `stop-dictation` ends a running dictation (needs `dictation`, see wake-dictation.md).
- `stop-automatic-live` ends an automatic recording on the installed app with a live voice. It is `mic-switch.md` MS16, which fails on the built-in mic as of 2026-10-28. The synthetic `long-tail` in stop-word-long-utterance.md does not cover it: `--simulate` has no HAL voice detector, and `config.stop-auto.json` lists only `入力ストップ`, not the user's bare `ストップ`.

## How to get to it (user POV)

- Say `入力ストップ` or another configured stop word, alone.

## Driving it with verify.sh

Preconditions:

- Safe config. `$V doctor` passes.

- **Japanese stop word.** Run `$V say stop-ja "入力ストップ"` and `VS_RUN_ID=<id> $V run stop-ja`. The output has `heard: 入力ストップ  (stop word, nothing is recording)`.
- **Latin stop word.** Run `$V say stop-st "エスティー"` and `VS_RUN_ID=<id> $V run stop-st`. The output has `heard: st  (stop word, nothing is recording)`.

## Gotchas

- `stopCommand` runs only when an app in `skipWhileMicInUseBy` is using the microphone. The safe config lists none, so `stop.marker` is expected to be absent. The proof is the `(stop word, nothing is recording)` line.
- Make `stop-ja` exactly as written, with no `[[slnc]]` around it. Padded with silence (`"[[slnc 800]]入力ストップ[[slnc 1200]]"`), Kyoko's last mora is lost and the run logs `heard: 入力ストッ` with no stop (seen 2026-10-14).
- Kyoko's エスティー is recognized as lowercase `st`. A live voice has produced uppercase `ST`. This run does not exercise the uppercase spelling.
