"""Read a #49 daily JSONL journal. This module never writes the journal."""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path


ORIGINS = {
    "automatic": "automatic",
    "auto": "automatic",
    "自動": "automatic",
    "wake": "wake",
    "ウェイク": "wake",
    "manual": "manual",
    "手動": "manual",
}


@dataclass(frozen=True)
class JournalEntry:
    id: str
    timestamp: datetime
    text: str
    origin: str
    end_reason: str
    recording_seconds: float
    schema_version: int


class JournalReader:
    """Plug-in point for the #49 writer. `read` returns entries and does not modify `path`."""

    def read(self, path: Path) -> list[JournalEntry]:
        raise NotImplementedError


class JournalReadError(ValueError):
    pass


def _first(obj: dict, *keys: str, default=None):
    for key in keys:
        if key in obj and obj[key] is not None:
            return obj[key]
    return default


def parse_entry(obj: dict) -> JournalEntry:
    if not isinstance(obj, dict):
        raise JournalReadError("line is not an object")
    raw_id = _first(obj, "id")
    raw_ts = _first(obj, "timestamp")
    raw_text = _first(obj, "text")
    if raw_id is None or raw_ts is None or raw_text is None:
        raise JournalReadError("missing id, timestamp, or text")
    ts_text = str(raw_ts).replace("Z", "+00:00")
    try:
        timestamp = datetime.fromisoformat(ts_text)
    except ValueError as exc:
        raise JournalReadError("bad timestamp") from exc
    if timestamp.tzinfo is None:
        raise JournalReadError("timestamp needs a timezone")
    origin_raw = str(_first(obj, "origin", default="automatic"))
    origin = ORIGINS.get(origin_raw, origin_raw)
    end_reason = str(_first(obj, "endReason", "end_reason", "終了理由", default=""))
    seconds = _first(obj, "recordingSeconds", "recording_seconds", "録音秒数", default=0)
    try:
        seconds_f = float(seconds)
    except (TypeError, ValueError) as exc:
        raise JournalReadError("bad recording seconds") from exc
    schema = _first(obj, "schemaVersion", "schema_version", default=1)
    try:
        schema_i = int(schema)
    except (TypeError, ValueError) as exc:
        raise JournalReadError("bad schemaVersion") from exc
    return JournalEntry(
        id=str(raw_id),
        timestamp=timestamp,
        text=str(raw_text),
        origin=origin,
        end_reason=end_reason,
        recording_seconds=seconds_f,
        schema_version=schema_i,
    )


def journal_files(path: Path) -> list[Path]:
    if path.is_file():
        return [path]
    if not path.exists():
        raise JournalReadError(f"journal not found: {path}")
    return sorted(item for item in path.glob("*.jsonl") if item.is_file() and not item.is_symlink())


class JsonlJournalReader(JournalReader):
    def __init__(self) -> None:
        self.warnings: list[str] = []

    def read(self, path: Path) -> list[JournalEntry]:
        entries: list[JournalEntry] = []
        for file in journal_files(path):
            with file.open("r", encoding="utf-8") as handle:
                for lineno, line in enumerate(handle, start=1):
                    if not line.strip():
                        continue
                    try:
                        obj = json.loads(line)
                        entries.append(parse_entry(obj))
                    except (json.JSONDecodeError, JournalReadError):
                        self.warnings.append(f"journal: skip {file.name}:{lineno}")
        entries.sort(key=lambda entry: (entry.timestamp, entry.id))
        return entries
