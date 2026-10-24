#!/usr/bin/env python3
"""Ask Jev which tray a monologue belongs in.

Same request shape as voice_switch_ai.jev.build_body.
Needs TYPESAFE_API_KEY or JEV_API_KEY for a live run.
`--list` and `--score` stay offline. The pane confidence floor is not reused.
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))

from voice_switch_ai.calibration import format_report, recommend
from voice_switch_ai.jev import build_body

# (utterance, expected choice, edge)
CASES = [
    ("うん", "none", "backchannel"),
    ("へえ", "none", "backchannel"),
    ("なるほど", "none", "backchannel"),
    ("はいはい", "none", "backchannel"),
    ("そうだね", "none", "backchannel"),
    ("保存先はジャーナルでいくことにした", "none", "decision"),
    ("今日はこの方針で決めた", "none", "decision"),
    ("なんか疲れた", "none", "feeling"),
    ("この設計でいいのか不安だ", "none", "feeling"),
    ("いい天気だな", "none", "feeling"),
    ("人生とは何だろう？", "none", "rhetorical"),
    ("なんでこうなるんだろうね、みたいな", "none", "rhetorical"),
    ("それが問題だろうか", "none", "rhetorical"),
    ("本当にそれでいいのかな", "none", "rhetorical"),
    ("明日までに報告書を出さなきゃ", "task", "clear"),
    ("請求書を金曜までに出さないと", "task", "clear"),
    ("やること、依存関係を更新する", "task", "clear"),
    ("テストを書かなきゃ", "task", "clear"),
    ("TODO ログの権限を直す", "task", "clear"),
    ("APIのレート制限って何だっけ", "research", "clear"),
    ("この関数の仕様を調べて", "research", "clear"),
    ("なぜビルドが落ちるか教えて", "research", "clear"),
    ("ジャーナルのスキーマはどうなってる？", "research", "clear"),
    ("田中さんへの返信の下書き、来週の件は承知しました", "draft", "clear"),
    ("メールの文面を考えている、お疲れさまです", "draft", "clear"),
    ("送る文面は短くしたい、了解ですで始めよう", "draft", "clear"),
    ("クロードにテストを書いてと頼みたい", "agent", "clear"),
    ("カーソルにこの差分を見てほしい", "agent", "clear"),
    ("エージェントに日次のまとめを頼む", "agent", "clear"),
    ("パソコンにリマインダーを入れて", "agent", "clear"),
    ("仕様を調べなきゃいけないし、田中さんに聞く文面もいる", "research", "mixed"),
    ("クロードに調べてと頼まなきゃ", "agent", "mixed"),
    ("うん、報告書出さなきゃ", "task", "mixed"),
    ("決めた、でもまだ調べなきゃ", "research", "mixed"),
    ("請求書、金額を確認", "none", "fragment"),
    ("あの件どうしよう", "none", "fragment"),
    ("しなきゃ？", "none", "fragment"),
    ("これIssueにしなきゃ", "task", "issue"),
    ("ミューズ、要約して", "agent", "explicit"),
    ("今日のメモ、重複してるから一つにまとめたい", "task", "fragment"),
]


def ask(key, utterance):
    import urllib.request

    body = json.dumps(build_body(utterance)).encode()
    req = urllib.request.Request(
        "https://api.typesafe.ai/v1/systemone",
        data=body,
        method="POST",
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=10) as response:
        return json.load(response)["answers"]["tray"]


def expected_map():
    return {utterance: expected for utterance, expected, _edge in CASES}


def print_list():
    edges = {}
    for _utterance, _expected, edge in CASES:
        edges[edge] = edges.get(edge, 0) + 1
    print(f"cases={len(CASES)}")
    for edge in sorted(edges):
        print(f"edge={edge} count={edges[edge]}")


def rows_from_answers(answers):
    wanted = expected_map()
    rows = []
    missed = 0
    for item in answers:
        utterance = item["utterance"]
        if utterance not in wanted:
            missed += 1
            print(f"unmatched {utterance}", file=sys.stderr)
            continue
        rows.append((item["choice"], float(item["confidence"]), wanted[utterance]))
    return rows, missed


def print_score(rows, source):
    report = format_report(recommend(rows), source)
    print(report)
    return report


def main(argv):
    if "--list" in argv:
        print_list()
        return 0
    if "--score" in argv:
        path = Path(argv[argv.index("--score") + 1])
        answers = json.loads(path.read_text(encoding="utf-8"))
        rows, missed = rows_from_answers(answers)
        if not rows:
            print("no matching rows", file=sys.stderr)
            return 2
        print_score(rows, "synthetic" if "synthetic" in path.name else "file")
        print(f"coverage={len(rows)}/{len(CASES)} missed={missed}")
        return 0
    key = os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY")
    if not key:
        sys.exit("TYPESAFE_API_KEY or JEV_API_KEY is required")
    rows = []
    right = 0
    for utterance, want, edge in CASES:
        answer = ask(key, utterance)
        got = answer["choice"]
        confidence = float(answer["confidence"])
        ok = got == want
        right += ok
        rows.append((got, confidence, want))
        print(f"{'OK ' if ok else 'NG '} want={want} got={got} conf={confidence:.2f} edge={edge} {utterance}")
    print(f"{right}/{len(CASES)}")
    print_score(rows, "live")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
