---
name: verify-triage-execution
description: Model-check (TLA+) that a dictation the journal triage classifies as a request either runs once through its configured route or stays visible to the user, and that nothing runs for a request the speaker did not mean except through a route the user marked auto. Covers classification, the proposal window (approve, dismiss, fix kind), auto routes, the run and its record in proposals.jsonl, crash and restart. Use when a change touches docs/journal-triage-execution.md or docs/journal-triage-design.md, or the code that implements them (the proposal window, dictation.triage.routes, proposals.jsonl, the triage classifier in the app), and before claiming that triage execution is safe.
---

# Verify triage execution (TLA+)

The triage classifier (`docs/journal-triage-design.md`) turns a dictation into a verdict. The execution design (`docs/journal-triage-execution.md`) turns a request verdict into a proposal, lets the user approve, dismiss, or fix it in the menu bar, or runs it at once when its route has `auto: true`, and records the outcome in `proposals.jsonl`. `TriageExec.tla` explores every order of those steps for two utterances, with any verdict Jev could return (it can be wrong), one app crash, and a restart.

At the sync commit no code exists yet. The model checks the design. When the code lands, map each action to the code (table below) before trusting a pass.

## The two worst cases

| Worst case | Properties |
|---|---|
| 1. A request the speaker meant is never run and nobody can see it | `NoSilentDrop` |
| 2. Something runs that the speaker did not ask for | `NoWrongExecution`, `StrictNoWrongExecution`, `AtMostOnce`, `DismissedNeverRuns` |

| Property | Plain sentence | Harm if broken |
|---|---|---|
| `NoWrongExecution` | A route runs only for the kind the speaker meant, unless an auto route started it. | A wrong command, URL, or memo entry goes out. |
| `StrictNoWrongExecution` | A route runs only for the kind the speaker meant. | Same; this one also counts auto routes, so it shows what `auto: true` costs. |
| `AtMostOnce` | A route runs at most once per proposal, plus once per explicit user retry. | A memo is written twice, a request is sent twice. |
| `DismissedNeverRuns` | Nothing runs for a proposal after the user dismissed it. | The user said no and it ran anyway. |
| `NoSilentDrop` | An unjudged verdict, or a request kind with no route, never ends only in the CLI list. | A menu-only user loses the request without a trace. |

A request misread as `thought` or `filler` is classifier accuracy, not ordering. It is measured by the review / eval loop in `docs/journal-triage-design.md`, not by this model.

## Configurations

| Config | Meaning | Expected result at the sync commit |
|---|---|---|
| `design` | The execution design: `executing` is appended to `proposals.jsonl` before a route runs and restored as "実行されたか不明" after a restart (流れ 6-7), and unjudged / unrouted request rows show in the proposal window as 確認待ち (流れ 3). No route is auto. | all PASS |
| `auto` | `design` with `self_task` marked `auto: true` | `FAIL StrictNoWrongExecution` (expected: an auto route runs on a wrong verdict), the rest pass |

## Findings that shaped the design (2026-11-21, before 流れ 3, 6, 7 were added)

The first version of the design had `PreRecord = FALSE` and `MenuShowsUnsure = FALSE`. Setting both back to FALSE in `design.cfg` reproduces these.

1. `AtMostOnce`: the user approves a proposal, the route runs, the app dies before it appends "done", the restart reads the proposal as still waiting, the user approves it again (or an auto route starts it again), and the route runs a second time. Fix: append `executing` before running, and restore it as "実行されたか不明" for the user to confirm or retry.
2. `NoSilentDrop`: Jev returns `unjudged` (or a request kind with no route) for a request, and the design puts it only in the CLI list, which the menu-only user never sees. Fix: show those rows in the proposal window for review.
3. `StrictNoWrongExecution` under `auto`: Jev says `self_task` for a plain thought, the auto route runs it at once. This is the price of `auto: true`; keep it for routes whose wrong run is cheap to undo.

## Check items (in this order)

1. **Real target** (once the proposal window exists): approve a proposal whose route is a slow command, quit VoiceSwitch.app while it runs, relaunch, and see the proposal shown as "実行されたか不明", not as a fresh proposal, and see the route not run again. Then speak an unclear request and see it in the proposal window, not only in `python3 scripts/journal_triage.py` output. Until someone has seen both, a model pass is unverified on the app.
2. **Logs on every degraded path** (once code exists): the app logs one line each for `triage: <id> executing <kind>` before a route runs, `triage: <id> restored unknown` on restart, `triage: <id> no route for <kind>`, and `triage: <id> unjudged`, so a real run can tell them apart.
3. **Model**: `sh .claude/skills/verify-triage-execution/check.sh design` must print only PASS lines. `auto` must print only `FAIL StrictNoWrongExecution`; any other failure is a new finding.
4. **Mutants**: `sh .claude/skills/verify-triage-execution/mutants.sh` must print `CAUGHT` for every line. `MISSED` means a property went blind; `anchor missing` means the model changed and the mutant table in `mutants.sh` needs the new text.

## Run

```sh
sh .claude/skills/verify-triage-execution/check.sh design     # design | auto
sh .claude/skills/verify-triage-execution/mutants.sh
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` the first time, the same jar as `verify-meet-pause`). Needs Java 11+. Each config takes a few seconds (8 496 distinct states for `design`); `mutants.sh` about 30 s. `check.sh` runs one TLC per invariant and prints `PASS` / `FAIL` / `ERROR` per invariant; traces are in `${TMPDIR}/verify-triage-execution/<config>/<invariant>.out`. Run one `check.sh` or `mutants.sh` at a time: they share that folder.

Read every result with the `/statechart` skill: a failure as its shortest path in plain words, a pass as a chart of the states.

## Telling a real bug from a modeling error

Replay the trace against the design text or the code, one action per step. If a step does something the design or code cannot do (for example `Approve` on a verdict the user would not accept), the model is wrong: fix the action, not the property. The `Bounded` constraint (runs ≤ 3) only stops broken variants from exploring forever; a correct design never reaches it.

## Coverage and sync

Sync commit: eb8b65c (design documents only; no implementation yet). Move it only after items 3 and 4 pass at the new commit and the table below names the code.

| Model action | Design text (and, once written, the code) |
|---|---|
| `Classify` | design.md "判定（コードの真理値表）"; execution.md 流れ 2-3 (unjudged and unrouted rows become 確認待ち) |
| `Approve`, `Dismiss`, `Fix` | execution.md 流れ 5 (「実行」「却下」「種類を直す」) |
| `Auto` | execution.md 流れ 4 and `auto` in `dictation.triage.routes` |
| `Start`, `Run` | execution.md "実行は既存の実行部分だけを通る" (`WakeAction` open / post / command, `Sources/voice-switch/Config.swift:186`) |
| `Start` (its `PreRecord` write), `Record` | execution.md 流れ 6 |
| `Restart`, `Restore`, `Retry`, `ConfirmDone` | execution.md 流れ 7 and the `proposals.jsonl` row in 持ち主 |

When any of these change, rerun items 3 and 4, update the model if the change was intended, then move the sync commit.

## Not covered

The classifier's accuracy (Jev may return any verdict here); two app instances or two machines acting on the same proposal; the route's own failure modes (a command that fails, a URL that does not open), modeled only as "it ran"; the changelogmemo writer race with the changelog skill (execution.md 未決); Windows.
