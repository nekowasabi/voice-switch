# Windows dictation rationale

The Windows port now owns PCM16/16000 mono capture in the .NET parent and uses one absolute half-open sample clock for every wake, body, and stop decision.

## Synthesis

The implementation follows the parent arena synthesis. Candidate B is the base. Candidate A's extent rule and stop boundary rule are grafted in.

- `WinMmCapture` owns the capture device and stamps `PcmFrame.Start` from a single sample counter.
- `WindowsDictationRuntime` separates capture, finite recognition, and handoff with bounded channels.
- `DictationSession` is the pure session state machine.
- `SpeechPowerShellDictationRecognizer` keeps the existing Windows PowerShell 5 `System.Speech` bridge and uses finite in-memory PCM jobs sent over bounded standard input.
- `RegisteredSuperwhisperHandoff` writes one owned UUID WAV. It dispatches the registered Superwhisper file URI once only when the raw path contract is supported and no owned handoff is pending.

## Decisions

Foundational Thinking changed the data shape. The core data is `SampleRange`, `RecognitionExtent`, and `RecognizedUtterance`, not pasted text or trailing sample counts.

Fix Root Causes changed the stop handling. A standalone stop trims the source audio at the absolute lexical stop start. It never removes the last N samples after capture has advanced.

Boundary Discipline changed the adapters. WinMM, PowerShell JSON, WAV writing, registry dispatch, and Superwhisper `meta.json` parsing validate at the boundary. The session reducer stays pure.

Make Operations Idempotent changed handoff cleanup. Windows follows the macOS model: after launch it polls Superwhisper's recordings folder for up to 30 s and deletes its WAV once a run with `llmResult` or `result` appears. With no result the WAV is kept and logged. A sweep deletes files older than 10 minutes in the owned directory when the handoff is created, so leftovers converge without an operator. One handoff runs at a time off the capture loop; a dictation that ends while one is in flight is dropped with a log line, and the runtime keeps listening.

Prove It Works changed verification. Tests drive the production runtime with synthetic PCM, a delayed fake recognizer, and a recording handoff. The native SAPI path has a Windows-only synthetic probe that calls the production dictation recognizer and checks request identity, lexical ranges, and recognized text.

## Native SAPI collector

The recognizer child no longer passes PCM through an environment variable. Multi-second audio can exceed Windows environment limits. The parent writes raw PCM to `powershell.exe` standard input and drains stdout/stderr concurrently.

The PowerShell script no longer calls `Recognize()` in a loop. It compiles a small C# collector with `Add-Type`, attaches `SpeechRecognized`, `SpeechRecognitionRejected`, and `RecognizeCompleted`, starts `RecognizeAsync(Multiple)`, and waits for finite stream completion. Recognized and rejected speech is aggregated through EOF. Word timing uses `RecognitionResult.GetAudioForWordRange(word, word)` and adds phrase audio position once.

## External limitation

The registered Windows file route uses the raw `superwhisper://file//` argument shape. Tests verify construction and a mocked process launch; actual Superwhisper intake, transcription, and paste are not verified. Receiver decoding for whitespace, reserved URI characters, and non-ASCII paths is HOLD. Paths outside plain ASCII fail before anything is written or launched. `Transcribed` means a Superwhisper run wrote a result within 30 s, not that the paste landed. `NoResult` keeps the WAV for manual recovery until the 10 minute sweep.

The default owned handoff directory is `%LOCALAPPDATA%\voice-switch\dictation-handoffs`. It stores only UUID WAV files. It does not store transcript text. `dictation.recordingsDir` defaults to `%LOCALAPPDATA%\com.superwhisper.app\recordings`.

Windows foreground restoration and global finish/cancel shortcuts are narrow remaining OS-integration gaps. They are not treated as a core dictation gap.
