# Journal verification items

Source: https://github.com/nekowasabi/voice-switch/issues/49

Each id is one mechanical check. The harness prints `PASS <id>` or `FAIL <id>`. `SKIP swift-harness` and `SKIP windows-acl` are the only allowed skips on this Linux host.

Normative record. One JSON object per line, UTF-8, no BOM, `\n` only. Keys always present, in this order:

```json
{"id":"00000000-0000-0000-0000-000000000000","timestamp":"2026-10-24T14:24:00.0000000+09:00","text":"メモ","origin":"ウェイク","endReason":"Silence","recordingSeconds":1.25,"schemaVersion":1}
```

- `id` is the history entry id, lowercase `8-4-4-4-12`.
- `timestamp` is ISO 8601 with a timezone. A numeric offset or `Z` both qualify. Production stamps local time with its offset.
- `origin` is `自動`, `ウェイク`, or `手動`.
- `endReason` is the key for 終了理由. The issue does not name this key. The value is the finish token the caller already has (`Silence`, `FinishCommand`, `StandaloneStop`, `MaximumDuration`, or the same strings the session log already uses), or JSON `null` when Add was not given one. Do not add a `FinishReason` case for the journal.
- `recordingSeconds` is the key for 録音秒数. The issue does not name this key. The value is a JSON number of seconds, or JSON `null` when Add was not given one.
- `schemaVersion` is the integer `1`.

The daily file name is the calendar date of that timestamp in its own offset (`2026-10-23T23:30:00+09:00` → `2026-10-23.jsonl`, `2026-10-23T16:00:00Z` → `2026-10-23.jsonl`).

## Items

- id: history-hook
  spec: 永続ジャーナルは短期履歴に入った発話を、DictationHistory への追加時点で追記する。終了理由の状態機械は変えない。
  pass: A successful Add writes one new JSONL line whose id and text are the arguments. The line is readable from a second file handle before Add returns. The write runs inside `DictationHistory.Add` and `DictationHistory.add`. A line written from `DictationSession.Finish`, from a `FinishReason` transition, or only from the handoff before Add fails this item. Do not add finish states. Issue #48 owns that state machine.

- id: rejected-add-skips-journal
  spec: 履歴に入らなかった発話はジャーナルにも入らない。
  pass: Blank text, text over 100_000 chars, a duplicate id, a stale expectedGeneration, and an automatic transcript the policy rejects all return false and add zero bytes.

- id: memory-bounds
  spec: 短期履歴は現行のまま。メモリ内 30 分・最大 50 件。
  pass: A history built with the production defaults reports capacity 50 and retention of 30 minutes. Evicting past capacity or past retention removes the in-memory entry only.

- id: memory-clear-keeps-journal
  spec: 保存した発話はデバイス上に残る。メモリの消去では消えない。
  pass: Clear() removes the in-memory entry and leaves the JSONL bytes unchanged.

- id: memory-evict-keeps-journal
  spec: 30 分経過または 50 件超過でメモリから消えても、ジャーナルの行は残る。
  pass: After retention prune or capacity eviction, the previously appended line is still byte-identical.

- id: daily-file
  spec: ディレクトリ指定なら日単位ファイル。ファイル名は 2026-10-23.jsonl の形。
  pass: Two timestamps on different offset-dates create two files. Two timestamps on the same offset-date append to one file.

- id: single-file
  spec: パスが .jsonl で終わる場合は単一ファイルへの追記。
  pass: A path ending in `.jsonl` (any case) appends to that file and creates no dated sibling. A path that does not end in `.jsonl` is a directory.

- id: fields
  spec: 項目は id、timestamp（ISO 8601、タイムゾーンつき）、text、origin、終了理由、録音秒数、schemaVersion。
  pass: The line has exactly those seven keys, in the order above. timestamp matches an ISO 8601 instant with `Z` or a numeric offset. schemaVersion is 1. A text containing a quote and a newline round-trips as one physical line.

- id: origin-automatic
  spec: origin は自動／ウェイク／手動。
  pass: A non-strict WakeOrigin and manual false writes `自動`.

- id: origin-wake
  spec: origin は自動／ウェイク／手動。
  pass: A strict WakeOrigin and manual false writes `ウェイク`.

- id: origin-manual
  spec: origin は自動／ウェイク／手動。手動はホットキー開始。
  pass: Add with manual true writes `手動` even when `WakeOrigin.rule` is strict. A hotkey utterance that reaches history is `手動` because the Mac and Windows paths that log `dictation session: manual-hotkey` pass manual true into Add. The proof calls Add. It does not read a `Manual` property on `DictationAudio`, and it does not require a new `FinishReason`.

- id: expand-tilde
  spec: ~ の展開は recordingsDir と同様。
  pass: `~/Documents/voice-switch/journal` expands with the same home rule as `WindowsPaths.ExpandPath` and Swift `Platform.expandPath`.

- id: expand-localappdata
  spec: %LOCALAPPDATA% の展開は recordingsDir と同様。
  pass: `%LOCALAPPDATA%\voice-switch\journal` expands with `WindowsPaths.ExpandPath`. Swift on Windows uses the same `%VAR%` replacement.

- id: default-mac
  spec: 既定は ~/Library/Application Support/voice-switch/journal/
  pass: A null journalDir on the Mac resolver expands to that path under the home directory.

- id: default-windows
  spec: 既定は %LOCALAPPDATA%\voice-switch\journal\
  pass: A null journalDir on the Windows resolver expands to that path.

