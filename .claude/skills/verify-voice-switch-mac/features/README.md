# voice-switch (macOS) verification map

This directory is the maintained source for verifying what a Mac user of voice-switch experiences. Read the index, then use the matching feature file as the recipe.

| Feature | File | Touches Superwhisper |
|---|---|---|
| Wake word runs the command | [wake-command.md](wake-command.md) | no |
| Stop word | [stop-word.md](stop-word.md) | no |
| Stop word at the end of a long utterance | [stop-word-long-utterance.md](stop-word-long-utterance.md) | yes (stop-word endings paste; silence endings stay in history) |
| Wake word then dictation | [wake-dictation.md](wake-dictation.md) | yes |
| Automatic sessions without a wake word | [automatic-wake.md](automatic-wake.md) | yes |
| Speaker gate: automatic recordings of another voice are dropped, and 2 s+ stretches of another voice are cut from handed-off audio | [speaker-gate.md](speaker-gate.md) | SG9, SG4 yes (missing-mode config; SG4 automatic only) |
| Paste routing of automatic recordings (silence end never pastes, stop-word end pastes) | [paste-routing.md](paste-routing.md) | yes (real mode, TextEdit as target) |
| Local transcription without Superwhisper (LT0 engine baseline runs today; LT1–LT6 wait for the implementation) | [local-transcription.md](local-transcription.md) | LT0 reads its recordings only; LT5 yes |
| Only the user's voice starts a recording: speaker playback is echo-cancelled before the VAD (not yet run on a Mac) | [playback-silence.md](playback-silence.md) | VO1 no (missing-mode config); VO2 yes |
| HUD: vector glyphs, spinner until superwhisper finishes, check/cross, superwhisper's own toggle key and Esc | [hud-labels.md](hud-labels.md) | no (`$V hud`); the direct toggle recipe yes |
| Microphone switching, built-in mic fallback on unplug, sharing the mic with superwhisper, and dictation on every input (drives the installed app) | [mic-switch.md](mic-switch.md) | MS1–MS3, MS7–MS12 no; MS4–MS6 yes |

## Baseline preconditions

- `$V build` was run on the tree you are proving, and `$V doctor` exits 0.
- `V=.claude/skills/verify-voice-switch-mac/verify.sh`, run from the repo root.
- Use one `VS_RUN_ID` per proof so its runs share one evidence folder.
- Never drive the user's installed VoiceSwitch.app. Only drive `--simulate` processes this skill started. The one exception is [mic-switch.md](mic-switch.md), which needs the user's agreement first.

## Driving conventions

- Fixtures are Kyoko speech made with `$V say`. Put `[[slnc 800]]` or longer between phrases that must be separate utterances. Shorter gaps get merged into one utterance.
- The safe config (`config.safe.json`) has wake word `音声入力`, stop words `入力ストップ` and `st`, and no `dictation`.
- Feature configs go in your scratchpad. Keep `__EVIDENCE__` in commands so markers land in the evidence folder.

## Proof and skip reporting

- Quote the `heard:` line and the decision line that follows it.
- Name the marker file or Superwhisper result that proves the side effect.
- Report a feature that needs Superwhisper as not run when the user has not agreed to it. Do not report it as verified through the safe config.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph describing the user-visible behavior. It then uses exactly four H2 sections in this order: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify.sh`, and `Gotchas`.
