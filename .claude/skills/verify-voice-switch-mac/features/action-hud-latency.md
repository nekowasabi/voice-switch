# Action HUD latency

A user says an action wake word from `macOS.actions` (e.g. プログレス, グロック, クロード) and expects the mic HUD (`waiting`) at once. This entry measures how long "end of the word -> HUD waiting" takes, splits it into its parts, and shows which part can be shortened without the action missing. Users reported about 1 s; the target is 0.2 s.

The delay is serial: the segmenter closes the utterance `hangoverMs` after the last voiced frame (`Segmenter.push`), where voiced means energy VAD **or** the macOS HAL voice detector (`Segmenter.isSpeech`: `lastWasEnergy || voice`), then Apple Speech transcribes the closed utterance (`transcribe`, logged as `stt`), then the action match creates `Dictation(action:)` and the frame `defer` publishes `waiting` to `HUD.render`.

## Sub-features

- **AL1 real-mic breakdown (`$V latency log`).** Every action launch in `~/Library/Logs/voice-switch.log` with the `voice: phrase` line that opened it: `voiced`, `energy`, `hal`, and `hal_only = voiced - energy` (frames only the HAL detector called voice), plus `est_word_end_to_hud = hal_only + hangoverMs (+ stt)`. Read-only; this is the only item that sees the user's real mic and HAL detector.
- **AL2 synthetic floor and hangover sweep (`$V latency sim [word]`).** The lone word through `--simulate` (energy VAD only, no HAL) at `hangoverMs` 300 / 150 / 60 (`VS_HANGOVERS` overrides). Per row: what Apple Speech heard, `stt_ms`, whether `phase: waiting` fired, and `est_word_end_to_hud = hangoverMs + 30 + stt`. A row whose `heard` is not the word, or `phase_waiting=0`, means that hangover splits the word and the action misses.

Proven 2026-11-03 on HEAD 808d9f4 (dirty=2: this skill only):

- AL1: 27 launches with a phrase line. `hal_only` median 720 ms, max 1110 ms; launches with `hal=0` estimate 300 ms + stt. Example: `grokbot-note voiced=1080 energy=120 hal=1080 hal_only=960`. Older lines with `stt` show 87–157 ms (2026-10-10, 2026-10-30).
- AL2 (run `action-latency-proof3`, word プログレス):

  | hangoverMs | heard | stt_ms | est ms | waiting |
  |---|---|---|---|---|
  | 300 | プログレス | 160 | 490 | 1 |
  | 150 | プログレス | 155 | 335 | 1 |
  | 60 | プログレ | 161 | - | 0 (word split, action missed) |

- Word-length baseline, `VS_HANGOVERS=300` (runs `action-latency-grok`, `action-latency-claude`): グロック `voiced=360 stt=125 waiting=1`; クロード heard as `グロード` (`voiced=510`, Kyoko misread, action missed).

- AL1 on PR #71 (`fix/action-wake-latency`, installed 2026-11-03 08:06Z, real mic, 4 launches): `started early`, boundary 60 ms, but `voiced` 930–1020 and `hal_only` 810–930 are the same as on main (same words: grokbot-note 840–1140, herdr-claude 780–960). Estimate 870–990 ms + stt against main's 960–1260 ms + stt: the PR removes the hangover wait, and the HAL hold stays.
- Energy-only silence was tried and dropped: after `floor rebased` the energy VAD hears ~30 ms per word (`peakRms=0.0468 threshold=0.0369`), so it never marks the word end.
- Silero offline (`VS_VAD_TRACE=1 voice-switch --check <files>`, 15 superwhisper recordings): its probability falls below 0.35 a median 32 ms after the last loud window (p90 64–160 ms).
- AL1 with the Silero probe (`perf/action-wake-energy-gap`, 2026-11-03 10:38Z, real mic, 4 launches): 3 fired from Silero while HAL still held (`silentMs=0`), `voice_end_to_hud` 175 / 195 / 205 ms (stt 72–122, dispatch 51–71). One quiet `progress` never reached Silero 0.5 (`sileroSilentMs=-`) and fell back to the HAL path (221 ms after HAL release).

