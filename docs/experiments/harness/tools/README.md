# 判定ツール

アプリの `NoWakePolicy`（`Sources/voice-switch/WakeSafety.swift`）をそのまま呼ぶ、小さな Swift のツールです。

```sh
SWIFTC=/path/to/swiftc docs/experiments/harness/tools/build.sh <リポジトリのルート> <出力先>
```

| ツール | 使う集計 | 必要なコード |
|---|---|---|
| `agree_same_empty` | `app_eval.py` | main |
| `agree_pair_main` | `app_eval3.py`（変更前） | main |
| `agree_pair` | `app_eval3.py`（PR #38 以降） | PR #38 以降 |
| `hallucination_filter` | `s1_eval.py` | PR #38 以降 |

コードにない関数を使うツールは、作らずに飛ばします。集計スクリプトは、`clips/` と `heldout/` があるディレクトリで実行してください。

確かめた結果です。PR #38（`4081776`）で作ったツールで `s1_runs_seg.jsonl` を集計すると、想定Bでの雑音の通過は、変更前の `agree_pair_main` が基準 7/11 と held-out 3/6、`agree_pair` が 0/11 と 0/6 で、記録と同じでした。
