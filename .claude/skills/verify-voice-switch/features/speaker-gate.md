# Speaker gate (SG9 and SG4 implemented, dotnet build only, not verified on a device)

voice-switch should act only on the enrolled user's voice. The gate does two things in `RunHandoffAsync`. An automatic (no-wake-word) recording of 3 s or more whose voice does not match the enrolled user is dropped before it reaches Superwhisper (`SG9`). In every recording that is handed off, wake-word dictations included, a pause-delimited stretch of 2 s or more in another voice is cut out of the audio (`SG4`). Wake words and stop words are not checked, and recordings under 3 s pass unchecked, because about 1 s of audio does not separate speakers. The core is the same as on the Mac: the CAM++ model (`3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx`) through ONNX Runtime 1.24.2, the same fbank, and the same threshold. The IDs and pass conditions match `verify-voice-switch-mac/features/speaker-gate.md`. Keep the two in step.

## Sub-features

Status (2026-10-16): `SpeakerGate.cs` mirrors `SpeakerGate.swift` and was checked only with `dotnet build` and `make parity-test` on a Mac. It has not run on Windows. Config keys match the Mac: `speakerEnrollment` and optional `speakerThreshold` (default 0.30). Enroll with `voice-switch.exe --enroll-speaker <out.json> <in.wav>...` (one or more 16 kHz mono 16-bit PCM WAVs). The JSON is `{model, sampleRate, sources, embedding}`, with `embedding` the unit-length mean of the unit-length per-file embeddings. The model is the same ONNX file as on the Mac, placed like the WordAgreement model; restart after it first appears.

Measurement behind the rule (2026-10-16, the user's superwhisper recordings against FLEURS ja, CAM++, 5-clip mean enrollment): own-voice scores 0.25 to 0.6; EER 14-18 % at ~1 s, 2.9 % at 3 s (threshold 0.295), 0 % at 6 s (0.300).

- `SG1` own voice unchanged: wake and stop are not gated; an own automatic recording of 3 s+ logs `speaker gate: score=` at or above the threshold and continues to handoff.
- `SG2`, `SG3` another voice's wake word or stop word rejected: out of scope for this gate. They are ~1 s, where EER is 14-18 %, so gating them would reject the user about one time in six.
- `SG4` another voice inside the body is cut (implemented; same rule and log lines as the Mac, `SpeakerGate.WithoutOtherSpeakers`): 400 ms pauses delimit segments of 30 ms frames voiced above 0.1 x the 95th-percentile frame RMS; each segment of 2 s or more scoring under `speakerThreshold` is removed, unless under 1 s would remain. Expect `speaker gate: cut <ms> ms of <ms> ms in <n> segments`, and a handoff WAV shorter than `range=` by that much (`range=` stays the capture range). Measured on the Mac (30 mixes, 30 own-only clips): other speech removed mean 86 %, own speech 0.1 % in mixes and 0 % own-only. The C# core kept the same sample count as the Swift port and the numpy rule on 8 of those clips, run on a Mac. Not run on Windows.
- `SG5` overlap keeps the user: segments under 2 s are never cut, and overlapped speech shares a segment with the user, which usually scores as the user and is kept. Measured on the shared rule 2026-10-16: another voice at 0.25x or 0.5x the user's level was never cut (0/30); at equal level 3/30 recordings had a cut that removed up to 6 % of the user's speech.
- `SG6` accuracy report at ~1 s and 3 s+ over at least 3 thresholds; the measurement above is the first one, on sherpa-onnx.
- `SG7` fail-open: no enrollment or a missing or failing model gives today's behavior and a `speaker gate: <stage> unavailable, passing through` line.
- `SG8` kept audio is the original: every kept sample is bit-identical to the recording, and only whole cut segments are missing.
- `SG9` automatic sessions (implemented): under 3 s logs `speaker gate: skipped, <ms> ms is under 3000 ms`; 3 s+ logs `speaker gate: score=<s> ms=<t>`, and below the threshold `dictation dropped: automatic audio was another speaker`.
- `SG10` enrollment and matching use the same path, including whether the OS AEC was on (planned).

Playback front end (Windows-specific, planned):

- `PB1` video only: speakers playing a video that says `音声入力` give no wake.
- `PB2` user over video: an own-voice wake during playback still wakes.
- `PB3` compare the OS AEC on a communications stream (`IAcousticEchoCancellationControl`, Windows 11 build 22540+) against the existing `LoopbackShadow` comparison on the same recordings.
- `PB4` open question: does the OS AEC reference include every app's playback on the default render device?

## How to get to it (user POV)

- Enroll once from a few of your own recordings and point `speakerEnrollment` at the JSON.
- Turn on automatic sessions. Speech of 3 s or more from someone else nearby no longer reaches Superwhisper.

## Driving it with verify.sh

Preconditions: baseline from the index. Every `SG` item runs as `$V wav <fixture> <config>`, the same as the other features. `--output-dir` replaces Superwhisper, so these runs are safe.

- `SG9`: enroll from the user's recordings, then run a 3 s+ own recording and a 3 s+ FLEURS ja clip (both 16 kHz mono PCM16, with leading and trailing silence) with an `autoIdle` config plus `speakerEnrollment`. Expect `speaker gate: score=` on both, a handoff WAV for the own one, and `dictation dropped: automatic audio was another speaker` for the other. Not run on Windows yet.
- `SG7`: the same run with the enrollment path removed or pointing at a missing file.
- `SG4`: the Mac `sg4-mix-auto` fixture (own 6 s, FLEURS 5 s, own 6 s with 0.5 s gaps) through `--input-wav` with the `SG9` config. Expect `speaker gate: score=` at or above the threshold, `speaker gate: cut`, and a handoff WAV about the FLEURS speech (3.6 s) shorter than the input. Not run on Windows yet.
- `PB1` to `PB4` cannot be driven through `--input-wav`, because a file input has no render reference and opens no communications stream. Report them as not verified until a live harness exists.

## Gotchas

- TTS voices are not people. The first calibration used synthetic voices and set the threshold at 0.8, which rejects the real user (real own scores are 0.25 to 0.6). Calibrate on real recordings.
- `ResidentRuntime` / `SpeechPowerShell` receives only transcribed text, so the gate cannot score the audio there. The gate covers the `DictationRuntime` path only.
- The OS AEC changes the waveform. A past DSP run cut a final 「す」 from the body. Apply it to the analysis path only.
- An automatic session that ends on a short pause can split into recordings under 3 s, which pass unchecked.
