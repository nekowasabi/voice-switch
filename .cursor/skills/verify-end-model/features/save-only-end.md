# Save-only end

An automatic recording ends in history whether the user went quiet, pressed finish, or said the stop word. None of those endings paste.

## Sub-features

- `P0-SAVE-1` the paste log line is gone from both runtimes, the Windows test, the READMEs, and the older verify-skill pages that taught the exception.
- `P0-SAVE-2` Mac no longer rewrites an automatic origin to strict on `StandaloneStop`.
- `P0-SAVE-3` Windows no longer rewrites an automatic origin to `WakeRule.Strict` on `StandaloneStop`.
- `P0-SAVE-4` a Windows behavior test named `AutomaticEndsStayHistoryOnly` covers silence, finish, and stop-word. Each handoff stays `IsAutomatic`, and none of the logs contain the removed paste line.
- `P0-SAVE-5` `NoWakeTests` no longer asserts `Wake.IsAutomatic: false` on the stop-word end. That assertion is the old paste destination.
- `P0-SAVE-6` the `Program.cs` test list no longer says that case `pastes when ended by the stop word`.
- `P0-SAVE-7` `AutomaticEndsStayHistoryOnly` in `NoWakeTests.cs` asserts `trimExcluded` for the stop-word end, and names `FinishReason.StandaloneStop` and `FinishReason.FinishCommand`. The stop word ends the session before the silence timer. The saved audio ends where the stop word starts, so the transcript made from it does not contain the stop word. The finish control stays automatic. Decision 2 keeps that button save-only.

## How to get to it (user POV)

- Let an automatic recording end by waiting out the silence.
- End another with the finish control.
- End another by saying the stop word.
- In all three, the text is in history and the front app is unchanged.
- The stop-word recording's saved text does not contain the stop word.

## Driving it with check.py

Preconditions:

- Doctor passes.
- The Windows behavior exe is how `P0-SAVE-4` and `P0-SAVE-7` are executed. The source check proves the test name, `trimExcluded`, and the two finish reasons are in `NoWakeTests.cs`.

- **Drop the exception.** Run `python3 .cursor/skills/verify-end-model/check.py drive save-only-end`. `P0-SAVE-1` through `P0-SAVE-7` PASS.
- **Run the three ends.** Run `dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --ignore-failed-sources`. The line for `AutomaticEndsStayHistoryOnly` passes. The handoff origin stays automatic for silence, `FinishCommand`, and `StandaloneStop`. The stop-word log contains `trimExcluded`, and the saved transcript does not contain the stop word.
- **Proof.** Quote the PASS lines and the test line. If dotnet cannot start the exe, report that command and leave the runtime half `UNVERIFIED`. Do not mark `P0-SAVE-4` or `P0-SAVE-7` PASS from the source strings alone in the PR narrative. The checker PASS means those strings are present. The exe result is separate evidence.

## Gotchas

- Wake-word dictation still hands off for paste. This feature is only the automatic origin.
- A stop word still ends the session before the silence timer. The cut removes it from the saved audio, so the transcript does not contain it. The destination stays history.
- Deleting the log line from a comment while leaving the origin rewrite fails `P0-SAVE-2` or `P0-SAVE-3`.
- The older verify skills are user-facing maps. Leaving the paste sentence there fails `P0-SAVE-1`.
