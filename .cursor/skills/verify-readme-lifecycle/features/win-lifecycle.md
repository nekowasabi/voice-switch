# Windows lifecycle

`voice-switch.exe --tray-command` already talks to the running tray over `TrayIpc`. `start` and `pause` turn listening on and off. `stop` is another word for `quit`. `restart` quits that process and launches the exe again. `reload` re-reads config in the same process.

## Sub-features

- `win-stop-alias` maps `stop` and `quit` to the same pipe command.
- `win-restart-process` quits through the existing pipe, then launches this exe.
- `win-help-semantics` says so in `PrintHelp` and in both READMEs.
- `win-no-second-ipc` keeps a single `TrayIpcServer`.
- `win-make-lifecycle` adds `win-start` and `win-restart` beside the existing `win-stop`.
- `win-parse-tests` registers the parsing tests in the project `make win-test` runs.

## How to get to it (user POV)

- Start the published exe from `RELEASE_DIR` with `make win-start`, or by launching `voice-switch.exe` with no command.
- Stop the process with `make win-stop`, or ask it to exit with `--tray-command quit` or `--tray-command stop`.
- Restart the process with `make win-restart`, or with `--tray-command restart`.
- Turn listening off and on with `--tray-command pause` and `--tray-command start` while the process stays up.
- Re-read config with `--tray-command reload`. The process stays up.

## Driving it with verify-readme-lifecycle

Preconditions:

- `$V doctor` printed `doctor: ready`.
- Do not launch `explorer.exe` or the tray as part of this drive.

- **Check the commands.** Run `.cursor/skills/verify-readme-lifecycle/verify.sh drive win-lifecycle`. Stdout contains `PASS win-stop-alias`, `PASS win-restart-process`, `PASS win-help-semantics`, and `PASS win-no-second-ipc`.
- **Check make.** The same run prints `PASS win-make-lifecycle`.
- **Check the tests are registered.** The same run prints `PASS win-parse-tests`. Then run `make win-test` yourself when you need the suite's exit code. This drive does not run it.
- **Proof.** `exitcode.txt` is `0` when every id in this feature passed.

## Gotchas

- `make win-stop` force-quits `voice-switch.exe` so a later publish can replace DLLs. `--tray-command stop` asks the tray to quit over the pipe. The README has to keep those apart.
- Mapping `restart` to `Reload` fails `win-restart-process`. `ReloadAsync` stays a config reload.
- Adding `TrayCommand.Restart` or a second pipe server fails `win-stop-alias` or `win-no-second-ipc`.
- A test under `tests/windows/` that `VoiceSwitch.Windows.Tests.csproj` does not compile fails `win-parse-tests`.
