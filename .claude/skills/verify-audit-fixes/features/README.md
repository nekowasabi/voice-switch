# 機能マップ（監査の指摘 1〜20）

| 指摘 | 内容 | ファイル | 検査 |
|---|---|---|---|
| 1 | Windows の掃除が mode-restore.json を消す | windows-handoff-and-tray.md | C# テスト（未作成・dotnet が要る） |
| 2, 3 | 検証スクリプトが失敗を隠す | verification-harness.md | `check.sh 2`, `check.sh 3` |
| 4, 7, 18 | ジャーナルの追記・切り替え・削除 | journal.md | 手順のみ |
| 5 | HUD が届かなかった口述にも ✓ を出す | delivery-and-hangs.md | 手順のみ |
| 6, 16 | Windows の Reload 停止と診断 | windows-handoff-and-tray.md | C# テスト（未作成） |
| 8, 9 | 子プロセスを期限なしで待つ・キューに上限が無い | delivery-and-hangs.md | 手順のみ（未検証の指摘） |
| 10, 11 | 振り分け CLI のキャッシュと失敗理由 | triage-cli.md | `check.sh 10`, `check.sh 11` |
| 12, 13, 14, 15 | 実行されないテスト・予算・古いバイナリ・落ちない証明 | verification-harness.md | `check.sh 13` ほかは手順 |
| 17, 19, 20 | 削除・統合・増え続けるもの | cleanup.md | baseline と equivalence |
