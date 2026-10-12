# Tray host and IPC

`voice-switch.exe` with no diagnostic flag is a WinForms tray app with a Japanese menu (状態, 再開, 一時停止, 設定を再読み込み, 終了). A second invocation with `--tray-command status|start|pause|reload|quit` controls the running tray for the same config path.

## Sub-features

- `TI1` `--paused` start, then `start` runs the input WAV to `Finished`.
- `TI2` `pause` and `start` resume and repeat the outputs.
- `TI3` an invalid `reload` keeps the previous config and shows the error.
- `TI4` `quit` exits the host.

## How to get to it (user POV)

- The tray icon menu, or `voice-switch.exe --tray-command <command>` from a terminal.

## Driving it with verify.sh

Preconditions: PowerShell 7 (`pwsh.exe`) on Windows. This machine has only Windows PowerShell 5.1, so these steps are currently unreachable. Report them as not verified.

- `TI1`–`TI4`: `pwsh -File tests\windows\run-tray-host.ps1 -ReleaseDir <build dir> -OutputRoot <fresh dir>`, run from Windows. It asserts each state and writes `tray-host-summary.json` under the output root.

## Gotchas

- The tray single-instance lock is per Windows user and canonical config path. Always use the build's own `config.json` so the user's tray is never addressed by `--tray-command`.
- Without `--paused` and `--input-wav`, the tray opens the live microphone on launch.
