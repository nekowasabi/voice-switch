# 削除・統合・増え続けるもの（指摘 17・19・20）

## Sub-features
- 17: `scripts/triage_probe.py`、`docs/experiments/`、参照の無い `plan/` の 4 つ、`PaneRoute.swift` の `/proc` 処理、`Makefile:144` を消す。
- 19: TLC の準備処理を 1 つにまとめる。Jev への HTTP 呼び出しの写しを減らす。
- 20: `misses.jsonl`、`DictationJournal.notes`、`voice-switch.log` に上限を付ける。

## How to get to it (user POV)
利用者には見えない変更。見えるのは「何も変わらないこと」。

## Driving it with tests/equivalence.sh
1. 消す前のコミットを記録する。
2. 消す直前に `git fetch` し、origin/main で消す対象への参照が増えていないかを確かめる。
3. 消す。`tests/no-verification.txt`・各 `covers.txt`・`pane_route_proof.py` の文字列検査など、消した名前を指す行を同じコミットで直す。
4. `check.sh baseline` と `sh tests/equivalence.sh <1 のコミット>` が通る。
- 20: 上限の値を小さくした一時の設定で `--simulate` を流し、ファイルや行数が上限で止まることを見る。

## Gotchas
- TLC の準備をまとめたら、baseline の「tla+」の段が全部通ることを見る。1 つでも SKIP になったら戻す。
