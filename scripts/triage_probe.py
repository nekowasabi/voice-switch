#!/usr/bin/env python3
"""Send the classifier questions to Jev for a few utterances, to see which band each answer lands in.

Reads the listed journal rows for the given days plus invented control sentences. --dry-run prints what would be sent.
Each request and response is saved under --out so any case can be rerun. Live requests use a key from the environment.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.request
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import journal_triage as triage  # noqa: E402

# Invented sentences with the answer a person would give. They check the bands, not the user's speech.
CONTROLS = [
    ("えーと、あれ、うん", "filler"),
    ("この坂、思ったより長いな", "thought"),
    ("牛乳を買っておかないと", "action:self_task"),
    ("このスクリプトで祝日も数えられるようにして", "action:computer_change"),
]


def ask(key, body):
    request = urllib.request.Request("https://api.typesafe.ai/v1/systemone", data=json.dumps(body).encode("utf-8"), method="POST",
                                     headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=20) as response:
        return json.load(response)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("days", nargs="+", type=date.fromisoformat, metavar="YYYY-MM-DD")
    parser.add_argument("--root", type=Path, default=None, help="default: the parent of config.json's dictation.journalDir")
    parser.add_argument("--out", type=Path, default=Path("triage-probe"))
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)
    root = (args.root or triage.configured_root()).expanduser()

    cases = []
    for day in args.days:
        daily = []
        for device, sources in triage.sources_by_device(root, triage.configured_device()).items():
            entries = [e for source in sources for e in triage.journal_rows(source, day) if e]
            history, instants = triage.context_history(sources, day, entries)
            for entry in entries:
                previous = triage.prior_entries(entry, history, instants)
                daily.append((entry, device, triage.request_state(entry, previous)))
        daily.sort(key=lambda item: item[0].instant)
        cases += [(f"{device} {e.timestamp:%m-%d %H:%M:%S}", state, None) for e, device, state in daily]
    cases += [(f"control {i + 1}", {"utterance": {"text": text, "started": "自動"}}, want) for i, (text, want) in enumerate(CONTROLS)]

    key = None if args.dry_run else os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY")
    if not args.dry_run and not key:
        parser.error("set TYPESAFE_API_KEY or JEV_API_KEY, or use --dry-run")
    questions = triage.load_questions()
    args.out.mkdir(parents=True, exist_ok=True)
    for n, (name, st, want) in enumerate(cases, 1):
        body = {"model": "jev-latest", "state": st, "questions": questions}
        (args.out / f"{n:02d}-request.json").write_text(json.dumps(body, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"## {n:02d} {name}" + (f"  (want {want})" if want else ""))
        print(json.dumps(st, ensure_ascii=False))
        if args.dry_run:
            continue
        try:
            answers = ask(key, body)["answers"]
        except Exception as error:  # a failed call is an error, never an answer
            print(f"  ERROR {type(error).__name__}: {error}")
            continue
        (args.out / f"{n:02d}-response.json").write_text(json.dumps(answers, ensure_ascii=False, indent=2), encoding="utf-8")
        for qid, answer in answers.items():
            value = answer.get("noul")
            band = "はい" if value is not None and value >= 0.7 else "いいえ" if value is not None and value <= 0.3 else "迷い"
            print(f"  {qid:22s} {value!s:>6}  {band}")
    print(f"\nrequests and responses: {args.out.resolve()}")


if __name__ == "__main__":
    main()
