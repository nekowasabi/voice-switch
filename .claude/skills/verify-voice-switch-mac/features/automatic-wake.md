# Automatic sessions without a wake word

With `dictation.noWakeWord` switched on, speech can open a dictation without the wake word. Three ways exist. `followup` keeps listening for `followupMs` after a dictation. `autoIdle` starts when the keyboard and mouse have been idle for `idleMs`. `relaxedReadingDistance2` accepts a reading close to a wake word. All three require `dictation.superwhisperMode` to be set, and the recording goes only to that mode's history.

## Sub-features

- `auto-idle` logs `dictation session: automatic rule=auto_b`.
- `auto-followup` logs `rule=followup` within `followupMs` of a finished dictation.
- `auto-relaxed` logs `dictation session: wake-only rule=relaxed_d2`.
- `auto-short-dropped` drops an automatic recording shorter than `minHandoffMs` (default 1000). A cough or a click logs `dictation dropped: automatic audio shorter than minHandoffMs` and never reaches Superwhisper. Drive it with `$V say cough2 "[[slnc 1500]] えへん [[slnc 3000]]"` and `SIMULATE_REALTIME=1 $V run cough2 config.stop-auto.json` while nobody touches the keyboard or mouse.

## How to get to it (user POV)

- Stop typing for `idleMs`, then speak.

## Driving it with verify.sh

Preconditions:

- The user agreed to a run that drives their Superwhisper and switches its mode.
- A scratchpad config with `dictation` plus `"superwhisperMode": "<mode key with auto-paste off>"` and `"noWakeWord": {"autoIdle": true, "idleMs": 1000}`.

- **Idle start.** Do not touch the keyboard or mouse. Run `$V say auto-idle "今日は晴れです [[slnc 2000]]"` and `VS_RUN_ID=<id> $V run auto-idle <config>`. The output has `dictation session: automatic rule=auto_b inputIdleSeconds=<n>` with n of at least 1.

## Gotchas

- With `noWakeWord` enabled the `heard:` lines are suppressed for privacy. Read the `dictation session:` and `dictation recognition:` lines instead.
- Missing `superwhisperMode` silently disables all three. A run with no `rule=auto_b` line should first check the config.
- Typing during the run resets `inputIdleSeconds` and blocks `auto_b`.
