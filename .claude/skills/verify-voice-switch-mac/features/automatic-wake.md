# Automatic sessions without a wake word

With `dictation.noWakeWord` switched on, speech can open a dictation without the wake word. Three ways exist. `followup` keeps listening for `followupMs` after a dictation. `autoIdle` starts on speech; the target behavior (issue "自動ウェイクを操作中でも発火させる") no longer requires the keyboard and mouse to be idle, it only uses idle to choose where the result goes. `relaxedReadingDistance2` accepts a reading close to a wake word. All three require `dictation.superwhisperMode` to be set, and the recording goes only to history, never pasted.

## Sub-features

- `auto-idle` logs `dictation session: automatic rule=auto_b` after the keyboard and mouse were idle. The result goes to Superwhisper's no-auto-paste mode.
- `auto-busy` (target, not yet implemented on `d3685d6`). Speech while the user is operating the Mac also logs `dictation session: automatic rule=auto_b inputIdleSeconds=<below 1>`. Checks:
  - `AB1` the session starts at all (the baseline does not, see Gotchas).
  - `AB2` the recording starts at the phrase head, not at the last input. The session's audio length on `dictation session: end ... seconds=` is close to the spoken phrase (the `busy-speech` phrase is about 2 s), not near zero.
  - `AB3` the route is chosen at submit. Still operating at submit means local recognition into history and no Superwhisper call. The `end` line carries `route=local`; an idle submit carries `route=superwhisper`. No `open -a superwhisper` and no mode switch line appear on the local route.
  - `AB4` the frontmost app never changes. `busy-input.txt` reports `frontmost changes 0`.
  - `AB5` a wake word spoken while operating still wins. `音声入力 今日は晴れです` under busy input logs `automatic replaced by wake word` and a normal dictation.
- `auto-followup` logs `rule=followup` within `followupMs` of a finished dictation.
- `auto-relaxed` logs `dictation session: wake-only rule=relaxed_d2`.
- `auto-short-dropped` drops an automatic recording shorter than `minHandoffMs` (default 1000). A cough or a click logs `dictation dropped: automatic audio shorter than minHandoffMs` and never reaches Superwhisper. Drive it with `$V say cough2 "[[slnc 1500]] えへん [[slnc 3000]]"` and `SIMULATE_REALTIME=1 $V run cough2 config.stop-auto.json` while nobody touches the keyboard or mouse. Under busy input the same drop must still happen (`AB6`).

## How to get to it (user POV)

- Idle path. Stop typing for `idleMs`, then speak.
- Busy path. Keep typing or moving the mouse and talk at the same time.

## Driving it with verify.sh

Preconditions:

- The user agreed to a run that may drive their Superwhisper and switch its mode. The busy path must not touch Superwhisper; if it does, that is the failure being shown.
- Config `config.stop-auto.json` in this folder (`superwhisperMode: input`, `autoIdle: true`, `idleMs: 1000`), or a scratchpad copy of it.

- **Idle start.** Do not touch the keyboard or mouse. Run `$V say auto-idle "今日は晴れです [[slnc 2000]]"` and `VS_RUN_ID=<id> $V run auto-idle <config>`. The output has `dictation session: automatic rule=auto_b inputIdleSeconds=<n>` with n of at least 1.
- **Busy start (AB1 to AB4).** `$V say busy-speech "[[slnc 1500]] 今日の会議は三時からです [[slnc 3000]]"`, then `VS_BUSY_INPUT=1 SIMULATE_REALTIME=1 VS_RUN_ID=<id> $V run busy-speech config.stop-auto.json`. `VS_BUSY_INPUT=1` runs `busy-input.swift`, which posts an in-place mouse move every 200 ms for 45 s and samples the frontmost app. Expect, in order: `dictation session: automatic rule=auto_b inputIdleSeconds=0.<n>`, `dictation session: end ... route=local seconds=<about 2>`, one `dictation recognition: complete`, and `busy-input: ... frontmost changes 0`.
- **Busy wake word (AB5).** `$V say busy-wake "[[slnc 1500]] 音声入力 今日は晴れです [[slnc 3000]]"` and run it the same way. Expect `automatic replaced by wake word`.
- **Busy cough (AB6).** Run `cough2` with `VS_BUSY_INPUT=1`. Expect `dictation dropped: automatic audio shorter than minHandoffMs`.

## Gotchas

- With `noWakeWord` enabled the `heard:` lines are suppressed for privacy. Read the `dictation session:` and `dictation recognition:` lines instead.
- Missing `superwhisperMode` silently disables all three. A run with no `rule=auto_b` line should first check the config.
- Without `VS_BUSY_INPUT`, your own typing during the run changes `inputIdleSeconds`. Do not type during idle-path runs.
- Baseline on `d3685d6` (2026-10-14, run `busy-baseline`): busy input gives only `dictation recognition: complete id=1 rule=strict inputIdleSeconds=0.007` and no `automatic` line. That is the user's complaint, reproduced. A passing `auto-busy` run must differ from it.
- `AB4` counts every frontmost change, including the user's own app switches. The 2026-10-14 baseline logged 7 changes, all from the person at the keyboard. Ask the user to leave the Mac alone during the run, or judge AB4 only by changes to `com.superduper.superwhisper` or any change within 2 s after the `end` line.
- `route=` is the log contract this verification expects the implementation to add. If the implementation names it differently, update this file in the same PR.
- Synthetic Kyoko audio has no keyboard noise, so these runs do not show whether key clicks leak into the recording. That needs a live check with real typing.
