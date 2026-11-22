---
name: verify-triage-execution
description: Model-check (TLA+) the journal triage send path of plan A (docs/journal-triage-execution.md) - a sure request waits a grace period in まもなく送る and then goes out on its own, an exception waits in 確認が要るもの for the user - so that nothing goes out without the user or a full grace period, a heavy route goes out on its own only after the extra Jev check, a cancelled request never goes out, nothing goes out twice across a crash, and an unsure or unrouted request never ends where a menu user cannot see it. Use when a change touches docs/journal-triage-execution.md or docs/journal-triage-design.md, or the code that implements them (the 依頼 window, dictation.triage.routes and graceSeconds, the exception rules, proposals.jsonl, the triage classifier in the app, the Jev request in TriageRequest.swift, scripts/changelogmemo-add), and before claiming that triage sending is safe.
---

# Verify triage sending (TLA+)

The triage classifier (`docs/journal-triage-design.md`) turns a dictation into a verdict. Plan A of the execution design (`docs/journal-triage-execution.md`) files a request either in まもなく送る (sent on its own when its grace period runs out, unless cancelled) or, when an exception rule fires, in 確認が要るもの (sent only when the user presses a button). `TriageExec.tla` explores every order of those steps for two utterances, with any verdict Jev could return (it can be wrong), any answer of the extra check for a heavy route, one app crash, and a restart.

The macOS code implements the model as a pure reducer, `TriageExecution.reduce` in `Sources/voice-switch/TriageExecution.swift`, one case per model action. `conform/` feeds every transition TLC finds to that reducer (check item 5), so a model pass speaks for the reducer too. `TriageService` (classification, exception rules, `proposals.jsonl`, the grace timer, the routes) drives the reducer; `verify-voice-switch-mac/triage.py` runs it in the built binary (check item 2).

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

1. **Real target** (the menu bar app; nothing else can see the window). Until someone has done every step, the window and the icon count are unverified.
   1. In `~/.config/voice-switch/config.json`, add `"triage": {"enabled": true, "graceSeconds": 30, "routes": {"self_task": {"command": "cat >> /tmp/vs-triage-sent.txt; echo >> /tmp/vs-triage-sent.txt", "heavy": false}, "write_text": {"command": "sleep 30; cat >> /tmp/vs-triage-sent.txt; echo >> /tmp/vs-triage-sent.txt", "heavy": false}}}` under `dictation`. The app reads `TYPESAFE_API_KEY` or `JEV_API_KEY` from its own environment, and an app started by `open` (as `make relaunch` does) does not see a shell's exports: run `launchctl setenv TYPESAFE_API_KEY <key>` in your own terminal first. Without a key every request is an exception with the reason Jev のキーなし, and steps 5 and 6 cannot happen. Run `make relaunch`.
   2. Open the menu bar icon and pick 依頼. Expect a window titled `voice-switch 依頼` with `自動送信を一時停止` and the headings `確認が要るもの (0)`, `まもなく送る (0)`, `送った（今日 0） ▸`.
   3. Say an unclear request such as 調査してください。 Expect it under 確認が要るもの with a reason line (for example 種類が迷い), kind buttons, [送る] and [送らない], and the icon reads `🔔 1`. Press [送らない]. The row goes away, the icon reads `🔔`, and `/tmp/vs-triage-sent.txt` does not change.
   4. Say タスクに追加、〜 five times, more than 60 s apart. Each lands in 確認が要るもの with 送り先の最初の5件. Press [送る] on each. The file gains each text once.
   5. More than 60 s later, say タスクに追加、牛乳を買う. Expect it under まもなく送る with `残り 0:30` counting down and the route name. Press [取消]. The row goes away and the file does not change, also after 30 s.
   6. Say another one and press [すぐ送る]. The file gains it at once. Say another one and wait. At 0:00 the file gains it once and the row moves under 送った.
   7. Open 送った ▸ and pick 下書き in 種類を直す. `<root>/.triage/labels/<device>-app.jsonl` (root is the folder above `journalDir`) gains a line with `"target":["action:write_text"]`.
   8. Say a 下書き request, press [送る], and pick 終了 from the menu within 30 s. Relaunch. Expect the row under 確認が要るもの with 送ったか不明 and [送ったことにする] [やり直す] [送らない]. The file gains the text once (the route outlived the app) and never again unless you press [やり直す].
   9. `~/Library/Logs/voice-switch.log` has the item 2 lines for these ids and no dictated text in them.
