# Transcription recall (dropped phrases)

Every phrase the user spoke during a dictation must appear in the text that reaches history. The KPI is the number of expected phrases missing from Superwhisper's `rawResult`, and the target is zero. This feature exists because a captured WAV that contained `おはようございます` came back from Superwhisper as `きょうはいい天気ですね。` only (2026-10-12 09:14:21, model `cohere-transcribe-q4`).

## Sub-features

- `TR1` the synthetic corpus (`phrase-cases.json`: greeting before body, short lead, three phrases, long middle pause, no pause) has zero missing phrases.
- `TR2` real WAVs that once lost a phrase have zero missing phrases.
- `TR3` padding experiments: the same cases with leading silence added (`padMs`), to test whether a handoff change helps.

## How to get to it (user POV)

- Speak a dictation of two or more sentences with a short pause, then open history (Ctrl+Shift+F9) and compare.

## Driving it with verify.sh

Preconditions: Superwhisper installed and running, a mode with `autoPaste: false` (default key `new-mode-1`, override with `-ModeKey` in `superwhisper-recall.ps1`). The run switches Superwhisper to that mode and restores the previous mode at the end. It opens files in Superwhisper on the user's desktop and uses the mode's cloud models, so tell the user before running it.

- `TR1`: `$V phrases` once, then `$V recall`. Expect `KPI pad=0ms dropped=0/10 timedOut=0` and exit 0.
- `TR2`: copy the Superwhisper `recordings/<id>/Output.wav` of a reported drop into the fixtures folder, list it in a cases file (`[{ "name", "wav", "phrases": [["alt1","alt2"], ...] }]`), then `$V recall <cases.json>`.
- `TR3`: `$V recall <cases.json> 0,500,1000`. One KPI line per padding.
- Mac: `python3 superwhisper_recall_mac.py --spec phrase-cases.json --out /tmp/recall --mode-key <key>` speaks the same corpus with `say -v Kyoko` and prints the same KPI lines. `--cases <cases.json>` takes real WAVs, and `--pad-ms 0 500 1000` runs the padding experiment. The key is the Superwhisper mode with `autoPaste: false` (`~/Documents/superwhisper/modes/*.json`). Not yet run on a Mac.
- Evidence: `results.json` (per case: model, missing phrases, rawResult, llmResult) and `summary.json` under `/mnt/c/temp/voice-switch-verify/recall/<run>/`.

## Gotchas

- The result depends on the mode's `voiceModelID`. Every row records `model`. Never compare runs with different models.
- Superwhisper trims leading silence from its own `Output.wav`, so padding does not show up in its duration.
- A phrase can be dropped on one run and kept on the next. One clean run is not proof for a model that has dropped before; repeat the run.
- An expected phrase lists alternatives because the model may write kanji or kana (`天気` or `てんき`).
