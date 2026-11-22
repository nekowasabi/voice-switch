---
name: verify-triage-execution
description: Model-check (TLA+) the journal triage send path of plan A (docs/journal-triage-execution.md) - a sure request waits a grace period in まもなく送る and then goes out on its own, an exception waits in 確認が要るもの for the user - so that nothing goes out without the user or a full grace period, a heavy route goes out on its own only after the extra Jev check, a cancelled request never goes out, nothing goes out twice across a crash, and an unsure or unrouted request never ends where a menu user cannot see it. Use when a change touches docs/journal-triage-execution.md or docs/journal-triage-design.md, or the code that implements them (the 依頼 window, dictation.triage.routes and graceSeconds, the exception rules, proposals.jsonl, the triage classifier in the app), and before claiming that triage sending is safe.
---

# Verify triage sending (TLA+)

The triage classifier (`docs/journal-triage-design.md`) turns a dictation into a verdict. Plan A of the execution design (`docs/journal-triage-execution.md`) files a request either in まもなく送る (sent on its own when its grace period runs out, unless cancelled) or, when an exception rule fires, in 確認が要るもの (sent only when the user presses a button). `TriageExec.tla` explores every order of those steps for two utterances, with any verdict Jev could return (it can be wrong), any answer of the extra check for a heavy route, one app crash, and a restart.

At the sync commit no code exists yet. The model checks the design. When the code lands, map each action to the code (table below) before trusting a pass.

## The two worst cases

| Worst case | Properties |
|---|---|
| 1. A request the speaker meant is never sent and nobody can see it | `NoSilentDrop` |
| 2. Something goes out that the speaker did not ask for | `NoSendWithoutChance`, `NoWrongExecution`, `HeavyNeedsVerify`, `DismissedNeverRuns`, `AtMostOnce` |

| Property | Plain sentence | Harm if broken |
|---|---|---|
| `NoSendWithoutChance` | Nothing goes out unless the user pressed a button or it sat in まもなく送る for the whole grace period. | A request goes out before anyone could stop it. |
| `NoWrongExecution` | A route runs for something the speaker did not mean only after a grace period nobody stopped. | A wrong command, URL, or memo goes out with no chance to cancel. |
| `HeavyNeedsVerify` | A heavy route goes out on its own only after the extra Jev check agreed. | An agent or outward request goes out on one wrong verdict. |
| `DismissedNeverRuns` | Nothing goes out after the user pressed [取消] or [送らない]. | The user said no and it went out anyway. |
| `AtMostOnce` | A request goes out at most once, plus once per explicit retry. | A memo is written twice, a request is sent twice. |
| `NoSilentDrop` | An unjudged verdict, or a request kind with no route, never ends only in the CLI list. | A menu-only user loses the request without a trace. |
| `StrictNoWrongExecution` (`risk.cfg`) | A route never runs for something the speaker did not mean. | Expected to fail: this is what plan A costs. A confident wrong verdict on a light route goes out when nobody looks during the grace period. |

A request misread as `thought` or `filler` is classifier accuracy, measured by the review / eval loop in `docs/journal-triage-design.md`, not by this model.

## Configurations

| Config | Meaning | Expected result at the sync commit |
|---|---|---|
| `design` | Plan A: `look_up` heavy, `self_task` light; 送信中 is recorded before a route runs; unsure and unrouted rows go to 確認が要るもの | all PASS (74 632 distinct states) |
| `risk` | The same model, checking `StrictNoWrongExecution` only | `FAIL StrictNoWrongExecution` and nothing else |

## History

1. 2026-11-21, approval-only design: `AtMostOnce` failed (approve, the route runs, the app dies before recording it, the restart shows it as waiting, it runs again) and `NoSilentDrop` failed (unjudged rows stayed in the CLI list). The design gained 送信中 before running and 確認が要るもの for unsure rows. Setting `PreRecord` or `MenuShowsUnsure` to FALSE in `design.cfg` reproduces these.
2. 2026-11-22, plan A: approval became the exception path; the grace period, the extra check for heavy routes, and `NoSendWithoutChance` / `HeavyNeedsVerify` were added.

