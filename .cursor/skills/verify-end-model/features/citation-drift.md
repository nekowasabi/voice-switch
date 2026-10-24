# Citation drift

The issue's Phase 0 citations are checked once against main and stored, so a later edit cannot pretend the quoted branches were already gone or lived at the cited line numbers.

## Sub-features

- `DRIFT-1` the snapshot names each cited symbol as a match and each cited line number that moved.

## How to get to it (user POV)

- Open issue 48 and compare its `MacApp.swift` and `DictationRuntime.cs` quotations to the current app. The checker reads the committed snapshot instead of the live microphone.

## Driving it with check.py

Preconditions:

- `python3 .cursor/skills/verify-end-model/check.py doctor` prints `doctor PASS`.
- `drift.md` is the snapshot measured on `e22fa59ff4d176dc20165e90f52b954372397a90`.

- **Read the snapshot.** Run `python3 .cursor/skills/verify-end-model/check.py drive citation-drift`. The scoreboard prints `PASS DRIFT-1` and writes `/tmp/voice-switch-verify-end-model/evidence/citation-drift.txt`.
- **Proof.** The evidence line lists `drift.md`. The file still contains `issue cited L32`, `issue cited L28`, and `LINE DRIFT`.

## Gotchas

- Editing `drift.md` after the patch so the old line numbers disappear fails the point of the snapshot. The checker looks for the original phrases.
- A MATCH on the symbol is not a MATCH on the issue's line number. Both belong in the PR.
