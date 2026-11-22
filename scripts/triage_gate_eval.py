#!/usr/bin/env python3
"""Score where the app would send labeled utterances, with the current questions (Jev answers come from the cache).

Labels (JSONL): {"id", "expect": "drop" | "memo" | "route:<kind>", and either "device" + "day" (a journal row) or "text"}.
The app's own labels (.triage/labels/<device>-app.jsonl, written when the user presses 送る, 送らない or picks a kind)
work too: their "outcome" is the expected place, and the latest row per id wins.
Rows with "text" only (for example superwhisper recordings) are put in a scratch journal and asked like a journal row.
Prints one JSON object: counts of right and wrong outcomes, and the wrong rows' ids.
--sweep also scores other lines for the two gate questions (needs_local, keep_note) and prints the best few, so a
change to TriageClassifier's lines can be checked on the same labels first.
Usage: triage_gate_eval.py LABELS [--root JOURNAL_ROOT] [--sweep]
"""
import argparse
import json
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import journal_triage as jt  # noqa: E402


def listed(root, day, journal_dir):
    out = subprocess.run([sys.executable, str(HERE / "journal_triage.py"), "--format", "json", "--root", str(root),
                          "list", day, str(journal_dir)], capture_output=True, text=True, check=True).stdout
    data = json.loads(out)
    return {row["id"]: row for key in ("actions", "thoughts", "unjudged") for row in data[key]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("labels")
    parser.add_argument("--root", default=str(jt.configured_root()))
    parser.add_argument("--sweep", action="store_true")
    args = parser.parse_args()
    latest = {}
    for line in Path(args.labels).read_text(encoding="utf-8").splitlines():
        row = json.loads(line) if line.strip() else {}
        row.setdefault("expect", row.get("outcome"))
        if row.get("expect") and ("text" in row or ("device" in row and "day" in row)):
            latest[row["id"]] = row
    labels = list(latest.values())
    rows = {}
    for device, day in sorted({(l["device"], l["day"]) for l in labels if "day" in l}):
        rows.update(listed(args.root, day, Path(args.root) / device))
    loose = [l for l in labels if "day" not in l]
    if loose:
        with tempfile.TemporaryDirectory() as tmp:
            journal = Path(tmp) / "eval"
            journal.mkdir()
            lines = [json.dumps({"id": l["id"], "timestamp": f"2026-01-01T10:{i // 60:02d}:{i % 60:02d}.000+09:00",
                                 "text": l["text"], "origin": "ウェイク", "endReason": "stop", "recordingSeconds": 5,
                                 "schemaVersion": 1}, ensure_ascii=False) for i, l in enumerate(loose)]
            (journal / "2026-01-01.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")
            rows.update(listed(tmp, "2026-01-01", journal))
    counts, wrong = Counter(), []
    for label in labels:
        row = rows.get(label["id"])
        got = jt.outcome(row["text"], row["destinations"], row["answers"], row.get("origin")) if row else "drop"  # filler rows are not listed
        key = "right" if got == label["expect"] else f'{label["expect"]} -> {got}'
        counts[key] += 1
        if got != label["expect"]:
            wrong.append(label["id"])
    result = {"labels": len(labels), "right": counts["right"], "wrong": {k: v for k, v in counts.items() if k != "right"},
              "wrong_ids": wrong}
    if args.sweep:
        tried = []
        grid = [0.5, 0.6, 0.7, 0.8, 0.9]
        for session in grid:
            for keep in grid:
                for keep_automatic in grid:
                    if keep_automatic < keep:
                        continue
                    lines = {**jt.GATE_LINES, "session": session, "keep": keep, "keep_automatic": keep_automatic}
                    right = sum(1 for label in labels if (row := rows.get(label["id"])) and label["expect"] == jt.outcome(
                        row["text"], row["destinations"], row["answers"], row.get("origin"), lines))
                    tried.append((right, -sum(abs(lines[k] - jt.GATE_LINES[k]) for k in lines), lines))
        tried.sort(key=lambda t: (t[0], t[1]), reverse=True)
        result["sweep"] = {"current": jt.GATE_LINES, "best": [{"right": r, **l} for r, _, l in tried[:5]]}
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
