# Windows early wake: decision trail

Goal: port the Mac hillclimb winner (v3: earlyWakeMs probe, prefix-wake 150 ms rule, short-wake pause rule) to dotnet/.
The user verifies on a real Windows machine later; here only macOS-runnable checks exist.

## Facts that shaped the design
- Windows dictation mode: Segmenter -> RecognitionRequest queue (serial, cap 8, overflow exits 1) -> SAPI finite decode (fresh engine per request, ~100 ms warm per code comment) -> DictationSession.Apply -> DictationBoundaries.LeadingWake.
- Recognition is async. Mac resets the segmenter synchronously after an early wake. Windows cannot, so the closed utterance of the same audio still arrives after a probe wake.
- DictationSession awaiting branch starts the body at recognition.Source.Start when no wake is found, so that duplicate closed utterance would put the wake audio into the body.
- The ShortWakeReading rule already makes おんせい (≤4 kana) whole-closed-utterance only, so Windows did not have the Mac 音声認識 false wake for kana wakes. It would have it if the literal 音声 is a wake word (exact-text path and fusedSplit).
- Command mode (no dictation block) is SAPI grammar endpointing with no segmenter. Out of scope, same as Mac where earlyWakeMs only matters with dictation on.
- dotnet 8 SDK installed user-locally via mise. The Windows test exe builds with EnableWindowsTargeting and runs on macOS with fake recognizers: baseline 170 PASS, 0 FAIL.

## Decisions
1. Data shape: EarlyWakeMs config, RecognitionExtent.Probe, TailSilenceSamples on request and utterance, Segmenter.Probe(). One extent plus one number mirrors Mac tailSilentFrames, instead of three extents (probe / gap / pause).
2. Probes are queued only while the session is idle and no other probe is pending, so the serial SAPI queue cannot fill with probes (overflow exits the runtime).
3. A probe never rebases the floor and never uses RejectedWake or stop detection (closed-only rules stay closed-only).
4. Fuzzy reading hits (Distance > 0) never fire from a probe. Exact reading of a short wake fires from a probe only after 150 ms of tail silence (treated as a closed whole utterance at that point).
5. Awaiting-body branch clamps the body start to wakeEnd and ignores a recognition that ends at or before wakeEnd.
6. Short-wake pause rule (Mac Transcript.swift v3) applies to every extent in LeadingWake, including the fusedSplit path.
7. architect skipped: the alternatives (hangover 150 vs early probe vs both) were measured on Mac in the hillclimb; Windows mirrors the measured winner.

## Not verified here
SAPI latency per probe, SAPI word timings at a comma, live capture. All need the real Windows machine.

## Outcome (2026-10-10)
- Implemented by a delegate, reviewed by the lead. Windows tests 178 PASS / 0 FAIL / 11 SKIP (native-only), parity fail=0, Windows, Tests and WakeReplay projects build with 0 warnings.
- Review fix: the delegate allowed one probe in flight. The 150 ms probe was then dropped whenever the 60 ms probe was still decoding, which removes the prefix-wake early path. Cap raised to two (MaxPendingProbes). The new test fails with the cap at 1 and passes at 2.
- Delegate deviations accepted: the waiting-branch clamp covers every body start; a failed probe is logged and skipped instead of cancelling the session; a 、 lexeme does not count as a pause on Windows (punctuation-only words are dropped before matching).
- WakeReplay skips extent=Probe log lines, because they carry no tail silence.
- Not committed (the user commits on request).
