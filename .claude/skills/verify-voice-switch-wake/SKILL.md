---
name: verify-voice-switch-wake
description: Drive VoiceSwitch wake-recognition surrogates on Linux (labeled recognized-text corpus, WakeReplay fixture logs, near-miss census, synthetic wake-acceptance manifests, and the automatic-session URL safety tests). Use when a change can alter whether a recognized phrase wakes or stays quiet. Real microphone audio, Mac, and Windows hardware stay unverified.
---

# verify-voice-switch-wake

The surface is the Linux wake surrogate, not the tray app. A speaker's wake word is recognized on Windows by SAPI and on Mac by Speech. This box has neither a microphone nor those recognizers. `verify.sh` drives the same Core wake APIs and the existing replay tools on frozen recognized text.

`verify.sh` lives in this folder and finds the repo three directories above itself. From the repo root:

```sh
V="$PWD/.claude/skills/verify-voice-switch-wake/verify.sh"
```

Scratch root is `VS_WAKE_VERIFY_ROOT` (default `/tmp/voice-switch-wake-verify`). Evidence is `$VS_WAKE_VERIFY_ROOT/evidence`. Set `VS_RUN_ID` so each drive has its own folder. One `fixture-replay` at a time: its self-test rewrites `dotnet/VoiceSwitch.Windows.Core/DictationCore.cs` and restores it on exit.

Export this before any `dotnet` command:

```sh
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
```

This host is a stand-in. A run on a real microphone, a Mac, or a Windows desktop will not do these steps, and they stay unverified until one does:

- Build `wake-corpus` and `VoiceSwitch.WakeReplay` with the .NET 8 SDK and call `DictationBoundaries` on JSON rows and on `tests/fixtures/wake_log_replay_sample.log`. No WAV is decoded and no recognizer runs.
- Score `scripts/wake_acceptance.py` on `fixtures/pass-manifest.json`. That file is verification scaffolding, not a product log.
- Run three existing `VoiceSwitch.Windows.Tests` checks with `--only`. The win-x64 tray executable is not started.
- Leave Swift unbuilt. `swiftc` is not on this box. Mac wake matching stays exact normalized text and is not driven here.

A green surrogate is not a measured recognition rate.

## Launch

There is no resident process. Prepare the toolchain, build the two executables, then start one process per drive.

```sh
$V doctor
$V build
```

`$V build` runs:

```sh
dotnet build .claude/skills/verify-voice-switch-wake/corpus/WakeCorpus.csproj -c Release -t:Rebuild -o "$VS_WAKE_VERIFY_ROOT/build" --ignore-failed-sources
dotnet build dotnet/VoiceSwitch.WakeReplay/VoiceSwitch.WakeReplay.csproj -c Release -t:Rebuild --ignore-failed-sources
```

Ready means both logs contain `0 Error(s)`, `$VS_WAKE_VERIFY_ROOT/build/wake-corpus.dll` exists, and `dotnet/VoiceSwitch.WakeReplay/bin/Release/net8.0/VoiceSwitch.WakeReplay.dll` exists. The logs are `$VS_WAKE_VERIFY_ROOT/evidence/build-corpus.log` and `build-replay.log`. Both commands use `-t:Rebuild`. A DLL left from an earlier matcher is not treated as ready.

The Windows and test projects must already have been restored once. `$V` does not pass `--no-restore` for these two projects. Do not pass `-p:BaseIntermediateOutputPath`; it makes Core compile twice and fail with duplicate assembly attributes.

## Doctor

```sh
$V doctor
```

Doctor is read-only. It checks that `dotnet` is SDK 8.x, that `python3` exists, and that the corpus, the WakeReplay project, `DictationCore.cs`, `TextMatching.cs`, `NoWakePolicy.cs`, the fixture log, `scripts/wake_acceptance.py`, and the pass manifest exist. It prints the repo path, `HEAD`, the SDK version, and the scratch root. The last line is `doctor: ok`. Anything else, do not drive.

## Drive

```sh
VS_RUN_ID=<name> $V drive <feature>
```

`<feature>` is one of `reading-match`, `fixture-replay`, `near-miss`, `acceptance`, `safety`. Recipes are in [features/README.md](features/README.md).

`drive` rebuilds both executables before it starts the feature. It does not skip that because a DLL is already on disk.

The command prints `exit: <code>` and `evidence: <folder>`. Its own exit code is the app exit code, then the line checks below. A missing expected line fails the drive even when `VS_RUN_ID` is unset. `reading-match` requires the frozen corpus sha256, both floor lines, `false_wakes=0` on both rules, and `rows=47`. SPEC item 6 also names the row ids that must stay hits; a swap that keeps the totals still fails that item. `fixture-replay` requires `OK wake_log_replay self-test` and `in_band=False`. `near-miss` requires the whole baseline census fields (`agreed_true=2 agreed_false=7 gained=1 lost=0`, each `(shown N,` header, `d3_near_miss_intentional=1 d3_near_miss_ambient=0`, and the `法政入力` example line). A shorter needle such as `gained=1` is not enough, because it also matches `gained=10`. `acceptance` requires `"verdict": "pass"`. `safety` requires one exact `PASS` line for each of the three full test names.

## Evidence

Each drive writes `$VS_WAKE_VERIFY_ROOT/evidence/<VS_RUN_ID>/<feature>/`. When `VS_RUN_ID` is unset, the folder name is a timestamp. `$VS_WAKE_VERIFY_ROOT/last-evidence-path` points at the folder just written.

| File | Contents |
|---|---|
| `command.txt` | the argv, one token per line |
| `stdout.txt` | the process stdout |
| `stderr.txt` | the process stderr |
| `exitcode.txt` | the process exit code |
| `pid.txt` | the pid that was waited on |
| `git-head.txt` | `HEAD` when the drive started |

Proof standards:

- Drive the production wake APIs (`LeadingWake`, `EarlyWake`, `RejectedWake`) or the shipped replay and acceptance tools. A comment in a plan is not a result.
- Capture the command, both streams, and the exit code. For `reading-match`, the result is the `row` lines plus the `rule=` totals, not a single summary word.
- `false_wakes=0` is part of the result. A higher hit count with a new false wake is a failed drive.
- The corpus file's sha256 is part of the result. A quieter total that came from editing labels is a failed drive.
- `acceptance` does not measure recognition. It checks the evaluator against a synthetic manifest.
- `safety` must show the `PASS` line. `--only` with no matching name exits 0 and prints nothing, and that is a failed drive.
- These drives do not open a URL, paste, or touch the clipboard. Confirm that by the safety PASS lines, not by the name of the corpus tool.

## Cleanup

```sh
$V cleanup
```

If `$VS_WAKE_VERIFY_ROOT/active.pid` still names a live process whose command line contains `wake-corpus`, `VoiceSwitch.WakeReplay`, or `VoiceSwitch.Windows.Tests`, cleanup kills that pid. Any other pid is left alone. Cleanup then deletes `$VS_WAKE_VERIFY_ROOT/build`. Evidence under `$VS_WAKE_VERIFY_ROOT/evidence` stays, including the build logs. Cleanup does not delete the repo, the corpus, or `DictationCore.cs`.

## Helpers

`verify.sh` is the only helper. It is executable.

```sh
$V doctor
$V build
VS_RUN_ID=proof-reading-match $V drive reading-match
$V cleanup
```

After cleanup, `$VS_WAKE_VERIFY_ROOT/evidence/proof-reading-match/reading-match/stdout.txt` must still exist.

## Maintenance

When the wake APIs, the corpus rows, or the replay census change, run `/maintain-verification-skill` so this map stays honest.
