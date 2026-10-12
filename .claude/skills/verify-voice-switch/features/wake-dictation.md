# Wake-word dictation

The user says a wake word (`音声入力`), then the text to type. The recording ends on the standalone stop word (`入力ストップ`) or on silence. The body without the wake word and without the stop word is handed to Superwhisper, which here is replaced by a WAV in `handoffs/`.

## Sub-features

- `WD1` wake word then body then a separate stop word ends with `reason=StandaloneStop`.
- `WD2` a stop word said after a longer pause still ends the same session.
- `WD3` wake word and body without a standalone stop word end with `reason=Silence`.
- `WD4` a stop word inside the sentence (`今日は入力ストップではありません`) is body text, not a stop.

## How to get to it (user POV)

- Speak into the microphone while the tray app is listening. Here, a fixture WAV replaces the microphone.

## Driving it with verify.sh

Preconditions: baseline from the index, default config.

- `WD1`: `$V wav wake-body-separate-stop-clean`. Expect `leadingWake=True` → `body-start` → `standaloneStop=True` → `standalone-stop` → `handoff: RecordedLocally ... reason=StandaloneStop`, `handoff_wavs=1`. The handoff `range=` ends where `trimExcluded=` begins.
- `WD2`: `$V wav late-stop-after-silence-clean`. Same lines as `WD1`, with the stop recognized about 2 s later, `handoff_wavs=1`.
- `WD3`: `$V wav natural-one-breath-clean`. Expect `leadingWake=True` → `body-start` → `handoff ... reason=Silence`, `handoff_wavs=1`.
- `WD4`: `$V wav embedded-stop-clean`. Expect `body-start` → `handoff ... reason=Silence` and no `standalone-stop` line, `handoff_wavs=1`.
- Repeat with the `-silence-noise2` fixtures to prove the same lines under noise.

## Gotchas

- Superwhisper, the mode switch, and the paste are not exercised. `RecordedLocally` here means `LocalRecordingHandoff` wrote the WAV.
- A body longer than the segmenter cap arrives as `extent=PrefixHead`. That is normal, not a failure.
- With any `noWakeWord` switch on, the log suppresses transcript text and handoff details (`details suppressed`). Use the default config when you need them.
