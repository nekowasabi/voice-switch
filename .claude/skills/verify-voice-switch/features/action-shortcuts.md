# A keyboard shortcut per action

Each wake action can name a real keyboard shortcut, the same chord names as `pauseShortcut` (`Control+Shift+F6`). Pressing it starts that action's dictation without the wake word. Mac registers it with `RegisterEventHotKey`. Windows registers it with `RegisterHotKey`. A chord that does not parse is logged and skipped. The wake word still works.

## Sub-features

- `shortcut` on `macOS.actions` and `windows.actions`.
- The press starts a dictation bound to that action (`action-hotkey` in the log).
- A Grok Bot action started this way still shows the Grok Bot icon, because the session's action is the same one the wake word would select.

## How to get to it (user POV)

Add `"shortcut": "Control+Shift+F6"` to the action, restart the app, and press the chord. Speak the text. It is delivered as if the wake word had been heard.

## Driving it with verify.sh

Registering a hotkey needs the Mac or Windows desktop. This box proves the chord parse and the pending action name:

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project dotnet/VoiceSwitch.SharedChecks/VoiceSwitch.SharedChecks.csproj -c Release
```

Pass includes `action hotkey keeps the action name`. `$V wav` does not press keys.

## Gotchas

- The shortcut is read at launch. Restart after editing it.
- A chord already taken by another app is logged as unavailable and does nothing.
- Live key delivery is Mac-unverified and Windows-unverified on this machine.
