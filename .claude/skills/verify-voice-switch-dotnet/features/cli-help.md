# CLI help

`--help` and `-h` print the Windows usage text and exit 0. The text names every public flag, the default command, and the config and handoff paths.

## Sub-features

- `help-long` prints usage for `--help`.
- `help-short` prints the same usage for `-h`.

## How to get to it (user POV)

- Run `voice-switch.exe --help`.
- Run `voice-switch.exe -h`.

## Driving it with verify.sh

Preconditions:

- `$V doctor` prints `doctor: ok`.
- No `config.json` is next to the driver dll.

- **Show usage.** Run `VS_RUN_ID=cli-help $V cli -- --help`. Exit code 0. `stderr.txt` is empty. `stdout.txt` contains `voice-switch (Windows)`, `  voice-switch.exe --self-test           pure behavior tests, no mic and no command`, `  voice-switch.exe --help                this text`, `Default command: rundll32 url.dll,FileProtocolHandler superwhisper://record`, `Dictation handoff: `, and `Incompatible flags: --dry-run cannot be combined with --fire.`
- **Short flag.** Run `VS_RUN_ID=cli-help-h $V cli -- -h`. Exit code 0. `stderr.txt` is empty. `stdout.txt` is byte-for-byte the same as `cli-help/stdout.txt`.
- **Proof.** Keep both evidence folders. The two stdout files match, and both stderr files are empty.

## Gotchas

- The `Config:` line and the `Dictation handoff:` line follow `USERPROFILE`, `LOCALAPPDATA`, and a `config.json` beside the dll. Compare the stable lines above. With `USERPROFILE` unset, `Config:` is `%USERPROFILE%\.config\voice-switch\config.json`.
- A `config.json` in the driver output directory changes the `Config:` line. `$V cli` does not put one there. If the line points at `voice-switch-verify-cli.dll`'s directory, the output folder is contaminated.
- Help returns before the config file is opened. An empty stderr distinguishes it from the fatal paths, which always print a hint on stdout.
