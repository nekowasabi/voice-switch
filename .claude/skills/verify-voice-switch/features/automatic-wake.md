# Automatic no-wake recording

With `dictation.noWakeWord.autoIdle` on, speech that starts after the keyboard and mouse have been idle for `idleMs` opens an automatic recording without a wake word. It ends on silence or a stop word and goes to history only. If the user says a wake word while an automatic recording is open, the automatic recording is discarded and a normal wake-word dictation takes over.

## Sub-features

- `AW1` idle speech without a wake word opens `dictation session: automatic rule=auto_b`.
- `AW2` a wake word spoken during an automatic recording replaces it with a wake-word dictation.
- `AW3` the automatic recording does not arm the Superwhisper hotkeys (Escape stays with the foreground app).

## How to get to it (user POV)

- Turn on `autoIdle` in `config.json`, stop touching the keyboard and mouse for 3 s, then speak.

## Driving it with verify.sh

Preconditions: baseline from the index. Write a config variant with `"autoIdle": true` (for example `sed 's/"autoIdle": false/"autoIdle": true/' config.example.windows.json > <scratchpad>/auto.json`). Do not touch the Windows keyboard or mouse for 3 s before and during the run.

- `AW2`: `$V wav natural-one-breath-clean <scratchpad>/auto.json`. Expect `dictation session: automatic rule=auto_b` → `recognition: complete ... leadingWake=True ... rule=auto_b` → `dictation session: automatic replaced by wake word` → `body-start` (range starts after the wake word) → one `handoff`, `handoff_wavs=1`.
- `AW1`: needs a fixture with speech but no wake word. None ships yet. Add one to `tests/windows/compose-fixtures.ps1`, then expect `automatic rule=auto_b` → `handoff ... reason=Silence` with no `replaced` line.
- `AW3`: not provable with this harness. The hotkey hook needs a real dictation window and keyboard input. Report it as not verified and cite the absence of `hotkey: keyboard hook installed` after `automatic` in a live tray log as the closest evidence.

## Gotchas

- `inputIdleSeconds` is the real Windows desktop idle time. If the user is typing, `autoIdle` never fires and the run silently behaves like the default config. Check `inputIdleSeconds=` on the `automatic` line.
- With a `noWakeWord` switch on, handoff lines say `details suppressed`. Prove the source by the session lines, not the handoff message.
- `--output-dir` replaces Superwhisper, so the automatic transcription path (mode `superwhisperMode`, history only) is not exercised here.
