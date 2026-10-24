---
name: verify-end-model
description: Check voice-switch issue 48 Phase 0 (save-only ends, VAD silence clock, 1500 ms default, end sound) with the repo harness. Use when a change to Mac or Windows dictation ending must be shown against the issue 48 checklist, including NoiseBenchmark expectations and the Mac/Windows parity fixture.
---

# verify-end-model

voice-switch listens on a microphone and ends a recording on silence, a stop word, or a finish control. Issue 48 Phase 0 makes those three ends save-only, runs the silence timer on the VAD frame clock, unifies `dictation.endSilenceMs` at 1500, and plays the end sound on silence. The user-facing surface is the Mac menu-bar app (`Sources/voice-switch/MacApp.swift`) and the Windows tray app (`dotnet/VoiceSwitch.Windows/DictationRuntime.cs`). This machine has neither microphone path. The harness is `check.py` in this directory, plus `python3 tests/parity/run_parity.py` and the Windows behavior exe.

Other surfaces stay on their own skills. `.claude/skills/verify-voice-switch` drives `voice-switch.exe` from WSL. `.claude/skills/verify-voice-switch-mac` drives the Mac binary. Do not claim those runs from this skill.

The checklist is `items.json`. One line per item is the scoreboard. Required items must PASS. `UNVERIFIED` and `DEFERRED` are not passes.

## Launch

There is no server. From the repo root:

```sh
python3 .cursor/skills/verify-end-model/check.py doctor
```

Ready means that command prints `doctor PASS`. It does not build the apps. Building is part of Drive, and only for the harnesses named there.

## Doctor

`check.py doctor` checks that `Sources/voice-switch/MacApp.swift` and `dotnet/VoiceSwitch.Windows/DictationRuntime.cs` exist and that `items.json` parses. Run it again when a check fails for a missing file rather than a wrong value.

## Drive

```sh
python3 .cursor/skills/verify-end-model/check.py drive citation-drift
python3 .cursor/skills/verify-end-model/check.py drive
python3 tests/parity/run_parity.py
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --ignore-failed-sources
```

`drive` with no feature runs every item. `drive <feature>` runs one feature from `features/README.md`. The feature id is the file name without `.md`.

`check.py` is a source and JSON judge. It does not compile. After it is green, the parity script and the Windows behavior exe are the run that executes the new tests. Parity compiles `VoiceSwitch.Windows.Core` and, when `swiftc` exists, the pure Swift harness. The Windows exe is `net8.0-windows` with `EnableWindowsTargeting`. If `dotnet` cannot start it, the item that only the exe can prove stays `UNVERIFIED` in the PR. Do not mark it PASS.

The real microphone apps are not launched here. A green scoreboard does not prove a Mac or Windows listening session.

## Evidence

Each `drive` writes `/tmp/voice-switch-verify-end-model/evidence/<feature>.txt` (or `all.txt`). Quote those lines in the PR. Also keep the parity stdout and the Windows exe stdout when they ran.

Proof standards:

- A required item PASSes only when its `kind` matches the tree. A missing test name is a failure even if the production change looks right.
- `DRIFT-1` checks the committed snapshot in `drift.md`. Do not edit that snapshot to match a later tree. It records main before the patch.
- Device items (`P0-FIELD-*`) stay `UNVERIFIED`. The unit stand-ins do not promote them to PASS.
- Phase 1 and Phase 2 stay `DEFERRED` until a commit actually lands them.
- The Windows exe and the Mac app are production boundaries this skill does not cross. Say so.

## Cleanup

```sh
rm -rf /tmp/voice-switch-verify-end-model/scratch
```

That directory is unused scratch. Evidence under `/tmp/voice-switch-verify-end-model/evidence` stays. This skill starts no process, so there is nothing to kill. Do not kill `voice-switch` by name.

## Helpers

`check.py` is the only helper. It is executable. Invocation is the Drive commands above. Kinds in `items.json` are `contains_all`, `absent`, `present`, `json_equals`, `device`, and `deferred`.

`json_equals` strips `//` comments that sit outside strings before it parses. `config.example.windows.json` uses those comments. A missing file is a FAIL for `contains_all`, `present`, `absent`, and `json_equals`. `absent` FAILs when its path is missing.

Keep the map honest with `/maintain-verification-skill` when end reasons or the sample configs change.
