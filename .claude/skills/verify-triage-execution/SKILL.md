---
name: verify-triage-execution
description: Model-check (TLA+) the journal triage send path of plan A (docs/journal-triage-execution.md) - a sure request waits a grace period in まもなく送る and then goes out on its own, an exception waits in 確認が要るもの for the user - so that nothing goes out without the user or a full grace period, a heavy route goes out on its own only after the extra Jev check, a cancelled request never goes out, nothing goes out twice across a crash or a failed send, a request goes only to the candidate the user chose (or, untouched, to the one shown when its grace period ran out), and an unsure, unrouted or failed request never ends where a menu user cannot see it. Use when a change touches docs/journal-triage-execution.md or docs/journal-triage-design.md, or the code that implements them (the 依頼 window, dictation.triage.routes and graceSeconds, the exception rules, proposals.jsonl, the triage classifier in the app, the Jev request in TriageRequest.swift, scripts/memo-inbox-add, scripts/mail-send), and before claiming that triage sending is safe.
---

# Verify triage sending (TLA+)

The triage classifier (`docs/journal-triage-design.md`) turns a dictation into a verdict. Plan A of the execution design (`docs/journal-triage-execution.md`) files a request either in まもなく送る (sent on its own when its grace period runs out, unless cancelled) or, when an exception rule fires, in 確認が要るもの (sent only when the user presses a button). `TriageExec.tla` explores every order of those steps for two utterances, with any verdict Jev could return (it can be wrong), any answer of the extra check for a heavy route, a kind with two candidate destinations (the first is the default; the user can pick the other on 確認が要るもの or switch to it in まもなく送る), a route that succeeds or fails, one app crash, and a restart.

The macOS code implements the model as a pure reducer, `TriageExecution.reduce` in `Sources/voice-switch/TriageExecution.swift`, one case per model action. `tests/triage/run.sh` checks that the reducer takes the model's key steps, rule by rule (check item 5). `TriageService` (classification, exception rules, `proposals.jsonl`, the grace timer, the routes) drives the reducer; `verify-voice-switch-mac/triage.py` runs it in the built binary (check item 2).

## The two worst cases

| Worst case | Properties |
|---|---|
| 1. A request the speaker meant is never sent and nobody can see it | `NoSilentDrop` |
| 2. Something goes out that the speaker did not ask for | `NoSendWithoutChance`, `NoWrongExecution`, `NoWrongDestination`, `HeavyNeedsVerify`, `DismissedNeverRuns`, `AtMostOnce` |

| Property | Plain sentence | Harm if broken |
|---|---|---|
| `NoSendWithoutChance` | Nothing goes out unless the user pressed a button or it sat in まもなく送る for the whole grace period. | A request goes out before anyone could stop it. |
| `NoWrongExecution` | A route runs for something the speaker did not mean only after a grace period nobody stopped. | A wrong command, URL, or memo goes out with no chance to cancel. |
| `NoWrongDestination` | A request goes to the candidate stored for it. That differs from the candidate the speaker meant only after a grace period in which nobody touched the row; once the user chose a candidate (a switch in まもなく送る, [<name> に送る], a kind button), only that one gets it. | The request reaches Muse when the user picked Grok Bot, or the default after the user switched away from it. |
| `HeavyNeedsVerify` | A heavy route goes out on its own only after the extra Jev check agreed. | An agent or outward request goes out on one wrong verdict. |
| `DismissedNeverRuns` | Nothing goes out after the user pressed [取消] or [送らない]. | The user said no and it went out anyway. |
| `AtMostOnce` | A request goes out at most once, plus once per explicit [やり直す] (after 送ったか不明 or 送信に失敗). A failed attempt counts as going out. | A memo is written twice, a request is sent twice. |
| `NoSilentDrop` | An unjudged verdict, or a request kind with no route, never ends only in the CLI list; a request whose route failed stays in 確認が要るもの (also after a restart) until the user presses [やり直す] or [送らない]. | A menu-only user loses the request without a trace. |
| `StrictNoWrongExecution` (`risk.cfg`) | A route never runs for something the speaker did not mean. | Expected to fail: this is what plan A costs. A confident wrong verdict on a light route goes out when nobody looks during the grace period. |

A request misread as `thought` or `filler` is classifier accuracy, measured by the review / eval loop in `docs/journal-triage-design.md`, not by this model.

## Configurations

