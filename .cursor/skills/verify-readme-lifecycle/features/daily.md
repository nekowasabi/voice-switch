# Daily use

A person reads README_ja.md first, then uses the menu-bar app on a Mac or the tray app on Windows with Superwhisper. The English README carries the same steps.

## Sub-features

- `readme-ja-primary` names README_ja.md as the primary daily guide.
- `readme-daily-use` says both desktops stay running and that dictation assumes Superwhisper.
- `readme-config-paths` gives the config path on each OS, in the order the code resolves it.
- `readme-perm-mac` lists microphone, speech recognition, and Accessibility, and says iTerm and System Events automation are not requested.
- `readme-perm-windows` says Windows has a microphone privacy setting and no Accessibility or Automation dialog, and that UIPI blocks the hook.
- `readme-human-owned` says a person installs the app and runs the hand tests.

## How to get to it (user POV)

- Open README_ja.md for the daily steps. Open README.md for the same steps in English.
- On a Mac, the running app is the menu-bar icon. On Windows, it is the tray icon.
- Edit the config file at the path the README gives for that OS.
- Grant the permissions the README names. Do not grant Automation for iTerm or System Events for this app.

## Driving it with verify-readme-lifecycle

Preconditions:

- `$V doctor` printed `doctor: ready`.
- The working tree is the checkout under test.

- **Read the primary guide.** Run `.cursor/skills/verify-readme-lifecycle/verify.sh drive daily`. Stdout contains `PASS readme-ja-primary` and `PASS readme-daily-use`.
- **Check paths.** The same run prints `PASS readme-config-paths`.
- **Check permissions.** The same run prints `PASS readme-perm-mac` and `PASS readme-perm-windows`.
- **Check who installs.** The same run prints `PASS readme-human-owned`.
- **Proof.** The evidence directory holds `stdout.txt` with those lines and `exitcode.txt` containing `0` when this feature is driven alone and every id in it passed.

## Gotchas

- The old README line `hotkey: Accessibility not granted yet` is not in MacApp.swift. Leaving it in either README fails `readme-perm-mac`.
- `iTerm` and `System Events` appear in the README only to say the app does not request them. Adding a request for them fails the source half of `readme-perm-mac`.
- Windows config is the `config.json` beside the exe when that file exists, and only then `%USERPROFILE%\.config\voice-switch\config.json`. Documenting only the home path fails `readme-config-paths`.
- A sentence that is close but not the exact string in items.md fails the id.
