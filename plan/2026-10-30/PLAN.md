# PLAN: journal focus app and TaskChute context

One slice. Branch `feat/journal-focus-tcc`, cut from `origin/main` at `2c28b7d`. No second branch. Do not merge, push, or open a PR.

## Facts

Checked in this tree on 2026-10-30. There is no TaskChute or TCC client. A search for `TaskChute`, `taskchute`, `tcc2`, and `journalTask` under the repo returns nothing. "App vs TCC2" is an external Grok Bot routine, not code in this repo.

The operator's TaskChute access is the `tcc2` CLI. Its commands are `login`, `logout`, `status`, and `mcp`. It speaks MCP over stdio (`tools/call` `get_taskchute` with `start_date` and `end_date`). One call is about 8 seconds and needs a login. Calling it inside `DictationHistory.Add` is rejected. Add has to stay fast, and a journal failure must not drop an utterance.

On the operator's WSL box a tmux status helper already writes the current task to `~/.cache/tmux/taskchute.txt`. That path is an example the operator can put in config. It is not a product default.

Do not add a network client.

## Baseline

`.cursor/skills/verify-dictation-journal/verify.sh doctor` printed `doctor: ready` (dotnet 8.0.425, `swiftc` absent, host Linux).

`.cursor/skills/verify-dictation-journal/verify.sh drive` on this commit's parent, before the skill edit, printed `journal-tests ran 31 failed 1`. The only failure was `FAIL examples-readme config.example.grokbot.json dictation has no journalDir`. Evidence is `/tmp/voice-switch-journal-verify/evidence/20261030T013407Z`. `SKIP windows-acl`, `SKIP swift-harness`, and `SKIP wsl-share` were also printed. `wsl-share` is not an items.md id. This slice fixes the grokbot `journalDir` line. It does not touch the WSL share case.

## Design

Two shapes were compared against the code.

The file snapshot stays. `dictation.journalTaskFile` is an optional string next to `journalDir`. The app expands `~` and `%VAR%` with the same `WindowsPaths.ExpandPath` and `Platform.expandPath` rules, reads at most 4096 bytes at append time, and writes `taskchute` null when the key is unset or the file is missing, oversized, unreadable, or not a usable task. Nothing is thrown. The journal directory is unchanged when the task path is bad.

Calling `tcc2`, or polling it on a background thread inside voice-switch, does not stay. The repo has no client, the call is too slow for Add, and a new network API was ruled out. The external updater already owns freshness.

The file design changes three details of the suggestion.

`schemaVersion` stays `1`. The new keys are always present and nullable, so a reader that looks up names still works. No code in this repo branches on the integer. Historical lines lack the keys, and append-only plus `immutable-text` forbid rewriting them. Key presence is the marker. A bump to 2 would describe the same fact and force every fixture to move.

`focusApp.exe` on Windows is `Process.ProcessName` from the existing `ProcessOfWindow` helper. No directory and no `.exe` suffix glued on. `MainModule.FileName` can throw and can contain a user profile path. Mac leaves `exe` null and fills `bundleId`. Windows leaves `bundleId` null. Both sides use the same four keys. If every value is null, the whole `focusApp` is JSON null.

Focus and the task are different moments. Focus is the app chosen when the session target is set, stashed on the existing per-id `JournalNote`, because Superwhisper is frontmost by the time Add runs. The task is whatever the file says at append, because "what the user is doing right now" is journal time. `readAt` is the same string as `timestamp`.

`Dictation.target` on Mac is set once, from `NSWorkspace.shared.frontmostApplication`, when the `Dictation` value is created (`MacApp.swift`). It is not mutated later. `submit` already calls `DictationJournal.note` before handoff. Extend that call with `d.target` (`localizedName`, `bundleIdentifier`, `processIdentifier`). The automatic path then passes `NSWorkspace.shared.frontmostApplication` into `handoff` for paste. That expression must not be the journal value.

Windows sets `target` at three sites in `DictationRuntime.cs`, all after `sessionId` exists. Wake uses `target = foregroundWindow()` (about line 412). Automatic start uses `target = window` (about line 632). The manual hotkey uses `target = window` (about line 834). Note the process there. `ResetSession` sets `target = 0` and must not note an app. `Submit` later does `audio = audio with { Target = foregroundWindow() }` for an automatic session so paste returns to wherever the user is now. The journal does not read `audio.Target`. `JournalNotes.Note` merges, so the end-reason note in `Submit` keeps the focus noted at start.

The harness injects a `FocusApp` through `JournalNotes.Note` or an optional `Add` argument. The argument wins. Core does not call `GetForegroundWindow` or `NSWorkspace`. There is no single mutable "current app" callback on the journal. Two sessions can overlap, and the note map is already keyed by id.

## Record

New lines only. Key order stays the normative record in `.cursor/skills/verify-dictation-journal/items.md`. After the existing seven keys, always:

```json
"focusApp":{"name":"Ghostty","bundleId":"com.mitchellh.ghostty","exe":null,"pid":4242}
```

