# Windows dictation rationale

The Windows port now owns PCM16/16000 mono capture in the .NET parent and uses one absolute half-open sample clock for every wake, body, and stop decision.

## Synthesis

The implementation follows the parent arena synthesis. Candidate B is the base. Candidate A's extent rule and stop boundary rule are grafted in.

- `WinMmCapture` owns the capture device and stamps `PcmFrame.Start` from a single sample counter.
- `WindowsDictationRuntime` separates capture, finite recognition, and handoff with bounded channels.
- `DictationSession` is the pure session state machine.
- `SpeechPowerShellDictationRecognizer` keeps the existing Windows PowerShell 5 `System.Speech` bridge and uses finite in-memory PCM jobs sent over bounded standard input.
- `RegisteredSuperwhisperHandoff` writes one owned UUID WAV and dispatches the registered Superwhisper file URI once.

## Decisions

Foundational Thinking changed the data shape. The core data is `SampleRange`, `RecognitionExtent`, and `RecognizedUtterance`, not pasted text or trailing sample counts.

Fix Root Causes changed the stop handling. A standalone stop trims the source audio at the absolute lexical stop start. It never removes the last N samples after capture has advanced.

Boundary Discipline changed the adapters. WinMM, PowerShell JSON, WAV writing, registry dispatch, and manifest recovery validate at the boundary. The session reducer stays pure.

Make Operations Idempotent changed handoff cleanup. A submitted handoff is retained as an owned lease until `--complete-handoff <id>` acknowledges completion or cancellation. Startup sees the owned manifest and refuses a second handoff.

Prove It Works changed verification. Tests drive the production runtime with synthetic PCM, a delayed fake recognizer, and a recording handoff. The native SAPI path has a Windows-only synthetic probe that calls the production dictation recognizer and checks request identity, lexical ranges, and recognized text.

## Native SAPI collector

The recognizer child no longer passes PCM through an environment variable. Multi-second audio can exceed Windows environment limits. The parent writes raw PCM to `powershell.exe` standard input and drains stdout/stderr concurrently.

The PowerShell script no longer calls `Recognize()` in a loop. It compiles a small C# collector with `Add-Type`, attaches `SpeechRecognized`, `SpeechRecognitionRejected`, and `RecognizeCompleted`, starts `RecognizeAsync(Multiple)`, and waits for finite stream completion. Recognized and rejected speech is aggregated through EOF. Word timing uses `RecognitionResult.GetAudioForWordRange(word, word)` and adds phrase audio position once.

## External limitation

Superwhisper's registered Windows file intake is verified. Automatic completion correlation is not. The implementation therefore reports `SubmittedUnconfirmed` and does not claim transcription or auto-paste succeeded.

The external file is not deleted by a timer or process exit. Manual completion is explicit:

```text
voice-switch.exe --complete-handoff <id>
```

The default owned handoff directory is `%LOCALAPPDATA%\voice-switch\dictation-handoffs`. It stores only a UUID WAV, a minimal manifest, and a permanent empty synchronization file. It does not store transcript text.

Windows foreground restoration, global finish/cancel shortcuts, and automatic completion are narrow remaining OS-integration gaps. They are not treated as a core dictation gap.
