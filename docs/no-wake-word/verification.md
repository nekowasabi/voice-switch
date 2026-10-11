# VM verification — 2026-10-11

Base: main `8c11668277b629dd70432ed21c6ff22ee03d8e87`.
Branch: `feat/no-wake-word`.

Run `scripts/verify_no_wake_word.sh` with .NET 8 and Swift 6 available on PATH. The complete run exited 0 in the dot VM.

- Windows app Release build: passed, no warnings or errors.
- C# behavior tests: 193 passed; 11 native Windows tests explicitly skipped.
- Cross-platform parity: 112 passed; 20 pre-existing/declared platform gaps; no failures. Pure Swift tests executed.
- URL actions: 73 checks passed, including source-copy mutation checks.
- WakeReplay self-test and overnight metrics self-test: passed.
- Wake telemetry Python test and five acceptance-evaluator tests: passed.
- Three native Windows probe/harness projects: built successfully. Their native UI/audio paths were not executed.
- Synthetic harness self-validation: passed; detects altered output WAV and sidecar hashes.
- macOS-target Swift syntax parse: passed. This is not a macOS build or Apple framework type check.

New checks cover immutable automatic provenance, disabled defaults, unknown input-idle rejection, non-kana/short-reading exclusions, exact retained synthetic PCM/preroll across repeated recordings, no raw-transcript logging in experimental mode, local-only automatic delivery, all paste-safety combinations, bounded/expired history, clear and cancellation during transcription, verified mode fallback, crash recovery, overlapping handoffs, duplicate delivery, and pre-existing Superwhisper results.

The baseline WakeReplay self-test failed because its expectations predated the existing long-reading distance-2 matcher. That failure was reproduced on the clean base. The self-test expectations were corrected; the baseline matcher was not changed by that correction.

## Open native gates

Mac build, native UI/layout, permission behavior, hotkey conflicts, microphone/Speech behavior, Windows foreground/IME/clipboard behavior, installed Superwhisper mode metadata and URL timing, and real-world false-wake/MISS results remain unverified. Follow [the native checklist](native-validation.md) and [the frozen acceptance gates](../wake-acceptance.md). Automatic rules remain disabled by default.

Manual behavior is deliberately more conservative: without a confirmed no-auto-paste Superwhisper mode, use local transcription/history. Unsupported text controls, uncertain IME state, or unsafe clipboard restoration also leave the result in history. Mac Japanese input-method sources require explicit history copy/paste.
