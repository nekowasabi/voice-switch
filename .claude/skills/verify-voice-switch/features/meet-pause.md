# Pause during Google Meet (Windows, implemented; not driven on the tray)

While Chrome or Edge holds the microphone (a Google Meet call), the tray stops listening. No wake word, stop word, or automatic recording is heard, and the tray icon shows 停止中. When the call ends, the tray resumes on its own, but only if the user had not paused it. The macOS app has had this since `fc7144b` / `a2c5975`. Windows has it on the `win-port` branch, behind `pauseDuringMeet`, with unit tests W1-1–W1-4 and the ordering model-checked by the `verify-meet-pause-windows` skill. None of the items below has run on the tray. The log lines to quote are `tray: meet hold (<browser>)` and `tray: meet resume`.

## Sub-features

The properties are model-checked and are not driven here: `MP-F1` (no mic during the call; `HeldMeansOff`, `MicOffInCall`, `HoldTakesEffect`), `MP-F2` (a manual pause survives the call; `ManualPauseKept`), and `MP-F3` (resume after the call; `HeldResumes`). See `../../verify-meet-pause-windows/SKILL.md` for the design rules that the port must follow.

Acceptance items on the real tray need the user's agreement to drive the installed tray and join a call:

- `MPW0` **Detection signal.** During a live Meet in Chrome and then in Edge, `MicInUse.By(["chrome","msedge"])` returns the browser. If Meet's capture shows up under another process name (`audiodg`, a helper), the list must change.
- `MPW1` Joining a call logs the hold and the tray shows 停止中 within 3 s.
- `MPW2` During the call, `音声入力` / `入力ストップ` and a no-wake phrase produce no `dictation session:` line.
- `MPW3` Leaving the call logs the resume, and the tray shows 待ち受け中 within 3 s. The wake word works again.
- `MPW4` Pausing from the menu (or `pauseShortcut`) before the call, then leaving the call, keeps 停止中.
- `MPW5` Pressing 開始 during the call listens until the call ends, and it is still listening after the call.
- `MPW6` Picking another mic from the tray menu, or unplugging the USB mic, during the call does not return to 待ち受け中.
- `MPW7` Starting the tray during a call ends held, not listening, within one poll.
- `MPW8` With `pauseDuringMeet` absent or false, nothing changes from today's behavior.

## How to get to it (user POV)

- Put `"pauseDuringMeet": true` in the tray's config.json, start the tray, then join a Google Meet call in Chrome with the mic on, and leave it.
- Optionally use 一時停止 / 開始, the pause hotkey, or the マイク menu before, during, or after the call.

## Driving it with verify.sh

`$V wav` feeds a WAV through `--input-wav` and has no browser holding the mic, so it cannot reach this feature. Drive MPW0–MPW8 by hand on the installed tray after the port lands. Quote the hold and resume log lines and the `tray:` lines from the log (tray menu → ログを開く). For MP-F1–MP-F3, run `sh .claude/skills/verify-meet-pause-windows/check.sh`.

## Gotchas

- Any Chrome or Edge capture counts as a call, not only Meet.
- `MicInUse` failing reads as "no call", so the tray resumes mid-call.
- If the runtime ignores a stop (`StopCurrentRunAsync` timing out), the capture can stay open during the call. The model does not cover this.
- A manual pause that races a runtime crash can be undone by the device retry on today's code (see the finding in the skill). Do not report it as a Meet bug.
