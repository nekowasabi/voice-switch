# Triage

Triage reads a journal and prints one `triage:` line per utterance. It writes derived files and leaves the journal bytes alone.

## Sub-features

- `triage-task` sends one clear task marker to the tray.
- `triage-ambiguous` keeps an unclear utterance as a memo when cloud is off.
- `triage-muse-read` summarizes locally when the utterance starts with `ミューズ、` and asks for a summary.
- `triage-muse-write` keeps an issue request in the tray.
- `triage-journal-untouched` keeps the sample journal hash stable.

## How to get to it (user POV)

- Run `python3 scripts/voice-switch-ai try`.
- Run `python3 scripts/voice-switch-ai triage --journal tests/fixtures/ai/journal --out <dir>`.

## Driving it with voice-switch-ai

Preconditions:

- `python3 scripts/voice-switch-ai doctor` prints `ready=yes`.
- The sample journal is `tests/fixtures/ai/journal`.

- **One-shot.** Run `python3 scripts/voice-switch-ai try`. Exit code is 0. Stdout contains `cloud=off`, `journal_unchanged=yes`, `triage: local=task@1.00 -> tray`, `triage: local=ambiguous jev=off -> memo`, `triage: muse=research@1.00 -> execute`, and `triage: muse=agent@1.00 -> tray`.
- **Explicit out directory.** Run `python3 scripts/voice-switch-ai triage --journal tests/fixtures/ai/journal --out /tmp/voice-switch-ai-triage`. Exit code is 0. `/tmp/voice-switch-ai-triage/triage.log` contains the same `triage:` lines.
- **Proof.** Save stdout to `reports/verify-voice-switch-ai/proof.txt`. Read `tray.jsonl` and confirm every `externalWrite` value is false.

## Gotchas

- `try` deletes and rewrites only the `voice-switch-ai-try` directory under the temp directory.
- A fake `TYPESAFE_API_KEY` must not change `try`. The line stays `jev=off`.
- The Jev line `triage: jev=task@0.87 -> tray` is covered by the unit test, because `try` does not call Jev.
- Putting `--out` inside the journal directory exits 1.
