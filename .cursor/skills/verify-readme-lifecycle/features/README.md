# README and lifecycle verification map

This directory is the maintained source for the human README and the start, stop, and restart commands. Read the index before driving, then use the matching feature file.

The product surface is the two README files, `make` on a Mac, and `voice-switch.exe --tray-command` plus `make win-*` on Windows. This harness does not launch the app.

## Baseline preconditions

- Run from the repo root of this checkout.
- `$V` is `.cursor/skills/verify-readme-lifecycle/verify.sh`.
- `$V doctor` has printed `doctor: ready`.
- Do not start the installed menu-bar app or the Windows tray.
- Do not point the script at a machine config under `~/.config/voice-switch`.

## Driving conventions

- Treat every command as literal.
- A `PASS <id>` line is the observation. A `SKIP` line is not a pass.
- `drive` with no feature runs the whole checklist. `drive <feature>` runs one file.
- The script reads files. It does not install, open, or quit the app.

## Proof and skip reporting

- Record the evidence directory the script prints.
- `SKIP live-mac-handtest` means a person still has to install and try the app on a Mac.
- `SKIP live-win-handtest` means a person still has to install and try the app on Windows.
- Do not report a skipped hand test as verified because the README describes it.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph. Then exactly four H2 sections, in order: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify-readme-lifecycle`, `Gotchas`.

## Features

- [Daily use](./daily.md) covers the primary README, Superwhisper, config paths, and permissions.
- [Hand tests](./hand-tests.md) covers log paths, log lines, the human-owned note, and the out-of-scope files.
- [Windows lifecycle](./win-lifecycle.md) covers `--tray-command` stop and restart, help text, make targets, and the parsing tests.
- [Mac lifecycle](./mac-lifecycle.md) covers `make start`, `make stop`, and `make restart`.
