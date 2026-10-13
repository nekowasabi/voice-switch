# Wake word then dictation

With `dictation` configured, a wake word opens a dictation. The body is recorded until `endSilenceMs` of silence or a stop word, and the recording is handed to Superwhisper, which pastes the text.

## Sub-features

- `dictation-one-breath` cuts the wake word off when the body follows in the same breath.
- `dictation-waiting` waits for the body after a lone wake word, up to `startTimeoutMs`.
- `dictation-stop-word` ends the body with a stop word.

## How to get to it (user POV)

- Say `音声入力`, then the text, then pause or say `入力ストップ`.

## Driving it with verify.sh

Preconditions:

- The user agreed to a run that drives their Superwhisper.
- A scratchpad config equal to `config.safe.json` plus `"dictation": {"endSilenceMs": 1200, "startTimeoutMs": 3000, "maxSeconds": 60}`.

- **Waiting dictation.** Run `$V say dict-wait "音声入力 [[slnc 1000]] 今日は晴れです [[slnc 2000]]"` and `VS_RUN_ID=<id> $V run dict-wait <config>`. The output has `dictation started (waiting for text)`, then `dictation: <n> chars in <ms> ms`.
- **Stop word.** Run `$V say dict-stop "音声入力 [[slnc 1000]] 今日は晴れです [[slnc 900]] 入力ストップ"` and run it with the same config. The output has `dictation finished by stop word`.

## Gotchas

- The handoff opens Superwhisper and it pastes into the frontmost app. Keep a scratch text field in front.
- `dictation: no superwhisper result within 30 s` means Superwhisper did not pick up the file. That is a handoff failure, not a recognition failure.
