# Harness cases

The issue adds three cases next to the existing NoiseBenchmark expectations, and the same three to the Mac/Windows parity fixture. Field metrics stay unverified here.

## Sub-features

- `P0-HARNESS-1` `silence-while-recognition-pending` is in the NoiseBenchmark expectation switch, `tests/parity/fixtures/end_model.json`, `tests/parity/run_parity.py`, and `tests/parity/swift/main.swift`.
- `P0-HARNESS-2` `resume-during-grace` is in those same four files.
- `P0-HARNESS-3` `button-end` is in those same four files.
- `P0-HARNESS-4` the NoiseBenchmark switch keeps the `intentional-silence`, `late-stop-after-silence`, and `embedded-stop` arms, and adds three arms beside them. `silence-while-recognition-pending` and `resume-during-grace` are one successful handoff with `FinishReason.Silence`. `button-end` is one successful handoff with `FinishReason.FinishCommand`. Copy the existing `HarnessExpectation` arm shape.
- `P0-HARNESS-5` `tests/parity/fixtures/end_model.json` contains the three ids. Each object keeps a space after the colon in `"id"`, `"reason"`, `"pastes": false`, and `"endSound": true`.

```json
{"id": "silence-while-recognition-pending", "reason": "Silence", "pastes": false, "endSound": true}
{"id": "resume-during-grace", "reason": "Silence", "pastes": false, "endSound": true}
{"id": "button-end", "reason": "FinishCommand", "pastes": false, "endSound": true}
```

- `P0-FIELD-LATENCY`, `P0-FIELD-EARLY`, and `P0-FIELD-MIX` are device checks.
- `P1-HUD` and `P2-ROUTE` are deferred.

## How to get to it (user POV)

- The three new cases are the silence-while-waiting, the resume-during-grace, and the finish-button stories in the other feature files. This feature is the shared fixture those stories must show up in.

## Driving it with check.py

Preconditions:

- Doctor passes.
- `end_model.json` uses the three objects under `P0-HARNESS-5`. Silence and resume expect `Silence`. The button expects `FinishCommand`. Each has `"pastes": false` and `"endSound": true`.
- The parity C# harness and the Swift harness both read that file and fail if `pastes` or `reason` drifts.
- `python3 tests/parity/run_parity.py` is the execution. `swiftc` absence skips the Swift half and must be reported. It is not a PASS for the Swift file check. The Swift file check is the source `present` item. The compile is extra evidence.

- **Names.** Run `python3 .cursor/skills/verify-end-model/check.py drive harness-cases`. `P0-HARNESS-1` through `P0-HARNESS-5` PASS. The field items print `UNVERIFIED`. The phase items print `DEFERRED`.
- **Parity.** Run `python3 tests/parity/run_parity.py`. The end-model cases PASS on C#. If Swift compiled, they PASS there too.
- **Proof.** Quote the checker lines and the parity lines that name the three ids.

## Gotchas

- NoiseBenchmark's generator only emits a WAV expectation when a `*-clean.json` points at a WAV. The three ids live in the expectation switch so a later WAV picks them up. The runnable form on this machine is the parity fixture plus the Windows tests named in the other features.
- Do not retarget `intentional-silence` (handoff 0), `late-stop-after-silence` (Silence, one handoff), or `embedded-stop` (Silence, one handoff).
- A device metric reported as PASS because a unit test passed is a failed review.
- Phase 1 and Phase 2 do not fail this drive.
