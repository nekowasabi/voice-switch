# voice-switch

ウェイクワードを単独で言うと、設定したコマンドを実行する常駐メニューバーアプリ（macOS）。  
Say a wake word on its own → run a configured command (typically Superwhisper’s record toggle).

## Install

```sh
make install
# edit ~/.config/voice-switch/config.json
```

Requires macOS 26+ (Apple SpeechTranscriber) and microphone / speech / Accessibility grants as prompted.

## Config

See `config.example.json`. Important keys:

| Key | Role |
| --- | --- |
| `command` | Shell command on wake word (default `open -g superwhisper://record`) |
| `stopWords` / `stopCommand` | End an in-progress Superwhisper recording |
| `dictation` | One-breath dictation handoff to Superwhisper |
| `macrowhisper` | Optional [macrowhisper](https://github.com/ognistik/macrowhisper) CLI hooks (see below) |

## macrowhisper integration

[macrowhisper](https://github.com/ognistik/macrowhisper) watches Superwhisper recordings and runs insert/URL/Shortcut/shell/AppleScript actions. voice-switch does **not** reimplement that; it calls the same public CLI the [Alfred workflow](https://github.com/ognistik/macrowhisper/tree/main/alfred) uses before starting Superwhisper.

Documented CLI used here ([cli-reference](https://github.com/ognistik/macrowhisper/blob/main/docs/cli-reference.md)):

| Config field | CLI |
| --- | --- |
| `scheduleAction` | `macrowhisper --schedule-action <name>` (one-shot for the next / active recording) |
| `autoReturn` | `macrowhisper --auto-return true` (one-shot Return; mutually exclusive with schedule) |
| `activeAction` | `macrowhisper --action <name>` (persistent fallback) |
| `modeKey` | `open -g superwhisper://mode?key=…` |
| `bin` | Absolute path or name on `PATH` (default `macrowhisper`) |
| `onDictationHandoff` | Also prepare before one-breath WAV handoff (default `true` when any hook is set) |

Priority when more than one is set: `scheduleAction` > `autoReturn` > `activeAction` (macrowhisper treats schedule and auto-return as mutually exclusive).

Example: `config.example.macrowhisper.json`

```json
"macrowhisper": {
  "bin": "macrowhisper",
  "scheduleAction": "autoPaste",
  "onDictationHandoff": true
}
```

Requirements on the Mac:

1. Install macrowhisper (`brew install ognistik/formulae/macrowhisper`) and start the service (`macrowhisper --start-service`).
2. Follow macrowhisper’s Superwhisper text-input checklist (paste off, etc.).
3. Ensure `scheduleAction` / `activeAction` names exist in `~/.config/macrowhisper/macrowhisper.json` (`macrowhisper --list-actions`).

Wake-word path: prepare CLI hooks → run `command` (record toggle).  
One-breath dictation: optional prepare → open WAV in Superwhisper (macrowhisper’s watcher handles the result when configured).

## Build

```sh
make          # VoiceSwitch.app in .build/
make install  # → ~/Applications
```
