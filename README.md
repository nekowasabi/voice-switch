# voice-switch

Menu-bar app. A wake word on its own runs a command through `/bin/sh`. A wake word followed by more speech is recorded and opened in superwhisper, which transcribes it. On `main`, the text is not passed to another program.

[日本語](README_ja.md)

The bundle identifier is `local.voice-switch`. `Info.plist` sets `LSMinimumSystemVersion` to 26.0. A successful Mac build is not claimed here.

## Install

`make install` builds a release binary, wraps it as `VoiceSwitch.app`, ad-hoc signs it as `local.voice-switch`, copies the app to `~/Applications`, copies `config.example.json` to `~/.config/voice-switch/config.json` when that file is missing, and opens the app.

`make uninstall` quits the app and removes that copy. `make logs` follows `~/Library/Logs/voice-switch.log`.

## Permissions

The app asks for the microphone and for on-device speech recognition. While a dictation is recording it also installs a keyboard tap for superwhisper's record and cancel shortcuts, which needs Accessibility. Until that is granted, the log says `hotkey: Accessibility not granted yet`.

## Config

`$VOICE_SWITCH_CONFIG`, or `~/.config/voice-switch/config.json` when that variable is unset. The file is re-read between utterances when its modification time changes.

`command` and `stopCommand` are run with `/bin/sh -c`. The sample file is `config.example.json`.

A wake word has to be the whole utterance. The sample words are `音声入力`, `音声入る`, `おんせい`, `音声に入るよ`, `音声に入る`, and `音声によって`, with locale `ja_JP`.

If `dictation` is absent, a lone wake word runs `command`. In the sample, that command is `open -g superwhisper://record`.

If `dictation` is present:

- A lone wake word starts a dictation and waits for speech. The sample wait is `startTimeoutMs` 3000. Silence before any speech cancels it.
- An utterance that starts with a wake word and continues is recorded until about `endSilenceMs` of silence (sample 1200), a stop word, superwhisper's record shortcut, or `dictation.maxSeconds` (sample 60). The wake-word audio is cut.
- The wav is opened in superwhisper. The app polls `dictation.recordingsDir` (sample `~/Documents/superwhisper/recordings`) for a new `meta.json`, reads `llmResult` or else `result`, and logs the length. On `main` that string is not passed on. The code activates the app that was frontmost when the wake word was heard, while superwhisper is frontmost, so superwhisper does not skip its paste.

`stopWords` said on their own end an in-progress dictation. If an app in `skipWhileMicInUseBy` is using the microphone instead, the same word runs `stopCommand`. The sample word is `入力ストップ`. The sample `stopCommand` is the same superwhisper record URL, which toggles.

A wake word is ignored while an app in `skipWhileMicInUseBy` has the microphone open. Dictation is skipped when the frontmost bundle id is in `dictation.excludeBundleIDs`.

The top-level `maxSeconds` (sample 2.5) caps a short wake-word utterance. It is not the dictation cap.

The menu items are 一時停止 (releases the microphone), マイク, 設定ファイルを開く, ログを開く, ログイン時に起動, and 終了.

## CLI

- `voice-switch --check a.wav` feeds files through the VAD and transcriber and prints a verdict per utterance.
- `voice-switch --simulate a.wav` feeds one file through the live path instead of the microphone.

## Not on main

[PR #3](https://github.com/nekowasabi/voice-switch/pull/3) is open and not merged. This section is not current `main` behavior.

On that branch, a non-empty `llmResult` (otherwise `result`) is passed as argv, not through a shell:

```
/usr/bin/env computer-use-jev -goal <text>
```

Whitespace-only text does not start it. The app does not wait for it to exit. A non-zero status is logged. The branch does not pass `-key`. `TYPESAFE_API_KEY` is not written in source or in config. If the CLI reads a key, it reads that environment variable. This repo does not contain a key value.

A successful Mac build of that branch is not claimed here. It is also not claimed that superwhisper stops pasting after the focus change on that branch.
