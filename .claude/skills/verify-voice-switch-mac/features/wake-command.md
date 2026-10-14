# Wake word runs the command

When the user says a configured wake word on its own and no `dictation` is configured, voice-switch runs `command` (by default `open -g superwhisper://record`).

## Sub-features

- `wake-exact` runs `command` for a whole-utterance match.
- `wake-normalized` matches after dropping spaces and punctuation and folding Latin case.
- `wake-embedded-ignored` ignores a wake word inside a longer sentence.

## How to get to it (user POV)

- Say `音声入力` alone, with a pause before and after.

## Driving it with verify.sh

Preconditions:

- Safe config. `$V doctor` passes.

- **Wake alone.** Run `$V say wake-alone "音声入力"` and `VS_RUN_ID=<id> $V run wake-alone`. The output has `heard: 音声入力  -> wake` and lists `wake.marker`.
- **Embedded phrase.** Run `$V say wake-embedded "音声入力してください"` and `VS_RUN_ID=<id> $V run wake-embedded`. The output has a `heard:` line without `-> wake`, and no `wake.marker`.

## Gotchas

- `wake-normalized` has no reliable `say` recipe. Kyoko pauses at 「、」, so `"音声、入力。"` becomes two utterances (`heard: 音声`, `heard: 入力`) and never matches (seen 2026-10-14). Normalization removes only the characters in `Transcript.swift`'s punctuation set.
- `-> wake` alone is not proof. `command` runs in a child shell, so check `wake.marker`.
- A config that lists an app in `skipWhileMicInUseBy` skips the wake while that app holds the microphone. The safe config lists none.
