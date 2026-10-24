# End-model verification map

This directory is the maintained source for verifying issue 48 Phase 0. Read it before driving the checklist, then use the matching feature file.

## Baseline preconditions

- The repo root contains `Sources/voice-switch/MacApp.swift` and `dotnet/VoiceSwitch.Windows/DictationRuntime.cs`.
- `python3 .cursor/skills/verify-end-model/check.py doctor` prints `doctor PASS`.
- Drive from that repo root. Do not point the checker at another checkout.
- The microphone apps are not part of this baseline. Their absence is a reported gap, not a skipped success.

## Driving conventions

- Start with `citation-drift` so the scoreboard shows the issue's citations against the recorded main snapshot.
- Then drive the other features. A required FAIL blocks Phase 0.
- Treat every command as literal.
- `UNVERIFIED` and `DEFERRED` lines stay in the scoreboard. Do not delete them to make the run look green.
- Do not weaken a journey so a check passes. If a fixture cannot run, report the command and the missing tool.

## Proof and skip reporting

- Capture the checker line and, when a test ran, the assertion output.
- A source edit without the named test is not proof.
- Record the feature id with the evidence file.
- Report an unrun Windows or Mac path with the command that failed to start and the reason.

## Features

- [Citation drift](./citation-drift.md) records whether issue 48's cited branches match main.
- [Save-only end](./save-only-end.md) keeps silence, finish, and stop-word automatic recordings in history.
- [VAD clock end](./vad-clock-end.md) ends silence on the frame clock while a recognizer is still pending. The silence anchor follows live speech during that wait, and speech during the grace resumes the same recording.
- [Silence default and sound](./silence-default-and-sound.md) unifies `endSilenceMs` at 1500 and plays the end sound on silence.
- [Harness cases](./harness-cases.md) adds the three issue cases to NoiseBenchmark and the parity fixture, and lists the checks this machine cannot run.
