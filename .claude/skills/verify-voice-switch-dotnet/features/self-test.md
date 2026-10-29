# Self-test

`--self-test` checks the wake command, the stop command, an ignored phrase, and the energy segmenter, then prints one line. It does not open a microphone and does not run the configured command.

## Sub-features

- `self-test-wake` requires `音声入力` to decide `run-command` with `echo wake`.
- `self-test-stop` requires `入力ストップ` to decide `run-command` with `echo stop`.
- `self-test-miss` requires `違います` to decide `ignore`.
- `self-test-segmenter` requires synthetic loud frames to produce an utterance.

## How to get to it (user POV)

- Run `voice-switch.exe --self-test`.
- Run `voice-switch.exe --vad-selftest`. The parse alias calls the same `SelfTest.Run`.

## Driving it with verify.sh

Preconditions:

- `$V doctor` prints `doctor: ok`.

- **Run the self-test.** Run `VS_RUN_ID=self-test $V cli -- --self-test`. The process exits 0. `stdout.txt` is exactly `self-test: ok` plus a newline (`printf 'self-test: ok\n' | cmp -s - stdout.txt`). `stderr.txt` is empty.
- **Alias.** Run `VS_RUN_ID=self-test-alias $V cli -- --vad-selftest`. Exit code, stdout, and stderr match the `self-test` drive.
- **Proof.** A failing sub-check exits 1 and writes one of these stderr tails: `self-test: wake decision failed`, `self-test: stop decision failed`, `self-test: miss decision failed`, `self-test: segmenter decision failed`. Exit 0 with `self-test: ok` means all four passed. Keep `stdout.txt`, `stderr.txt`, and `exitcode.txt`.

## Gotchas

- The success line has no timestamp. A `fatal:` line means the flag never reached `SelfTest.Run`.
- This drive does not prove `--fire` or a live recognizer. Those paths are outside the self-test.
- The segmenter check uses generated samples inside the process. It does not read a wav and does not load a speech engine.
