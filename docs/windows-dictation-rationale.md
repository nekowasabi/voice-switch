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

## Phases, shortcuts, HUD, focus

`DictationPhase` (Idle, Waiting, Recording, Ended) is derived at the end of each loop. A change is logged as `dictation phase: X`. Ended is set only by a stop word or the finish key.

Finish and cancel borrow Superwhisper's own keys. `DictationHotkeys` in the core reads `toggleRecordingShortcut` and `cancelRecordingShortcut` from `%LOCALAPPDATA%\com.superwhisper.app\preferences.json`, with Control+Space and Escape as fallback. The tray installs a `WH_KEYBOARD_LL` hook only while the phase is Waiting or Recording. It swallows the key and its keyUp. The runtime takes the pending command each frame. Cancel sends nothing. Finish sends the body.

The HUD is a 160x40 rounded top-center form with `WS_EX_NOACTIVATE`, `TOOLWINDOW`, `TRANSPARENT`, and `TOPMOST`. The confirmation sound is off by default and plays on a lone wake only. After it the VAD gets zeros for 600 ms so the sound is not heard as speech.

Focus restore records the foreground window at wake. For 2 s after handoff, and only while Superwhisper is in front, the tray calls `AttachThreadInput` plus `SetForegroundWindow`. `AllowSetForegroundWindow` can only grant the foreground process, and a synthetic Alt toggles the menu bar and breaks the paste, so neither is used.

Known deviations are UIPI (keys typed into an elevated window bypass the hook), WAV kept on `NoResult` where macOS deletes it, and Alt or Win chords not specially handled.

## Verification status

Release compile and tests with fakes cover the phases, hotkeys, HUD state, sound gate, and focus logic. A hand test on Windows is still needed for live mic wake, Ctrl+Space and Esc, HUD placement on multiple monitors, focus restore, and real Superwhisper paste.
