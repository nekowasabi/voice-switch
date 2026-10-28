---
name: verify-voice-switch-grokbot
description: Prove the Windows URL voice-switch would open for a Grok Bot wake-word action, and statically check the Swift mirror. Use when changing inputParam, grokbot prompt links, action URL query composition, or grokbot example config/docs. Runs on Linux via VoiceSwitch.Windows.Core; does not launch the Grok Bot app.
---

# verify-voice-switch-grokbot

The user-facing surface for this feature is a wake-word URL action. After Superwhisper returns text, Windows opens `rundll32 url.dll,FileProtocolHandler <url>` and Mac runs `/usr/bin/open -g -- <url>`. For Grok Bot the URL is `grokbot://app/v1/prompt?id=<BOT_ID>&text=<encoded message>`.

This Linux box cannot launch the Grok Bot app, cannot run the win-x64 exe (`--fire` would call rundll32), and cannot compile Swift. Proof here is the production `WakeAction.InputUrl` method in `VoiceSwitch.Windows.Core` (the same method SuperwhisperHandoff uses) plus source greps of the Swift twin. Live Grok Bot compose-and-send stays Mac/Windows-unverified.

Other surfaces stay with their own skills: `verify-voice-switch` (Windows tray WAV drive) and `verify-voice-switch-mac` (macOS `--simulate`). Do not use those for this feature on this box.

```sh
V=/workspace/voice-switch/.claude/skills/verify-voice-switch-grokbot/verify.sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
```

Override the scratch root with `VS_GROKBOT_VERIFY_ROOT`. Default is `/tmp/voice-switch-grokbot-verify`.

## Launch

There is no long-lived server. Launch means build the probe once, then each drive is its own process.

```sh
$V build
```

Ready means `$V doctor` prints `ok probe` and `ok build` with a commit that matches the tree you are proving. `build` restores and publishes `.claude/skills/verify-voice-switch-grokbot/url-probe` into `$VS_GROKBOT_VERIFY_ROOT/build/` (run as `dotnet exec …/url-probe.dll`), and builds Core and the Tests project. `verify.sh` sets `DOTNET_ROOT` to `$HOME/.dotnet`. It never runs `make`, never publishes the tray exe, and never opens a URL.

## Doctor

```sh
$V doctor
```

Checks: `dotnet` 8.x on PATH, the probe binary exists, the probe can `ConfigLoader.Load` a `windows.actions` JSON, and it prints the build commit next to `git rev-parse HEAD`. A mismatch means rebuild. `swiftc` missing is a note, not a failure.

## Drive

```sh
VS_RUN_ID=<name> $V drive <feature>
```

`<feature>` is a stem from [features/README.md](features/README.md): `wake-action-url`, `grokbot-prompt-link`, `grokbot-text-limits`, `automatic-no-action-url`, `grokbot-config-docs`.

The probe is the harness. It calls production `WakeAction.InputUrl` / `ConfigLoader.Load`. It never starts rundll32 or `open`.

```sh
$V tests                         # full Windows.Tests run
$V tests "wake action URL"       # --only substring
$V swift-mirror baseline         # existing input= machinery in Swift and C#
$V swift-mirror grokbot          # inputParam + grokbot limits + grokbot prompt text truncated log
$V win-build                     # optional: Release win-x64 compile of the Windows app
```

`--fire` on `voice-switch.exe` runs `config.command` through rundll32. It does not compose a named action URL and it does not run on this Linux box. Do not use it as proof here.

## Evidence

Each drive leaves `$VS_GROKBOT_VERIFY_ROOT/evidence/<run>/<feature>/` (default `/tmp/voice-switch-grokbot-verify/evidence/<run>/<feature>/`).

| File | Contents |
|---|---|
| `config.json` | the exact JSON the probe loaded |
| `stdout.txt` / `stderr.txt` | probe or test output, including `GOT` / `WANT` / `MATCH` |
| `want.txt` | the URL the recipe required |
| `exitcode.txt` | process outcome |
| `swift-mirror.txt` | static marker check, when the recipe runs it |

Proof standards:

- The URL string `WakeAction.InputUrl` returns is the Windows URL SuperwhisperHandoff would pass to rundll32. Quote `GOT` and `WANT`. A screenshot of Grok Bot is not available here.
- Drive production `InputUrl` / `ConfigLoader`. Do not reimplement percent-encoding in the recipe.
- Swift is proven by source markers (`$V swift-mirror grokbot`) and by the parity suite, not by running `open`.
- Automatic sessions must not open action URLs. Prove that with the named unit test, not by inspecting a URL string.
- `--fire` and speaking a wake word on a machine with Grok Bot installed are documented in `docs/grokbot-message.md`. They are not this skill's proof.

## Cleanup

```sh
$V cleanup
```

Deletes `$VS_GROKBOT_VERIFY_ROOT/build` only. Evidence stays. There is no process to kill: every drive exits on its own. Delete `$VS_GROKBOT_VERIFY_ROOT/evidence/<run>` yourself when you no longer need it.

## Isolation

Drives do not share state. Each writes its own config under the evidence folder. Two drives can run in parallel if they use different `VS_RUN_ID` values. Nothing opens the microphone, Superwhisper, or Grok Bot. The user's `~/.config/voice-switch/config.json` is never read.

## Helpers

| Path | Invocation |
|---|---|
| `verify.sh` | `$V build`, `$V doctor`, `$V drive <feature>`, `$V tests [only]`, `$V swift-mirror [baseline\|grokbot]`, `$V cleanup` |
| `url-probe/` | built by `$V build`; `dotnet exec $VS_GROKBOT_VERIFY_ROOT/build/url-probe.dll compose --url URL --text TEXT` |
| `swift_mirror.py` | `$V swift-mirror grokbot` |

Keep helper output in the evidence folder. Do not reverse-engineer flags; they are listed in `$V` usage and in `url-probe --help`.
