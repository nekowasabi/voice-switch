---
name: verify-pr-merge
description: Merge gate for the open voice-switch PRs #58 (secret redaction in logs), #60 (keyboard shortcut per action) and #67 (journal focusApp / TaskChute). On this Mac it trial-merges one PR into origin/main in a throwaway worktree, builds the real Swift binary from the result, drives the PR's feature through --simulate, and prints BLOCK / PASS / FAIL / MANUAL / UNVERIFIED lines plus a verdict. Use before merging any of those PRs, after the author rebases or retargets one, and when someone claims a PR is merge-ready.
---

# verify-pr-merge

The surface is the macOS `voice-switch` binary (`Sources/voice-switch`). The Windows .NET side has no host here (`dotnet` is absent on this Mac), so every Windows item prints `UNVERIFIED` and is listed in the feature file with the command to run on a Windows/WSL box.

Everything goes through `verify.sh`. Run it by absolute path. It finds the repo from its own location.

```sh
P=/Users/takets/repos/voice-switch/.claude/skills/verify-pr-merge/verify.sh
$P check <pr>        # prepare → build → drive → verdict, one call (pr: 58 60 67)
$P cleanup [<pr>]    # worktrees, builds and refs/verify/* go; evidence stays
```

Exit code of `check`: 0 `verdict: ready`, 1 `verdict: fail`, 2 `verdict: blocked`. Ready still leaves `MANUAL` and `UNVERIFIED` lines for a human.

## Launch

`$P prepare <pr>` (also the first step of `check`):

1. `git fetch origin main +pull/<pr>/head:refs/verify/pr-<pr>`. The user's branches and working tree are not touched.
2. `gh pr view` → `pr.json`. A non-OPEN state or a base other than `main` is a `BLOCK` line (ghstack PRs target `gh/cursor/<n>/base`; GitHub's merge button would land them there).
3. `git worktree add --detach ~/Library/Caches/voice-switch-pr-verify/wt/pr-<pr> origin/main`, then `git merge --no-commit --no-ff refs/verify/pr-<pr>`.
4. What gets built, written to `tree.txt`:
   - `merged`: clean merge. The only tree that can be `ready`.
   - `merged-source-only`: conflicts only outside `Sources/` and `dotnet/`. Those files take main's side; the app source is the real merge result. The `BLOCK merge` line stays.
   - `pr-head+27183c9`: conflicts in source. The PR head plus main's Swift 6.4 compile fix `27183c9` (the PRs sit on `2c28b7d`, which does not compile here). Verification scaffolding, never a merge result.
   - `pr-head`: as above but `27183c9` does not apply. Expect the build to fail on the base's errors.

`$P build <pr>` calls the worktree's own `.claude/skills/verify-voice-switch-mac/verify.sh build` with `VS_VERIFY_ROOT=~/Library/Caches/voice-switch-pr-verify/pr-<pr>/mac` (separate per PR). It is judged by whether `build/release/voice-switch` exists, because that harness pipes `swift build` into `tail` and exits 0 on a compile error. On failure it rebuilds once into `swift-build-full.txt` and quotes the first three `error` lines. Warm builds take about 1 min, a cold one about 3 min.

Ready = `PASS swift-build`. There is no long-lived process; each drive is one 40 s `--simulate` run.

## Doctor

```sh
$P doctor <pr>
```

Read-only. Worktree present and its `tree.txt`, the `BLOCK` lines so far, the mac harness doctor (binary, Kyoko voice, `--vad-selftest` incl. Whisper model at `~/Library/Application Support/voice-switch/models/`, built-from commit vs worktree HEAD), and whether `dotnet` exists.

## Drive

`$P drive <pr>`. What each PR drives and the pass lines are in [features/README.md](features/README.md). In short:

- **#58** `config.secret.json`: the wake command contains `${VS_VERIFY_MISSING_TOKEN:-s3cret-value}` and the literal value of `VS_VERIFY_API_TOKEN=vsverify-supersecret-42`, and exits 3 so `Platform.runCommand` logs `command exited 3: <command>`. Kyoko says 音声入力.
- **#67** `config.journal.json`: an automatic (no wake word) session with `superwhisperMode: vs-verify-missing` (nothing reaches Superwhisper), `journalDir` in the evidence folder and `journalTaskFile` = `<mac root>/task.txt` written by the drive. `SIMULATE_REALTIME=1`. Do not type during this run.
- **#60** `--simulate` never builds `AppDelegate`, so no hotkey is registered. The drive only checks the wiring in the tree and prints `MANUAL as-live-press`.

## Evidence

`~/Library/Caches/voice-switch-pr-verify/evidence/<run>/pr-<pr>/`:

| File | Contents |
|---|---|
| `result.txt` | every BLOCK / PASS / FAIL / MANUAL / UNVERIFIED line, the proof summary |
| `pr.json`, `main.txt`, `tree.txt`, `merge.txt`, `conflicts.txt` | what was merged onto which main and how |
| `build.txt`, `swift-build-full.txt` | build output (the second only on failure) |
| `stdout.txt`, `drive.txt`, `config.json` | the `--simulate` runtime log, the harness summary, the exact config the run used |
| `task.txt` | #67 only: the task file the run read |
| `journal.jsonl` | #67 only: the journal lines the run wrote |

The raw run (config, input.aiff, markers, exit code) is under `pr-<pr>/mac/evidence/<run>/` and is deleted by cleanup; the copies above are not.

Proof standards:

- A `merged` or `merged-source-only` tree proves the merge result; `pr-head*` proves only the PR's own code. Say which.
- Quote the log line, not just PASS. #58: `command exited 3: touch …/wake.marker; : $VS_VERIFY_MISSING_TOKEN $VS_VERIFY_API_TOKEN; exit 3`.
- Side effects count: #58 needs `wake.marker`, #67 needs a line in `journal.jsonl`.
- `BLOCK` lines are merge blockers, not test noise. Do not call a PR mergeable while one is printed.

## Cleanup

```sh
$P cleanup        # all of 58 60 67
$P cleanup 58
```

Runs the mac harness cleanup (kills only its own unfinished `--simulate` PID), `git worktree remove --force` on the PR worktree, deletes `refs/verify/pr-<pr>` and `pr-<pr>/` (build, fixtures, raw runs), then `git worktree prune`. `evidence/` stays; delete `~/Library/Caches/voice-switch-pr-verify/evidence/<run>` by hand when done. Run cleanup after every failed attempt too.

## Isolation

Each PR has its own worktree, build path and fixtures, so two PRs can be checked one after the other without rebuilding the other. Do not run two `drive`s at the same moment: both use the Apple Speech recognizer, and the #67 drive reads the real keyboard/mouse idle time. Nothing touches `~/Applications/VoiceSwitch.app`, its config, or the microphone (never use `make` here; it reinstalls and relaunches the user's app).
