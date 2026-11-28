# 振り分け CLI（指摘 10・11）

## Sub-features
- 10: `list` / `review` の `--no-context` で得た答えを、文脈ありの実行に使い回さない（逆も）。
- 11: Jev のキー切れ・429・通信失敗のとき、stderr に HTTP ステータスかエラーの種類を 1 行出す。口述の本文・アプリ名・タスク名は出さない。

## How to get to it (user POV)
`python3 scripts/journal_triage.py list 2026-11-21` を、`--no-context` あり・なしで流して結果を比べる。キーが切れた日に `list` を流す。

## Driving it with check.sh
- `check.sh 10` / `check.sh 11` が `triage_checks.py` を流す。`tests/test_journal_triage.py` の World（一時の HOME）と Net（偽の urlopen と接続の禁止）を使う。
- 守る振る舞い: `python3 -m unittest tests/test_journal_triage.py`（baseline に入っている）。C3（`--no-context` は発話だけ送る）・C4（同じ質問文の答えは聞き直さない）・C6（stderr に本文を出さない）が通り続けること。

## Gotchas
- 10 を直すとキャッシュのキーが変わり、既存のキャッシュは一度だけ聞き直しになる。C4 の「新しい質問文なら聞き直す」と矛盾しないことを unittest で確かめる。
- 11 のメッセージは失敗のたびではなく最初の 1 回に絞ってよい。検査は「理由が出るか」だけを見る。