| Config | Meaning | Expected result at the sync commit |
|---|---|---|
| `design` | Plan A: `look_up` with two candidates (the default heavy, the second light), `self_task` with one light candidate; 送信中 is recorded before a route runs; unsure and unrouted rows go to 確認が要るもの | all 7 PASS (463 779 distinct states) |
| `risk` | The same model, checking `StrictNoWrongExecution` only | `FAIL StrictNoWrongExecution` and nothing else |

## History

1. 2026-11-21, approval-only design: `AtMostOnce` failed (approve, the route runs, the app dies before recording it, the restart shows it as waiting, it runs again) and `NoSilentDrop` failed (unjudged rows stayed in the CLI list). The design gained 送信中 before running and 確認が要るもの for unsure rows. Setting `PreRecord` or `MenuShowsUnsure` to FALSE in `design.cfg` reproduces these.
2. 2026-11-22, plan A: approval became the exception path; the grace period, the extra check for heavy routes, and `NoSendWithoutChance` / `HeavyNeedsVerify` were added.
3. 2026-11-22, failed sends: `Run` always succeeded, so 流れ 6's 失敗 had no model. `Fail` (the route's exit code, HTTP status or `open` result says it failed) files the row as failed; `Retry` and `Dismiss` take it from there, and `NoSilentDrop` gained its second clause.
4. 2026-11-22, candidate destinations (execution.md 作業依頼と送り先の候補): `route`, `runRoute`, the speaker's `intentRoute` and `SwitchDestination` were added, and `SendNow` / `Fix` pick a candidate. `NoWrongDestination` as first written let any graced send go anywhere, so a switch the app ignored (`switch-ignored` in `mutants.sh`) passed it: the row still showed the default and its grace period ran out. The check-only variable `picked` (the user chose a candidate) now makes that a failure.

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
   9. Add `"computer_change": {"command": "exit 3", "heavy": false}` to `routes`, relaunch, say a request to change the PC and press [送る]. Expect it under 確認が要るもの with 送信に失敗（command exit 3） and [やり直す] [送らない], and the icon count up by one. Quit and relaunch: the row is still there. Press [送らない]; it goes away.
   10. `~/Library/Logs/voice-switch.log` has the item 2 lines for these ids and no dictated text in them.
   11. Make `delegate` an array of two light candidates, `[{"name": "A", "command": "cat >> /tmp/vs-triage-a.txt", "heavy": false}, {"name": "B", "command": "cat >> /tmp/vs-triage-b.txt", "heavy": false}]`, and relaunch. Say a request to a bot (for example 案内ボットに会議室を予約してもらって). On 確認が要るもの expect [A に送る] and [B に送る]; press [B に送る]: only `/tmp/vs-triage-b.txt` gains it. After five sends to each and a 61 s wait, say another: まもなく送る shows A and 送り先を変更; pick B. Only the B file gains it when the grace period ends, also when you quit and relaunch inside the grace period.
