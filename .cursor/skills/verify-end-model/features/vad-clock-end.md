# VAD clock end

Silence ends from the VAD frame clock. A stop-word recognition still in flight does not hold the timer. Speech during the grace continues the same recording.

## Sub-features

- `P0-CLOCK-1` Mac's silence guard does not read `!stopCheck.pending`. Stop-word detection still submits on the finalized utterance beside the frame loop.
- `P0-CLOCK-2` Windows `TryFinishSilence` no longer defers on `pending.Count > 0`. The open-utterance defer stays, because that flag is the segmenter hangover.
- `P0-CLOCK-6` the frame loop calls `AdvanceSpeechTo(lastLiveSpeechEnd)` while recognitions are still pending. The gate `pending.Count == 0 && session.AdvanceSpeechTo(lastLiveSpeechEnd, originalStore.Copy) is { } advanced` is gone.
- `P0-CLOCK-3` `SilenceEndsWhileRecognitionPending` exists. The recognizer is still pending when the session ends.
- `P0-CLOCK-4` that test asserts a `dictation silence: fired` line whose `pending=` count is greater than 0. The checker requires the test name, `dictation silence: fired`, and `pending=` in `Program.cs`. A fired line with `pending=0` fails the test run.
- `P0-CLOCK-5` `SpeechDuringGraceDoesNotEnd` exists. A pause shorter than `endSilenceMs` does not submit. Speech then resumes in the same session. A later full silence submits once, with `FinishReason.Silence`.

## How to get to it (user POV)

- Stop talking and wait. The recording ends on the silence timer even if recognition is still working.
- Stop talking, then start again before the timer finishes. The recording continues without a gap in the saved audio.
- Say the stop word and pause. If recognition lands before the timer, the session still ends on the stop word and the saved transcript does not contain it.

## Driving it with check.py

Preconditions:

- Doctor passes.
- `P0-CLOCK-6` is the anchor update. Removing only the `TryFinishSilence` defer leaves the frame loop waiting on `pending.Count == 0`, and a long body ends at the first stale anchor.

- **Static gates.** Run `python3 .cursor/skills/verify-end-model/check.py drive vad-clock-end`. All six items PASS.
- **Pending recognizer.** The Windows exe runs `SilenceEndsWhileRecognitionPending`. The fired silence log shows `pending=` above 0. The submission reason is `Silence`. A late stop-word result does not add a second submission and does not paste.
- **Grace.** The Windows exe runs `SpeechDuringGraceDoesNotEnd`. There is one submission, after the second silence, not after the short pause.
- **Proof.** Quote the checker lines and the two test results.

## Gotchas

- Do not bring back the pending-count defer to keep an old test on `StandaloneStop`. If a fixture now ends as `Silence`, the audio already contained a full `endSilenceMs` before the stop recognition was applied. Pace that fixture or lengthen its `endSilenceMs` so the stop word lands first. That is the new contract.
- `wordCheck.pending` is the agreement check, not the stop-word check. `P0-CLOCK-1` is the silence guard's `!stopCheck.pending`. Keep `stopCheck.submit` and `stopWordCut`.
- The open-utterance defer stays. A pause inside the hangover is not yet silence on the segmenter clock.
- `P0-CLOCK-6` is the frame-loop call that binds `advanced`. The recognition-failure call that binds `kept`, and the end-of-file silence finish, stay behind `pending.Count == 0`.
- `P0-FIELD-LATENCY` is the p95 claim. This feature does not mark it PASS.
