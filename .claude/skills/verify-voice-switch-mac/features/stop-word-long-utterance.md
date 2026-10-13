# Stop word at the end of a long utterance

During a dictation, saying the stop word right after a long sentence, without a pause in between, ends the dictation and keeps the stop word out of the text. Today an utterance longer than `maxSeconds` (2.5 s) is never handed to the stop check (`Segmenter.swift`, the `skipping` branch returns no `.utterance`), so the stop word is recorded as text. This file is the acceptance check for the fix that checks the tail of every utterance, long ones included, with the suffix match from PR #31.

## Sub-features

- `stop-long-tail` stops when a long utterance ends with the stop word (the case the fix adds).
- `stop-short-pause` stops when a short pause separates the stop word (works today, must keep working).
- `stop-word-continued` does not stop when speech continues after the stop word inside one utterance.
- `stop-word-embedded-long` does not stop when the stop word sits in the middle of a long utterance.

## How to get to it (user POV)

- While a dictation is recording, keep talking and finish with `入力ストップ`, then stop talking.

## Driving it with verify.sh

Preconditions:

- `$V build` on the tree under test and `$V doctor` passes.
- The user agreed to runs that touch their Superwhisper. Each run that records switches Superwhisper to the `input` mode (auto-paste off), adds one history entry, and restores the previous mode. Nothing is pasted.
- Superwhisper has a mode with key `input` and `autoPaste: false` (`~/Documents/superwhisper/modes`). Otherwise edit `superwhisperMode` in a scratchpad copy of `config.stop-auto.json`.
- Do not touch the keyboard or mouse during a run. The session starts only after 1 s of input idle (`autoIdle`, `idleMs: 1000`).

Make the fixtures once.

```sh
$V say long-tail "今日は天気が良いので午後から公園まで散歩に行こうと思っています入力ストップ [[slnc 3000]]"
$V say short-pause "今日は天気が良いので午後から公園まで散歩に行こうと思っています [[slnc 700]] 入力ストップ [[slnc 3000]]"
$V say continued "今日はいい天気です [[slnc 700]] 入力ストップですと言いました [[slnc 3000]]"
$V say embedded-long "入力ストップという言葉を使って今日の予定を長めに説明しています [[slnc 3000]]"
```

Run each with the stop config.

```sh
C=/Users/ttakeda/repos/voice-switch/.claude/skills/verify-voice-switch-mac/config.stop-auto.json
VS_RUN_ID=<id> $V run long-tail $C
```

Expected lines in `stdout.txt` after `dictation session: automatic rule=auto_b`:

| Fixture | Expected (after the fix) | Today on main |
|---|---|---|
| `long-tail` | `dictation finished by stop word` | `dictation ended by 1200 ms silence` |
| `short-pause` | `dictation finished by stop word` | same |
| `continued` | `dictation ended by 1200 ms silence` | same |
| `embedded-long` | `dictation ended by 1200 ms silence` | same |

- **Proof of the stop.** Quote the `dictation session: automatic` line and the `finished by stop word` or `ended by` line in order.
- **Proof the stop word is out of the text.** Find the Superwhisper history entry whose `meta.json` `datetime` matches the run's `superwhisper mode: input` line (within a second or two) and whose `modeName` is `input-voice-switch`. Its `result` must not end with `入力ストップ`. Copy that `meta.json` into the evidence folder as `superwhisper-meta.json`. Do not take the newest entry blindly. The user's own VoiceSwitch.app writes entries into the same mode while it runs.
- Run with `SIMULATE_REALTIME=1`. Otherwise the 40 s `--simulate` window can close before Superwhisper returns.

Result after the fix (2026-10-13, evidence `stop-fix`):

| Fixture | End line | Superwhisper `result` |
|---|---|---|
| `long-tail` | `dictation finished by stop word` | `きょうはてんきがよいのでごごからこうえんまでさんぽにいこうとおもっています。` |
| `short-pause` | `dictation finished by stop word` | `きょうは、天気がよいので、午後から、公園まで散歩に行こうと思っています。` |
| `continued` | `dictation ended by 1200 ms silence` | `ストップです」といいました。` |
| `embedded-long` | `dictation ended by 1200 ms silence` | `を使って今日の予定を長めに説明しています。` |

## Gotchas

- `noWakeWord` suppresses `heard:` and `dictating:` lines, so the transcript is not in `stdout.txt`. Use Superwhisper's `meta.json` for the text.
- The automatic path stands in for a wake-word dictation because it never pastes. The stop check (`isStopWord`) is the same code for both. A wake-word run would paste into the frontmost app.
- `[[slnc 3000]]` at the end is needed. Without trailing silence the session runs to the end of the file and ends by silence anyway, which hides a missed stop.
- Kyoko must say the sentence without an internal pause longer than `hangoverMs` (200 ms) for `long-tail` to be one utterance. Check `seconds` in `afinfo` of the fixture (over 2.5 s) and, after the fix, the log of the tail check.
- Baseline on main (2026-10-13, evidence `stop-baseline` and `stop-baseline-rt`): `long-tail` ended by `1200 ms silence after 4320 ms of audio`, so the stop word was recorded. The `superwhisper-meta.json` saved there (`入力ストップ。`) was the newest entry taken blindly and may belong to another recording.
- The automatic session can open after the start of the phrase. `continued` and `embedded-long` lost their opening words in the fixed run too. That is a separate open issue. Judge this feature by the end line and by whether the result ends with `入力ストップ`, not by the full sentence.
- A missing `dictation session: automatic` line means the run never started a session (input not idle, or Superwhisper busy). That run proves nothing; rerun.
