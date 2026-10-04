# Windows PR review follow-up

Cloud follow-up to PR #1's review comment, based on `c991bff` and the handoff commits `975078b` / `3366726`. No personal PC, microphone, or Superwhisper process was used. Existing changes outside the review fixes were retained.

## Review disposition

| Finding | Change / evidence | Remaining limit |
| --- | --- | --- |
| An idle stop word can start recording | Missing `stopCommand` and the known Superwhisper recording toggles are suppressed in command mode. The Windows sample no longer supplies that toggle. Decision and resident-dispatch regressions cover the change. | Arbitrary custom commands remain an operator-supplied idempotent stop contract; Windows cannot observe external recording state. |
| SAPI lexical boundaries | The native probe passes production collector output into `LeadingWake` and `StandaloneStopRange`; `--expect-leading-wake` requires a non-null body start. A later change (2026-10-04) splits a SAPI word that fuses the wake word's tail with the first body characters by character count; on 84 TTS fixtures it changed no wake hit and opened no false session. | The Windows handoff reported body start 22240 and stop range 1120..18240 samples from production collection. These results were not rerun in this Linux cloud environment. Real-speaker recognition is unverified. |
| URI paths with special characters | Paths outside the conservative raw-path character set are never launched and fail before the WAV is written. Tests assert zero launches and no owned directory. Documentation no longer claims end-to-end intake validation. | Receiver decoding, actual import, transcription, and paste remain unverified. |
| Ancestor junction rejection | Trusted ancestors may be redirected; the owned root and entries must be ordinary paths. Preflight covers every entry in the owned root before the WAV write and before the 10 minute sweep deletes anything. | Linux symlink/sentinel tests pass. Native Windows junction tests are present but skipped in cloud. This assumes trusted ancestors and cooperative access; it does not claim atomic protection against hostile concurrent filesystem replacement. |
| Second body lost while a submission is pending | Superseded by the macOS model: the runtime keeps listening, one handoff runs at a time, and a body that ends while one is in flight is dropped with `dictation dropped: previous one still in flight` (Mac `handoffBusy`). | The dropped body is lost by design, as on macOS. |

The manual lease (`--complete-handoff`, manifests, admission refusal) was later replaced by the macOS poll-then-delete: the WAV is deleted once Superwhisper writes a result, kept on `NoResult`, and swept after 10 minutes.

A cloud regression also exposed an existing race: a late non-wake recognition could let idle retention reset the next open wake utterance. Reset now requires no open utterance or an already-skipping long utterance. The regression recognizer classifies source ranges rather than request ordinals and checks the resulting body range.

## Cloud verification

Environment: Linux, .NET SDK 8.0.425, net8 targets.

- `make win-publish`: Release CLI/Tray build and framework-dependent win-x64 publish, within repository `artifacts/windows`.
- Portable regression: **101 PASS / 5 Windows-only SKIP / 0 FAIL**.
- Parity: **62 PASS / 29 explicitly allowed differences / 0 FAIL**. The additional difference is intentional removal of the unsafe default stop toggle. The SDK 8 parity harness uses standard output paths and explicitly compiles its entry point to avoid duplicate core compilation.
- Native dictation probe: Release compilation succeeded; Windows execution not performed.
- `make win-verify`, `PC=wsl make -n`, `PC=WSL make -n`, and `git diff --check` passed.
- Independent read-only reviews examined the handoff changes, lifecycle, boundaries, recovery, and test assertions; no blocking findings remain from that review.

The same multi-session PCM fixture now reaches the handoff twice for every handoff status, and the runtime exits 0.

Windows native SAPI, junctions, named-pipe behavior, real microphone input, macOS, and Superwhisper end-to-end behavior were not rerun. `swiftc` was unavailable. No external application launch, Superwhisper settings change, merge, or deployment was performed. `pstack` / `poteto-mode` skills and a checkout `.agents/skills` directory were unavailable; their specific workflows were not claimed as executed.


## Windows revalidation, 2026-10-03

The cloud Git bundle was restored and its SHA-256 and commit `0a680db345afdebb19c6310c2bdcfe3371f0238c` verified before testing. WSL .NET SDK 10.0.400 rebuilt the same net8 runtime and published Windows binaries.

- Portable regressions: **101 PASS / 5 Windows-only SKIP / 0 FAIL**.
- Native Windows regressions: **105 PASS / 1 Linux-only SKIP / 0 FAIL**. All five cloud-skipped Windows checks passed, including the owned junction and named-pipe checks. The remaining skip is the portable symlink test covered by the native junction case.
- Parity: **62 PASS / 29 allowed differences / 0 FAIL**. `win-verify` and `git diff --check` passed.
- Seven paced synthetic scenarios passed, including consecutive local-recording sessions, embedded and standalone stop words, silence, and cancellation.
- The native production collector probes ran with their predicate flags. Leading wake returned body start **22240** and standalone stop returned **1120..18240**, in absolute samples.
- The native tray lifecycle passed Start/Pause/Resume, valid and invalid Reload, Quit, IPC ownership, and child-process cleanup checks.

A read-only review using the locally installed poteto-mode/pstack instructions through WSL Codex found no blocking implementation regression. It identified missing predicate flags in the published native probe commands; `BUILD.md` now includes those flags. The unavailable `deslop` skill was not claimed as executed.

No real microphone, Superwhisper launch/settings, production deployment, or macOS execution was used. URI receiver decoding and actual external import/transcription/paste remain unverified. These results do not remove the documented manual-recovery and platform limitations.
