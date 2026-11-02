# Fixture replay

Fixture replay runs the WakeReplay self-test on `tests/fixtures/wake_log_replay_sample.log`. The tool scores the log, then checks that `scripts/wake_log_replay.sh --self-test-restore` puts `DictationCore.cs` back.

## Sub-features

- `self-test-score` checks the frozen census counts in the sample log.
- `self-test-restore` rewrites `DictationCore.cs` to two historical blobs and restores the working tree on exit.

## How to get to it (user POV)

- Run `verify.sh drive fixture-replay`.
- Do not run a second self-test in the same repo until this one exits. They share one source file.

## Driving it with verify-wake

Preconditions:

- `verify.sh doctor` prints `doctor: ok`.
- No other process is running `VoiceSwitch.WakeReplay --self-test` or `scripts/wake_log_replay.sh` without `SKIP_H2B` and `SKIP_H2D`.
- `git hash-object dotnet/VoiceSwitch.Windows.Core/DictationCore.cs` is recorded before the drive.

- **Replay.** Run `VS_RUN_ID=fixture-replay verify.sh drive fixture-replay`. Exit code 0. Evidence stdout contains `OK wake_log_replay self-test` and `in_band=False`. The drive rebuilds WakeReplay before the self-test.
- **Restore.** Run `git hash-object` on `DictationCore.cs` again. The hash matches the one recorded before the drive.
- **Failure shape.** A broken census prints `FAIL self-test:` on stderr and exits 1. There is no `OK` line to salvage.

## Gotchas

- `--self-test` is a WakeReplay flag. It is not a flag of `scripts/wake_log_replay.sh`. The wrapper's restore entry is `--self-test-restore`.
- The wrapper with neither `SKIP_H2B` nor `SKIP_H2D` checks out historical `DictationCore.cs` blobs. Use the `verify.sh` drive, which calls `dotnet exec ... --self-test` and relies on the script's EXIT trap.
- The sample log prints `in_band=False` and a non-zero `missing_fields` count. That is the fixture. Do not change the matcher to make the band true.
- One fixture-replay at a time. A second writer in `DictationCore.cs` can leave the restore trap holding the wrong bytes.
- This drive does not measure a new wake phrase. The counts are the sample log's counts. New behavior belongs in `reading-match` unless this census itself changes.
