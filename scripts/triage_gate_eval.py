#!/usr/bin/env python3
"""Score where the app would send labeled utterances, with the current questions (Jev answers come from the cache).

Labels (JSONL): {"id", "expect": "drop" | "memo" | "route:<kind>", and either "device" + "day" (a journal row) or "text"}.
Rows with "text" only (for example superwhisper recordings) are put in a scratch journal and asked like a journal row.
Prints one JSON object: counts of right and wrong outcomes, and the wrong rows' ids.
Usage: triage_gate_eval.py LABELS [--root JOURNAL_ROOT]
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
    args = parser.parse_args()
    labels = [json.loads(l) for l in Path(args.labels).read_text(encoding="utf-8").splitlines() if l.strip()]
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
    print(json.dumps({"labels": len(labels), "right": counts["right"], "wrong": {k: v for k, v in counts.items() if k != "right"},
                      "wrong_ids": wrong}, ensure_ascii=False))


if __name__ == "__main__":
    main()
