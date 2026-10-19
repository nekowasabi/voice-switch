# Wake word then dictation

With `dictation` configured, a wake word opens a dictation. The body is recorded until `endSilenceMs` of silence, a stop word, `maxSeconds`, or the finish hotkey, and the recording is handed to Superwhisper, which pastes the text unless the tmux pane route takes it. A lone wake word with nothing said within `startTimeoutMs` logs `dictation cancelled: nothing said after the wake word`. Wake-word dictations skip the Silero and agreement checks that apply only to automatic recordings, so the recording HUD needs no confirmation. A one-breath dictation shows `phase: recording` at once. A lone wake word shows `phase: waiting` (microphone glyph) first, then `phase: recording` (red pulsing dot) once speech is heard. During the handoff the listener logs `delivery: processing` (spinning arc) and afterwards `delivery: done` (green check, then the HUD closes) or `delivery: failed` (red cross) when Superwhisper returned no words; see hud-labels.md.

## Sub-features

- `dictation-one-breath` cuts the wake word off when the body follows in the same breath.
- `dictation-waiting` waits for the body after a lone wake word, up to `startTimeoutMs`.
- `dictation-stop-word` ends the body with a stop word.

## How to get to it (user POV)

- Say `音声入力`, then the text, then pause or say `入力ストップ`.

## Driving it with verify.sh

Preconditions:

- The user agreed to a run that drives their Superwhisper.
- A scratchpad config equal to `config.safe.json` plus `"dictation": {"endSilenceMs": 1200, "startTimeoutMs": 3000, "maxSeconds": 60}`.

- **Waiting dictation.** Run `$V say dict-wait "音声入力 [[slnc 1000]] 今日は晴れです [[slnc 2000]]"` and `VS_RUN_ID=<id> $V run dict-wait <config>`. The output has `dictation started (waiting for text)`, `phase: waiting`, `phase: recording`, `dictation ended by <about 1200> ms silence` (the measured silence, frame-quantized), `dictation session: end reason=Silence`, `delivery: processing`, then `dictation: <n> chars in <ms> ms`. `dictation delivered: superwhisper` follows when the tmux pane route did not take the text, then `delivery: done`. `verify.sh run` does not echo `delivery:` lines; read `stdout.txt`.
- **Stop word.** Run `$V say dict-stop "音声入力 [[slnc 1000]] 今日は晴れです [[slnc 900]] 入力ストップ"` and run it with the same config. The output has `dictation finished by stop word`, `phase: ended`, and `dictation session: end reason=StandaloneStop`.
- **Lone wake, no Superwhisper.** The cancel path never hands off. `$V say wake-lone "音声入力 [[slnc 5000]]"` and `VS_RUN_ID=<id> $V run wake-lone <config>`: run `maint` (2026-10-17) logged `dictation started (waiting for text)`, `phase: waiting`, `dictation cancelled: nothing said after the wake word`, `phase: idle`, and no `delivery:` line.

## Gotchas

- The handoff opens Superwhisper and it pastes into the frontmost app. Keep a scratch text field in front.
- `dictation: no superwhisper result within 30 s sid=...` means no new run in Superwhisper's recordings folder finished, i.e. none had a `meta.json` with a result or `processingTime`. `dictation: superwhisper heard no words` means one finished with an empty result. Either way nothing falls back to local history and the HUD shows the red cross (`delivery: failed`); check Superwhisper itself.
- The `dictation ended`, `finished`, `cancelled`, `dropped`, `session:` and `dictation:` lines carry a trailing ` sid=...` (and `dictation:` also ` audio=<s>s`, the untrimmed length). `heard:` and `dictation started` lines carry none. Match them by prefix.
- A wake word followed straight by the stop word leaves almost no audio and logs `dictation dropped: audio shorter than minSpeechMs`.