- id: invalid-fallback
  spec: パスが不正ならログに残し、既定の保存先にフォールバックする。設定の読み込みは成功する。
  pass: A parsed `journalDir` that is empty, still contains `%UNSET%`, has an invalid character, or is a directory path whose leaf is an existing file does not throw from config load. The writer logs a line containing `dictation journal:` and `using default`, and the record is written under the OS default directory. A previously loaded custom directory is not kept. Rejecting that path inside `ConfigLoader` fails this item, because a load error keeps the previous config instead.

- id: unwritable-fallback
  spec: 書き込み不可ならログに残し、既定の保存先にフォールバックする。設定の読み込みは成功する。
  pass: A parent that cannot be created (the parent path is a file) does not throw from config load. The writer logs a line containing `dictation journal:` and `using default`, and the record is written under the OS default directory. A previously loaded custom directory is not kept.

- id: reload-keeps-config
  spec: 不正な再読込で前の有効な設定を維持する。解析できた悪い journalDir のフォールバックとは別である。
  pass: Load a config whose `journalDir` is a writable custom directory and append one line there. Replace the file with JSON that `ConfigLoader` rejects. Reload keeps that custom directory, the log contains `config reload failed, keeping previous`, and the next append still goes there. A later file that parses with a different `journalDir` appends there. A parsed `journalDir` that is only an unusable path does not take this branch. That case is `invalid-fallback` and `unwritable-fallback`. Reading the catch text is not a pass. The Swift file follows the same rule when `swift-harness` runs.

- id: no-symlink
  spec: mode 復元ストアの流儀。リンク先へは書かない。
  pass: When the resolved directory or the `.jsonl` file is a symlink, the writer logs and falls back to the default. It does not create the target through the link.

- id: perms-unix
  spec: ディレクトリ 0700／ファイル 0600。
  pass: On a non-Windows host the journal directory mode is 0700 and the JSONL file mode is 0600, including when the file already existed as 0644. The check uses `stat` mode bits, not the requested creation mode alone.

- id: perms-leaf-only
  spec: 0700 はジャーナルのディレクトリに掛ける。親を巻き込みすぎない。
  pass: Creating `<root>/journal` sets 0700 on `journal` only. `<root>` stays whatever it was (the test creates it as 0755).

- id: windows-acl
  spec: Windows は mode 復元ストアと同じく、現在ユーザーだけの ACL。
  pass: On Windows, the directory DACL is protected and grants the current user full control without inherited rules. This host prints `SKIP windows-acl` unless `OperatingSystem.IsWindows()` is true.

- id: append-only
  spec: JSONL は末尾追記だけ。アプリが落ちても既存分が壊れない。原文は書き換えない。
  pass: After a second append, the first line's bytes are unchanged and the file length grew by the new line only. A partial trailing fragment left from a simulated crash stays in place and the next append adds a complete line after it. Parsing complete lines still yields the first record.

- id: flush-visible
  spec: Add が戻った時点で、別ハンドルから既存行が読める。
  pass: The writer flushes before Add returns. A second FileStream opened after Add sees the new line while the writer object is still alive.

- id: immutable-text
  spec: 原文は不変。加工は別ファイル。この Issue は派生データを書かない。
  pass: The journal type has no method that replaces a record's text. Delete-by-id rewrites the file only by dropping lines, and every surviving line is byte-identical.

- id: delete-day
  spec: 日単位の削除。
  pass: DeleteDay("yyyy-MM-dd") removes that offset-date's records. In directory mode the day's file is gone. In single-file mode only those lines are removed. Other days' lines stay byte-identical. An unknown day removes nothing and returns 0.

- id: delete-entry
  spec: 件単位の削除。
  pass: DeleteEntry(id) removes that one line. Other lines stay byte-identical. A missing id returns false and does not change the file bytes.

- id: text-only
  spec: テキストのみ。音声ファイルは残さない。
  pass: The JSON object has no audio, path, or sample field. After Add, the journal directory contains only `.jsonl` files (and no `.wav`).

- id: journal-failure-isolated
  spec: ジャーナルに書けなくても、短期履歴への追加そのものは成功する。
  pass: When the configured path and the default path both refuse writes, Add still returns true and Snapshot contains the text.

- id: examples-readme
  spec: config.example と README を更新する。
  pass: Every `config.example*.json` that already contains a `dictation` object contains `journalDir`. `config.example.macos-url.json` has no `dictation` block and must not gain one. `README.md` and `README_ja.md` mention `dictation.journalDir`, both default directories, `.jsonl` single-file mode, fallback, 0700/0600, and day/entry deletion. They say there is no viewer.

- id: no-ui
  spec: 閲覧 UI は作らない。
  pass: The journal sources do not reference `Windows.Forms`, `NSWindow`, `HistoryWindow`, or a new menu title. No file named `*Journal*Window*` exists.

- id: both-sources
  spec: Mac と Windows の両方。
  pass: `Sources/voice-switch/DictationJournal.swift` and `dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs` both contain `自動`, `ウェイク`, `手動`, and `schemaVersion`. Swift `DictationHistory.add` calls the journal. C# `DictationHistory.Add` calls the journal.

- id: swift-harness
  spec: Mac 側の同じ規則を swiftc で実行する。
  pass: When `swiftc` exists, the Swift harness prints the same PASS ids for path expansion, append, fallback, permissions, deletion, and `reload-keeps-config`. When `swiftc` is absent, the C# harness prints `SKIP swift-harness` and this is not a pass.
