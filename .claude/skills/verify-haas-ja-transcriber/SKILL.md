---
name: verify-haas-ja-transcriber
description: Drive the opt-in Haas-ja-v1.0 dictation path on Linux without an NVIDIA GPU. Use when changing dictation.transcriber, the external Haas runner, or the Japanese local-model docs. Proves config decode, a mock runner that skips Superwhisper, failure without fallback, and that build/test do not download the model.
---

# verify-haas-ja-transcriber

voice-switch records 16 kHz mono WAV and, when `dictation.transcriber` is `haas`, runs an external command instead of Superwhisper. The command prints UTF-8 text on stdout. This box has no NVIDIA GPU, so the drive uses a mock command and the C# test suite. It does not load Haas-ja-v1.0.

Other skills keep their surfaces. `verify-voice-switch` is the Windows tray. `verify-voice-switch-mac` is the Mac app. Do not start those for this feature.

```sh
V=/workspace/vs-haas-ja/.claude/skills/verify-haas-ja-transcriber/verify.sh
export PATH="$HOME/.dotnet:$HOME/swift/usr/bin:$PATH"
export DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
```

`VS_HAAS_VERIFY_ROOT` overrides the scratch root. The default is `/tmp/voice-switch-haas-verify`. `VS_RUN_ID` names the evidence folder. The default is `run`.

## Launch

There is no server. Launch means the toolchain in Doctor is present. Each drive starts its own `dotnet` and `python3` processes and waits until they exit.

Ready means `verify.sh doctor` prints `ok doctor`. That does not mean the product items pass. A drive before the Haas code exists fails the product items and still exits cleanly with `FAIL` lines.

## Doctor

```sh
$V doctor
```

Read-only. Checks `dotnet` 8.x, `python3`, `git`, and that the script can see the repo. Notes whether `swiftc` starts. A missing `libncurses.so.6` is worked around with a symlink to `libncursesw.so.6` under the scratch root, for `swiftc` only. Notes that no NVIDIA GPU is required. Does not build, does not download weights, and does not read `~/.config/voice-switch/config.json`.

## Drive

```sh
VS_RUN_ID=<name> $V drive
VS_RUN_ID=<name> $V drive HJ-DOCS
```

Item ids are in [items.md](items.md). The feature map is [features/README.md](features/README.md).

`drive` with no id runs every item. `drive <id>` runs one. The Windows suite items (`HJ-CFG-CS`, `HJ-ROUTE-OK`, `HJ-ROUTE-FAIL`, `HJ-DEFAULT`, `HJ-REG`) share one `dotnet run` per drive invocation. A single-item drive runs only what that item needs.

The mock runner is a script the tests write and the production handoff executes. Superwhisper is not installed and must not be launched.

```sh
$V drive HJ-ROUTE-OK
$V drive HJ-ROUTE-FAIL
$V drive HJ-SWIFT
$V drive HJ-GPU
```

## Evidence

Each drive writes `$VS_HAAS_VERIFY_ROOT/evidence/<run>/`.

| File | Contents |
|---|---|
| `doctor.txt` | doctor stdout when you ran doctor yourself |
| `<item>.txt` | that item's command output |
| `summary.txt` | `PASS` / `SKIP` / `FAIL` lines and the counts |

Proof standards:

- Exercise `ConfigLoader.Load` and the handoff type the app constructs. Do not re-parse JSON in the recipe and call it a decode test.
- A route proof includes the mock stdout and the callback that received it, plus the process list showing no Superwhisper.
- A failure proof includes the untouched callbacks and `HandoffStatus.NoResult`.
- `make -n` is the proof that the download is not on the build path. A dry run that still fetches fails the item.
- `HJ-GPU` stays `SKIP` on this box. Do not mark it pass because the mock runner worked.

## Cleanup

```sh
$V cleanup
```

Deletes `$VS_HAAS_VERIFY_ROOT/work` and `$VS_HAAS_VERIFY_ROOT/lib`. Leaves `$VS_HAAS_VERIFY_ROOT/evidence`. Does not kill by process name. The drive's children exit with the script. Delete an evidence run yourself when you no longer need it.

## Isolation

Drives do not read the user's voice-switch config. Test configs are temporary files. Two full drives at once will fight over `dotnet` build outputs in the repo, so run one full drive at a time. Different `VS_RUN_ID` values keep evidence apart.

## Helpers

| Path | Invocation |
|---|---|
| `verify.sh` | `$V doctor`, `$V drive [item-id]`, `$V cleanup` |
| `items.md` | the ids and the exact `PASS` lines |
| `features/` | user-facing recipes |

`verify.sh` is executable. Its usage is printed for an unknown argument.

After the product changes, run `/maintain-verification-skill` when the map drifts. No cadence is set.
