# Pause during Google Meet

While a Google Meet call holds the microphone, voice-switch stops listening: no wake word, stop word or automatic recording is heard, and the menu bar shows 🔕. When the call ends, voice-switch resumes on its own, but only if the user had not paused it by hand. Implemented 2026-10-29 in `AppDelegate.followMeet` (`MacApp.swift`); MP-F1–MP-F3 are model-checked by the `verify-meet-pause` skill, and MP1–MP8 have not been run on the installed app yet.

## Sub-features

State words used below: `userWanted` is the user's menu toggle, `meetHold` is `AppDelegate.meetHeld`, and `running` is `Listener.running` (the engine tap is open). The hold calls `Listener.stop()`, so `Listener.wanted` is false while held and `meetHeld` remembers to resume.

Decided by the user on 2026-10-29:

- Meet runs in a browser, and only Chrome and Edge are supported. A call is "a bundle ID in the fixed `AppDelegate.meetBrowsers` list holds microphone input" (`micInUse(by:)`, polled every 2 s). Any mic use by those browsers counts, not only Meet.
- A manual resume during the call wins over the hold until that call ends.

Properties for the formal model (TLA+, every interleaving of Meet start/end, menu toggle, `AVAudioEngineConfigurationChange`, device-list change, the 10 s retry in `attemptStart`, and `checkStall`):

- `MP-F1` **No mic during Meet.** Once the hold has been applied, `meetHold ∧ ¬override ⇒ ¬running`. Harm if broken: voice-switch hears the call and pastes or runs commands from it. The restart paths (`restart()`, the retry timer's `wanted ∧ ¬running`, `checkStall`) are the likely breakers, because Meet opening the mic can itself post a configuration change.
- `MP-F2` **Meet end respects a manual pause.** `¬userWanted` before or during the call ⇒ the call ending never sets `running`. Harm: a user who turned voice-switch off finds it listening again.
- `MP-F3` **Meet end resumes.** `userWanted ∧ Meet ends` ⇒ eventually `running` (assuming `start()` eventually succeeds). Harm: voice-switch stays silently off after every call.
- `MP-F4` **No new recording starts under the hold.** Follows from MP-F1: with the tap closed no frame reaches `consume()`, and `halt()` drops a half-collected utterance. Not a separate model property.
- `MP-F5` **Menu matches state.** `refresh()` reads `running` and `wanted` directly; checked on the app by MP4/MP5, not in the model.

Acceptance items on the real app (`mic-switch.md` style, needs the user's agreement):

- `MP0` **Detection signal.** During a live Meet in Chrome, `mic-holder` / `micInUse` reports which bundle ID holds input (`com.google.Chrome`, a helper, or a PWA ID). Runnable today. Decides the default list.
- `MP1` Joining a call logs the hold and shows 🔕 within 3 s.
- `MP2` Saying `音声入力` and `入力ストップ` during the call produces no `heard:` decision and no marker.
- `MP3` Leaving the call logs the resume and shows 🔔 within 3 s; the wake word works again.
- `MP4` Pausing from the menu before joining, then leaving the call, keeps 🔕 (MP-F2).
- `MP5` Resuming from the menu during the call listens until the call ends, and the call ending leaves it listening (override default).
- `MP6` Unplugging or switching the mic during the call does not reopen input (MP-F1 restart paths).
- `MP7` Launching voice-switch while a call is already running starts held, not listening.
- `MP8` A dictation open when the call starts is ended without pasting (MP-F4).

## How to get to it (user POV)

- Join a Google Meet call in Chrome with the microphone on, then leave it.
- Optionally toggle 一時停止 / 再開 from the menu bar before, during, or after the call.

## Driving it with verify.sh

Preconditions:

- `$V build` was run on the tree being proved and the installed app updated from it.
- The app's config.json has `"pauseDuringMeet": true` (absent or false turns the feature off; read at launch).
- The user agreed to drive the installed app and join a Meet call (as for `mic-switch.md`).

- **MP0.** Join a Meet call in Chrome, then in Edge, and quote the `meet: <bundle ID> holds the microphone, pausing` line for each. No line within 3 s means `AppDelegate.meetBrowsers` does not contain the ID the browser reports. No tool here lists every process holding input yet (`mic-holder` does not), so finding the right ID then needs one that reads `kAudioProcessPropertyBundleID` for each process with `kAudioProcessPropertyIsRunningInput`.
- **MP1–MP8.** Do each step from the list above and quote the `meet: <bundle ID> holds the microphone, pausing` / `meet: the browser released the microphone, resuming` log lines and the menu bar title (🔕/🔔, 一時停止/再開).
- **MP-F1–MP-F3.** Run the `verify-meet-pause` skill, not `verify.sh`.

## Gotchas

- `--simulate` has no CoreAudio process list, so it cannot see Meet; MP1–MP8 need the installed app.
- Chrome may report input under a helper process, not `com.google.Chrome`. MP0 settles this before the list is fixed.
- Detection by mic use also pauses for any other Chrome page that uses the mic. Detecting the Meet tab by window title would need Screen Recording permission.
- voice-switch's own input is a different process and never matches the list.
- If Chrome releases and reacquires the mic mid-call (device switch, reconnect) and a poll lands in the gap, voice-switch resumes and pauses again, listening for up to 2 s.
- A CoreAudio error makes `micInUse` return nil, which reads as "no call" and resumes mid-call.
