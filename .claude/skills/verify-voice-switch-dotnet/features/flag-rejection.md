# Flag rejection

Unknown flags and incompatible combinations throw during parse. The process exits 1 before it loads config, opens a microphone, or starts a command.

## Sub-features

- `flag-unknown` rejects a flag the parser does not know.
- `flag-fire-dry-run` rejects `--fire` together with `--dry-run`.
- `flag-output-dir` rejects `--output-dir` without `--input-wav`.
- `flag-wav-fast` rejects `--input-wav-fast` without `--input-wav`.

## How to get to it (user POV)

- Run `voice-switch.exe --no-such-flag`.
- Run `voice-switch.exe --fire --dry-run`.
- Run `voice-switch.exe --output-dir <folder>` with no `--input-wav`.
- Run `voice-switch.exe --input-wav-fast` with no `--input-wav`.

## Driving it with verify.sh

Preconditions:

- `$V doctor` prints `doctor: ok`.
- Do not pass a real command to `--fire` by itself. The rejection drives below throw before `CommandRunner.Run`.

- **Unknown flag.** Run `VS_RUN_ID=flag-unknown $V cli -- --no-such-flag`. Exit code 1. `stderr.txt` contains `fatal: unknown argument: --no-such-flag`. `stdout.txt` contains `hint: copy config.example.windows.json to `.
- **Fire with dry-run.** Run `VS_RUN_ID=flag-fire-dry-run $V cli -- --fire --dry-run`. Exit code 1. `stderr.txt` contains `fatal: --dry-run cannot be combined with --fire.` `stdout.txt` contains the same hint tail.
- **Output directory alone.** Run `VS_RUN_ID=flag-output-dir $V cli -- --output-dir /tmp/voice-switch-dotnet-verify/not-a-handoff`. Exit code 1. `stderr.txt` contains `fatal: --output-dir requires --input-wav.` The folder is not created.
- **Fast wav alone.** Run `VS_RUN_ID=flag-wav-fast $V cli -- --input-wav-fast`. Exit code 1. `stderr.txt` contains `fatal: --input-wav-fast requires --input-wav.`
- **Proof.** For each drive, keep both streams and `exitcode.txt`. Confirm `/tmp/voice-switch-dotnet-verify/not-a-handoff` does not exist after the output-dir drive.

## Gotchas

- The catch writes the hint to stdout for every exception, including parse errors. An empty stdout on these drives means a different path ran.
- The `fatal:` and `hint:` lines start with a timestamp. Match the tail.
- `--fire` without `--dry-run` is not this feature. After a successful config load it starts `cmd.exe`.
