# Stop word at the end of a long utterance

During a dictation, saying the stop word right after a long sentence, without a pause in between, ends the dictation and keeps the stop word out of the text. An utterance longer than `maxSeconds` (2.5 s) reaches the stop check through the `.tail` event `Segmenter.swift` emits at its end, and the stop word is matched as a suffix (PR #31). Before that fix the stop word was recorded as text; the baseline notes below are kept for comparison. On the automatic path the cut recording still has to pass Silero and the Apple Speech plus Whisper agreement before Superwhisper gets it, so a `dictation dropped:` line after the stop means no history entry, though the stop itself still worked. Since 07edefc an automatic recording ended by the stop word is handed off like a wake-word dictation. It logs `dictation session: automatic ended by StandaloneStop, pasting like a wake-word dictation`, does not switch to the no-paste mode, and Superwhisper pastes into the app in front. Recordings that end on silence still go to the no-paste mode and stay in history.

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
- The user agreed to runs that touch their Superwhisper and paste. `long-tail` and `short-pause` end by the stop word, so they hand off in the active mode and paste into the frontmost app. Put a scratch TextEdit document in front first (`osascript -e 'tell application "TextEdit" to activate' -e 'tell application "TextEdit" to make new document'`), never the terminal. `continued` and `embedded-long` end on silence, so they switch to the `input` mode (auto-paste off), add one history entry, and restore the previous mode.
- To check only that `continued` and `embedded-long` do not stop, without Superwhisper, use a scratchpad copy of `config.stop-auto.json` with `superwhisperMode` set to a missing mode (see SKILL.md Drive). Do not run `long-tail` or `short-pause` with it. The stop-word handoff ignores that mode and still opens Superwhisper.
- Superwhisper has a mode with key `input` and `autoPaste: false` (`~/Documents/superwhisper/modes`). Otherwise edit `superwhisperMode` in a scratchpad copy of `config.stop-auto.json`.
- Do not touch the keyboard or mouse during a run. Input during the run changes `inputIdleSeconds` and where the result goes.

Make the fixtures once.

```sh
$V say long-tail "今日は天気が良いので午後から公園まで散歩に行こうと思っています入力ストップ [[slnc 3000]]"
$V say short-pause "今日は天気が良いので午後から公園まで散歩に行こうと思っています [[slnc 700]] 入力ストップ [[slnc 3000]]"
$V say continued "今日はいい天気です [[slnc 700]] 入力ストップですと言いました [[slnc 3000]]"
$V say embedded-long "入力ストップという言葉を使って今日の予定を長めに説明しています [[slnc 3000]]"
```

Run each with the stop config.

```sh
C=.claude/skills/verify-voice-switch-mac/config.stop-auto.json
VS_RUN_ID=<id> $V run long-tail $C
```

Expected lines in `stdout.txt` after `dictation session: automatic rule=auto_b`:

| Fixture | Expected |
|---|---|
| `long-tail` | `dictation finished by stop word`, then `end reason=StandaloneStop` and `automatic ended by StandaloneStop, pasting like a wake-word dictation` |
| `short-pause` | same as `long-tail` |
| `continued` | `dictation ended by 1200 ms silence` |
| `embedded-long` | `dictation ended by 1200 ms silence` |

- **Proof of the stop.** Quote the `dictation session: automatic` line and the `finished by stop word` or `ended by` line in order.
- **Proof the stop word is out of the text.** For `long-tail` and `short-pause`, read the pasted text back with `osascript -e 'tell application "TextEdit" to get text of front document'`. It must not end with `入力ストップ`. The matching Superwhisper entry is in the user's active mode, not `input-voice-switch`. Match its `meta.json` `datetime` to the handoff time, and do not take the newest entry blindly, because the user's own VoiceSwitch.app writes entries too. Copy it into the evidence folder as `superwhisper-meta.json`. Close the TextEdit document with `saving no` afterwards.
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
- The stop check (`stopWordStart` in `Transcript.swift`, applied by `stopWordCut` in `MacApp.swift`) is the same code for automatic and wake-word dictations. Both now paste when the stop word ends them, so the automatic path no longer avoids the paste.
- Re-run on 2026-10-15 (run `maint-stoplong`, missing-mode config). `continued` ended by `1200 ms silence after 2880 ms` and `embedded-long` by `1200 ms silence after 3510 ms`. Neither stopped. `long-tail` and `short-pause` were not re-run, because they paste.
- `[[slnc 3000]]` at the end is needed. Without trailing silence the session runs to the end of the file and ends by silence anyway, which hides a missed stop.
- Kyoko must say the sentence without an internal pause longer than `hangoverMs` (200 ms) for `long-tail` to be one utterance. Check the duration in `afinfo` of the fixture (over 2.5 s). The tail check logs nothing under `noWakeWord`, so the proof is `dictation finished by stop word` instead of `ended by ... ms silence`. Re-proven on 2026-10-14 (run `maint`, `long-tail`: `confirmed by agreed words`, `finished by stop word`, `dictation: 33 chars`).
- Baseline on main (2026-10-13, evidence `stop-baseline` and `stop-baseline-rt`): `long-tail` ended by `1200 ms silence after 4320 ms of audio`, so the stop word was recorded. The `superwhisper-meta.json` saved there (`入力ストップ。`) was the newest entry taken blindly and may belong to another recording.
- The automatic session can open after the start of the phrase. `continued` and `embedded-long` lost their opening words in the fixed run too. That is a separate open issue. Judge this feature by the end line and by whether the result ends with `入力ストップ`, not by the full sentence.
- A missing `dictation session: automatic` line means the run never started a session (input not idle, or Superwhisper busy). That run proves nothing; rerun.
