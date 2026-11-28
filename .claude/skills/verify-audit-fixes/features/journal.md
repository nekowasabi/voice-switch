# ジャーナル（指摘 4・7・18）

## Sub-features
- 4: Mac で 2 つの handoff が同時に追記しても、2 行とも完全な JSON 行として残る。
- 18: `deleteEntry` / `deleteDay` / `filter` を消す（または `filter` を `rename` 1 回の置き換えにする）。
- 7 は Windows 側（windows-handoff-and-tray.md）。

## How to get to it (user POV)
自動の口述とウェイクの口述がほぼ同時に終わったあと、メニューの履歴と `journalDir` の当日の `.jsonl` に両方が入っているかを見る。

## Driving it with verify-journal-append
- 守る振る舞い: `bash .claude/skills/verify-journal-append/run.sh check`（baseline に入っている）。
- 4 の再現: `tests/journal/main.swift` に、2 つのスレッドから `DictationJournal.bind` と `DictationHistory.add` を交互に 200 回ずつ呼ぶ検査を足す。全行が JSON として読めて、400 件そろうことを確かめる。直す前に欠けが出なければ、4 は直さない。
- 18 を消すなら、`tests/journal/*` と verify-journal-append の該当ケースを同じコミットで消し、baseline を通す。

## Gotchas
- 4 は監査の時点で未検証（仕組みは読んで確認、競合は再現していない）。
- 削除の判定は、関数名の grep ではなく baseline の合否で行う。