2. **Logs on every degraded path, in the built binary**: `VS_BIN=<binary> python3 .claude/skills/verify-voice-switch-mac/triage.py` must print only PASS lines. It runs `--simulate - --triage-fixture` with an isolated HOME and VOICE_SWITCH_CONFIG, the Jev keys removed and Jev replaced by fixed answers, and counts route starts in files. Its runs show each line once: `triage: <id> pending <kind> until <time>` (sure request), `triage: <id> exception <reason>` (unsure request), `triage: <id> verify <route> <probability>` (heavy route), `triage: <id> sending <kind>` before a route runs, `triage: <id> restored unknown` on relaunch after a SIGKILL during a send, and `triage: <id> failed <kind> <reason>` when the route fails (scenario (e): the route is the real `scripts/memo-inbox-add` pointed at an inbox whose folder is a file, so it exits 1 and the line ends `command exit 1`; the row's last `proposals.jsonl` stage is `failed` with that `failureReason`, and neither the rest of the run nor a relaunch with the inbox writable starts the route again). The reasons are fixed words; the script fails when any line of a run's stdout contains a text that run dictated. Scenario `destinations` gives `delegate` three candidates and checks (g) [B に送る] on 確認が要るもの reaches B only, (h) a switch to B in まもなく送る reaches B only after B's grace period, (f) an untouched request reaches the default A only, (i) a SIGKILL after the switch and a relaunch still reach B only, and (j) a switch to a candidate never sent to lands in 確認が要るもの with that candidate's warm-up reason (rules 4 and 5 count per destination). Scenario `gates` checks (k) an answer of `needs_local` 0.9 logs `triage: <id> session` and neither starts a light bot route nor writes the memo route, (l) a thought with `keep_note` 0.9 reaches `dictation.triage.memo` once and one with 0.2 does not, and (m) an unsure kind worth keeping becomes one memo and leaves review while one not worth keeping stays in review. It also checks a cancelled request never starts its route, a sure one starts it exactly once, `enabled: false` files nothing, and the user's real `proposals.jsonl`, triage cache, journals, changelogmemo and memo inbox stay untouched.
3. **Model**: `sh .claude/skills/verify-triage-execution/check.sh design` must print only PASS lines; `check.sh risk` must print only `FAIL StrictNoWrongExecution`. Any other failure is a new finding.
4. **Mutants**: `sh .claude/skills/verify-triage-execution/mutants.sh` must print `CAUGHT` for every line (17 at the sync commit; 4 for failed sends: a failed row taken off the screen or hidden by the restore, caught by `NoSilentDrop`; a failed row sent again on its own, and a retry not counted, caught by `AtMostOnce`; 6 for destinations, all caught by `NoWrongDestination`: `Run` sends the default, a graced send goes to the default, [<name> に送る] or a kind button keeps the shown candidate, a switch is ignored, a restart forgets the chosen candidate). `<NL>` in a mutant is a newline. `MISSED` means a property went blind; `anchor missing` means the model changed and the table in `mutants.sh` needs the new text.
5. **Reducer, rule by rule**: `sh tests/triage/run.sh` prints `PASS reducer 6 rules`. It drives `TriageExecution.reduce` through a sure send (送信中 written before the route runs, sent once), review for unsure / unrouted / unverified heavy requests, a cancel, a crash during a send (restored as 送ったか不明, no resend), a failed send (no resend until retry), and the user's chosen candidate. (Until 2026-11-22 a TLC dump of every transition, 5.5 MB, was replayed into the reducer; it checked the same orderings twice and had to be regenerated on every model change, so it was removed.)
6. **App-side triage against Python and the config**: `sh tests/triage/run.sh` must print seven PASS lines and no FAIL.
   - `decide` and `thresholds`: `TriageClassifier` gives every case of `tests/parity/fixtures/triage_decide.json` its `want` and reads `thresholds.json` as Python's `read_thresholds` does (15 files).
   - `request`: `TriageRequest` (`Sources/voice-switch/TriageRequest.swift`, the request body `TriageService` sends) builds, from each case of `tests/parity/fixtures/triage_state.json`, the same state Python builds from the same journal rows (`tests/test_journal_triage.py` C3 / C7 read the same file). The cases hold 60 s and 61 s, 59.6 s (sent as 59), a row at the same instant, a pasted row, another device's row, a third listed row, an empty app name and a -05:00 time. The classification request carries the seven table questions of `Resources/triage_questions.json` (with `kind_delegate`) plus the two gates `needs_local` and `keep_note`, and `model` only; the heavy-route check carries `verify_route` only and the state plus `route` {kind, destination}; no bundleId, pid, exe or task id is in either body.
   - `question hash`: the hash `TriageFiles.questionHash` gives the seven questions `TriageRequest` sends equals Python's `question_hash`, for the file's wording and for a changed one with non-ASCII, a slash and a quote.
   - `routes`: `dictation.triage.routes` loads with the five kinds (look_up, self_task, write_text, computer_change, delegate); `lookup`, `Self_task`, `memo` and an empty key fail the load; a reload with one of them keeps the previous config (wake words and routes) and logs one line with `keeping previous` that names `routes`; the next valid reload applies. One object is one candidate, an array is its candidates in order with each one's `name`, `heavy` and `graceSeconds`; an array with a candidate that has no delivery, or is not an object, fails the load.
   - `exceptions`: `TriageProposal.routeExceptions` counts 送り先の最初の5件 (sends with outcome `sent`) and 60秒以内に同じ送り先 (60 s inclusive, earlier rows only) among rows with the same `routeID`, so five sends to Muse do not warm up Grok Bot (9 cases).
   - `proposals`: `TriageFiles` reads back the last line of a row with its `routeIndex` and `routeName`, so a relaunch keeps the chosen candidate.
7. **The memo inbox has one writer**: `python3 -m unittest tests.test_memo_inbox_add` must print `OK`. `scripts/memo-inbox-add` only appends one JSON line to voice-switch's own inbox; changelogmemo is written by the changelog skill alone, which pulls new inbox lines in (execution.md "changelogmemo への取り込み"). The test checks one whole line per add with its kind and text, earlier bytes never change, 48 parallel adds each land once and whole, a line cut by a crash stays and the next line starts fresh, an unwritable inbox exits non-zero and writes nothing, bad input writes nothing, and the default path is `~/repos/changelog/voice-switch-inbox.jsonl`.
8. **Mutants of items 6, 7 and 9**: `sh .claude/skills/verify-triage-execution/code-mutants.sh` must print `CAUGHT` for every line (22): the inbox script gluing a new line onto a cut one, exiting 0 when it cannot write, and truncating the inbox; mail-send putting the text into the AppleScript source, using the whole text as the subject, exiting 0 when osascript fails, and sending without exactly one recipient; the previous rules (61 s, 3 rows, other device, pasted rows), bundleId instead of the app name, `verify_route` in the classification batch, the check without its route, a request without `kind_delegate`, a hash that escapes slashes; routes that take any key, refuse `delegate`, or read only one object; exceptions counted per kind, counting failed sends, or a repeat under 60 s only.
9. **Mail to Muse**: `python3 -m unittest tests.test_mail_send` must print `OK`. `scripts/mail-send <recipient>` sends stdin through Mail.app with `osascript`; the test puts a fake `osascript` first on `PATH` that records its AppleScript source, arguments and environment, so no mail is sent. It checks the first line is the subject and the whole text the body, a quote or `do shell script` in the text or recipient never reaches the AppleScript source (they arrive as arguments), a failing `osascript` makes a non-zero exit, and no or two recipients send nothing.

## Run

Every change (about 70 s, stops at the first failure):

```sh
sh tests/check.sh
```

Before a PR only (minutes; they prove the checks still bite and drive the real binary):

```sh
sh .claude/skills/verify-triage-execution/check.sh risk          # must print only FAIL StrictNoWrongExecution
sh .claude/skills/verify-triage-execution/mutants.sh
sh .claude/skills/verify-triage-execution/code-mutants.sh
bash .claude/skills/verify-journal-append/run.sh mutants
VS_BIN=<voice-switch binary> python3 .claude/skills/verify-voice-switch-mac/triage.py
```

Pinned TLC v1.7.4 (`tla2tools.jar`, sha256 checked, fetched with an authenticated `gh` into `~/.cache/tla2tools-v1.7.4/` the first time). Needs Java 11+. Run one `check.sh` or `mutants.sh` at a time, or give each its own `TMPDIR`.

Read every TLA+ result with the `/statechart` skill.

## Telling a real bug from a modeling error

Replay the trace against the design text or the code, one action per step. If a step does something the design or code cannot do (for example `SendNow` on a verdict the user would not accept), the model is wrong: fix the action, not the property. The `Bounded` constraint (runs ≤ 3) only stops broken variants from exploring forever; a correct design never reaches it.

## Coverage and sync

Sync commit: 6be3ff4. Move it only after items 2 to 9 pass at the new commit and the table below names the code. (Until 2026-11-22 item 7 checked `scripts/changelogmemo-add` writing changelogmemo beside the changelog skill without a shared lock; it never passed reliably, and the script was replaced by the single-writer inbox.)

| Model action | Design text | Code |
|---|---|---|
| `Classify`, `Exception`, `Shown` | design.md "判定（コードの真理値表）"; execution.md 例外になる条件 and 流れ 2-3 (`flagged` stands for rules 4-5 and an unsure answer, which only add exceptions) | `TriageExecution.reduce` case `(.said, .classify)` and `shown`; `TriageService.classify` builds `flagged` from `proposal.reasons` |
| `GraceExpire` | execution.md 流れ 4 and `graceSeconds` | case `(.pending, .graceExpire)`; `TriageService.armTimer` |
| `SendNow`, `Fix`, `Dismiss` | execution.md 画面 ([すぐ送る], [<name> に送る], the kind buttons, [取消], [送らない]) and 流れ 5; 作業依頼と送り先の候補 流れ 2 | cases `sendNow`, `fix` (both take the candidate index), `dismiss`; `TriageService.send(_:as:routeIndex:)` / `dismiss`, `TriagePanel` |
| `SwitchDestination`, `Candidate`, `HeavyAt` | 作業依頼と送り先の候補 流れ 3 (猶予の間なら別の候補に替えられる、猶予が過ぎたら表示中の送り先に送る) and 5 (例外は送り先ごと) | case `(.pending, .switchDestination)` and `Rules.contains(_:at:)` / `isHeavy(_:at:)`; `TriageService.switchDestination` recomputes the new candidate's exceptions; `TriagePanel` 送り先を変更 |
| `Start` (its `PreRecord` write), `Run`, `Record` | execution.md 流れ 6 and "送るのは既存の実行部分だけを通る" | cases `(.approved, .start)` (persist 送信中, then the `execute` effect), `run`, `record`; `TriageService.execute` calls `executeWakeAction` |
| `Restart`, `Restore`, `Retry`, `ConfirmDone` | execution.md 流れ 7 and the `proposals.jsonl` row in 持ち主 | `restore`, case `.restart`, `retry`, `confirmDone`; `TriageService.start` reads `proposals.jsonl` (`TriageFiles.loadProposals`) |
| `Fail` | execution.md 流れ 6 (失敗) and 流れ 7 (最後の行が「失敗」なら確認に戻す) | case `(.executing, .fail)` and `retry` / `dismiss` from `.failed`; `TriageService.execute` passes `executeWakeAction`'s `onFailure` reason (`Platform.runCommand`: start error or exit status; `postAction`: HTTP status or no response; `Platform.runURLProcess`: the `open` result) to `.fail` and logs `triage: <id> failed <kind> <reason>`; `TriageService.review` lists `.failed`; `TriagePanel` shows 送信に失敗（理由）; `TriageFiles.loadProposals` reads an older `done` + `outcome: failed` row as `failed` |
| `Crash`, `Done` | (model only) | `.crash` and `.done` exist so the reducer takes every model step; the app never sends them |

Code with no model action, and the item that checks it:

| Code | Design text | Item |
|---|---|---|
| `TriageRequest` (`Sources/voice-switch/TriageRequest.swift`): the six-question body, `previous`, the heavy-route `verify_route` body | design.md "Jev への問い" and "状態" | 6 (`request`, `question hash`), 8 |
| `TriageSettings.init(from:)` in `Config.swift`: `dictation.triage.routes` takes the five kinds only, each one object or an array of candidates; `ConfigFile.reloadIfChanged` keeps the previous config | execution.md 設定 and 作業依頼と送り先の候補 設定 | 6 (`routes`), 8 |
| `TriageProposal.routeExceptions` in `TriageFiles.swift`: rules 4 and 5 per `routeID`; `TriageProposal.routeIndex` / `routeName` in `proposals.jsonl` | execution.md 例外になる条件; 作業依頼と送り先の候補 流れ 4-5 | 6 (`exceptions`, `proposals`), 8; 2 (`destinations` (i), (j)) |
| `scripts/mail-send`: Muse by mail through Mail.app, text passed to `osascript` as arguments | 作業依頼と送り先の候補 決めたこと 3 | 9, 8 |
| `TriageService.Simulation` `action: switch` and `routeIndex` (only under `--triage-fixture`) | (test entry point) | 2 (`destinations`) |
| `scripts/memo-inbox-add`: one JSON line appended to voice-switch's own inbox; non-zero exit when it cannot write | execution.md "changelogmemo への取り込み" | 7, 8 |
| The failure path in the binary: a non-zero exit from the route to `failed` in `proposals.jsonl`, the log line, no resend after a relaunch | execution.md 流れ 6-7 | 2 (scenario (e)) |

When any of these change, rerun items 2 to 9, update the model if the change was intended, then move the sync commit.

## Not covered

The classifier's accuracy (measured separately with `scripts/triage_gate_eval.py <labels>`; Jev may return any verdict here, and the extra check may also be wrong); two app instances or two machines acting on the same request; a route that fails after it did part of its work (modeled as failed; [やり直す] may repeat that part); which rows `TriageService.review` puts in 確認が要るもの outside the run of triage.py (item 1 only); real time (the grace period is one step that can happen at any point); the window and the icon (item 1 only; the candidate buttons and 送り先を変更 included); a real mail through Mail.app (`mail-send` is checked with a fake `osascript`, and Muse's handling of the mail is outside voice-switch); a heavy candidate other than the first (the model's `HeavyAt` makes only candidate 0 heavy; `TriageService.switchDestination` flags any heavy candidate); more than two candidates in the model (triage.py runs three); the changelog skill's import of the inbox (a skill instruction, not code); Windows.
