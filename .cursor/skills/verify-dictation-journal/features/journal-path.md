# Journal path

`dictation.journalDir` chooses the folder or the single `.jsonl` file. `~` and `%LOCALAPPDATA%` expand the same way `recordingsDir` does. A bad or unwritable path is logged and the default directory is used. A config file that fails to parse does not replace the previous journal path.

## Sub-features

- `expand-tilde` expands `~` like recordingsDir.
- `expand-localappdata` expands `%LOCALAPPDATA%` like recordingsDir.
- `default-mac` uses `~/Library/Application Support/voice-switch/journal` when the key is absent.
- `default-windows` uses `%LOCALAPPDATA%\voice-switch\journal` when the key is absent.
- `daily-file` writes one file per offset-date under a directory.
- `single-file` appends to the path itself when it ends in `.jsonl`.
- `invalid-fallback` logs and uses the default for an invalid path.
- `unwritable-fallback` logs and uses the default when the path cannot be created.
- `reload-keeps-config` keeps the previous config when the file fails to parse.
- `examples-readme` documents the key in the sample configs and both READMEs.
- `journal-failure-isolated` still accepts the history entry when every path fails.

## How to get to it (user POV)

- Set `dictation.journalDir` in `config.json` to a folder, or to a path ending in `.jsonl`.
- Use `~/Documents/voice-switch/journal` on a Mac sample and `%LOCALAPPDATA%\voice-switch\journal` on a Windows sample.
- Save a broken `config.json`. The app keeps the previous settings and logs `config reload failed, keeping previous`.
- Point `journalDir` at a path that cannot be written. The log contains `dictation journal:` and `using default`, and the line appears in the default directory.

## Driving it with verify-journal

Preconditions:

- `$V doctor` printed `doctor: ready`.
- `LOCALAPPDATA` in the test is a scratch value, not the user's real profile.

- **Expand and default.** Run `.cursor/skills/verify-dictation-journal/verify.sh drive journal-path`. Stdout contains `PASS expand-tilde`, `PASS expand-localappdata`, `PASS default-mac`, and `PASS default-windows`.
- **Daily and single file.** The same run contains `PASS daily-file` and `PASS single-file`.
- **Fallback.** The same run contains `PASS invalid-fallback` and `PASS unwritable-fallback`. Each failing path produces a log line containing `dictation journal:` and `using default`.
- **Bad reload.** The same run contains `PASS reload-keeps-config`.
- **Samples and docs.** The same run contains `PASS examples-readme`.
- **Isolated failure.** The same run contains `PASS journal-failure-isolated`.
- **Proof.** `stdout.txt` in the evidence directory contains those lines and `exitcode.txt` is `0`.

## Gotchas

- `config.example.macos-url.json` is command mode. Adding a `dictation` block would turn dictation on. The item fails if that file gains `journalDir`.
- An absent `journalDir` is the default directory, not an error, and it does not log `using default`.
- Fallback uses the OS default directory. The previous custom directory is kept only when the whole config reload fails to parse.
- A bad or unwritable `journalDir` still has to load. Treating it as a config error keeps the previous directory and fails `invalid-fallback`.
- A path is a single file only when the expanded path ends in `.jsonl`. Case does not matter.
