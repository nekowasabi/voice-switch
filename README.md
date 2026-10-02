# voice-switch

ウェイクワードを単独で言うと、設定したコマンドを実行する常駐アプリ。  
Say a wake word on its own → run a configured command (typically Superwhisper record toggle).

## Platforms

| | macOS | Windows |
|---|---|---|
| Menu / tray UI | Menu-bar app | Console resident (tray TBD) |
| Wake-word STT | Apple SpeechTranscriber | Not wired yet |
| Superwhisper hook | `open -g superwhisper://record` | `cmd /c start "" superwhisper://record` |
| Build | `make` | `PC=wsl make` |

See [BUILD.md](./BUILD.md) for `$PC` details and verification.

## Quick start (macOS)

```sh
make install
# edit ~/.config/voice-switch/config.json
```

## Quick start (Windows / WSL)

```sh
export PC=wsl
make win-verify   # no SDK required
make              # needs Swift for Windows (swift.exe on PATH)
```

Copy `config.example.windows.json` to `%USERPROFILE%\.config\voice-switch\config.json` (or set `VOICE_SWITCH_CONFIG`).
