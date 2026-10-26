# Only the user's voice starts a recording

An automatic recording should start only when the user speaks into the microphone. Sound from the speakers (a video, music, a call) must not start one, because a recording that sits in 録音中 blocks the user's own dictation. `Listener.start` in `MacApp.swift` turns on voice processing I/O (`setVoiceProcessingEnabled(true)`) when `dictation.echoCancellation` is absent or true, with other-audio ducking off so the video keeps its volume, and logs `echo cancellation unavailable, raw input:` when it cannot. The IDs and pass conditions match the Windows file `verify-voice-switch/features/playback-silence.md` (`VO1` to `VO10`). Keep the two in step.

## Sub-features

Status 2026-10-26: written and checked only by `make parity-test` (markers of `dictation_echo_cancelled_input`) on WSL, which has no Swift toolchain. Not built and not run on a Mac. Open question: whether macOS voice processing cancels audio that other apps play, or only audio played through the same engine. `VO1` decides it.

- `VO1` playback alone starts nothing: a speech clip played on the Mac's speakers for 30 s while nobody talks gives no `automatic` session line in the live log.
- `VO2` the user over playback starts a recording, and Superwhisper's text has the user's words and none of the video's.
- `VO5` the user alone is unchanged: the same dictation with and without `"echoCancellation": false` gives the same text.
- `VO6` to `VO10` as on Windows.
- `VO11` the video keeps its volume while voice-switch listens (ducking off).

## How to get to it (user POV)

- Turn on automatic sessions. Play a video on the speakers. Without this feature a recording starts by itself; with it voice-switch stays idle until you speak.

## Driving it with verify.sh

`--simulate` feeds a file and never opens the microphone, so it cannot prove this feature. Use the installed app only with the user's agreement, or a build run with the microphone and a config whose `dictation.superwhisperMode` is missing so nothing pastes.

- `VO1`: start listening, `afplay <speech.wav>` for 30 s, nobody speaks. Pass: the log has `echo cancellation: voice processing on` and no automatic session. Control: the same with `"echoCancellation": false` must start one.
- `VO11`: with music playing, start and stop listening; the music level must not change.

## Gotchas

- Check this first on a Mac: if `engine.start()` throws with voice processing on, `start()` logs `echo cancellation unavailable, raw input:`, sets `voiceProcessingFailed`, rebuilds the engine and starts raw. The log must show `listening on` after it, not a restart loop.

- Voice processing I/O changes the input format (channel count and rate); `start()` reads `inputFormat(forBus:)` after enabling it, so the converter sees the new format.
- Enabling it after `kAudioOutputUnitProperty_CurrentDevice` is set would reset the pinned device, so `start()` enables it first.
- macOS can prompt or reroute output when voice processing starts on some Bluetooth headsets (HFP). Note the output device in the evidence.
