# Merge checklist per PR

One file per open PR. Each lists the merge blockers, what `verify.sh check <pr>` drives on this Mac, and what stays for a human or for a Windows host.

| PR | Feature | File | Last run (2026-11-02, origin/main 27467c4) |
|---|---|---|---|
| #58 | Secret values stay out of the log | [pr-58.md](pr-58.md) | `verdict: blocked` — tree `merged-source-only`, build and all six `sr-*` PASS |
| #60 | A keyboard shortcut per action | [pr-60.md](pr-60.md) | `verdict: blocked` — tree `pr-head+27183c9`, build PASS, live press MANUAL |
| #67 | Journal focusApp and TaskChute | [pr-67.md](pr-67.md) | `verdict: fail` — tree `pr-head`, Swift build fails until rebased |

## Merge order

1. **#58** first. Retarget to `main` (`gh pr edit 58 --base main`, or land with ghstack) and resolve `tests/cough-filter/run_policy_tests.py`.
2. **#67** is independent of the stack. Rebase onto main. It and #58 both edit `tests/journal/Program.cs`; re-run `check` on whichever lands second.
3. **#60** last. Its base contains the closed #59. Decide #59 first (see pr-60.md).

After any PR lands, `git fetch` and re-run `check` on the others: main moved, so their merge result changed.

## Common items (every PR)

- `BLOCK` none: state OPEN, base `main`, clean merge into current origin/main.
- `PASS swift-build (merged)`.
- PR's own drive lines PASS.
- Windows, on a host with .NET 8: `dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release` exit 0, and `dotnet run --project dotnet/VoiceSwitch.SharedChecks/VoiceSwitch.SharedChecks.csproj -c Release` all `PASS` (that project only exists once #58 is in).
- `python3 tests/parity/run_parity.py` and `python3 tests/url-actions/run.py` on the merged tree, with no new FAIL against main.
