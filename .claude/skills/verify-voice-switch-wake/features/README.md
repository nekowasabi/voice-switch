# Wake surrogate verification map

This directory is the maintained source for verifying wake-word behavior that this Linux box can actually execute. Read the index before driving, then use the matching feature file as the recipe.

The product user says a wake word into a microphone. That path is not reachable here. The user of this map is the engineer checking whether a recognized phrase wakes or stays quiet. Real-audio recognition rate, Mac, and Windows hardware are unverified.

## Baseline preconditions

- Repo root is the `voice-switch` worktree that contains this skill (this checkout is `/workspace/voice-switch-wake-6h`).
- `dotnet` SDK 8.x and `python3` are on `PATH`. Export `DOTNET_ROOT` and `DOTNET_CLI_TELEMETRY_OPTOUT=1` as in [SKILL.md](../SKILL.md).
- The Windows project and `VoiceSwitch.Windows.Tests` have already been restored.
- Set `VS_WAKE_VERIFY_ROOT` when two runs must not share a scratch directory. The default is `/tmp/voice-switch-wake-verify`.
- Run `verify.sh doctor` and require the last line `doctor: ok`.
- Do not drive a WakeReplay self-test that another process already started. It shares `DictationCore.cs`.

## Driving conventions

- Start from the frozen corpus and the fixture log. Do not relabel rows to make a total look better.
- Treat every command as literal. Keep quoted test names and flags unchanged.
- Run drives through `verify.sh drive`.
- One `fixture-replay` at a time. `reading-match`, `near-miss`, and `acceptance` do not rewrite source files.
- Leave evidence in place when cleaning up. Cleanup deletes only the scratch build and a pid this run recorded.

## Proof and skip reporting

- Capture the command, stdout, stderr, and the exit code.
- A recognition claim needs the `row` lines (hit, miss, false_wake, reject) for both the configured-reading rule and the surface-only rule. The totals are not enough.
- Report an unrun feature with the command that was not executed. Do not mark it verified because another feature passed.
- `wake_latency_eval.py` needs macOS `say`, `afconvert`, and `swift`. Skip it and say so. It is not a stand-in for these features.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph describing the observable behavior. It then uses exactly four H2 sections in this order.

1. `Sub-features` lists short IDs with one line for each behavior.
2. `How to get to it (user POV)` lists every entry point an engineer uses here.
3. `Driving it with verify-wake` starts with `Preconditions:` and uses labeled bullets that pair each action with an exact command and observable result.
4. `Gotchas` lists traps that can waste or invalidate a verification run.

## Features

- [Reading match](reading-match.md) scores the frozen 47-row corpus with production `LeadingWake`, `EarlyWake`, and `RejectedWake`.
- [Fixture replay](fixture-replay.md) runs WakeReplay `--self-test` on the sample log and restores `DictationCore.cs`.
- [Near-miss census](near-miss.md) classifies the same sample log without swapping Core.
- [Acceptance manifest](acceptance-manifest.md) runs `wake_acceptance.py` on the synthetic pass manifest.
- [Automatic safety](automatic-safety.md) runs the three tests that keep automatic sessions from pasting or opening action URLs.
