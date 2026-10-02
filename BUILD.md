# Build notes

## `$PC` switch

| Environment | Default `make` | What you get |
|---|---|---|
| unset / anything except `wsl` | `make app` | macOS `VoiceSwitch.app` (existing flow) |
| `PC=wsl` or `PC=WSL` | `make win` | Windows `voice-switch.exe` via Swift for Windows |

Case-insensitive: `WSL` and `wsl` both select Windows.

## macOS

Requirements: macOS 26+, Swift 6 toolchain, Xcode.

```sh
make          # or: make app
make install  # → ~/Applications/VoiceSwitch.app + config if missing
```

Config example: `config.example.json` (`open -g superwhisper://record`).

Useful flags: `--check`, `--simulate`, `--fire`, `--vad-selftest`.

## Windows (WSL or native)

Requirements: [Swift for Windows](https://www.swift.org/install/windows/). From WSL, `swift.exe` must be on `PATH`.

```sh
export PC=wsl
make                 # → /mnt/c/takeda/tools/voice-switch/voice-switch.exe
make win-verify      # layout check without a Swift SDK (works on Linux CI / this box)
```

Override install dir: `RELEASE_DIR=/mnt/c/path make win`.

Config example: `config.example.windows.json` — same `superwhisper://record` deep link via `cmd /c start`.

```text
voice-switch.exe --vad-selftest   # shared energy VAD (no mic/STT)
voice-switch.exe --fire           # run config command once (Superwhisper hook)
voice-switch.exe                  # resident; mic/STT backend not wired yet
```

### What works on Windows today

- Config load / reload, platform command hook, path expansion (`%LOCALAPPDATA%`, …)
- Shared `Segmenter` VAD (`--vad-selftest`)
- `--fire` → `superwhisper://record` (Superwhisper for Windows must be installed)

### Still needs a Windows machine + follow-up

- Microphone capture (WASAPI; AVAudioEngine is Apple-only)
- On-device STT (Apple SpeechTranscriber is macOS-only)
- System-tray UI (AppKit menu bar is macOS-only)
- Mic-in-use detection / CGEvent hotkey tap equivalents

Shared types (`Config`, `Segmenter`, `Transcript`, `Platform`) are ready for those backends behind `#if os(Windows)`.
