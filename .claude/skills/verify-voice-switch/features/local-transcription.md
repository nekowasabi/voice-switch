# Local transcription without Superwhisper

The Windows half of the Mac feature [local-transcription.md](../../verify-voice-switch-mac/features/local-transcription.md). `SuperwhisperHandoff.cs` `SubmitAsync` stops launching `superwhisper://file//` and polling `%LOCALAPPDATA%\com.superwhisper.app\recordings`. It transcribes the WAV locally instead and passes the text to `TmuxPaneRouter.RouteAsync` or the `windows.actions` URL. The planned engine is the same as on the Mac: Cohere Transcribe as a GGUF on transcribe.cpp (Vulkan). speech.cpp, used today for word agreement, does not list Cohere. Status 2026-10-21: nothing here is drivable yet.

## Sub-features

- `LT0` engine baseline on Windows: transcribe.cpp built with Vulkan (see its `docs/build-windows.md`). It runs on the WAVs in Superwhisper's Windows recordings folder, and the result is compared with their `meta.json` `rawResult`. The Mac helper `engine_bench_mac.py` takes the same steps. A Windows port only needs different `--cli` and `--recordings` paths (unverified).
- `LT1` to `LT6`: same as the Mac file (wake dictation without Superwhisper, missing model, `input=` for actions, latency, the Superwhisper path unchanged, cancel).

## How to get to it (user POV)

- Without Superwhisper installed, set the local transcriber in the config, then dictate after the wake word. The text arrives in the WSL tmux pane.

## Driving it with verify.sh

- Not drivable yet. After the implementation, `$V wav <fixture> <config>` with the local transcriber in the config. Prove it with the transcript in `stdout.txt`, `handoff_wavs=0` (no file handed to Superwhisper), and the routed text.

## Gotchas

- Windows today uses speech.cpp's parakeet-ja and qwen3-asr models (about 1.5 GB) for word agreement. Adding Cohere means a second runtime and another 1.56 GB (Q4_K_M).
- The parity defined in the team discussion covers behavior: operation, cancel, failure, and routing. Recognition quality across the two OSes is not promised to match.
