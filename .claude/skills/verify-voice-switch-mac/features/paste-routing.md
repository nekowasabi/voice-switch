# Paste routing of automatic recordings

An automatic (no-wake-word) recording ended by silence goes to history only and must never paste. One ended by the stop word is pasted into the app in front, like a wake-word dictation. The routing depends on Superwhisper's mode at the moment it writes its final result, so this feature is proven against the real Superwhisper, not `vs-verify-missing`.

## Sub-features

- `PR1` silence end does not paste: voice-switch switches Superwhisper to `dictation.superwhisperMode` (autoPaste off) and restores the previous mode only after Superwhisper has judged the audio.
- `PR2` stop-word end pastes: `automatic ended by stop word, pasting like a wake-word dictation`, then Superwhisper pastes in the active mode.
- `PR3` the restore waits for a judged run. Superwhisper 3.0.1 writes `meta.json` three times: a placeholder (`duration` 0, `processingTime` 0, empty `result`), then `result`, then `llmResult` when the mode has a language model. A run that heard nothing ends with a filled `duration` and an empty `result`. Restoring at the placeholder pasted a silence-ended recording on 2026-10-16.
- `PR4` no judged run within 30 s keeps the temporary mode (`superwhisper mode: kept ... until the run finishes`); the next handoff or launch restores it.

## How to get to it (user POV)

- Turn on `dictation.noWakeWord.autoIdle` and set `dictation.superwhisperMode` to a Superwhisper mode with auto-paste off.
- Speak without a wake word and stop talking: nothing is pasted, the text shows up in history. Speak and end with `入力ストップ`: the text is pasted.

## Driving it with verify.sh

Preconditions: baseline from the index, the user's agreement (it drives their Superwhisper and TextEdit), and nobody touching the keyboard or mouse.

- `PR3` without Superwhisper: `swiftc Sources/voice-switch/SuperwhisperModes.swift tests/macos/SuperwhisperResult/main.swift -o /tmp/superwhisper-result && /tmp/superwhisper-result`. It replays the observed write sequences and fails on the 2026-10-15 code (placeholder read as a finished empty run).
- Fixtures: `sg-own-auto` (6 s of the user's own speech, then silence) and `sg-own-stop` (the first 7.6 s of it, 0.5 s silence, Kyoko `入力ストップ` at 0.6 gain, 3 s silence), both 16 kHz in the fixtures folder.
- Config: a scratchpad copy of `config.stop-auto.json` with `"dictation": {..., "superwhisperMode": "<the user's no-paste mode>", "noWakeWord": {"autoIdle": true}}`.
- `PR1` + `PR2` at scale: `.claude/skills/verify-voice-switch-mac/paste-routing.sh <runs per case> <config>`. It alternates the two fixtures with TextEdit in front and prints `routed correctly <n>/<total>`; exit 0 only when every run routed correctly. Zero misroutes in 150 runs per case is what supports "99 % or better" (rule of three); 20 per case only supports about 93 %.
- Observed 2026-10-16 with the fix: 10/10 silence ends without a paste and the stop-word end pasted. Forcing the mode back to `default` between the placeholder and the final write pasted the text into TextEdit, which is the failure this feature guards.

## Gotchas

- `--simulate` without `SIMULATE_REALTIME=1` ends before the silence closes the session.
- The stop word must follow the speech within the automatic `endSilenceMs`; a longer gap ends the session by silence first and the stop word opens a separate, empty one.
- Superwhisper's `modeName` in `meta.json` is the mode at each write, not at the start, so it shows which mode the final write (and the paste decision) used.
- Windows has its own lookup (`SuperwhisperHandoff.FindResult`), which only accepts a non-empty result and so never reads the placeholder as finished. It still restores the mode after the 30 s wait.
