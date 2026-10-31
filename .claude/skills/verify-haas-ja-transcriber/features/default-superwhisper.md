# Default stays Superwhisper

A config that does not mention Haas still opens Superwhisper for the body and still runs the existing Windows and Mac tests.

## Sub-features

- `HJ-DEFAULT` checks the handoff factory.
- `HJ-CFG-CS` checks that an absent transcriber decodes as Superwhisper.
- `HJ-REG` reruns the Windows behavior suite.
- `HJ-PARITY` reruns the parity contract.

## How to get to it (user POV)

- Leave `dictation.transcriber` out of the config, or set it to `superwhisper`.
- Speak a wake word and a body. The WAV still goes to Superwhisper. Nothing calls `haas_transcribe.py`.

## Driving it with verify.sh

Preconditions:

- `verify.sh doctor` is ok.
- Baseline before this change: Windows.Tests exit 0, parity `fail=2` from `libncurses.so.6` and `automatic transcript policy checks failed`.

- **Factory.** Run `verify.sh drive HJ-DEFAULT`. The pass line `haas default factory keeps the superwhisper handoff` is present.
- **Suite.** Run `verify.sh drive HJ-REG`. Exit code of the test process is 0 and no line starts with `FAIL `.
- **Parity.** Run `verify.sh drive HJ-PARITY`. The summary fail count is 2 or less, and any remaining failures are the two baseline ones.

## Gotchas

- Wrapping the Haas handoff in `LocalHistoryHandoff` makes the default look unchanged while Haas failures fall back to SAPI. `HJ-DEFAULT` requires the Haas return type to stay `HaasRunnerHandoff` even when wrap was requested.
- Do not "fix" the two baseline parity failures inside this change unless the fail count stays at or below 2 and no new failure appears.
- `HJ-REG` is the full suite, not `--only haas`. A Haas test that passes while an old test fails is still a failure.
