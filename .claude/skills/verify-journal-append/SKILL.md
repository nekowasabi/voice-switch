---
name: verify-journal-append
description: Bounded-exhaustive check (real Swift code, every order of up to 4 steps) that the macOS dictation journal keeps every utterance DictationHistory accepted exactly once on disk, never rewrites bytes already written, keeps each line a whole JSON record even after a crash cut a line, keeps each record's delivery value, and logs a line whenever it falls back to the default directory; plus an exhaustive check (real script, every input combination, HTTP replaced) that scripts/journal_triage.py lists the right rows, sends each listed text (automatic ones too) to Jev once unless --no-jev or no key, sends the text alone, and takes Jev's answer above the floor before the local markers. Use when a change touches DictationJournal.open / record / write / appendLine / deleteEntry / filter / noteFailure / line in Sources/voice-switch/DictationJournal.swift, DictationHistory.add in Sources/voice-switch/WakeSafety.swift, or scripts/journal_triage.py, and before claiming a journal bug fixed.
---

# verify-journal-append

## The promise and the harm

- Promise: every utterance that `DictationHistory.add` accepts is written to the journal exactly once with its intended `delivery`, and nothing already on disk is lost.
- Promise of the triage script: it reads the journal only, and sends nothing but the utterance text. With an API key and without `--no-jev`, every listed text goes to Jev, automatic monologues included, because their tone shifts and fixed markers misread them (the user's decision, 2026-11-21). The app's own live path still never hands automatic text to Jev (README_ja.md:210); this script is a separate tool the user runs.
- Harm when it breaks: dictations vanish from the device. Issue #49 exists so that nothing is lost; #50 (digest, triage) reads only what this path wrote.
- Found at c901865: `appendLine` opened the file with `FileHandle(forWritingTo:)` and read its last byte, which fails with EBADF on a non-empty file. The first line of a day was written, the second fell back to the default directory and failed there too. Real journals held one line per day.

## Check items (run in this order)

1. **observe** — the symptom on the real target. `bash .claude/skills/verify-journal-append/run.sh observe [journal-dir]` prints line counts per day file (never the text). Default dir: `~/Library/Application Support/voice-switch/journal`. After the rebuilt app ran on a day with several dictations, that day must show more than one line. Until someone has seen that, the fix is unverified on the real app, whatever item 2 says.
2. **check** — `bash .claude/skills/verify-journal-append/run.sh check [length]` (default length 4: 2 592 sequences, about 10 s; length 5 is about 6× longer; then the triage test, 16 800 combinations, about 9 s). Exit 0 and `PASS J1`..`PASS J4`, `PASS T1`..`PASS T6` is a pass.
3. **mutants** — `bash .claude/skills/verify-journal-append/run.sh mutants` (15 mutants, about 1 min). Each mutant breaks one guard in a copy of the Swift sources or of the script and must print `CAUGHT`. A `MISSED` line means the check went blind. An `anchor missing` line means the guarded code changed: update the mutant table in run.sh before trusting item 2.

Needs `swiftc` (Xcode or Command Line Tools) and `python3` (standard library only). Nothing is written outside `${TMPDIR}/verify-journal-append` and the test's own temp directories. The triage check never opens a socket: `urlopen` is replaced, and `socket.connect` is blocked and counted.

## What the check does

`main.swift` compiles with the real `SecretLog, Config, Platform, DictationJournal, WakeSafety, Transcript` sources (module `voice_switch`). For each mode (journalDir is a directory; journalDir ends in `.jsonl`) it runs every sequence of the steps below and checks the invariants after every step.

| Step | Meaning | Code it drives |
|---|---|---|
| R / S | record an utterance on day A / day B (text has a quote and a newline; delivery takes paste, sticky, pane, action, history, null in turn) | `record` → `line` → `write` → `appendLine` |
| F | a crash cut a write: newline repair, then half a line, no newline | the state `appendLine` lines 193-197 must repair |
| B / U | the configured location breaks (its parent becomes a file) / is repaired | `write` failing → `noteFailure` → fallback in `record` |
| D | `deleteEntry` of the oldest live record | `deleteEntry` → `filter` |

| Invariant | Plain sentence |
|---|---|
| J1 | every live record is on disk exactly once, in the configured or the fallback location |
| J2 | bytes already on disk stay a prefix of the file, except in a file `deleteEntry` rewrote |
| J3 | every line is a whole JSON record with its original text and its delivery (the string, or JSON null), or a cut fragment left as it was |
| J4 | a record that landed in the fallback directory came with a `using default` log line (the silent-fallback check) |

## The triage check (T1-T6)

`tests/test_journal_triage.py` runs `journal_triage.main` once per combination of origin (手動, ウェイク, 自動, absent, unknown) × delivery (absent, null, paste, sticky, pane, action, history) × local marker hits (0, 1, 2) × `--no-jev` × API key set × `--min-confidence` (default, 0.5) × Jev answer (0.95, 0.9, 0.85, 0.6, 0.4, none, unknown choice, bad confidence, URLError, not JSON). The expected result comes from the rules, not from the script. `JOURNAL_TRIAGE=<path>` points it at a copy, which is how the script mutants run.

| Rule | Plain sentence |
|---|---|
| T1 | no Jev request with `--no-jev` or without an API key; no real connection ever |
| T2 | otherwise exactly one Jev request per listed row, whatever its origin (自動 included) and its local markers |
| T3 | a request is a POST to api.typesafe.ai whose `state` is `{"dictation": text}`; focusApp, taskchute, timestamp and endReason are not in the body or headers |
| T4 | a Jev answer at or above the floor (default 0.9) wins, `none` included; below the floor, any error, or no request: one local hit, else none |
| T5 | stderr carries no text, focusApp or taskchute |
| T6 | only delivery history, null or absent is listed, with time, focusApp.name and taskchute.name; the journal bytes do not change; broken rows are skipped and counted |

## Reading a failure

A failure prints one line: `FAIL J1 mode=dir seq=RRBR step=2(R): record 1A2B3C4D is on disk 0 times`. Replay it by hand: the letters are the steps above, the step number is where the invariant broke. Before calling it a product bug, check the step is faithful to the code (the F step once glued two cut lines together because it skipped the newline repair the real code does; that was a harness error). If the step matches the code, report the bug with that sequence. Do not loosen an invariant to make it pass.

## Scope

- Verified: the Swift journal code above, two modes, every order of 4 steps from the 6 above, one process, one thread.
- Verified: `scripts/journal_triage.py`, every combination listed above, in one process with HTTP replaced.
- Not covered: the Windows journal (`dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs`; `tests/journal/Program.cs` has `delivery-noted`, unrun on this Mac without dotnet); which delivery the handoff notes (`MacApp.swift` handoff and `transcribeToHistory`, `SuperwhisperHandoff.cs`, `LocalHistoryHandoff.cs`; read by hand only); that `DictationHistory.add` carries the note (`tests/journal/main.swift` `delivery-note` checks it); concurrent writers or two app instances; a crash in the middle of `filter`'s temp-file rename; disk full; permissions (0700/0600) and symlinks, which `tests/journal/main.swift` checks; the fallback directory itself failing; the JSON fields `focusApp` / `taskchute` (their values are not checked, only that the line parses); real Jev answers (the triage check never calls TypeSafe).
- Known behaviour, not a failure here: after a fallback, `deleteEntry` only looks at the current target, so a record written before the switch cannot be deleted through the API.

## Coverage and sync

Sync commit: 808efcc (items 2 and 3 pass there). Move it only after items 2 and 3 pass at the new commit.

| File | Functions |
|---|---|
| Sources/voice-switch/DictationJournal.swift | open, record, write, noteFailure, appendLine, deleteEntry, filter, line |
| Sources/voice-switch/WakeSafety.swift | DictationHistory.add (only that it calls record; not driven here) |
| scripts/journal_triage.py | journal_rows, classify, ask_jev, main |

When any of these change after the sync commit, rerun items 2 and 3, fix the harness if the change was intended, then move the sync commit.
