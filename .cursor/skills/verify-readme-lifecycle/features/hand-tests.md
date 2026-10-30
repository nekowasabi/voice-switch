# Hand tests

A person starts the app, speaks a wake word, and reads the log. The README names the file and the lines to look for. This harness checks that those names exist in the README and in the source. It does not speak or install.

## Sub-features

- `readme-handtest-logs` requires the log tokens that Swift and C# actually write.
- `readme-log-locations` requires `make logs`, both log paths, and the Mac and Windows hand-test steps.
- `scope-pane-proof` keeps `scripts/pane_route_proof.py` unchanged from `origin/main`.
- `scope-no-sw0` keeps `.claude/sw0.md` and `.claude/sw0b.md` out of git.
- `live-mac-handtest` is the human Mac run. The harness skips it.
- `live-win-handtest` is the human Windows run. The harness skips it.

## How to get to it (user POV)

- On a Mac, run `make logs` and read `~/Library/Logs/voice-switch.log` after speaking a wake word.
- On Windows, open `%LOCALAPPDATA%\voice-switch\voice-switch.log` after speaking a wake word.
- Look for a line that starts with `tmux:` and contains `jev=`, plus the other prefixes the README lists.

## Driving it with verify-readme-lifecycle

Preconditions:

- `$V doctor` printed `doctor: ready`.
- `origin/main` is reachable for the diff check.

- **Check the log contract.** Run `.cursor/skills/verify-readme-lifecycle/verify.sh drive hand-tests`. Stdout contains `PASS readme-handtest-logs` and `PASS readme-log-locations`.
- **Check scope.** The same run prints `PASS scope-pane-proof` and `PASS scope-no-sw0`.
- **Leave the live runs to a person.** The same run prints `SKIP live-mac-handtest` and `SKIP live-win-handtest`.
- **Proof.** `stdout.txt` in the evidence directory contains those four results. The skip lines are not passes.

## Gotchas

- `phase:` on Mac and `dictation phase:` on Windows are different lines. A README that mentions only one of them fails `readme-handtest-logs`.
- `jev=` is a field on the `tmux:` decision line. `jev:` and `jev body:` are separate failure lines in PaneRoute.swift. All three have to be named.
- Passing this feature does not mean a hand test ran.
- Editing `scripts/pane_route_proof.py` fails `scope-pane-proof` even when the edit looks harmless.
