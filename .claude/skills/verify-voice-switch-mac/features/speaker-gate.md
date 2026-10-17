# Speaker gate (SG9 and SG4 implemented)

voice-switch should act only on the enrolled user's voice. The gate does two things. An automatic (no-wake-word) recording of 3 s or more whose voice does not match the enrolled user is dropped before it reaches Superwhisper, so a colleague talking nearby or a video no longer fills Superwhisper's history (`SG9`). In every recording that is handed off, wake-word dictations included, a pause-delimited stretch of 2 s or more in another voice is cut out of the audio (`SG4`). Wake words and stop words are not checked, and recordings under 3 s pass unchecked, because about 1 s of audio does not separate speakers (measured 2026-10-16, below). Both OSes share the same core: the CAM++ embedding model (`3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx`, 27 MB, sherpa-onnx release) through the ONNX Runtime that already runs Silero, the same fbank front end, and the same threshold. The Windows map has the same IDs in `verify-voice-switch/features/speaker-gate.md`. Keep the two in step.

## Sub-features

Config keys (same names on both OSes): `speakerEnrollment` (path of the enrollment JSON; absent means the gate is off and behavior is unchanged) and optional `speakerThreshold` (default 0.30). Enroll with `voice-switch --enroll-speaker <out.json> <in.wav>...` (one or more 16 kHz mono 16-bit PCM WAVs; superwhisper's `recordings/<id>/output.wav` already are). The JSON is `{model, sampleRate, sources, embedding}`, where `embedding` is the unit-length mean of the unit-length per-file embeddings. `make speaker-model` fetches the model into the Whisper models folder; it is not in the repo or the app bundle. The model session is loaded once, so restart voice-switch after the model first appears.

Measurement behind the rule (2026-10-16, the user's superwhisper recordings against FLEURS ja, CAM++, 5-clip mean enrollment): own-voice cosine scores sit at 0.25 to 0.6. At ~1 s (a wake word or stop word) EER is 14-18 %, at 3 s 2.9 % at 0.295, at 6 s 0 % at 0.300. CAM++ beat ERes2NetV2 at 3 s and longer. The app's CAM++ embedding matches sherpa-onnx's to `max |1-cos| = 3.4e-11` on 10 real clips, 5 own and 5 FLEURS. The check ran `--enroll-speaker` with one input per clip and compared the result with sherpa-onnx in a scratch script outside the repo.

| ID | State |
| --- | --- |
| `SG1` | own voice unchanged: holds by construction for wake and stop (not gated). For automatic recordings, an own-voice recording of 3 s+ must still reach handoff (`sg-own-auto` below). `ms=` in `speaker gate: score=<s> ms=<t>` is the inference time (110-134 ms on 4-6 s) |
| `SG2`, `SG3` | out of scope for this gate: another voice's wake word or stop word is ~1 s, where EER is 14-18 %. Gating them would reject the user about one time in six. Revisit only with a model that separates 1 s audio |
| `SG7` | implemented; no enrollment, a missing model or a failing inference passes through and logs `speaker gate: <enrollment\|model\|inference> unavailable, passing through: <error>` |
| `SG8` | implemented; kept samples are bit-identical to the recording, and a cut segment is removed whole (no fades, no partial cuts) |
| `SG9` | implemented and verified e2e on 2026-10-16 (below) |
| `SG4` | implemented and verified e2e on 2026-10-16 on an automatic recording and on a wake-word dictation through the real Superwhisper (below) |
| `SG5` | by construction: segments under 2 s are never cut, and overlapped speech sits in one segment with the user, which usually scores as the user and is kept. Measured 2026-10-16 (4 s of FLEURS added inside 12 s of the user's own recordings, 30 per level): at 0.25x and 0.5x the user's level nothing was cut (0/30); at equal level 3/30 recordings had a cut, removing up to 6 % of the user's own speech. A threshold of 0.25 lowers that to 2/30 and 2 % but removes less separated speech, so 0.30 stays |
| `SG6`, `SG10`, `PB1`-`PB4` | planned |

Shared core (same IDs and pass conditions on both OSes):

- `SG1` own voice unchanged: the user's wake, stop, and dictation give the same lines and markers as before, and an own automatic recording of 3 s+ logs `speaker gate: score=` at or above the threshold and continues to handoff.
- `SG2` another voice's wake is rejected (out of scope, see the table).
- `SG3` another voice's stop word is rejected (out of scope, see the table).
- `SG4` another voice inside the body is cut. Rule (identical on both OSes, `SpeakerGate.withoutOtherSpeakers` / `WithoutOtherSpeakers`): 30 ms frames are voiced above 0.1 x the 95th-percentile frame RMS of the whole recording; 400 ms of unvoiced frames end a segment; each segment of 2 s or more is scored against the enrollment, and one under `speakerThreshold` is removed. If under 1 s would remain, the original is handed off and `speaker gate: kept all <ms> ms, cutting <n> segments would leave <ms> ms` is logged. A cut logs `speaker gate: cut <ms> ms of <ms> ms in <n> segments`. It runs on the handed-off audio of wake-word dictations and of automatic recordings (after `SG9` passes the whole recording), so the local-history fallback gets the same audio. Measured 2026-10-16 on 30 own+FLEURS+own mixes and 30 own-only clips: other speech removed mean 86 % (median 100 %), own speech removed 0.1 % in mixes and 0 % in own-only clips. Sliding 3 s windows were tried first and lost up to 30 % of own-only recordings.
- `SG5` overlap keeps the user: when both voices overlap, the own-voice phrases are kept. Losing the user is worse than letting the other voice through. Segments under 2 s and segments that score as the user are kept whole.
- `SG6` accuracy report: own accept rate and other reject rate at ~1 s and 3 s+, over at least 3 thresholds. The 2026-10-16 measurement above is the first one; it ran on sherpa-onnx, not the app.
- `SG7` fail-open: with no enrollment or a missing or failing model, behavior is exactly today's, and stdout says the gate is unavailable.
- `SG8` kept audio is the original: every kept sample is bit-identical to the recording, and only whole cut segments are missing.
- `SG9` automatic sessions: an automatic recording under 3 s logs `speaker gate: skipped, <ms> ms is under 3000 ms` and goes on. One of 3 s+ logs `speaker gate: score=<s> ms=<t>`, and below the threshold it logs `dictation dropped: automatic audio was another speaker` and never reaches Superwhisper.
- `SG10` enrollment and matching use the same path: enrollment audio goes through the same capture and processing as live matching.

Playback front end (Mac-specific):

- `PB1` video only: a Japanese video playing through the speakers that says `音声入力` gives no wake.
- `PB2` user over video: an own-voice wake while the video plays still wakes.
- `PB3` the process tap captures other apps' playback while voice-switch is running and keeps a fixed lag to the mic.
- `PB4` open question: does Voice Processing IO cancel audio played by other apps? Measure it once.

## How to get to it (user POV)

- Enroll once from a few of your own superwhisper recordings and point `speakerEnrollment` at the JSON.
- Turn on automatic sessions (`dictation.noWakeWord`). Speech of 3 s or more from someone else nearby no longer reaches Superwhisper; your own does.
- When someone else talks for 2 s or more between your own phrases, with a pause around it, their part is missing from what Superwhisper transcribes.

## Driving it with verify.sh

Preconditions: baseline from the index. `$V build` also runs `make speaker-model` and, once, enrolls the user from the 5 newest superwhisper recordings with a transcript and `meta.json` `duration` of 3000 ms or more (`--enroll-speaker` rejects the whole set when one input is under 3 s: `speaker enrollment failed: <wav> is under 3 s; short audio does not separate speakers`) into `~/Library/Caches/voice-switch-verify/speaker/own.json`; `speaker/own.txt` lists them. Delete `own.json` to enroll again. Without real-voice fixtures, Kyoko stands in for the other voice: run `maint-spk` (2026-10-17, scratchpad copy of `config.stop-auto.json` with `vs-verify-missing` and this enrollment, `SIMULATE_REALTIME=1`) logged `speaker gate: skipped, 2340 ms is under 3000 ms` for `busy-speech` and `speaker gate: score=0.178 ms=135` then `dictation dropped: automatic audio was another speaker` for `long-speech` (9.09 s). With the enrollment path pointing at a missing file (`SG7`, run `maint-sg7`) `long-speech` logged `speaker gate: enrollment unavailable, passing through: ...` and went on to `dictation recognition: complete`. `config.speaker.json` is `config.safe.json` plus `speakerEnrollment: __CACHE__/speaker/own.json` (`run` replaces `__CACHE__`).

Automatic runs need a config without Superwhisper: a scratchpad copy of `config.stop-auto.json` with `"superwhisperMode": "vs-verify-missing"` and `"speakerEnrollment": "__CACHE__/speaker/own.json"`. Fixtures are real voices, not `say`: 1.5 s of silence, one clip, 3 s of silence, written as 16 kHz mono 16-bit AIFF into the fixtures folder. Use an own recording that is not in `own.txt`, 5 to 9 s long, and a FLEURS ja clip for the other voice. Do not type during the runs.

- `SG9` and `SG1` (automatic): `SIMULATE_REALTIME=1 VS_RUN_ID=<id> $V run sg-own-auto <config>` and the same for `sg-other-auto`. On 2026-10-16 (run `sg-camplus`) the own recording 1792105896 logged `dictation session: end reason=Silence ... seconds=6.45`, `speaker gate: score=0.756 ms=134`, `dictation recognition: complete id=1 rule=auto_b`. The FLEURS clip logged `seconds=4.41`, `speaker gate: score=0.270 ms=110`, `dictation dropped: automatic audio was another speaker reason=Silence`.
- Wake is not gated: `$V say sg-own-wake "音声入力"` and `VS_RUN_ID=<id> $V run sg-own-wake .../config.speaker.json`. Expect `heard: 音声入力  -> wake`, `wake.marker`, and no `speaker gate:` line. Seen on 2026-10-16 (run `sg-camplus`).
- `SG7`: run `sg-own-auto` with the enrollment path pointing at a missing file and check the `enrollment unavailable` line.
- `SG4` (automatic): a fixture `sg4-mix-auto` of 1.5 s silence, own 6 s, 0.5 s gap, FLEURS 5 s, 0.5 s gap, own 6 s, 3 s silence (own parts from two non-enrolled recordings with no pause of 1 s or more), run like `SG9`. On 2026-10-16 (run `sg4-cut`, own 1792108304 and 1792106250, FLEURS 10339149383603832153) it logged `seconds=18.33`, `speaker gate: score=0.636 ms=347` (the whole recording passes `SG9`), `speaker gate: cut 3570 ms of 18330 ms in 1 segments`, and `dictation recognition: complete`. The FLEURS clip has 3.6 s of speech in its 5 s, so the cut is all of it; the rest of the span is silence and stays. The same runs of `sg-own-auto` (score 0.756, no cut line) and `sg-other-auto` (score 0.270, dropped) did not change. `--simulate` keeps the local-history text in memory and never writes the handed-off WAV, so the cut audio itself is checked with the hidden `voice-switch --speaker-cut <enrollment.json> <in.wav>`, which prints `kept=<samples>`. On the same fixture as a WAV it logged `cut 3600 ms of 22500 ms` and `kept=302400`, matching the numpy rule exactly. A scratch lever ran it on the 30 mixes and 30 own-only clips of the measurement: 60 of 60 kept counts equal to numpy, and the C# core equal on 8 of them.
- `SG5`: measured on the rule, not through `--simulate` (the rule equals the binary on 60/60 items). To check a build, mix another voice into an own recording without a pause, run `voice-switch --speaker-cut <enrollment.json> <in.wav>`, and compare `kept=` with the input length.
- Wake-word dictations go through the same cut, but they open Superwhisper, so ask the user first. Recipe used 2026-10-16 (run `sg4-wake-rt`): Kyoko `音声入力`, 1 s silence, then the body of `sg4-mix-auto` (own 6 s, FLEURS, own 6 s), with `config.speaker.json` plus `"dictation": {"endSilenceMs": 1200, "startTimeoutMs": 3000, "maxSeconds": 60}`, run with `SIMULATE_REALTIME=1` (without it the run ends before the 1200 ms silence closes the session). Log: `-> wake`, `dictation session: end reason=Silence ... seconds=18.33`, `speaker gate: cut 3600 ms of 18330 ms in 1 segments`, `dictation: 47 chars ... audio=14.73s`, `dictation delivered: superwhisper`. Superwhisper's `result` held only the user's words; the FLEURS sentence (興味をそそられる村を30分ほど散策するのは価値があります) was absent.
- `PB1` to `PB4` cannot be driven by `--simulate`, because a file input has no playback reference. Report them as not verified until a live harness exists.

## Gotchas

- TTS voices are not people. The first calibration used `say` voices, and the "other" voices all fell back to one voice, which put the threshold at 0.8; real own-voice scores are 0.25 to 0.6, so 0.8 rejected the user. Calibrate on real recordings only.
- The FLEURS clip above scored 0.270 against a 0.30 threshold. The margin at 3-5 s is thin; that is the 2.9 % EER showing. Longer recordings separate better.
- An automatic session can end on a 1.2 s pause, so a real recording with pauses may split into sessions under 3 s, which pass unchecked. Pick a clip that runs on without long pauses.
- `--simulate` reads the real keyboard idle time, so do not type during automatic runs.
- The cut only sees pauses. Another voice that follows the user with no pause of 400 ms shares the user's segment and is kept (median 100 % removed, but the 10th percentile of mixes kept all of it).
