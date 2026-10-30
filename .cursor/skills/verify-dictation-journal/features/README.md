# Dictation journal verification map

This directory is the maintained source for verifying the persistent journal from issue #49. Read the index before driving the harness, then use the matching feature file as the recipe.

The product surface is a file on disk, not a window. Users reach it by dictating (the app appends when the utterance enters history) and by editing `dictation.journalDir` in `config.json`. Deletion is a journal operation, not a HUD button.

## Baseline preconditions

- Run from the repo root.
- `$V` is `.cursor/skills/verify-dictation-journal/verify.sh`.
- `$V doctor` has printed `doctor: ready`.
- Scratch lives under `/tmp/voice-switch-journal-verify/scratch`. Never point the harness at the user's `~/Library` or `%LOCALAPPDATA%`.
- Do not start the installed menu-bar app or tray app.

## Driving conventions

- Start every recipe from a fresh scratch directory. `drive` already does this.
- Treat every command as literal.
- A `PASS <id>` line is the observation. A `SKIP` line is not a pass.
- The C# harness calls production `DictationHistory.Add` and `WindowsPaths.ExpandPath`.

## Proof and skip reporting

- Capture the Add and the file bytes, not only the exit code.
- Record the feature id and the evidence directory with every artifact.
- `SKIP swift-harness` means the Swift compiler was not on PATH. Say Mac 未検証.
- `SKIP windows-acl` means this host is not Windows. Say the ACL path is Windows 未検証.
- Do not report a skipped entry point as verified through a different path.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph. Then exactly four H2 sections, in order: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify-journal`, `Gotchas`.

## Features

- [Append on accept](./append-on-add.md) covers the history hook, the JSONL fields, and memory expiry leaving the file in place.
- [Journal path](./journal-path.md) covers `dictation.journalDir`, expansion, daily versus single-file, and fallback.
- [Durability and permissions](./durability-permissions.md) covers append-only bytes, symlink refusal, and 0700/0600.
- [Delete a day or an entry](./delete-journal.md) covers day and entry deletion without rewriting surviving lines.
- [Focused app and TaskChute context](./journal-context.md) covers `focusApp`, optional `dictation.journalTaskFile`, and `taskchute` on each new line.
