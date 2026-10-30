# Focused app and TaskChute context

Each accepted journal line names the app that was in front when dictation started, and the TaskChute task recorded in an optional local file at append time. A missing fact is JSON null. The utterance is still kept.

## Sub-features

- `focus-app-shape` writes name, bundleId, exe, and pid, and writes no window title.
- `focus-app-null` writes focusApp null when no app was captured.
- `focus-app-at-start` keeps the noted app across a later end-reason note, and an Add argument wins.
- `focus-wired-at-start` notes the session-start app on Mac and Windows, not the paste retarget.
- `task-unset-null` writes taskchute null when journalTaskFile is unset, and does not call tcc2.
- `task-json-file` reads a JSON task and stamps source and readAt.
- `task-plain-file` uses the first non-empty line as the name.
- `task-missing-file` writes null when the file is absent and still accepts the utterance.
- `task-oversize` ignores a file larger than 4096 bytes.
- `task-bad-json` does not use a broken brace body as the name.
- `task-expand-path` expands ~ and %LOCALAPPDATA% like journalDir.
- `task-failure-isolated` accepts the utterance when the task path cannot be read.
- `task-reload` follows a new task path and keeps the old one when reload fails to parse.
- `examples-task-file` documents journalTaskFile and puts journalDir on the grokbot sample.
- `fields` writes the nine keys, including these two, with schemaVersion 1.

## How to get to it (user POV)

- Dictate while Ghostty, or another editor, is the app you are typing in. Open that day's `.jsonl` file. `focusApp.name` is that app. It is not Superwhisper, which comes forward while the recording is transcribed.
- Set `dictation.journalTaskFile` to a small file an external updater refreshes. Put one task name on the first line, dictate, and see that name under `taskchute`. Delete the file, dictate again, and see `taskchute` null. The text is still in the file.
- Leave `journalTaskFile` out of `config.json`. New lines have `taskchute` null. voice-switch does not launch TaskChute.

## Driving it with verify-journal

Preconditions:

- `$V doctor` printed `doctor: ready`.
- Scratch is under `/tmp/voice-switch-journal-verify/scratch`. Do not point the task file at the user's real `~/.cache`.

- **Shape.** Run `.cursor/skills/verify-dictation-journal/verify.sh drive journal-context`. Stdout contains `PASS focus-app-shape`, `PASS focus-app-null`, `PASS focus-app-at-start`, and `PASS fields`.
- **Wiring.** The same run contains `PASS focus-wired-at-start`. That check reads source. It does not activate an app.
- **Task file.** The same run contains `PASS task-unset-null`, `PASS task-json-file`, `PASS task-plain-file`, `PASS task-missing-file`, `PASS task-oversize`, `PASS task-bad-json`, `PASS task-expand-path`, `PASS task-failure-isolated`, and `PASS task-reload`.
- **Samples.** The same run contains `PASS examples-task-file`.
- **Proof.** `stdout.txt` in the evidence directory contains those PASS lines and `exitcode.txt` is `0`.

## Gotchas

- The line shape is the normative record in `items.md`. `schemaVersion` stays 1. Do not rewrite old JSONL lines.
- Capture the app when the session's target is chosen. Mac `submit` notes `d.target`. The automatic `handoff` that reads `NSWorkspace.shared.frontmostApplication` is for paste, and that value must not be the journal app. Windows notes the hwnd stored in `target` at wake, automatic start, and the manual hotkey. `Submit` may set `audio.Target` from a later `foregroundWindow()` for paste. The journal does not read `audio.Target`.
- `JournalNotes.Note` merges. A later note that sets `endReason` and omits focus keeps the focus from session start.
- Read the task file at append, with a 4096 byte cap. Unset, missing, oversized, unreadable, or not a usable task is JSON null. Add still returns true. Do not start `tcc2`, and do not add a network client.
- `readAt` is the same string as `timestamp` on that line. `source` is `file`.
- There is no default path. `~/.cache/tmux/taskchute.txt` is one updater's file on one machine. The sample config value is null.
- `config.example.macos-url.json` is command mode. A new `dictation` block fails `examples-task-file` and `examples-readme`.
- This Linux host has no `swiftc` and no Windows desktop. `SKIP swift-harness` and `SKIP windows-acl` stay non-passes. NSWorkspace and a real foreground window stay unverified. Say Mac 未検証 and Windows UI 未検証 for those two.
