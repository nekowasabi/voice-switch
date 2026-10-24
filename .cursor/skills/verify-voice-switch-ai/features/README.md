# voice-switch-ai verification map

This directory is the maintained source for the user-facing CLI. Read this index, then use the matching feature file.

## Baseline preconditions

- The working directory is the repository root.
- `python3` is Python 3.10 or newer.
- The sample journal exists at `tests/fixtures/ai/journal`.
- `python3 scripts/voice-switch-ai doctor` prints `ready=yes`.
- Each drive uses its own `--out` directory, outside the journal directory.
- Do not export a real API key for `try`. Cloud stays off.

## Driving conventions

- Start from the sample journal unless the feature names another fixture.
- Treat every command as literal.
- Assert on stdout lines and on JSON fields in the derived directory.
- Do not treat a unit-test double as a substitute for `try` when the feature file names `try`.

## Proof and skip reporting

- Record the command, the exit code, and the named stdout line.
- Record a second read of the derived file when the command writes one.
- If a path needs macOS or Windows, report it as Mac 未検証 or Windows 未検証 with the command that was not run.
- Do not mark a skipped entry point verified through a different command.

## Feature entry contract

Each feature file has an H1, one opening paragraph, and four H2 sections: `Sub-features`, `How to get to it (user POV)`, `Driving it with voice-switch-ai`, and `Gotchas`.

## Features

- [Triage](./triage.md) covers local routing, the muse prefix, and the offline Jev fallback.
- [Tray](./tray.md) covers draft proposals and local approval.
- [Digest](./digest.md) covers the daily and weekly files.
- [Search](./search.md) covers recall over the sample journal.
- [Probe](./probe.md) covers the labeled set and the offline score.
