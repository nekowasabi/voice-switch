# Silence default and sound

Both sample configs and the null fallback use 1500 ms. A silence end plays the same end sound as finish and the stop word.

## Sub-features

- `P0-DEFAULT-1` `dictation.endSilenceMs` is 1500 in `config.example.json`, `config.example.windows.json`, `config.example.windows-url.json`, and `config.example.macrowhisper.json`.
- `P0-DEFAULT-2` Mac no longer falls back with `endSilenceMs ?? 1200`.
- `P0-DEFAULT-3` Windows core and the runtime no longer fall back with `EndSilenceMs ?? 1200`.
- `P0-DEFAULT-4` the high-value warning on both OSes says `example is 1500`.
- `P0-DEFAULT-5` `example is 2400` is gone from those two warning sites.
- `P0-DEFAULT-6` `OmittedEndSilenceMsDefaultsTo1500` exists. A config that omits `endSilenceMs` logs 1500 and its silence deadline uses 1500.
- `P0-SOUND-1` Mac no longer limits `playStopSound()` to `key == .finish` on the silence path. Silence plays it too. The stop-word path still plays it.
- `P0-SOUND-2` the Windows end-sound condition is `FinishReason.Silence or FinishReason.StandaloneStop or FinishReason.FinishCommand`. A bare `FinishReason.Silence` elsewhere, such as the end-of-file finish, does not count.
- `P0-SOUND-3` `SilenceEndPlaysStopSound` exists. A body that ends on silence rings once. A start timeout does not ring.

## How to get to it (user POV)

- Install from a sample config and talk. Quiet for about 1.5 seconds ends the recording and plays the end sound.
- The same wait is 1.5 seconds on Mac and on Windows.

## Driving it with check.py

Preconditions:

- Doctor passes.
- `.claude/hillclimb/**` configs stay at their experiment values. `tests/parity/fixtures/text_matching.json` stays at 1200. `DictationRuntimeTestConfig` keeps its explicit 1200 parameter so existing timing logs stay explicit.

- **Configs and fallbacks.** Run `python3 .cursor/skills/verify-end-model/check.py drive silence-default-and-sound`. Every required item PASSes.
- **Omitted value.** The Windows exe runs `OmittedEndSilenceMsDefaultsTo1500`.
- **Sound.** The Windows exe runs `SilenceEndPlaysStopSound`. `StopSounds` is 1 for the silence end and 0 for the start timeout.
- **Proof.** Quote the checker lines and both test lines.

## Gotchas

- Changing the test helper's default from 1200 to 1500 rewrites unrelated tests. The product default is the null fallback and the sample files.
- The warning text cites `example is 1500`. The threshold stays above 10000 ms. `ShouldWarnHighEndSilence(2400)` stays a case that does not warn.
- `maxSeconds` does not gain an end sound in this feature. The issue asks for silence.
- A wake-word session uses the same `dictation.endSilenceMs`. Phase 0 unifies that field. It does not add a separate wake-word default.
