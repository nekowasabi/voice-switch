---
name: verify-audit-fixes
description: 2026-11-28 の監査（ponytail-audit）の指摘 1〜20 を直すときの検証。直す前に今の振る舞いを守る基準（baseline）を通し、各指摘には「直る前は FAIL、直したら PASS」になる検査を当てる。macOS の検証スクリプト・TLA+ の行数予算・振り分け CLI（scripts/journal_triage.py）と、Windows の Superwhisper handoff・トレイ・ジャーナルを直す前と、直したと言う前に使う。
---

# verify-audit-fixes

監査の指摘を直しても、利用者に見える振る舞いが壊れないことを確かめる。指摘ごとの中身と検査は `features/` にある。

## Launch

アプリは起動しない。検査は一時フォルダと偽のコマンド（PATH の先頭に置く shim）で動き、利用者の VoiceSwitch.app・キャッシュ・ネットワークには触れない。`make`・`make install`・`make stop` は使わない（利用者のアプリを入れ替えたり止めたりするため）。

## Doctor

`sh .claude/skills/verify-audit-fixes/check.sh doctor`

swift・swiftc・java・python3・git の有無と、dotnet が無いときに走らせられない指摘（1・6・7・16、8 と 18 の C# 側）を出す。

## Drive

リポジトリのルートで実行する。終了コード 0 が PASS。

| コマンド | 確かめること | 直す前 |
|---|---|---|
| `check.sh baseline` | `sh tests/check.sh` が通る（守る振る舞い全体。120 秒以内） | PASS |
| `check.sh 2` | swift build が失敗したら `verify-voice-switch-mac/verify.sh build` も失敗する | FAIL |
| `check.sh 3` | Swift の検査が落ちたら `verify-sticky-mode/verify.sh drive` も失敗する | FAIL |
| `check.sh 10` | `--no-context` でキャッシュした答えを、文脈ありの実行で使い回さない | FAIL |
| `check.sh 11` | Jev が 401 を返したとき、stderr に理由が出る（本文・アプリ名・タスク名は出さない） | FAIL |
| `check.sh 13` | HudBlip・HudNoise・MeetPause が 2026-11-22 の行数を 1 行でも越えたら `budget.sh` が落ちる | FAIL |
| `check.sh all` | 上の全部 | baseline 以外 FAIL |

直す手順:
1. 直す前に `check.sh baseline` と、対象の番号を流す。番号は FAIL になるはず。PASS なら指摘が間違っているので、直さずに報告する。
2. 直す。
3. 同じ番号が PASS、`check.sh baseline` も PASS になることを確かめる。
4. 構造を変えた修正（17〜19 の削除・統合）は `sh tests/equivalence.sh <直す前のコミット>` で前後を比べる。

番号の無い指摘は、`features/` にある手順で確かめる。「未検証」の指摘は、まず再現させる。再現しなければ直さない。

## Evidence

各実行は `${VS_AUDIT_ROOT:-~/Library/Caches/voice-switch-audit}/<UTC 時刻>-<pid>/` に `summary.txt` と番号ごとの `.log` を残す。最後の行の `evidence` がその場所を指す。判定は終了コードと、利用者に見える結果（失敗が失敗として返るか、理由が表示されるか）で行う。件数や並びは比べない。

## Cleanup

一時フォルダと shim は終了時に `trap` で消える。証拠のフォルダは消さない。要らなくなったら、日付を見て古いものから手で消す。

## Gotchas

- 2 の検査は、前回のビルドが残っている状態を作ってから行う。空のフォルダでは `built-from.txt` を書けずにたまたま失敗するので、そのままでは不具合を見逃す。
- 3 の検査は `sh` の shim で TLC の段（anchors・check・break・finding）を飛ばす。TLA+ のモデルそのものは baseline で検査する。
- Windows の指摘は、この Mac では走らない。WSL の機械で `make win-test` に足したテストを流す（`features/windows-handoff-and-tray.md`）。
