# Citation drift against issue #48

Measured on main `e22fa59ff4d176dc20165e90f52b954372397a90` before the Phase 0 edit. The issue's checklist item "Phase 0 のコード分岐の引用が現行 main と一致していること" was unchecked. The symbols match. Two cited line numbers do not.

## MATCH

- Mac `Sources/voice-switch/MacApp.swift:919` still gates silence on `stopCheck.pending`. The issue named that condition. The same guard's fallback is `?? 1200`.
- Mac `MacApp.swift:926` plays the end sound only for `key == .finish`. Stop-word end plays at the earlier stop-word branch. Silence does not.
- Mac `MacApp.swift:1238` rewrites an automatic origin to strict when the token is `StandaloneStop`. `MacApp.swift:1239` logs `pasting like a wake-word dictation`.
- Windows `dotnet/VoiceSwitch.Windows/DictationRuntime.cs:1006` defers in `TryFinishSilence` while `pending.Count > 0` (or the segmenter utterance is open). The issue named `pending.Count > 0`.
- Windows `DictationRuntime.cs:1076` plays the end sound only for `FinishReason.StandaloneStop` or `FinishReason.FinishCommand`.
- Windows `DictationRuntime.cs:1165` rewrites an automatic origin to `WakeRule.Strict` when the reason is `StandaloneStop`. `DictationRuntime.cs:1168` logs `pasting like a wake-word dictation`.
- `config.example.json:17` is `"endSilenceMs": 1200`. The value matches the issue. The issue cited L32.
- `config.example.windows-url.json:15` is `"endSilenceMs": 2400`. The value matches the issue. The issue cited L28.
- The code fallback is already 1200 on both OSes. `DictationCore.cs:547` and `DictationRuntime.cs:694` use `EndSilenceMs ?? 1200`. The OS split the issue describes is the sample files, not the null fallback.

## LINE DRIFT

- `config.example.json`: issue cited L32. The key is at line 17. Value MATCH (1200).
- `config.example.windows-url.json`: issue cited L28. The key is at line 15. Value MATCH (2400).

## Not cited, same split

- `config.example.windows.json:16` is 2400.
- `config.example.macrowhisper.json:27` is 1200.
- `TryFinishSilence` also defers when the segmenter utterance is open (`HasOpenUtterance`). That is the VAD hangover buffer, not the recognizer queue. Phase 0 removes the recognizer defer and keeps the open-utterance defer.

Hillclimb configs under `.claude/hillclimb/` use 2500. They are experiment snapshots, not the product default.
