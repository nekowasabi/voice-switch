# Haas-ja transcriber acceptance items

Measurable on this Linux box without an NVIDIA GPU. `verify.sh drive` prints one `PASS`, `SKIP`, or `FAIL` line per id and exits 0 only when every non-skipped item passed. Live GPU inference is `SKIP` on purpose.

The product names these tests and markers. A missing test is a failure, not a skip.

## HJ-CFG-CS

C# config decode in `dotnet/VoiceSwitch.Windows.Tests`. `verify.sh` runs the suite once (see HJ-REG) and requires these exact `PASS` lines:

- `haas config absent transcriber stays superwhisper`
- `haas config decodes command timeout and vocabulary`
- `haas config rejects an unknown transcriber`
- `haas config rejects an empty command`

Absent or `"superwhisper"` keeps today's handoff. `"haas"` decodes `dictation.haas.command` (string array), `timeoutSeconds` (omitted means 120), and optional `vocabulary`. Any other transcriber string, or `"haas"` with a missing or empty command, or a timeout that is not a positive number, fails the load. The exception text contains `dictation.transcriber`, `dictation.haas.command`, or `dictation.haas.timeoutSeconds` respectively.

## HJ-ROUTE-OK

Same suite, exact `PASS` lines:

- `haas runner sends mock text to tmux and does not launch superwhisper`
- `haas runner sends mock text to the action url and not to tmux`
- `haas runner pastes when tmux does not take the text`

A real mock executable (a script on disk, spawned by the production handoff) writes UTF-8 text to stdout and exits 0. The handoff must pass that text to the tmux callback, or to the action URL, or to the paste callback. It must not start a process whose file name or arguments contain `superwhisper` or `rundll32`. The WAV path is the last argument. When `vocabulary` is set, `--vocab` and that path are the two arguments before the WAV.

## HJ-ROUTE-FAIL

Same suite, exact `PASS` lines:

- `haas runner non-zero exit does not send and does not fall back`
- `haas runner empty stdout does not send and does not fall back`
- `haas runner timeout does not send and does not fall back`

Non-zero exit, empty or whitespace-only stdout, and a process that outlives `timeoutSeconds` each return `HandoffStatus.NoResult`. The tmux callback, the paste callback, and the action URL callback are not called. The concrete handoff type is `HaasRunnerHandoff`, not `LocalHistoryHandoff`. No Superwhisper process is started.

## HJ-DEFAULT

Same suite, exact `PASS` line:

- `haas default factory keeps the superwhisper handoff`

`DictationHandoffs.Create` with no transcriber (and with `"superwhisper"`) returns a `LocalHistoryHandoff` when local-history wrap is requested, and a `RegisteredSuperwhisperHandoff` when it is not. It never returns `HaasRunnerHandoff` for those configs. With `"haas"`, it returns `HaasRunnerHandoff` even when the caller asked for the local-history wrap.

## HJ-REG

`dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release` exits 0. The output has no `FAIL ` line. This includes the Haas tests and the previous suite.

## HJ-RUNNER

`python3 -m py_compile scripts/haas_transcribe.py` exits 0.

`python3 scripts/haas_transcribe.py --help` exits 0 and mentions the WAV argument and `--vocab`. It must do this without importing `haas_asr` (the import is inside a function, not at module level).

Running the script on a missing WAV exits non-zero. Stderr contains `haas_asr` and tells the reader to install it (`make haas-setup` or `pip install`). This subcheck is `SKIP` only when `haas_asr` is already importable.

The script contains calls to `haas_asr.transcribe`, `haas_asr.transcribe_longform`, and `haas_asr.Vocabulary`. Audio longer than 30 seconds uses longform. Stdout of a real run is only the transcript. Library logs stay on stderr.

## HJ-MAKE

`Makefile` defines `haas-setup` and `haas-model`. Neither is a prerequisite of `build`, `app`, `install`, `model`, `relaunch`, `win`, `win-build`, `win-test`, `win-publish`, `parity-test`, or `url-test`.

`make -n build`, `make -n win-test`, and `make -n parity-test` do not print `haas-setup`, `haas-model`, `haas-asr`, or `huggingface`.

Drive does not run `haas-setup` or `haas-model`.

## HJ-DOCS

- `README.md` and `README_ja.md` each have a heading `Haas-ja-v1.0 (Japanese local option)`.
- Both name `third-intelligence/Haas-ja-v1.0`, say Linux + NVIDIA GPU (CUDA/Triton) is required, and say in-process macOS is unsupported.
- Both show `dictation.transcriber` set to `haas` and a `dictation.haas.command` array.
- `MEMO.md` names `Haas-ja-v1.0`, the GPU constraint, and that Cohere GGUF / Whisper remain the CPU and Mac alternative.
- `config.example.haas.json` is JSON, `dictation.transcriber` is `haas`, and `dictation.haas.command` is a non-empty array of strings.
- `config.example.json` and `config.example.windows.json` do not gain a `transcriber` key.

## HJ-SWIFT

Compile and run `tests/haas-transcriber/main.swift` with `swiftc` plus `Config.swift`, `DictationJournal.swift`, `WakeSafety.swift`, `Transcript.swift`, `Platform.swift`, and `HaasTranscriber.swift`. Require these stdout lines and exit 0:

- `PASS haas swift absent transcriber stays superwhisper`
- `PASS haas swift decodes command timeout and vocabulary`
- `PASS haas swift rejects an unknown transcriber`
- `PASS haas swift mock text`
- `PASS haas swift mock non-zero does not return text`
- `PASS haas swift mock empty does not return text`
- `PASS haas swift mock timeout`

`verify.sh` points `LD_LIBRARY_PATH` at a scratch symlink `libncurses.so.6` -> `libncursesw.so.6` before it tries `swiftc`. On this box that is enough for `swiftc` to start. If `swiftc` still cannot start, `SKIP` with the compiler's stderr. Do not treat that skip as a pass of the Mac app. A missing `HaasTranscriber.swift` or a compile error is a failure.

## HJ-SWIFT-WIRE

Static source check, no `swiftc`:

- In `handoff` in `MacApp.swift`, the Haas branch (`handoffHaas` or `HaasRunner.run`) appears before `/usr/bin/open`.
- The Haas failure path logs `dictation: haas runner failed` and returns `.noResult`.
- That Haas function does not return `.notDispatched`.
- Windows logs the same `dictation: haas runner failed` string.
- `Program.cs` calls `DictationHandoffs.Create`.
- `DictationHandoffs.cs` constructs `HaasRunnerHandoff` on the `"haas"` branch and calls `LocalHistoryHandoff.Wrap` only on the Superwhisper branch.

## HJ-PARITY

`python3 tests/parity/run_parity.py` with `PATH="$HOME/.dotnet:$HOME/swift/usr/bin:$PATH"`. The `SUMMARY` fail count is less than or equal to 2. A new failure line that is not the pre-existing `libncurses.so.6` Swift compile failure and not `automatic transcript policy checks failed` is a failure of this item. Pass count may rise.

## HJ-GPU

`SKIP`. Live `scripts/haas_transcribe.py` against `third-intelligence/Haas-ja-v1.0` needs Linux, an NVIDIA GPU, and CUDA/Triton. This box does not run that. A person with a GPU owns it.