Reading:

- STT is 78–160 ms and hangover 300 ms, so the path without HAL is about 0.45–0.5 s (AL2, and the AL1 rows with `hal=0`).
- `hal_only` is not the tail alone: with the floor rebased the energy VAD is nearly blind (`energy=120`), so the word itself is mostly HAL-voiced. Compare real `voiced` with the same word's sim `voiced` instead: grokbot-note ~1050–1140 vs グロック 360 (~700 ms), herdr-claude ~780–960 vs クロード 510 (~300–450 ms). That excess is the estimate of how long HAL holds past the word end (guess: assumes real and Kyoko word lengths match), and it accounts for the gap between ~0.5 s and the reported ~1 s.
- Every `silent`-based boundary inherits the HAL hold, because `silent` counts frames where `isSpeech` (`lastWasEnergy || voice`) is false: the utterance close, `Segmenter.probe()` (`silent == 2` / `prefixGapFrames`), and the 60/150 ms commit rule the 2026-11-03 idea team proposed. Shortening hangover or speeding STT cannot reach 0.2 s while HAL holds 300–700 ms; hangover 60 already splits プログレス. The lever is a word-end signal that does not wait for HAL: `Segmenter.noteVoice` feeds Silero per window and `probe()` fires on its silence (see the AL1 Silero row above).

## How to get to it (user POV)

Say an action wake word alone and wait for the mic glyph at the top of the screen. The HUD shows `waiting` and closes 3 s later if nothing more is said.

## Driving it with verify.sh

Preconditions: `$V build` on the tree you prove, `$V doctor` exits 0. Do not type during AL2 (the run needs ~25 s per hangover, three in a row).

```sh
$V latency log                                   # AL1, reads ~/Library/Logs/voice-switch.log
VS_HANGOVER=300 $V latency log <other.log>       # another log, or the hangover the user had then
VS_RUN_ID=<id> $V latency sim プログレス          # AL2, three --simulate runs
VS_RUN_ID=<id> VS_HANGOVERS="300 200 150" $V latency sim グロック
```

- AL2 config is `config.action-latency.json` with `__HANGOVER__` and `__WORD__` filled per row. Its `dictation.superwhisperMode` is `vs-verify-missing` and the action is a `command` that only touches `action.marker`. The fixture is the word followed by 5 s of silence, so `startTimeoutMs` (3 s) cancels the waiting action: nothing is handed off, no URL opens, and `action_marker` must stay `no`.
- Evidence: `evidence/<id>/latency.tsv` (the table), `evidence/<id>/config-h<N>.json`, and per row `evidence/<id>/h<N>/latency-action/{stdout.txt,heard.txt,config.json,input.aiff,exitcode.txt}`.
- Proof: quote the table and, per row, the `heard:` line plus `phase: waiting` / `hud: waiting` from `stdout.txt`. For AL1 quote the summary line and at least one row with its `voice: phrase` values.

## Gotchas

- Log time is whole seconds (`Config.swift` `log`, `.iso8601`), so neither item measures wall time. The HUD dispatch (consume -> main -> `orderFrontRegardless`) is not measured; there is no fixed wait in it (`HUD.render`'s 0.2 s is on hiding).
- `--simulate` has no HAL detector (`voiceActivity.device == 0`), so AL2 is the floor, not the user's experience. Room reverb and main-thread load are also absent there.
- `hal_only` counts HAL-only frames over the whole phrase, not only after the word; quiet speech under the energy threshold also lands there (e.g. `energy=30`). Treat it as an upper bound of the tail.
- The `heard:` line (and its `stt`) is not logged while `dictation.noWakeWord.enabled` is true, which is the user's current setup, so recent AL1 rows show `stt=-`.
- While an automatic session is open, an action word is transcribed once to replace the session and again on the idle path (`MacApp.swift` 1140–1148 then 1368); AL1/AL2 do not cover that path.
- Kyoko's word is clean TTS. Real speech has internal gaps; the 60 ms split seen with Kyoko is a lower bound of how often short hangovers split real words.
