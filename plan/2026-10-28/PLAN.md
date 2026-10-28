# .NET structure refactor slices

Behavior-preserving moves under `dotnet/` only. No Swift edits, no parity-contract edits, no push, no merge, no PR. Acceptance for every slice is all 14 items in [SPEC.md](./SPEC.md).

Measured sizes on `de166b1`: `dotnet/VoiceSwitch.Windows/DictationRuntime.cs` is 2624 lines, `dotnet/VoiceSwitch.Windows/Program.cs` is 956 lines. The Windows project globs `*.cs`. The test project does not. Item 6 fails until each new top-level file has a `Compile Include` in `dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj`, with a `Link` in the existing `VoiceSwitch.Windows.<File>.cs` style.

`tests/parity` reads specific files as text. These strings have to remain in the file named here. Moving them fails item 4.

Stay in `dotnet/VoiceSwitch.Windows/Program.cs`:

- `public static class CommandRunner`, `ComSpec`, `"/c " + command`
- `ReloadIfChanged`, `config reload failed, keeping previous`, `ConfigReload.Unchanged`
- `config.Dictation is not null`, `new WindowsDictationRuntime`, `MicCapture.Open(null, config)`
- `--recognizers`, `--check-device`, `RunRecognizerDiagnostics`, `RunDeviceDiagnostic` (the call sites in `Program.Run` and the help text satisfy this)
- `Program.Run`, `SelfTest` (including `if (!VerifySegmenter())`), and `HandleRecognizerLine` (the parity checker extracts that method from this file)

Stay in `dotnet/VoiceSwitch.Windows/DictationRuntime.cs`:

- `inflight is { IsCompleted: false }`, `dictation dropped: previous one still in flight`
- `hotkeys.Take()`, `DictationCommand.Cancel`, `FinishReason.FinishCommand`, `hotkeys?.End()`
- `micInUseBy(config.SkipWhileMicInUseBy`, `is using the microphone`, `MicInUse.By`
- `TranscribeDeadline.SecondsFromPcmBytes(pcm.Length)`

That last call sits inside `SpeechPowerShellDictationRecognizer`. Leave that class, and its PowerShell `Script` string, in `DictationRuntime.cs`.

Stack:

1. Slice 1 on `feat/refactor-structure` (this branch, after the docs commit).
2. Slice 2 on `feat/refactor-structure-s2`, cut from slice 1.
3. Slice 3 on `feat/refactor-structure-s3`, cut from slice 2.

## 1. Extract WinMmCapture #high

Goal: move the waveIn capture type out of `DictationRuntime.cs` without changing its public or internal shape.

Files:

- Create `dotnet/VoiceSwitch.Windows/WinMmCapture.cs` with `public sealed class WinMmCapture` and everything nested in it (`WaveCallback`, `WaveFormat`, `WaveHdr`, `IWaveInNative`, the P/Invoke declarations).
- Delete that class from `DictationRuntime.cs`. The class starts at `public sealed class WinMmCapture` and ends at the brace before `public static class PowerShellScript`.
- Add a `Compile Include` for the new file to `VoiceSwitch.Windows.Tests.csproj`.

Leave the nested types nested. `TestSupport.cs` constructs `WinMmCapture.IWaveInNative`, `WinMmCapture.WaveFormat`, and `WinMmCapture.WaveCallback`.

Acceptance: SPEC items 1–14.

Risk: the wave header layout is covered by `WaveFormatSizeForTest` (18) and `WaveHeaderSizeForTest` (48). A reordered field changes those. After the move, run `dotnet format` on `WinMmCapture.cs` and `DictationRuntime.cs`. Formatting `DictationRuntime.cs` also rewrites the three pre-existing whitespace findings on the comment lines and `internal const string Script` just above the recognizer script (baseline lines 2147–2149). Keep that whitespace result. Do not change the characters inside the `"""` script. `NoWakeTests.cs` stays unformatted.

## 2. Extract the child-process host #high

Goal: move the two process helpers out of `DictationRuntime.cs`. Call sites keep the same type names.

Files:

- Create `dotnet/VoiceSwitch.Windows/ChildProcessJob.cs` containing `public static class PowerShellScript` and `public static class ChildProcessJob`, including `ExtendedLimitInformation` and its `DllImport`s.
- Delete those two classes from `DictationRuntime.cs`.
- Add a `Compile Include` for the new file to the test csproj.

`Program.cs`, `WordAgreement.cs`, and `SpeechPowerShellDictationRecognizer` call `PowerShellScript.StartInfo` and `ChildProcessJob.Start`. Same namespace, so the call sites do not change.

Acceptance: SPEC items 1–14.

Risk: `ExtendedLimitInformation` is a sequential layout passed to `SetInformationJobObject`. Move the struct and the constants with the class, in the same order. This slice starts from slice 1, so `WinMmCapture` is already gone and the two classes sit between the runtime class and `SpeechPowerShellDictationRecognizer`.

## 3. Extract the wake PowerShell host from Program.cs #high

Goal: move the resident-mode speech host out of `Program.cs`. `Program.Run` still calls it, and the parity strings listed above stay in `Program.cs`.

Files:

- Create `dotnet/VoiceSwitch.Windows/SpeechPowerShell.cs` containing, in this order: `RecognizerMessage`, `SpeechPowerShell`, `ISpeechProcess`, `SpeechProcess`.
- Delete those types from `Program.cs`. Keep `Program`, `CheckMode`, `ResidentRuntime`, `ConfigFile`, `ConfigReload`, `CommandRunner`, `Log`, and `SelfTest`.
- Add a `Compile Include` for the new file to the test csproj.

The wake `Script` raw string moves verbatim with `SpeechPowerShell`. `Program.Run` still contains `SpeechPowerShell.RunRecognizerDiagnostics`, `SpeechPowerShell.RunDeviceDiagnostic`, `SpeechPowerShell.Start`, and `CommandRunner.Run`.

Acceptance: SPEC items 1–14.

Risk: `check_windows_runtime_call_chain` extracts `HandleRecognizerLine` from `Program.cs` only. That method stays on `ResidentRuntime` in `Program.cs`. The self-test mutation gate looks for `if (!VerifySegmenter())` in `Program.cs`. `SelfTest` stays there. Format the new file and `Program.cs`. The raw string contents stay byte-for-byte.
