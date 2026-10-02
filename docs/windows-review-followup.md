# Windows PR review follow-up

Cloud follow-up to PR #1's review comment, based on `c991bff` and the handoff commits `975078b` / `3366726`. No personal PC, microphone, or Superwhisper process was used. Existing changes outside the review fixes were retained.

## Review disposition

| Finding | Change / evidence | Remaining limit |
| --- | --- | --- |
| An idle stop word can start recording | Missing `stopCommand` and the known Superwhisper recording toggles are suppressed in command mode. The Windows sample no longer supplies that toggle. Decision and resident-dispatch regressions cover the change. | Arbitrary custom commands remain an operator-supplied idempotent stop contract; Windows cannot observe external recording state. |
| SAPI lexical boundaries | The native probe passes production collector output into `LeadingWake` and `StandaloneStopRange`; `--expect-leading-wake` requires a non-null body start. No speculative proportional timing split was added. | The Windows handoff reported body start 22240 and stop range 1120..18240 samples from production collection. These results were not rerun in this Linux cloud environment. Real-speaker recognition is unverified. |
| URI paths with special characters | Paths outside the conservative raw-path character set are never launched; the complete original PCM and manifest remain as `DeferredUnsent`. Tests assert zero launches and exact decoded samples. Documentation no longer claims end-to-end intake validation. | Receiver decoding, actual import, transcription, and paste remain unverified. |
| Ancestor junction rejection | Trusted ancestors may be redirected; the owned root and entries must be ordinary paths. Preflight covers WAV, manifest, temporary state, sync, and obsolete lock paths before use. | Linux symlink/sentinel tests pass. Native Windows junction tests are present but skipped in cloud. This assumes trusted ancestors and cooperative access; it does not claim atomic protection against hostile concurrent filesystem replacement. |
| Second body lost while a submission is pending | Entry is refused before capture when retained state or synchronization prevents admission. Runtime stops/releases capture after any external handoff result. A competing already-captured second body is retained as `DeferredUnsent`, with whole-PCM equality and no second launch tested. | Manual completion/recovery remains required. Storage/lock failures can prevent durable retention and are reported as failures; recording then stops. |

Cleanup retains the manifest when WAV removal fails. Post-launch partial `.json.tmp` files are removed before the final manifest during manual completion, so cleanup failures remain retryable. Orphan files without a final manifest require explicit manual recovery/removal; admission reports their paths.

A cloud regression also exposed an existing race: a late non-wake recognition could let idle retention reset the next open wake utterance. Reset now requires no open utterance or an already-skipping long utterance. The regression recognizer classifies source ranges rather than request ordinals and checks the resulting body range.

## Cloud verification

Environment: Linux, .NET SDK 8.0.425, net8 targets.

- `make win-publish`: Release CLI/Tray build and framework-dependent win-x64 publish, within repository `artifacts/windows`.
- Portable regression: **101 PASS / 5 Windows-only SKIP / 0 FAIL**.
- Parity: **62 PASS / 29 explicitly allowed differences / 0 FAIL**. The additional difference is intentional removal of the unsafe default stop toggle. The SDK 8 parity harness uses standard output paths and explicitly compiles its entry point to avoid duplicate core compilation.
- Native dictation probe: Release compilation succeeded; Windows execution not performed.
- `make win-verify`, `PC=wsl make -n`, `PC=WSL make -n`, and `git diff --check` passed.
- Independent read-only reviews examined the handoff changes, lifecycle, boundaries, recovery, and test assertions; no blocking findings remain from that review.

The same multi-session PCM fixture produces two local-recording handoffs but only one external result before capture disposal for submitted, unsent, busy, and failed results. Tests also exercise admission in the production tray factory before microphone creation.

Windows native SAPI, junctions, named-pipe behavior, real microphone input, macOS, and Superwhisper end-to-end behavior were not rerun. `swiftc` was unavailable. No external application launch, Superwhisper settings change, merge, or deployment was performed. `pstack` / `poteto-mode` skills and a checkout `.agents/skills` directory were unavailable; their specific workflows were not claimed as executed.
