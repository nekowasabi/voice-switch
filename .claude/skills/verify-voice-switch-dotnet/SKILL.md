---
name: verify-voice-switch-dotnet
description: Drive VoiceSwitch.Windows.Program.Run on the Linux .NET 8 host for --help, --self-test, and fail-closed CLI paths, and keep stdout, stderr, and the exit code. Use when a change under dotnet/ must be shown not to change that CLI behavior. Mac simulation stays in verify-voice-switch-mac. The win-x64 tray exe is built here and is not started.
---

# verify-voice-switch-dotnet

The surface this skill drives is the Windows CLI entry `VoiceSwitch.Windows.Program.Run` (`dotnet/VoiceSwitch.Windows/Program.cs`). On a Windows machine `TrayHost.Main` calls `Program.Run` for every non-tray launch, including `--help` and `--self-test`. This Linux box has the .NET 8 SDK and no Windows desktop session, so `verify.sh` compiles those same sources into a small host and calls `Program.Run` itself.

`verify.sh` lives in this folder. It finds the repo from its own path. Run it by absolute path:

```sh
V=/workspace/voice-switch-refactor/.claude/skills/verify-voice-switch-dotnet/verify.sh
```

Override the scratch root with `VS_VERIFY_ROOT` (default `/tmp/voice-switch-dotnet-verify`). Evidence is `$VS_VERIFY_ROOT/evidence`. Set `VS_RUN_ID` so each drive has its own folder.

The host is a stand-in for `voice-switch.exe`. A Windows run will not do these steps, and they stay unverified until one does:

- Generate a `net8.0-windows` project under the scratch root whose sources are the `Compile Include` links from `dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj`, plus `WindowFocusStub.cs` when `WindowFocus.cs` is not linked.
- Call `VoiceSwitch.Windows.Program.Run` from that host. Tray launches (no arguments, `--paused`, `--tray-command`, `--record-only`) stay out of this skill.
- Leave Windows Forms, the microphone, and `powershell.exe` unloaded. `WindowFocusStub` makes paste and focus return empty.

Export this before any `dotnet` command, including the regression commands below:

```sh
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
```

## Launch

There is no resident process. Prepare the toolchain, build the win-x64 assembly, then start one CLI process per drive.

```sh
$V doctor
$V build
```

`$V build` runs `dotnet build dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj -c Release -r win-x64 --no-restore -o $VS_VERIFY_ROOT/win-x64`. Ready means the log contains `0 Warning(s)` and `0 Error(s)` and `$VS_VERIFY_ROOT/win-x64/voice-switch.dll` exists. The log is copied to `$VS_VERIFY_ROOT/evidence/win-x64-build.log`. That dll is a Windows apphost output. This skill does not execute it.

`$V cli` is the launch that reaches a usable CLI. It restores and builds the Linux host (about 2 seconds warm; the first restore is longer), then runs one `dotnet exec` of `voice-switch-verify-cli.dll` and waits until that process exits. Ready means `exitcode.txt` exists in the evidence folder.

The repo must already have been restored once (`dotnet restore` of the Windows csproj with `-r win-x64`, and of the test csproj). `$V build` passes `--no-restore`. `$V cli` restores only its own generated project.

## Doctor

```sh
$V doctor
```

Doctor is read-only. It checks that `dotnet` is SDK 8.x, that `python3` exists, and that `Program.cs`, `CliOptions.cs`, the test csproj, and `WindowFocusStub.cs` exist. It prints the repo path, `HEAD`, the dirty-file count, the SDK version, and the scratch root. The last line is `doctor: ok`. Anything else, do not drive.

## Drive

```sh
VS_RUN_ID=<name> $V cli -- <args>
```

`<args>` are the arguments a user would pass to `voice-switch.exe`. `--help`, `-h`, and `--self-test` return before config load. A bad flag throws inside `CliOptions.Parse`. `--listen-seconds 5` with no config file fails inside `ConfigLoader.Load` and returns before any listen window.

The command prints `exit: <code>` and `evidence: <folder>`. Its own exit code is the app's exit code. Recipes and expected files are in [features/README.md](features/README.md).

One `cli` at a time per `VS_VERIFY_ROOT`. Drives share one driver project.

## Evidence

Each drive writes `$VS_VERIFY_ROOT/evidence/<VS_RUN_ID>/`.

| File | Contents |
|---|---|
| `command.txt` | `dotnet`, `exec`, the dll path, then each argument on its own line |
| `stdout.txt` | the process stdout |
| `stderr.txt` | the process stderr |
| `exitcode.txt` | the process exit code |
| `elapsed-ms.txt` | milliseconds from process start to exit, excluding the driver build |
| `pid.txt` | the pid that was waited on |
| `git-head.txt` | `HEAD` when the drive started |

`$VS_VERIFY_ROOT/evidence/win-x64-build.log` is the win-x64 build log from `$V build`.

Proof standards:

- Drive `Program.Run` through `$V cli`. A unit-test PASS line is a separate regression check. It does not prove this CLI path.
- Capture the action and the result. Keep `command.txt`, both streams, and `exitcode.txt`.
- `--help` and `--self-test` must leave stderr empty. `--self-test` stdout is exactly `self-test: ok` and a newline.
- Every exception path prints a timestamped `fatal:` line on stderr and a timestamped `hint: copy config.example.windows.json to ...` line on stdout. Assert the stable tail of those lines. The timestamp and the config path depend on the clock and on `USERPROFILE`.
- `--self-test` and `--help` create no config file and no handoff wav. `--listen-seconds` with a missing config must exit before the listen window (a 3 second `timeout` is enough to see that).
- `--output-dir` is the production local-handoff switch. This skill does not feed a wav through it, because that path constructs `SpeechPowerShellDictationRecognizer` and asks for `powershell.exe`.
- `--fire` by itself loads config and then starts `cmd.exe`. Do not drive `--fire` alone on this host.
- The host appends to the Windows log only when `OperatingSystem.IsWindows()` is true. On this box a drive does not create `%LOCALAPPDATA%\voice-switch\voice-switch.log`.

## Cleanup

```sh
$V cleanup
```

If `$VS_VERIFY_ROOT/active.pid` still names a live process whose command line contains `voice-switch-verify-cli`, cleanup kills that pid. Any other pid is left alone. Cleanup then deletes the driver directory and the win-x64 output directory. Evidence under `$VS_VERIFY_ROOT/evidence` stays, including `win-x64-build.log`.

## Helpers

`verify.sh` is the only helper. It is executable.

```sh
$V doctor
$V build
VS_RUN_ID=self-test $V cli -- --self-test
$V cleanup
```

## Regression

These checks compile and run the same sources. They are the refactor gate in `plan/2026-10-28/SPEC.md`. They do not replace a `cli` drive.

```sh
dotnet build dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj -c Release -r win-x64 --no-restore
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release --no-restore
python3 tests/parity/run_parity.py
python3 -m unittest discover -s tests -p 'test_*.py'
```

The behavior suite prints one `PASS` or `FAIL` line per check. On this host the baseline is 211 `PASS` lines, 0 `FAIL` lines, and 11 `SKIP` lines for Windows-only tray IPC and the registry. Parity prints `SUMMARY pass=118 allow=23 fail=0`.

`python3 tests/url-actions/run.py` already fails on this tree: `AssertionError` on the Swift source string `guard modeRequested, superwhisperAutoPasteDisabled`. Leave that failure as it is.

## Maintenance

When the CLI surface or the test csproj links change, run `/maintain-verification-skill` so this map stays honest.
