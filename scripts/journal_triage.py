#!/usr/bin/env python3
"""List journal entries whose delivery stayed in history or was undecided."""

from __future__ import annotations

import argparse
import html
import json
import os
import re
from dataclasses import dataclass
from datetime import date, datetime
from pathlib import Path


@dataclass(frozen=True)
class Utterance:
    timestamp: datetime
    text: str
    origin: str | None
    focus_app: str | None
    task_name: str | None


def default_journal_dir():
    if os.name == "nt":
        local = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    else:
        local = Path.home() / "Library" / "Application Support"
    return local / "voice-switch" / "journal"


def display_name(metadata):
    name = metadata.get("name") if isinstance(metadata, dict) else None
    return name if isinstance(name, str) else None


def journal_rows(path, day):
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
                    display_name(row.get("focusApp")), display_name(row.get("taskchute")),
                )


def markdown(value):
    text = (value or "").encode("utf-8", errors="replace").decode("utf-8")
    escaped_html = html.escape(" ".join(text.split()), quote=False)
    return re.sub(r"([\\`*_\[\]~])", r"\\\1", escaped_html) or "—"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("day", type=date.fromisoformat, metavar="YYYY-MM-DD")
    parser.add_argument("journal_dir", type=Path, nargs="?", default=default_journal_dir(),
                        help="journal directory or a single .jsonl file")
    args = parser.parse_args(argv)

    print(f"## {args.day}")
    print()
    skipped = 0
    try:
        for entry in journal_rows(args.journal_dir.expanduser(), args.day):
            if entry is None:
                skipped += 1
                continue
            print(f"- **{entry.timestamp:%H:%M:%S}** · "
                  f"focusApp: {markdown(entry.focus_app)} · taskchute: {markdown(entry.task_name)} "
                  f"— {markdown(entry.text)}")
    except OSError as error:
        parser.error(str(error))

    print(f"\nSkipped malformed rows: {skipped}")


if __name__ == "__main__":
    main()
