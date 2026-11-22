---
name: verify-journal-append
description: Bounded-exhaustive check (real Swift code, every order of up to 4 steps) that the macOS dictation journal keeps every utterance DictationHistory accepted exactly once on disk, never rewrites bytes already written, keeps each line a whole JSON record even after a crash cut a line, keeps each record's delivery value, and logs a line whenever it falls back to the default directory; plus an exhaustive check (real script, every input combination, HTTP replaced) that the triage CLI scripts/journal_triage.py lists the right rows, decides each row by the truth table of docs/journal-triage-design.md (never filler on an error or without Jev), sends only the table's state with the right previous utterances, reuses cached answers until the question wording changes, drops cache lines of deleted ids, and labels, evaluates and tunes thresholds offline. Use when a change touches DictationJournal.open / record / write / appendLine / deleteEntry / filter / noteFailure / line in Sources/voice-switch/DictationJournal.swift, DictationHistory.add in Sources/voice-switch/WakeSafety.swift, scripts/journal_triage.py, Resources/triage_questions.json, tests/parity/fixtures/triage_decide.json or triage_state.json, and before claiming a journal or triage bug fixed.
---

# verify-journal-append

## The promise and the harm

- Promise: every utterance that `DictationHistory.add` accepts is written to the journal exactly once with its intended `delivery`, and nothing already on disk is lost.
- Promise of the triage CLI: it reads the journal only and never rewrites it. With an API key and without `--no-jev`, every listed text without a cached answer goes to Jev, automatic monologues included (the user's decision, 2026-11-21), with the state of docs/journal-triage-design.md and nothing more: the text, how the recording started, the app and task names, the time, and up to 2 earlier utterances. Nothing that cannot be trusted becomes `filler`, which the list hides. The app's own live path still never hands automatic text to Jev (README_ja.md:210); this CLI is a separate tool an agent runs.
- Harm when it breaks: dictations vanish from the device. Issue #49 exists so that nothing is lost; #50 (digest, triage) reads only what this path wrote. A triage bug hides a request as filler, or sends more of the user's context than they agreed to.
- Found at c901865: `appendLine` opened the file with `FileHandle(forWritingTo:)` and read its last byte, which fails with EBADF on a non-empty file. The first line of a day was written, the second fell back to the default directory and failed there too. Real journals held one line per day.

## Check items (run in this order)

1. **observe** — the symptom on the real target. `bash .claude/skills/verify-journal-append/run.sh observe [journal-dir]` prints line counts per day file (never the text). Default dir: `~/Library/Application Support/voice-switch/journal`. After the rebuilt app ran on a day with several dictations, that day must show more than one line. Until someone has seen that, the fix is unverified on the real app, whatever item 2 says.
2. **check** — `bash .claude/skills/verify-journal-append/run.sh check [length]` (default length 4: 2 592 sequences, about 10 s; length 5 is about 6× longer; then the triage test, 19 tests, about 7 s). Exit 0 and `PASS J1`..`PASS J4`, `PASS T1`, `PASS T6`, `PASS C1`..`PASS C10` is a pass.
3. **mutants** — `bash .claude/skills/verify-journal-append/run.sh mutants` (29 mutants, about 3 min). Each mutant breaks one guard in a copy of the Swift sources or of the script and must print `CAUGHT`. A script mutant runs in a copy of `scripts/` and `Resources/triage_questions.json` with the same layout. A `MISSED` line means the check went blind. An `anchor missing` line means the guarded code changed: update the mutant table in run.sh before trusting item 2.

Needs `swiftc` (Xcode or Command Line Tools) and `python3` (standard library only). Nothing is written outside `${TMPDIR}/verify-journal-append` and the test's own temp directories: the triage test sets `HOME` to a temp folder, so the real cache, config.json and journal are never read or written. The triage check never opens a socket: `urlopen` is replaced, and `socket.connect` is blocked and counted.

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

## The triage check (T1, T6, C1-C10)

`tests/test_journal_triage.py` drives `journal_triage.main` through its arguments, one exhaustive loop per group of rules, and reads back only what a caller sees: the JSON on stdout, each request to Jev, the files under the journal root and the cache, stderr. The expected destinations come from the truth table in `tests/parity/make_triage_decide.py`, written from the design, not from the script; it also writes `tests/parity/fixtures/triage_decide.json` (811 cases), which Swift and C# are to read in the next stage. `JOURNAL_TRIAGE=<path>` points the test at a copy, which is how the script mutants run.

| Loop | Combinations |
|---|---|
| decide | the fixture: 3 bands of each of the 6 questions with the edge values 0, 0.1, 0.3 / 0.31, 0.5, 0.69 / 0.7, 0.9, 1 (729), thresholds of its own per question at each edge and 0.01 on both sides (36), and each question missing or not a probability, or no answer at all (46) |
| list | origin (手動, ウェイク, 自動, absent, unknown) × local marker hits (0, 1, 2) × Jev, `--no-jev`, no key × 36 answers and 5 failures (HTTP error, not JSON, a question missing, a string, above 1); delivery (7 values) × origin × key |
| state | origin × app present or not × task present or not × `--no-context` × an earlier utterance or none; every subset of up to 4 earlier rows from: same device and listed at 0, 1, 59, 60, 61 seconds, a pasted row and another device's row at 1 and 60 seconds; one across midnight |
| cache | every sequence of 4 steps from: append a row, delete the oldest row, run, run with the network down, change the question wording, run `--no-jev` |
| labels | label each destination for a row of this device and of another; a good label, then bad destinations and an unknown id; review order |
| scoring | eval over 40 labels at default and custom thresholds with the cache deleted; the split learned from tune's counts with labels added in two orders; tune with an improvement, fewer than 30 labels, a holdout that disagrees, and a holdout that gets lighter with one more filler miss |

| Rule | Plain sentence |
|---|---|
| T1 | no Jev request with `--no-jev` or without an API key; no real connection ever |
| T6 | only delivery history, null or absent is listed, grouped as actions, thoughts or unjudged, with time, app and task; filler is only counted; broken rows are skipped and counted; folders under the root whose name starts with a dot are not devices |
| C1 | every answer has exactly the destinations of the truth table in the fixture; at 0.7 a question is yes, at 0.3 it is no |
| C2 | `filler` only when wants_action is no and is_filler is yes; a failed request, a malformed answer, `--no-jev` or no key never give filler (the markers give `action:<kind>` or `unjudged:no-jev`) |
| C3 | a request is a POST to api.typesafe.ai with the questions of Resources/triage_questions.json and a state of utterance (text, started), context (app, task, time) and previous only; started is the origin when it is 手動, ウェイク or 自動; no bundleId, pid, exe, ids, endReason or taskchute id anywhere in the request; with `--no-context` only the utterance |
| C4 | every listed row without a cached answer is asked exactly once, whatever its origin (自動 included) and its local markers; a row with a cached answer under the same question wording is not asked again; after the wording changes it is; a failed request leaves no cache line and is asked again next run; each cache line records the wording |
| C5 | the journal bytes never change; after any run the cache holds one line per listed id that is still in the journal and nothing else |
| C6 | stderr carries no text, app or task |
| C7 | previous is from the same device, listed rows only, more than 0 and at most 60 seconds before, the 2 nearest at most; tests/parity/fixtures/triage_state.json holds shared cases that verify-triage-execution item 6 feeds to the Swift app |
| C8 | the split into tuning and holdout depends on the id only; tune proposes only from 30 labels and only when the holdout gets lighter without more filler misses; `--apply` writes the proposal to thresholds.json, which list then uses |
| C9 | eval and tune send no request and score the answers kept in the labels, at the current thresholds, with the design's weights (content to filler 10, request to thought 3, unjudged 0.5, other misses 1) |
| C10 | label appends exactly one line to `<root>/.triage/labels/<this device>-cli.jsonl` and changes no other file; a bad destination or an unknown id writes nothing; review writes nothing outside the cache and puts unjudged rows first, then answers nearest a threshold |

T2 (one request per listed row whatever its origin), T3 (the text alone) and T4 (the 0.9 floor and the markers) of the first version are replaced: T2 by C4, T3 by C3 and C7 (the user decided on 2026-11-21 to send the context), T4 by C1 and C2. T5 is now C6.

## Reading a failure

A failure prints one line: `FAIL J1 mode=dir seq=RRBR step=2(R): record 1A2B3C4D is on disk 0 times`. Replay it by hand: the letters are the steps above, the step number is where the invariant broke. Before calling it a product bug, check the step is faithful to the code (the F step once glued two cut lines together because it skipped the newline repair the real code does; that was a harness error). If the step matches the code, report the bug with that sequence. Do not loosen an invariant to make it pass.

## Scope

- Verified: the Swift journal code above, two modes, every order of 4 steps from the 6 above, one process, one thread.
- Verified: `scripts/journal_triage.py`, every combination listed above, in one process with HTTP replaced and HOME in a temp folder.
- Not covered: the Windows journal (`dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs`; `tests/journal/Program.cs` has `delivery-noted`, unrun on this Mac without dotnet); which delivery the handoff notes (`MacApp.swift` handoff and `transcribeToHistory`, `SuperwhisperHandoff.cs`, `LocalHistoryHandoff.cs`; read by hand only); that `DictationHistory.add` carries the note (`tests/journal/main.swift` `delivery-note` checks it); concurrent writers or two app instances; a crash in the middle of `filter`'s temp-file rename; disk full; permissions (0700/0600) and symlinks, which `tests/journal/main.swift` checks; the fallback directory itself failing; the JSON fields `focusApp` / `taskchute` (their values are not checked, only that the line parses); real Jev answers (the triage check never calls TypeSafe); the C# reader of `triage_decide.json` (Windows comes later; the Swift reader is `sh tests/triage/run.sh`); whether tune finds the lowest weight on the tuning labels (it is a coordinate search; on ties it takes the edge nearest the current threshold, C8); pruning the cache of days other than the one listed (`prune_other_days`); two label writers at once; the content of the eval report (only that one is written); `label --no-jev`.
- Known behaviour, not a failure here: after a fallback, `deleteEntry` only looks at the current target, so a record written before the switch cannot be deleted through the API.

## Coverage and sync

Sync commit: 3b87e20 (items 2 and 3 pass there). Move it only after items 2 and 3 pass at the new commit.

| File | Functions |
|---|---|
| Sources/voice-switch/DictationJournal.swift | open, record, write, noteFailure, appendLine, deleteEntry, filter, line |
| Sources/voice-switch/WakeSafety.swift | DictationHistory.add (only that it calls record; not driven here) |
| scripts/journal_triage.py | journal_sources, journal_rows, decide, request_state, ask_jev, local_category, classify_day, prior_entries, context_history, prune_cache, listing, review, add_label, read_labels, score, split_labels, find_thresholds, tune, main |
| Resources/triage_questions.json | the wording whose hash keys the cache; C3 checks a request carries exactly the 6 classifier questions, not `verify_route` (the app's extra check for a heavy route) |
| tests/parity/make_triage_decide.py | the truth table and tests/parity/fixtures/triage_decide.json |

When any of these change after the sync commit, rerun items 2 and 3, fix the harness if the change was intended, then move the sync commit.