```json
"focusApp":{"name":"WindowsTerminal","bundleId":null,"exe":"WindowsTerminal","pid":9001}
```

```json
"taskchute":{"name":"メール","id":"12","startedAt":"2026-10-30T09:00:00+09:00","source":"file","readAt":"<same string as timestamp>"}
```

`taskchute` object keys are `name`, `id`, `startedAt`, `source`, `readAt`. `source` is the string `file`. Write the object only when `name` or `id` is a non-empty string. A numeric JSON id is stored as a decimal string. `startedAt` is copied as a string, or null when it is not a string. Plain text uses the first non-empty trimmed line as `name`, with `id` and `startedAt` null. A body whose first non-whitespace character is `{` and which is not a usable object is null, not a name. Cap is 4096 bytes. Over that, do not parse. UTF-8 BOM is allowed inside the cap. Do not log the file body.

Unset `journalTaskFile` means do not read anything. Sample configs use JSON null, not `~/.cache/tmux/taskchute.txt`.

## Slice s1 journal focus app and TaskChute context

- **branch:** `feat/journal-focus-tcc`
- **goal:** Every new journal line carries `focusApp` and `taskchute`. Focus is the session-start app. The task comes from `dictation.journalTaskFile` or is null. Add does not call tcc2 and does not throw when the file is bad. `config.example.grokbot.json` gains `journalDir`.
- **files:**
  - `dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs` adds `JournalTaskFile` at the end of `DictationConfig`, default null.
  - `dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs` adds `FocusApp`, extends `JournalEntry` and `JournalNote`, merges focus in `JournalNotes.Note`, reads the task file inside the journal, and passes `JournalTaskFile` from both `JournaledConfig.LoadInitial` and `ReloadIfChanged`. `SchemaVersion` stays 1. Constant cap 4096.
  - `dotnet/VoiceSwitch.Windows.Core/DictationHistory.cs` takes an optional `FocusApp?` on `Add`. The argument wins over the note. A task-file failure does not escape `Add`.
  - `dotnet/VoiceSwitch.Windows/DictationRuntime.cs` notes the session-start process at the three `target` assignments above. Do not note `audio.Target`.
  - `Sources/voice-switch/Config.swift` adds `journalTaskFile`.
  - `Sources/voice-switch/DictationJournal.swift` writes the same keys, in the same order, and parses the task file the same way. No `frontmostApplication` in this file.
  - `Sources/voice-switch/WakeSafety.swift` accepts an optional focus on `add` and passes the note through. Defaulted so existing callers compile.
  - `Sources/voice-switch/MacApp.swift` notes `d.target` from `submit`. Do not note the frontmost app read later for paste.
  - `tests/journal/Program.cs` updates the `fields` assertion from seven keys to nine and adds one `Run` per new id. `tests/journal/main.swift` keeps the Swift writer compiling. New context ids are not required from the Swift harness on this host.
  - `config.example.json`, `config.example.windows.json`, `config.example.windows-url.json`, `config.example.macrowhisper.json`, and `config.example.grokbot.json` get `"journalTaskFile": null` inside `dictation`. grokbot also gets `journalDir`, same value as `config.example.windows.json`. `config.example.macos-url.json` gains no `dictation` block.
  - `README.md` and `README_ja.md` each gain one sentence on `dictation.journalTaskFile` in the existing journal paragraph. Keep every phrase `examples-readme` already checks.
- **acceptance:** `examples-readme`, `fields`, `focus-app-shape`, `focus-app-null`, `focus-app-at-start`, `focus-wired-at-start`, `task-unset-null`, `task-json-file`, `task-plain-file`, `task-missing-file`, `task-oversize`, `task-bad-json`, `task-expand-path`, `task-failure-isolated`, `task-reload`, `examples-task-file`. `verify.sh drive` then prints `PASS` for every other items.md id except `SKIP swift-harness` and `SKIP windows-acl`.
- **risk:** This host cannot run NSWorkspace or a Windows foreground window. The wiring items read source. Mac 未検証 and Windows UI 未検証 stay true for a live focus change. A Swift typo is unseen until a Mac build. Mirror the C# key order. Do not rewrite old JSONL. Do not commit `.claude/sw0.md` or `.claude/sw0b.md`.
- **effort:** #high

### Checks the implementer runs

```sh
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet format --include dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs dotnet/VoiceSwitch.Windows.Core/DictationHistory.cs dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs dotnet/VoiceSwitch.Windows/DictationRuntime.cs tests/journal/Program.cs
dotnet build dotnet/VoiceSwitch.Windows.Core/VoiceSwitch.Windows.Core.csproj -c Release --nologo
dotnet build dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj -c Release -r win-x64 --nologo
dotnet build tests/journal/JournalTests.csproj -c Release --nologo
.cursor/skills/verify-dictation-journal/verify.sh doctor
.cursor/skills/verify-dictation-journal/verify.sh drive
```

Touched `.cs` files add no warnings. `drive` exits 0. Stdout has `PASS` for every items.md id except the two host skips. Commit on `feat/journal-focus-tcc`. Do not push.