## Check items (in this order)

1. **Real target** (once the 依頼 window exists): speak a sure request with a slow light route, see it in まもなく送る with a countdown, cancel one and see it never sent, let another run out and see it sent once. Quit VoiceSwitch.app while a route runs, relaunch, and see 送ったか不明 in 確認が要るもの, not a fresh send. Speak an unclear request and see it in 確認が要るもの. Until someone has seen all of these, a model pass is unverified on the app.
2. **Logs on every degraded path** (once code exists): one line each for `triage: <id> pending <kind> until <time>`, `triage: <id> exception <reason>`, `triage: <id> verify <route> <probability>`, `triage: <id> sending <kind>` before a route runs, and `triage: <id> restored unknown` on restart.
3. **Model**: `sh .claude/skills/verify-triage-execution/check.sh design` must print only PASS lines; `check.sh risk` must print only `FAIL StrictNoWrongExecution`. Any other failure is a new finding.
4. **Mutants**: `sh .claude/skills/verify-triage-execution/mutants.sh` must print `CAUGHT` for every line (7 at the sync commit). `MISSED` means a property went blind; `anchor missing` means the model changed and the table in `mutants.sh` needs the new text.

## Run

```sh
sh .claude/skills/verify-triage-execution/check.sh design     # design | risk
sh .claude/skills/verify-triage-execution/mutants.sh
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` the first time, the same jar as `verify-meet-pause`). Needs Java 11+. `design` takes about 15 s, `mutants.sh` about 1 min. `check.sh` runs one TLC per invariant and prints `PASS` / `FAIL` / `ERROR` per invariant; traces are in `${TMPDIR}/verify-triage-execution/<config>/<invariant>.out`. Run one `check.sh` or `mutants.sh` at a time: they share that folder.

Read every result with the `/statechart` skill: a failure as its shortest path in plain words, a pass as a chart of the states.

## Telling a real bug from a modeling error

Replay the trace against the design text or the code, one action per step. If a step does something the design or code cannot do (for example `SendNow` on a verdict the user would not accept), the model is wrong: fix the action, not the property. The `Bounded` constraint (runs ≤ 3) only stops broken variants from exploring forever; a correct design never reaches it.

## Coverage and sync

Sync commit: e31725c (design documents only; no implementation yet). Move it only after items 3 and 4 pass at the new commit and the table below names the code.

| Model action | Design text (and, once written, the code) |
|---|---|
| `Classify`, `Exception`, `Shown` | design.md "判定（コードの真理値表）"; execution.md 例外になる条件 and 流れ 2-3 (`flagged` stands for the warm-up and repeat rules 4-5, which only add exceptions) |
| `GraceExpire` | execution.md 流れ 4 and `graceSeconds` |
| `SendNow`, `Fix`, `Dismiss` | execution.md 画面 ([すぐ送る], [送る], the kind buttons, [取消], [送らない]) and 流れ 5 |
| `Start` (its `PreRecord` write), `Run`, `Record` | execution.md 流れ 6 and "送るのは既存の実行部分だけを通る" (`WakeAction`, `Sources/voice-switch/Config.swift:186`) |
| `Restart`, `Restore`, `Retry`, `ConfirmDone` | execution.md 流れ 7 and the `proposals.jsonl` row in 持ち主 |

When any of these change, rerun items 3 and 4, update the model if the change was intended, then move the sync commit.

## Not covered

The classifier's accuracy (Jev may return any verdict here, and the extra check may also be wrong); two app instances or two machines acting on the same request; the route's own failure modes, modeled only as "it ran"; real time (the grace period is one step that can happen at any point); the changelogmemo writer race with the changelog skill (execution.md 未決); Windows.
