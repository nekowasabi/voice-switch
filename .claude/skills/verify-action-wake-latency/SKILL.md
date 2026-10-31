---
name: verify-action-wake-latency
description: Drive Linux checks that an action wake word opens the action dictation from the early-wake probe (Windows tests, the Mac early-wake source contract, and the url-action static check). Use when a change touches early wake, macOS.actions wake words, or windows.actions wake words. A live microphone, Mac app, and Windows desktop stay unverified.
---

# verify-action-wake-latency

The user-facing change is the wait between an action wake word and the action dictation (HUD waiting, action icon, action chime). This box has no microphone and cannot launch the Mac app or the Windows tray. `verify.sh` drives the Windows test process and the source contracts that stand in for the Mac listener.

`verify.sh` lives in this folder. The repo root is three directories above it. From the repo root:

```sh
V="$PWD/.claude/skills/verify-action-wake-latency/verify.sh"
```

Scratch root is `VS_AWL_VERIFY_ROOT` (default `/tmp/voice-switch-action-wake-verify`). Evidence is `$VS_AWL_VERIFY_ROOT/evidence`. Set `VS_RUN_ID` so each drive has its own folder.

Export this before any `dotnet` command. `verify.sh` exports it itself.

```sh
export PATH="$HOME/.dotnet:$HOME/swift/usr/bin:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
```

This host is a stand-in. A run on a Mac or a Windows desktop will not do these steps, and they stay unverified until one does:

- Run `VoiceSwitch.Windows.Tests` with the .NET 8 SDK. No SAPI recognizer and no microphone.
- Read `Segmenter.earlyWake`, the Mac listener's early-wake block, and the two README sentences. `swiftc` on this box exits 127 (`libncurses.so.6`). The script does not install that library and does not compile the Mac target.
- Run `tests/url-actions/run.py` without `--require-swift`, and `tests/parity/run_parity.py`.

Item ids and pass conditions are in [items.md](items.md). That file is the map.

## Launch

There is no resident process. Each drive starts one process at a time and waits for it.

```sh
$V doctor
```

Ready means `doctor` prints `doctor: ok`. The Windows test project must already have been restored. Drives pass `--no-restore`.

## Doctor

```sh
$V doctor
```

Doctor is read-only. It checks that `dotnet` is SDK 8.x, that `python3` exists, and that `items.md`, the Windows test project, `DictationCore.cs`, `DictationRuntime.cs`, `Program.cs`, `Segmenter.swift`, `MacApp.swift`, `Transcript.swift`, both READMEs, `tests/url-actions/run.py`, and `tests/parity/run_parity.py` exist. It prints the repo path, `HEAD`, the SDK version, the scratch root, and either `swiftc: ok` or `swiftc: unavailable`. The last line is `doctor: ok`. Anything else, do not drive.

## Drive

```sh
VS_RUN_ID=<name> $V drive
VS_RUN_ID=<name> $V drive <id>
```

No id runs every item in [items.md](items.md). One id runs that item.

A `dotnet-pass` item passes when its `needle:` line appears in the test stdout. `drive` of one such item runs:

```sh
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore -- --only "<text after PASS >"
```

`--only` that matches nothing exits 0 and prints no `PASS` line. That is a failed item. `drive` of every item runs the test project once, without `--only`, and checks every needle against that stdout. `awl-suite` passes only on that full run: exit 0, no line `FAIL `, at least 243 lines `PASS `.

`awl-url-static`, `awl-mac-early`, and `awl-readme` read the files named in [items.md](items.md). `awl-swift-exec` is `SKIP` whenever `swiftc --version` is not exit 0, and also when it is exit 0, because this skill does not compile a Mac listener. `awl-parity` passes when the summary has not gotten worse than the baseline in [items.md](items.md).

The command prints one `PASS`, `FAIL`, or `SKIP` line per item, then `items pass=<n> skip=<n> fail=<n>`, then `evidence: <folder>`. Its exit code is 0 when `fail` is 0. `SKIP` does not fail the drive.

## Evidence

Each drive writes `$VS_AWL_VERIFY_ROOT/evidence/<VS_RUN_ID>/`. When `VS_RUN_ID` is unset, the folder name is a timestamp. `$VS_AWL_VERIFY_ROOT/last-evidence-path` points at the folder just written.

| File | Contents |
|---|---|
| `git-head.txt` | `HEAD` when the drive started |
| `items.txt` | one result line per item |
| `<id>/command.txt` | the argv, one token per line |
| `<id>/stdout.txt` | the process stdout, or the checker text |
| `<id>/stderr.txt` | the process stderr |
| `<id>/exitcode.txt` | the process exit code |

Proof standards:

- A `PASS` needle is a line the test process prints. A plan sentence is not a result.
- `awl-suite` counts `PASS` lines. A quiet exit with no lines is a failed suite.
- The Mac items read the listener and `Segmenter.earlyWake`. They do not launch the app.
- `awl-parity` fails when a new `FAIL ` line appears. It does not fail when one of the two baseline Swift-library failures disappears.
- These drives do not open a URL, paste, or touch the clipboard.

## Cleanup

```sh
$V cleanup
```

If `$VS_AWL_VERIFY_ROOT/active.pid` still names a live process whose command line contains `VoiceSwitch.Windows.Tests`, `run_parity.py`, or `url-actions/run.py`, cleanup kills that pid. Any other pid is left alone. Evidence under `$VS_AWL_VERIFY_ROOT/evidence` stays. Cleanup does not delete the repo.

## Helpers

`verify.sh` is the only helper. It is executable.

```sh
$V doctor
VS_RUN_ID=proof $V drive awl-probe-wait
$V cleanup
```

After cleanup, `$VS_AWL_VERIFY_ROOT/evidence/proof/items.txt` must still exist.

## Maintenance

When the early-wake rule or the item ids change, run `/maintain-verification-skill` so this map stays honest.