2. **Logs on every degraded path, in the built binary**: `VS_BIN=<binary> python3 .claude/skills/verify-voice-switch-mac/triage.py` must print only PASS lines. It runs `--simulate - --triage-fixture` with an isolated HOME and VOICE_SWITCH_CONFIG, the Jev keys removed and Jev replaced by fixed answers, and counts route starts in files. Its runs show each line once: `triage: <id> pending <kind> until <time>` (sure request), `triage: <id> exception <reason>` (unsure request), `triage: <id> verify <route> <probability>` (heavy route), `triage: <id> sending <kind>` before a route runs, and `triage: <id> restored unknown` on relaunch after a SIGKILL during a send. The reasons are fixed words; the script fails when any line of a run's stdout contains a text that run dictated. It also checks a cancelled request never starts its route, a sure one starts it exactly once, `enabled: false` files nothing, and the user's real `proposals.jsonl`, triage cache, journals and changelogmemo stay untouched.
3. **Model**: `sh .claude/skills/verify-triage-execution/check.sh design` must print only PASS lines; `check.sh risk` must print only `FAIL StrictNoWrongExecution`. Any other failure is a new finding.
4. **Mutants**: `sh .claude/skills/verify-triage-execution/mutants.sh` must print `CAUGHT` for every line (7 at the sync commit). `MISSED` means a property went blind; `anchor missing` means the model changed and the table in `mutants.sh` needs the new text.
5. **Reducer conforms to the model**: `sh .claude/skills/verify-triage-execution/conform/conform.sh check` must print `PASS accept`, `PASS reject` and `PASS coverage`. `conform.sh mutants` must print `CAUGHT` for every line of `conform/mutants.txt` (7 at the sync commit).
   - `conform/transitions.json` holds every reachable state of `design.cfg` (20 731, with the speaker's intent left out because the app cannot see it) and every transition (52 206). `conform/gen.py` writes it from TLC: `TriageExecEvents.tla` prints each step of `Next` with its arguments, and each row action again as if that row's speaker had meant any other value, since the user, not the app, chooses the kind. gen.py stops when the printed real steps differ in number from the steps `Spec` generated.
   - accept: each transition is accepted and lands on the model's next state (all fields, both rows); a line goes to `proposals.jsonl` whenever the model's `persisted` changes; a route starts only in `start`, after its 送信中 line.
   - reject: from each reachable state, every event the model cannot take (1 317 561) returns nil. `crash` at `MaxCrashes` is exempt; the bound only keeps the model finite.
   - coverage: all 11 stages and 13 events occur, so a shrunken fixture fails.
   - After any change to `TriageExec.tla` or `design.cfg`, run `conform.sh gen` (about 1 min); `check` refuses a fixture whose model or config hash differs.
6. **App-side triage against Python and the config**: `sh tests/triage/run.sh` must print five PASS lines and no FAIL.
   - `decide` and `thresholds`: `TriageClassifier` gives every case of `tests/parity/fixtures/triage_decide.json` its `want` and reads `thresholds.json` as Python's `read_thresholds` does (15 files).
   - `request`: `TriageRequest` (`Sources/voice-switch/TriageRequest.swift`, the request body `TriageService` sends) builds, from each case of `tests/parity/fixtures/triage_state.json`, the same state Python builds from the same journal rows (`tests/test_journal_triage.py` C3 / C7 read the same file). The cases hold 60 s and 61 s, 59.6 s (sent as 59), a row at the same instant, a pasted row, another device's row, a third listed row, an empty app name and a -05:00 time. The classification request carries the six questions of `Resources/triage_questions.json` and `model` only; the heavy-route check carries `verify_route` only and the state plus `route` {kind, destination}; no bundleId, pid, exe or task id is in either body.
   - `question hash`: the hash `TriageFiles.questionHash` gives the six questions `TriageRequest` sends equals Python's `question_hash`, for the file's wording and for a changed one with non-ASCII, a slash and a quote.
   - `routes`: `dictation.triage.routes` loads with the four kinds (look_up, self_task, write_text, computer_change); `lookup`, `Self_task`, `memo` and an empty key fail the load; a reload with one of them keeps the previous config (wake words and routes) and logs one line with `keeping previous` that names `routes`; the next valid reload applies.
7. **changelogmemo-add beside a writer without the lock**: `python3 -m unittest tests.test_changelogmemo_add` must print `OK`. A stand-in for the changelog skill's Edit tool reads changelogmemo, adds `* skill <n>` and replaces the whole file, without the lock:
   - edit-like (reads, replaces 2 ms later, rests 10 ms) during 60 adds: every add exits 0 and every entry of both writers is there exactly once.
   - without rest (scratchpad race.py's condition) during 20 adds: every add ends within 30 s with exit 0 or 1, no `* skill` entry is lost and no added entry is doubled. Each replacement of this writer is based on a read from before the script's last write, so no writer without the lock can keep its own entry against it; the test does not ask for that.
8. **Mutants of items 6 and 7**: `sh .claude/skills/verify-triage-execution/code-mutants.sh` must print `CAUGHT` for every line (11): the script as it was at 1b4c0f5, the script without its check after publishing, the previous rules (61 s, 3 rows, other device, pasted rows), bundleId instead of the app name, `verify_route` in the classification batch, the check without its route, a hash that escapes slashes, and routes that take any key.

## Run

```sh
sh tests/triage/run.sh                                          # Swift truth table and thresholds reader against Python
sh .claude/skills/verify-triage-execution/check.sh design     # design | risk
sh .claude/skills/verify-triage-execution/mutants.sh
sh .claude/skills/verify-triage-execution/conform/conform.sh check   # gen | check | mutants
python3 -m unittest tests.test_changelogmemo_add                 # item 7
sh .claude/skills/verify-triage-execution/code-mutants.sh       # item 8
VS_BIN=<voice-switch binary> python3 .claude/skills/verify-voice-switch-mac/triage.py
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` the first time, the same jar as `verify-meet-pause`). Needs Java 11+. `design` takes about 15 s, `mutants.sh` about 1 min. `check.sh` runs one TLC per invariant and prints `PASS` / `FAIL` / `ERROR` per invariant; traces are in `${TMPDIR}/verify-triage-execution/<config>/<invariant>.out`. Run one `check.sh` or `mutants.sh` at a time: they share that folder, and a second run deletes the first one's files mid-run (its results then show other state counts or `ERROR`). When another session may be running them, set `TMPDIR` to a folder of your own. `conform.sh check` takes about 5 s, `conform.sh mutants` about 30 s, `triage.py` about 3 min (two 61 s waits let the warm-up and repeat rules pass).

Read every result with the `/statechart` skill: a failure as its shortest path in plain words, a pass as a chart of the states.

## Telling a real bug from a modeling error

Replay the trace against the design text or the code, one action per step. If a step does something the design or code cannot do (for example `SendNow` on a verdict the user would not accept), the model is wrong: fix the action, not the property. The `Bounded` constraint (runs ≤ 3) only stops broken variants from exploring forever; a correct design never reaches it.

## Coverage and sync

Sync commit: 792fd1c. Move it only after items 2 to 8 pass at the new commit and the table below names the code. At ba62698 items 2 to 6 and 8 pass and item 7 does not: against the writer without rest, `scripts/changelogmemo-add` never returns (its first add timed out after 30 s), so the sync commit stays at 792fd1c until item 7 passes.

| Model action | Design text | Code |
|---|---|---|
| `Classify`, `Exception`, `Shown` | design.md "判定（コードの真理値表）"; execution.md 例外になる条件 and 流れ 2-3 (`flagged` stands for rules 4-5 and an unsure answer, which only add exceptions) | `TriageExecution.reduce` case `(.said, .classify)` and `shown`; `TriageService.classify` builds `flagged` from `proposal.reasons` |
| `GraceExpire` | execution.md 流れ 4 and `graceSeconds` | case `(.pending, .graceExpire)`; `TriageService.armTimer` |
| `SendNow`, `Fix`, `Dismiss` | execution.md 画面 ([すぐ送る], [送る], the kind buttons, [取消], [送らない]) and 流れ 5 | cases `sendNow`, `fix`, `dismiss`; `TriageService.send` / `dismiss`, `TriagePanel` |
| `Start` (its `PreRecord` write), `Run`, `Record` | execution.md 流れ 6 and "送るのは既存の実行部分だけを通る" | cases `(.approved, .start)` (persist 送信中, then the `execute` effect), `run`, `record`; `TriageService.execute` calls `executeWakeAction` |
| `Restart`, `Restore`, `Retry`, `ConfirmDone` | execution.md 流れ 7 and the `proposals.jsonl` row in 持ち主 | `restore`, case `.restart`, `retry`, `confirmDone`; `TriageService.start` reads `proposals.jsonl` (`TriageFiles.loadProposals`) |
| `Crash`, `Done` | (model only) | `.crash` and `.done` exist so the reducer takes every model step; the app never sends them |

When any of these change, rerun items 2 to 5, update the model if the change was intended, then move the sync commit.

## Not covered

The classifier's accuracy (Jev may return any verdict here, and the extra check may also be wrong); two app instances or two machines acting on the same request; the route's own failure modes, modeled only as "it ran"; real time (the grace period is one step that can happen at any point); the window and the icon (item 1 only); for the changelogmemo writer race (item 7), a writer that rewrites the file in place instead of replacing it, and the real Edit tool's timing; Windows.
