#!/usr/bin/env python3
"""List and classify journal entries whose delivery stayed in history or was undecided."""

from __future__ import annotations

import argparse
import html
import http.client
import json
import os
import re
import sys
import urllib.request
from dataclasses import dataclass
from datetime import date, datetime
from pathlib import Path


LOCAL_MARKERS = {
    "task": r"\b(?:task|todo)\s*[:：]|(?:タスク|やること)\s*[:：]|タスクに(?:追加|登録)",
    "research": r"\bresearch\s*[:：]|(?:調査|リサーチ)\s*[:：]|調べて",
    "draft": r"\bdraft\s*[:：]|(?:下書き|ドラフト)(?:\s*[:：]|を(?:書いて|作って))",
    "agent": r"\bagent\s*[:：]|エージェント\s*[:：]|(?:エージェント|Claude|Codex|Grok|クロード)(?:に頼|に依頼)",
}
JEV_CRITERIA = {
    "task": "The speaker wants to record a concrete task or to-do.",
    "research": "The speaker asks to look something up, investigate it, or compare findings.",
    "draft": "The speaker wants a draft of a message or other written content.",
    "agent": "The speaker addresses an agent and asks it to carry out work.",
    "none": "The utterance has no clear intent or fits several categories equally well.",
}


@dataclass(frozen=True)
class Utterance:
    timestamp: datetime
    text: str
    origin: str | None
    focus_app: str | None
    task_name: str | None
    source: str | None = None


def default_journal_dir():
    if os.name == "nt":
        local = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    else:
        local = Path.home() / "Library" / "Application Support"
    return local / "voice-switch" / "journal"


def configured_root():
    """The parent of config.json's journalDir: each machine writes its own subfolder there (private, wsl, ...)."""
    try:
        config = json.loads((Path.home() / ".config" / "voice-switch" / "config.json").read_text(encoding="utf-8"))
        configured = config["dictation"]["journalDir"]
    except (OSError, ValueError, KeyError, TypeError):
        return default_journal_dir()
    if not isinstance(configured, str) or not configured.strip():
        return default_journal_dir()
    path = Path(os.path.expandvars(configured.strip())).expanduser()
    return path if path.suffix.lower() == ".jsonl" else path.parent


def journal_sources(path):
    """A .jsonl file, or a folder and each folder one level below it, labeled by that folder's name."""
    if path.suffix.lower() == ".jsonl":
        return [(None, path)]
    try:
        children = sorted(child for child in path.iterdir() if child.is_dir())
    except FileNotFoundError:
        children = []
    return [(None, path)] + [(child.name, child) for child in children]


def display_name(metadata):
    name = metadata.get("name") if isinstance(metadata, dict) else None
    return name if isinstance(name, str) else None


def journal_rows(path, day, label=None):
    single_file = path.suffix.lower() == ".jsonl"
    source = path if single_file else path / f"{day}.jsonl"
    try:
        stream = source.open("rb")
    except FileNotFoundError:
        if single_file:
            raise
        return
    with stream:
        for line in stream:
            try:
                row = json.loads(line.decode("utf-8"))
                if not isinstance(row, dict):
                    raise ValueError
                stamp, text = row.get("timestamp"), row.get("text")
                if not isinstance(stamp, str) or "T" not in stamp or not isinstance(text, str) or not text.strip():
                    raise ValueError
                text.encode("utf-8")
                stamp = re.sub(r"(\.\d{6})\d+", r"\1", stamp)
                timestamp = datetime.fromisoformat(stamp.replace("Z", "+00:00"))
            except (ValueError, RecursionError):
                yield None
                continue
            if timestamp.date() == day and row.get("delivery") in (None, "history"):
                origin = row.get("origin")
                yield Utterance(
                    timestamp, text, origin if isinstance(origin, str) else None,
                    display_name(row.get("focusApp")), display_name(row.get("taskchute")), label,
                )


def markdown(value):
    text = (value or "").encode("utf-8", errors="replace").decode("utf-8")
    escaped_html = html.escape(" ".join(text.split()), quote=False)
    return re.sub(r"([\\`*_\[\]~])", r"\\\1", escaped_html) or "—"


def ask_jev(key, text):
    body = json.dumps({
        "state": {"dictation": text},
        "model": "jev-latest",
        "questions": {"classification": {
            "type": "choice",
            "instructions": "Classify the speaker's intent in the speech-to-text output in dictation.",
            "criteria": JEV_CRITERIA,
        }},
    }).encode("utf-8")
    request = urllib.request.Request(
        "https://api.typesafe.ai/v1/systemone", data=body, method="POST",
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            answer = json.load(response)["answers"]["classification"]
        choice, confidence = answer["choice"], answer["confidence"]
        if choice in JEV_CRITERIA and type(confidence) in (int, float) and 0 <= confidence <= 1:
            return choice, confidence
    except (OSError, http.client.HTTPException, ValueError, KeyError, TypeError, RecursionError):
        pass
    return "none", None


def classify(entry, *, use_jev=False, min_confidence=0.9):
    matches = [category for category, marker in LOCAL_MARKERS.items() if re.search(marker, entry.text, re.IGNORECASE)]
    if len(matches) == 1:
        return matches[0]
    if not use_jev or entry.origin not in ("手動", "ウェイク"):
        return "none"
    key = os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY")
    if not key:
        return "none"
    choice, confidence = ask_jev(key, entry.text)
    category = choice if confidence is not None and confidence >= min_confidence else "none"
    score = "-" if confidence is None else str(confidence)
    print(f"jev: classification={category} confidence={score}", file=sys.stderr)
    return category


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("day", type=date.fromisoformat, metavar="YYYY-MM-DD")
    parser.add_argument("journal_dir", type=Path, nargs="?", default=None,
                        help="journal directory (its subfolders are read too) or a single .jsonl file; "
                             "default: the parent of config.json's dictation.journalDir")
    parser.add_argument("--jev", action="store_true", help="send unclassified manual and wake-word text to Jev")
    parser.add_argument("--min-confidence", type=float, default=0.9,
                        help="minimum Jev confidence from 0 to 1 (default: %(default)s)")
    args = parser.parse_args(argv)
    if not 0 <= args.min_confidence <= 1:
        parser.error("--min-confidence must be between 0 and 1")

    print(f"## {args.day}")
    print()
    skipped = 0
    root = (args.journal_dir or configured_root()).expanduser()
    entries = []
    try:
        for label, source in journal_sources(root):
            for entry in journal_rows(source, args.day, label):
                if entry is None:
                    skipped += 1
                else:
                    entries.append(entry)
        entries.sort(key=lambda entry: entry.timestamp)
        for entry in entries:
            category = classify(entry, use_jev=args.jev, min_confidence=args.min_confidence)
            where = f"{markdown(entry.source)} · " if entry.source else ""
            print(f"- **{entry.timestamp:%H:%M:%S}** · {where}`{category}` · "
                  f"focusApp: {markdown(entry.focus_app)} · taskchute: {markdown(entry.task_name)} "
                  f"— {markdown(entry.text)}")
    except OSError as error:
        parser.error(str(error))

    print(f"\nSkipped malformed rows: {skipped}")


if __name__ == "__main__":
    main()
