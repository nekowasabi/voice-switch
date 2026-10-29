# Missing config

`--listen-seconds 5` is the bounded diagnostic listener. With no config file, the process reports the missing file and exits 1 before the listen window starts.

## Sub-features

- `missing-config-listen` fails closed on `--listen-seconds 5` when the resolved config path does not exist.

## How to get to it (user POV)

- Run `voice-switch.exe --listen-seconds 5` before copying `config.example.windows.json` to the config path.

## Driving it with verify.sh

Preconditions:

- `$V doctor` prints `doctor: ok`.
- `USERPROFILE` is unset, so the config path printed by the hint is `%USERPROFILE%\.config\voice-switch\config.json`.
- That file does not exist. On this box the relative path is resolved from the current directory, and `File.OpenRead` reports it under that directory.
- No `config.json` sits beside the driver dll. If one does, this drive loads it and may block for the listen window. Stop and report that path.

- **Listen without a config.** Run `VS_RUN_ID=missing-config $V cli -- --listen-seconds 5`. Exit code 1. `stderr.txt` contains `fatal: Could not find file '` and `config.json`. `stdout.txt` contains `hint: copy config.example.windows.json to `. `elapsed-ms.txt` is under 2000.
- **Proof.** Read the path inside the `fatal:` line and confirm that file still does not exist. `elapsed-ms.txt` under 2000 shows the process returned before the 5 second listen window. Keep both streams, `exitcode.txt`, and `elapsed-ms.txt`.

## Gotchas

- `elapsed-ms.txt` at 5000 or more means the listener started. That fails the proof. The usual cause is a config file the precondition missed.
- The absolute path in `Could not find file` includes the current directory when `USERPROFILE` is unset, because the fallback path is relative and contains backslashes. Match `Could not find file` and `config.json`, not a machine-specific prefix.
- A bare `voice-switch.exe` with no flags is a tray launch on Windows (`CliOptions.IsTrayLaunch`). This drive uses `--listen-seconds` because that flag stays on the `Program.Run` path.
- The hint's destination string is the unresolved config path. The fatal line is the path `File.OpenRead` tried. Both are part of the proof.
