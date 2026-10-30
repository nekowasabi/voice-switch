# Mac lifecycle

`make install` already quits `local.voice-switch` with `osascript`, waits until `voice-switch` exits, and opens the app. `make stop`, `make start`, and `make restart` expose that quit and that open without a rebuild.

## Sub-features

- `mac-make-lifecycle` adds the three targets with the same quit and the same wait `install` uses.
- `mac-readme-lifecycle` documents them in both READMEs.
- `mac-cli-optional` allows a `--restart` flag in `main.swift` only when both READMEs name it.

## How to get to it (user POV)

- After `make install`, quit the menu-bar app with `make stop`.
- Open the copy in `~/Applications` with `make start`.
- Quit and open again with `make restart`.
- Read the log with `make logs`. That target is unchanged.

## Driving it with verify-readme-lifecycle

Preconditions:

- `$V doctor` printed `doctor: ready`.
- Do not run `make start`, `make stop`, or `osascript` from this drive. The script only reads the Makefile.

- **Check the targets.** Run `.cursor/skills/verify-readme-lifecycle/verify.sh drive mac-lifecycle`. Stdout contains `PASS mac-make-lifecycle` and `PASS mac-readme-lifecycle`.
- **Check the optional flag.** The same run prints `PASS mac-cli-optional`.
- **Proof.** `exitcode.txt` is `0` when every id in this feature passed. That still does not mean a Mac opened the app.

## Gotchas

- `make stop` must use `quit app id "$(ID)"`. A `kill` by process name is a different operation and fails the item.
- `make start` must `open` the installed app. A target that runs `swift build` fails `mac-make-lifecycle`.
- `make restart` has to wait. `install` already loops on `pgrep -x voice-switch` and `sleep`. Copy that wait.
- Do not add `--restart` to `main.swift` unless you also document it. The item passes when the flag is absent.
