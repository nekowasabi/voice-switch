# Local transcription without Superwhisper

A user who does not own Superwhisper still gets dictated text. voice-switch transcribes its own WAV with a local engine and sends the result to the same places as today: the tmux pane, the paste fallback, or the `input=` of a `macOS.actions` URL. The planned engine is the same Cohere Transcribe model that Superwhisper runs locally (`cohere-transcribe-q4`), loaded as a GGUF through [transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) (MIT) from the ungated HF repo `handy-computer/cohere-transcribe-03-2026-gguf`. The result is the raw transcript. Nothing replaces Superwhisper's LLM cleanup (`llmResult`), so lower accuracy is accepted. Status 2026-10-21: only LT0 can run. LT1 to LT6 wait for the implementation (planned key `dictation.transcriber: "local"`). Define their exact log lines when the feature lands, and update this file then.

## Sub-features

- `LT0` engine baseline. Runs the engine on Superwhisper's past recordings and compares it with their `rawResult`. Reports the character error rate (CER) and wall time against Superwhisper's `processingTime`. This is the accuracy and speed gate before the engine is wired in.
- `LT1` wake word then dictation with the local transcriber. The text reaches the pane or history. Superwhisper is never opened, and no new folder appears under `~/Documents/superwhisper/recordings`.
- `LT2` model or CLI missing. Nothing is sent, a failure is logged, and the run does not fall back to Superwhisper or to a cloud API.
- `LT3` a `macOS.actions` URL receives the local text as `input=`.
- `LT4` latency. The log shows the time from the end of the recording to the text being sent. Compare it with LT0's `localMs`.
- `LT5` regression. With `transcriber` absent, the Superwhisper path behaves as before ([wake-dictation.md](wake-dictation.md) still passes).
- `LT6` cancel during transcription (Esc) sends nothing.

## How to get to it (user POV)

- Without Superwhisper installed, set the local transcriber in the config and place the model (planned: a `make` target that downloads the GGUF, as `make model` does for Whisper).
- Say `音声入力`, speak a sentence, and stop. The text appears in the target tmux pane.

## Driving it with verify.sh

LT0 (runs today, reads only Superwhisper's existing recordings and opens nothing):

```sh
# one-time setup, both outside the repo
git clone --depth 1 https://github.com/handy-computer/transcribe.cpp ~/Library/Caches/voice-switch-verify/transcribe.cpp
cmake -S ~/Library/Caches/voice-switch-verify/transcribe.cpp -B ~/Library/Caches/voice-switch-verify/transcribe.cpp/build -DCMAKE_BUILD_TYPE=Release
cmake --build ~/Library/Caches/voice-switch-verify/transcribe.cpp/build -j --target transcribe-cli
curl -L -o "$HOME/Library/Application Support/voice-switch/models/cohere-transcribe-03-2026-Q4_K_M.gguf" \
  https://huggingface.co/handy-computer/cohere-transcribe-03-2026-gguf/resolve/main/cohere-transcribe-03-2026-Q4_K_M.gguf  # 1.56 GB, no login

python3 .claude/skills/verify-voice-switch-mac/engine_bench_mac.py \
  --out ~/Library/Caches/voice-switch-verify/evidence/<run>/lt0 [--limit 10] [--max-ms 15000] [--gguf <other quant>]
```

- It prints one line per recording (`dur`, `sw`, `local`, `cer`, `exit`) and ends with `KPI n=<n> failed=<f> meanCER=<x> p50local=<ms> p50superwhisper=<ms>`. It exits nonzero when no recording ran.
- Evidence: `results.json` (per recording: both texts, both times, CER, the tail of the engine's stderr), `summary.json`, and the converted `<id>.wav` files in the `--out` folder.
- Pass: `failed=0`. Record meanCER and p50local in the proof. The acceptance threshold is not decided yet, so report the numbers and quote the 2 worst rows.

LT1 to LT6: not driveable yet. Once implemented, use `$V run <fixture> <config>` with a scratchpad config that has `dictation` plus the local transcriber. Prove LT1 by showing that `~/Documents/superwhisper/recordings` has the same entry count before and after the run, together with the transcript log line.

## Gotchas

- The recordings are the user's real speech. Keep the evidence under `~/Library/Caches/voice-switch-verify/evidence` and do not commit it.
- `rawResult` is Superwhisper's output, not ground truth. CER measures the distance from Superwhisper, not from what was said. Superwhisper usually ran the same model (`modelName` `Cohere Transcribe`; filter with `--model`), so a large CER points at the GGUF quant or the runtime, not at the model.
- `localMs` includes loading the model, because `transcribe-cli` starts once per recording. The weights are memory-mapped, so a warm page cache makes the load cheap. The first run on 2026-10-21 took 4434 ms for a 3.7 s recording, and the following runs took about 230 to 640 ms. Report a cold run and the later rows separately before quoting a speed.
- `transcribe-cli` prints a diagnostic report on stdout. The transcript is the `text: ` line, and `realtime:` gives the engine's own time. The helper keeps only the `text: ` line.
- First result (2026-10-21, M-series Mac, Q4_K_M, 8 recordings of 3.6 to 9.3 s): `KPI n=8 failed=0 meanCER=0.056 p50local=252.5ms p50superwhisper=172.5ms`. 5 of 8 matched Superwhisper exactly. The differences were proper nouns and rare words (オミセイム→オミクロン, 臨技書→臨時賞) and one inserted filler (どうも、).
- `superwhisperMs` is Superwhisper's `processingTime`, which does not include its upload or polling. voice-switch also waits up to 30 s for `meta.json`, so the two numbers are not the same measure.
- Recordings Superwhisper made from the live mic are `output.ogg`. Only handed-off ones are `output.wav`. The helper converts both with `ffmpeg` (Homebrew).
- transcribe.cpp needs a language hint (`-l ja`) and has no auto-detect. English recordings get a high CER. Either skip them or say so in the proof.
- Linking transcribe.cpp into the app next to whisper.cpp means two copies of ggml (a possible symbol clash, unverified). Running `transcribe-cli` as a separate process avoids that.
