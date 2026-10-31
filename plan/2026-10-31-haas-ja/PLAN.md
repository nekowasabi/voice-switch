# PLAN: Haas-ja-v1.0 as an opt-in Japanese body transcriber

One slice. Effort is #high. Not #xhigh: the config shape, the runner contract, and the failure policy are already decided, and no #high attempt has failed yet.

## Problem

voice-switch sends the body WAV to Superwhisper on Mac and Windows. MEMO.md (2026-10-21) wanted a local body path under `dictation.transcriber` = `superwhisper` | `local`, with no automatic fallback. The recommended engine then was Cohere Transcribe GGUF via transcribe.cpp, which can run on CPU and Mac.

Haas-ja-v1.0 is a stronger open Japanese ASR model (Apache 2.0, 615.8M, Parakeet-TDT, weighted CER 6.12% on the publisher's bench) and it only runs on Linux with an NVIDIA GPU, CUDA, and Triton. Daily voice-switch targets are Mac and Windows. The body path has to be opt-in, and the same command contract has to work on a GPU Linux box, GPU WSL, or an ssh wrapper. In-process Haas on Mac is not a feature.

## Design

`dictation.transcriber` is absent, `"superwhisper"`, or `"haas"`. Absent and `"superwhisper"` keep the current Superwhisper handoff, including `LocalHistoryHandoff` on Windows. `"local"` is not implemented. Any other string fails config load. The message contains `dictation.transcriber must be "superwhisper" or "haas"`.

`dictation.haas` is read only when the transcriber is `haas`:

- `command`: non-empty array of non-empty strings. The process is this argv, then `--vocab` and the vocabulary path when `vocabulary` is set, then the WAV path as the last argument. No shell.
- `timeoutSeconds`: optional positive number. Omitted means 120. Zero or negative fails load with a message that contains `dictation.haas.timeoutSeconds`.
- `vocabulary`: optional non-empty path. The file is not required to exist at load time. The runner reads JSON of the form `{"Haas": ["ハース"]}` into `haas_asr.Vocabulary`.

A missing or empty `command` when the transcriber is `haas` fails load with a message that contains `dictation.haas.command`. A `haas` object on a Superwhisper config is ignored.

The runner contract is: 16 kHz mono WAV path in, plain UTF-8 text on stdout, exit 0. Non-zero exit, empty or whitespace-only stdout, or a timeout is failure. Trim the stdout before the empty check. Library logs stay on stderr.

On success, the text uses the existing send path:

- An action with `tokenEnv` is posted. An action without it opens the URL with the text. No tmux and no paste.
- An automatic recording goes to history only, same as Superwhisper's controlled handoff. It is not sent.
- Otherwise route with the existing tmux callback. Paste when `Decide(modeRequested: true, disposition)` says paste. Passing `modeRequested: true` means voice-switch owns the paste, because Superwhisper is not running. `Sent` and `SkippedNoBody` do not paste. `NotRouted` pastes the full text. `SendFailed` pastes `RouteResult.Body` when it is non-empty.

On failure, do not send, do not paste, do not open an action, do not start Superwhisper, and do not fall back to WhisperGate, WordAgreement, or SAPI. Windows returns `HandoffStatus.NoResult` (not `FailedBeforeDispatch`, which `LocalHistoryHandoff` would turn into a local transcript). Mac returns `.noResult` (not `.notDispatched`, which stores a local transcript). Both log a line that contains `dictation: haas runner failed`. The HUD failure state is the existing one (`DeliveryState.Failed` / `reportDelivery(.failed)`). Keep the WAV and log its path.

`DictationHandoffs.Create` in `dotnet/VoiceSwitch.Windows/DictationHandoffs.cs` is the only production choice. `Program.cs` calls it. The `haas` branch returns `new HaasRunnerHandoff` and does not call `LocalHistoryHandoff.Wrap`. The other branch constructs `RegisteredSuperwhisperHandoff` and wraps it only when the caller asked for the local-history wrap. Source order in that file is Haas construction, then Superwhisper construction, then `LocalHistoryHandoff.Wrap`.

Mac `handoff` in `MacApp.swift` calls `handoffHaas` before it would run `/usr/bin/open` on Superwhisper. `handoffHaas` does not return `.notDispatched`. Hotkeys still end the local recording. They do not switch a Superwhisper mode.

The runner process and the argv rules live in `Sources/voice-switch/HaasTranscriber.swift` (`HaasRunner.arguments`, `HaasRunner.interpret`, `HaasRunner.run`). That file imports Foundation only, so Linux `swiftc` can compile it with `Config.swift`, `DictationJournal.swift`, `WakeSafety.swift`, `Transcript.swift`, and `Platform.swift`. Swift validation throws from `DictationConfig.init(from:)`, so `JSONDecoder` and `ConfigFile.load` both reject a bad config. C# validation stays in `ConfigLoader.Validate` and throws `InvalidDataException`.

`scripts/haas_transcribe.py` is the GPU-side script. `--help` works without `haas_asr` installed, so `haas_asr` is imported inside a function, not at module level. A missing import prints `haas_asr` and mentions `make haas-setup` or `pip install`, then exits non-zero. The script loads `third-intelligence/Haas-ja-v1.0` by default. Audio longer than 30 seconds uses `haas_asr.transcribe_longform(model, audio, batch_size=64, vocabulary=vocab)`, which returns a string. Shorter audio uses `haas_asr.transcribe(model, [audio], vocabulary=vocab)[0]`, which returns a string (not `.text`). Redirect library stdout to stderr around load and transcribe, then print only the transcript. Optional `--model`. Do not vendor weights.

`make haas-setup` creates a venv (default `$HOME/.cache/voice-switch/haas-venv`) and pip-installs `haas-asr @ git+https://github.com/third-intelligence/Haas-ASR.git`. `make haas-model` downloads `third-intelligence/Haas-ja-v1.0` with `huggingface-cli download` or `huggingface_hub.snapshot_download` into `$HOME/.cache/voice-switch/models/Haas-ja-v1.0`. Neither target is a prerequisite of `build`, `app`, `install`, `model`, `relaunch`, `win`, `win-build`, `win-test`, `win-publish`, `parity-test`, or `url-test`. Do not run them from tests.

Haas is not an agreement model. Word agreement stays WhisperGate / parakeet-ja + qwen3. That swap is not trivial (GPU, offline, external process).

Add both `transcriber` and `haas` to Swift `DictationConfig` and C# `DictationConfig`, or the parity field comparison fails. Do not add `transcriber` to `config.example.json` or `config.example.windows.json`. Put the opt-in sample in `config.example.haas.json`.

New `.cs` files under `dotnet/VoiceSwitch.Windows/` need a `Compile Include` in `VoiceSwitch.Windows.Tests.csproj`. Core files do not.

## Measurable acceptance

`.claude/skills/verify-haas-ja-transcriber/items.md` is the list. The Windows tests use these exact names:

- `haas config absent transcriber stays superwhisper`
- `haas config decodes command timeout and vocabulary`
- `haas config rejects an unknown transcriber`
- `haas config rejects an empty command`
- `haas runner sends mock text to tmux and does not launch superwhisper`
- `haas runner sends mock text to the action url and not to tmux`
- `haas runner pastes when tmux does not take the text`
- `haas runner non-zero exit does not send and does not fall back`
- `haas runner empty stdout does not send and does not fall back`
- `haas runner timeout does not send and does not fall back`
- `haas default factory keeps the superwhisper handoff`

The route tests spawn a real mock executable. They record every `ProcessStartInfo`. None of them may contain `superwhisper` or `rundll32`. The timeout test sets `timeoutSeconds` to 1 and must return. The Swift file `tests/haas-transcriber/main.swift` prints the `PASS haas swift ...` lines named in items.md. `verify.sh` compiles it.

Docs: a heading `Haas-ja-v1.0 (Japanese local option)` in `README.md` and `README_ja.md`, the model id `third-intelligence/Haas-ja-v1.0`, the NVIDIA GPU requirement, Mac in-process unsupported (English `unsupported` / Japanese `非対応`), and a command example for Linux GPU, Windows via `wsl.exe`, and a remote GPU via a wrapper. `MEMO.md` gets a short 2026-10-31 note: the Japanese body recommendation is Haas via the external GPU runner; Cohere GGUF and Whisper stay the CPU and Mac alternative; `local` is not a config value.

## Regression guards

- `dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release` stays exit 0 with no `FAIL ` line. Baseline was 243 PASS, 0 FAIL.
- `python3 tests/parity/run_parity.py` stays at fail ≤ 2. The two known failures are the Swift harness (`libncurses.so.6` / `Swift pure harness failed to compile`) and `automatic transcript policy checks failed`. Do not add a third. Pass count may rise. Run it with `PATH="$HOME/.dotnet:$HOME/swift/usr/bin:$PATH"` and do not set `LD_LIBRARY_PATH` for this comparison. `verify.sh` sets that path only for its own Swift item.
- Do not change wake-word, action-wake, or cough-filter behavior when `transcriber` is absent.
- `dotnet format` on the touched C# projects. `swift-format` if the repo has it configured; otherwise say it is not configured. Python: `ruff` or `flake8` if present, otherwise `python3 -m py_compile`.
- Do not run `make haas-model`, do not download weights, do not print tokens.

## Out of scope

Live GPU inference. In-process Haas on Mac or CPU. Using Haas as the agreement model. Vendoring weights. Merging. Pushing, unless the orchestrator does that later. Other worktrees.
