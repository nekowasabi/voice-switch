# Voice-switch .NET CLI verification map

This directory is the maintained source for the Linux-runnable CLI behavior of `voice-switch.exe`. Read the index, then follow one feature file. The harness is `verify.sh` in the parent folder.

## Baseline preconditions

- Repo root is the worktree that contains `dotnet/VoiceSwitch.Windows/Program.cs`.
- `export PATH="$HOME/.dotnet:$PATH"` and `export DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- `dotnet` is SDK 8.x. The Windows csproj has been restored with `-r win-x64`, and the test csproj has been restored.
- `$V doctor` prints `doctor: ok`.
- `VS_VERIFY_ROOT` is unset, so evidence lands in `/tmp/voice-switch-dotnet-verify/evidence`. Set it only when two runs must stay apart.
- The driver output directory contains no `config.json`. `$V cli` does not copy one.
- `USERPROFILE` is unset on this box. `--help` then prints `Config: %USERPROFILE%\.config\voice-switch\config.json`.

## Driving conventions

- `$V` is the absolute path of `verify.sh`.
- Set `VS_RUN_ID` to the feature id before each drive.
- Pass user arguments after `--`: `VS_RUN_ID=<id> $V cli -- <args>`.
- One `cli` drive at a time against one scratch root. They share the generated driver project.
- Read `stdout.txt`, `stderr.txt`, and `exitcode.txt` after the process exits. Quote the stable text, not the timestamp.
- Cleanup deletes the driver and the win-x64 output. It leaves the evidence directory.

## Proof and skip reporting

- A drive proves the `cli` entry that was named. A `PASS` line from the behavior suite does not cover that entry.
- `--help` and `--self-test` are proved by an empty stderr and the stdout checks in their feature files.
- A fatal path is proved by the `fatal:` tail on stderr and the `hint:` tail on stdout, plus exit code 1.
- If a precondition is missing, stop and report the command and the unmet precondition. Do not switch to another entry and call this one verified.
- Tray launches, microphone capture, PowerShell recognition, and Superwhisper handoff are outside this map on this host.

## Feature entry contract

Each feature file has an H1, one paragraph, then four H2 sections in order: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify.sh`, `Gotchas`.

## Features

- [CLI help](./cli-help.md) covers `--help` and `-h`.
- [Self-test](./self-test.md) covers `--self-test` (wake, stop, miss, segmenter).
- [Flag rejection](./flag-rejection.md) covers unknown flags and incompatible combinations.
- [Missing config](./missing-config.md) covers `--listen-seconds` when the config file is absent.
