# Haas-ja transcriber verification map

This map is the user-facing surface of the opt-in Haas body transcriber. Read it before driving. Item ids live in [../items.md](../items.md). The harness is `verify.sh`.

## Baseline preconditions

- Worktree `/workspace/vs-haas-ja`, branch `feat/haas-ja-transcriber`.
- `PATH` includes `$HOME/.dotnet` and `$HOME/swift/usr/bin` when those directories exist.
- `DOTNET_ROOT=$HOME/.dotnet` and `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- No NVIDIA GPU is required. Do not download weights and do not `pip install` haas-asr during a drive.
- Scratch root: `VS_HAAS_VERIFY_ROOT` (default `/tmp/voice-switch-haas-verify`). Evidence is `$VS_HAAS_VERIFY_ROOT/evidence/<VS_RUN_ID>/`.
- Two drives may run at once only with different `VS_RUN_ID` values. They do not share evidence directories. They do share the repo, so do not run two drives that both compile the same test projects.

## Driving conventions

- Start with `verify.sh doctor`.
- `verify.sh drive` runs every item. `verify.sh drive <item-id>` runs one.
- Quote item ids exactly.
- A `SKIP` is not a pass. `HJ-GPU` is always skipped here. `HJ-SWIFT` is skipped only when `swiftc` cannot start, and the skip line must include the compiler error.

## Proof and skip reporting

- Capture the command, stdout, stderr, and exit code under the evidence directory.
- The mock runner is a real process. Assert the text it printed and that no Superwhisper process was started.
- Config proof is `ConfigLoader.Load` and Swift `JSONDecoder`, not a reimplementation of the parser.
- `make -n` proves the model targets are outside build and test. Do not run the download.
- Record the item id on every artifact.

## Feature entry contract

Each feature file has an H1, one paragraph, then four H2 sections: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify.sh`, `Gotchas`.

## Features

- [Enable Haas](./enable-haas.md) covers the opt-in config, the example file, and the docs.
- [Route the text](./route-text.md) covers a mock runner feeding tmux, paste, and an action URL.
- [Failure stays closed](./failure-closed.md) covers non-zero exit, empty stdout, and timeout.
- [Default stays Superwhisper](./default-superwhisper.md) covers a config that never mentions Haas.
