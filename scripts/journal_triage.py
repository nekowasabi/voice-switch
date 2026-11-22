#!/usr/bin/env python3
"""Classify and review dictation journal entries."""

from __future__ import annotations

import argparse
import hashlib
import html
import http.client
import json
import os
import re
import sys
import tempfile
import urllib.request
from bisect import bisect_left
from dataclasses import dataclass
from datetime import date, datetime, timedelta
from pathlib import Path


QUESTIONS_PATH = Path(__file__).resolve().parents[1] / "Resources" / "triage_questions.json"


def load_questions():
    return json.loads(QUESTIONS_PATH.read_text(encoding="utf-8"))


LOCAL_MARKERS = {
    "self_task": r"\b(?:task|todo)\s*[:：]|(?:タスク|やること)\s*[:：]|タスクに(?:追加|登録)",
    "look_up": r"\bresearch\s*[:：]|(?:調査|リサーチ)\s*[:：]|調べて",
    "write_text": r"\bdraft\s*[:：]|(?:下書き|ドラフト)(?:\s*[:：]|を(?:書いて|作って))",
    "computer_change": r"\bagent\s*[:：]|エージェント\s*[:：]|(?:エージェント|Claude|Codex|Grok|クロード)(?:に頼|に依頼)",
}
KINDS = tuple(LOCAL_MARKERS)
QUESTION_IDS = ("wants_action", "is_filler", *(f"kind_{kind}" for kind in KINDS))
DEFAULT_BAND = {"no": 0.3, "yes": 0.7}
STARTED = {"自動", "ウェイク", "手動"}


@dataclass(frozen=True)
class Utterance:
    timestamp: datetime
    text: str
    origin: str | None
    focus_app: str | None
    task_name: str | None
    id: str | None = None

    @property
    def instant(self):
        return self.timestamp.timestamp()


def default_journal_dir():
    if os.name == "nt":
        local = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    else:
        local = Path.home() / "Library" / "Application Support"
    return local / "voice-switch" / "journal"


def configured_journal():
    try:
        config = json.loads((Path.home() / ".config" / "voice-switch" / "config.json").read_text(encoding="utf-8"))
        configured = config["dictation"]["journalDir"]
    except (OSError, ValueError, KeyError, TypeError):
        return default_journal_dir()
    if not isinstance(configured, str) or not configured.strip():
        return default_journal_dir()
    return Path(os.path.expandvars(configured.strip())).expanduser()


def configured_root():
    journal = configured_journal()
    return journal if journal.suffix.lower() == ".jsonl" or journal == default_journal_dir() else journal.parent


def configured_device():
    journal = configured_journal()
    device = (journal.parent if journal.suffix.lower() == ".jsonl" else journal).name
    return device if device and device != ".." else default_journal_dir().name


def journal_sources(path):
    """A .jsonl file, or a folder and each folder one level below it, labeled by that folder's name."""
    if path.suffix.lower() == ".jsonl":
        return [(None, path)]
    try:
        children = sorted(child for child in path.iterdir() if not child.name.startswith(".") and child.is_dir())
    except FileNotFoundError:
        children = []
    return [(None, path)] + [(child.name, child) for child in children]


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
                entry_id = row.get("id")
                yield Utterance(
                    timestamp, text, origin if isinstance(origin, str) else None,
                    display_name(row.get("focusApp")), display_name(row.get("taskchute")),
                    entry_id if isinstance(entry_id, str) and entry_id else None,
                )


def markdown(value):
    text = (value or "").encode("utf-8", errors="replace").decode("utf-8")
    escaped_html = html.escape(" ".join(text.split()), quote=False)
    return re.sub(r"([\\`*_\[\]~])", r"\\\1", escaped_html) or "—"


def probability(value):
    return type(value) in (int, float) and 0 <= value <= 1


def valid_answers(answers):
    return isinstance(answers, dict) and all(probability(answers.get(qid)) for qid in QUESTION_IDS)


def read_thresholds(path):
    try:
        stored = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        stored = {}
    if not isinstance(stored, dict) or stored.keys() - set(QUESTION_IDS):
        raise ValueError("invalid thresholds")
    thresholds = {}
    for qid in QUESTION_IDS:
        band = stored.get(qid, DEFAULT_BAND)
        if not isinstance(band, dict) or band.keys() != DEFAULT_BAND.keys():
            raise ValueError("invalid thresholds")
        no, yes = band["no"], band["yes"]
        if not probability(no) or not probability(yes) or no >= yes:
            raise ValueError("invalid thresholds")
        thresholds[qid] = band
    return thresholds


def decide(answers, thresholds):
    """Return the destinations for the six Noul probabilities, or an error verdict."""
    if not valid_answers(answers):
        return ["unjudged:error"]
    intent = answers["wants_action"]
    intent_band = thresholds.get("wants_action", DEFAULT_BAND)
    if intent >= intent_band["yes"]:
        actions = [f"action:{kind}" for kind in KINDS
                   if answers[f"kind_{kind}"] >= thresholds.get(f"kind_{kind}", DEFAULT_BAND)["yes"]]
        return actions or ["unjudged:kind"]
    if intent > intent_band["no"]:
        return ["unjudged:intent"]
    filler = answers["is_filler"]
    filler_band = thresholds.get("is_filler", DEFAULT_BAND)
    if filler >= filler_band["yes"]:
        return ["filler"]
    if filler <= filler_band["no"]:
        return ["thought"]
    return ["unjudged:content"]


def request_state(entry, previous, *, include_context=True):
    state = {"utterance": {"text": entry.text, "started": entry.origin if entry.origin in STARTED else "不明"}}
    if not include_context:
        return state
    state["context"] = {k: v for k, v in (("app", entry.focus_app), ("task", entry.task_name),
                                         ("time", entry.timestamp.strftime("%H:%M"))) if v}
    if previous:
        state["previous"] = [
            {"text": prior.text, "seconds_before": int(entry.instant - prior.instant)} for prior in previous
        ]
    return state


def ask_jev(key, state, questions):
    body = json.dumps({"state": state, "model": "jev-latest", "questions": questions}).encode("utf-8")
    request = urllib.request.Request(
        "https://api.typesafe.ai/v1/systemone", data=body, method="POST",
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            raw = json.load(response)["answers"]
        answers = {qid: raw[qid]["noul"] for qid in QUESTION_IDS}
        if valid_answers(answers):
            return answers
    except (OSError, http.client.HTTPException, ValueError, KeyError, TypeError, RecursionError):
        pass
    return None


def local_category(text):
    matches = [f"action:{kind}" for kind, marker in LOCAL_MARKERS.items() if re.search(marker, text, re.IGNORECASE)]
    return matches if len(matches) == 1 else ["unjudged:no-jev"]


def question_hash(questions):
    return hashlib.sha256(json.dumps(questions, sort_keys=True).encode("utf-8")).hexdigest()


def cache_path(device, day):
    return Path.home() / "Library" / "Caches" / "voice-switch" / "triage" / device / f"{day}.jsonl"


def records(path):
    try:
        stream = path.open("rb")
    except FileNotFoundError:
        return
    with stream:
        for line in stream:
            try:
                row = json.loads(line)
            except (ValueError, RecursionError):
                continue
            if isinstance(row, dict) and isinstance(row.get("id"), str) and row["id"]:
                yield row


def write_atomic(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as tmp:
        pending = Path(tmp.name)
        try:
            tmp.write(content.encode("utf-8"))
            tmp.close()
            pending.replace(path)
        finally:
            pending.unlink(missing_ok=True)


def write_cache(path, entries):
    write_atomic(path, "".join(json.dumps(row) + "\n" for row in entries.values()))


def sources_by_device(journal, device):
    sources = {}
    for label, path in journal_sources(journal):
        sources.setdefault(label or device, []).append(path)
    return sources


def prior_entries(entry, history, instants):
    end = bisect_left(instants, entry.instant)
    start = bisect_left(instants, entry.instant - 60, max(0, end - 2), end)
    return history[start:end]


def context_history(paths, day, entries):
    history = list(entries)
    if day > date.min and any(e.timestamp.hour == e.timestamp.minute == 0 for e in entries):
        yesterday = day - timedelta(days=1)
        history.extend(entry for source in paths for entry in journal_rows(source, yesterday) if entry)
    history.sort(key=lambda entry: entry.instant)
    return history, [entry.instant for entry in history]


def prune_other_days(paths, device, day):
    for path in cache_path(device, day).parent.glob("*.jsonl"):
        try:
            cached_day = date.fromisoformat(path.stem)
        except ValueError:
            continue
        if cached_day == day:
            continue
        stored = {row["id"]: row for row in records(path)}
        if not stored:
            continue
        ids = {entry.id for source in paths for entry in journal_rows(source, cached_day) if entry}
        kept = {entry_id: row for entry_id, row in stored.items() if entry_id in ids}
        if kept != stored:
            write_cache(path, kept)


def classify_day(journal, day, thresholds, *, use_jev, include_context):
    key = (os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY")) if use_jev else None
    questions = load_questions() if key else None
    digest = question_hash(questions) if questions else None
    output = []
    skipped = 0
    for device, paths in sources_by_device(journal, configured_device()).items():
        rows = [entry for source in paths for entry in journal_rows(source, day)]
        entries = [entry for entry in rows if entry is not None]
        skipped += len(rows) - len(entries)
        history, instants = context_history(paths, day, entries) if key and include_context else ([], [])
        path = cache_path(device, day)
        stored = {row["id"]: row for row in records(path)}
        ids = {entry.id for entry in entries}
        kept = {entry_id: row for entry_id, row in stored.items() if entry_id in ids}
        for entry in entries:
            answers = None
            if not key:
                destinations = local_category(entry.text)
            else:
                saved = kept.get(entry.id, {})
                if saved.get("question_hash") == digest and valid_answers(saved.get("answers")):
                    answers = saved["answers"]
                else:
                    previous = prior_entries(entry, history, instants) if include_context else []
                    state = request_state(entry, previous, include_context=include_context)
                    answers = ask_jev(key, state, questions)
                    if answers is not None and entry.id is not None:
                        kept[entry.id] = {"id": entry.id, "answers": answers, "question_hash": digest}
                destinations = decide(answers, thresholds)
            output.append((entry, device, destinations, answers))
        if kept != stored:
            write_cache(path, kept)
        prune_other_days(paths, device, day)
    output.sort(key=lambda item: item[0].instant)
    return output, skipped


def result_row(entry, device, destinations, answers):
    return {"id": entry.id, "device": device, "timestamp": entry.timestamp.isoformat(), "text": entry.text,
            "origin": entry.origin, "app": entry.focus_app, "task": entry.task_name,
            "destinations": destinations, "answers": answers}


def describe_row(row):
    return (f'- {datetime.fromisoformat(row["timestamp"]):%H:%M:%S} · {markdown(row["device"])} · '
            f'{", ".join(row["destinations"])} · {markdown(row["app"])} · {markdown(row["task"])} — {markdown(row["text"])}')


def listing(rows, day, skipped):
    result = {"day": str(day), "actions": [], "thoughts": [], "unjudged": [], "filler_count": 0, "skipped": skipped}
    for entry, device, destinations, answers in rows:
        if destinations == ["filler"]:
            result["filler_count"] += 1
        else:
            group = {"action": "actions", "thought": "thoughts", "unjudged": "unjudged"}[destinations[0].partition(":")[0]]
            result[group].append(result_row(entry, device, destinations, answers))
    return result


def list_text(result):
    lines = [f'## {result["day"]}']
    for title, group in (("依頼", "actions"), ("考え", "thoughts"), ("未判定", "unjudged")):
        lines.append(f"### {title}")
        lines.extend(describe_row(row) for row in result[group])
    lines.append(f'フィラー {result["filler_count"]} 件 / 読めなかった行 {result["skipped"]} 件')
    return "\n".join(lines)


def common_options(parser):
    parser.add_argument("--root", type=Path, default=argparse.SUPPRESS,
                        help="journal root or a single .jsonl file")
    parser.add_argument("--format", choices=("json", "text"), default=argparse.SUPPRESS)


def classification_options(parser):
    parser.add_argument("--no-jev", action="store_true", help="use local markers only")
    parser.add_argument("--no-context", action="store_true", help="send only the utterance to Jev")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    shared = argparse.ArgumentParser(add_help=False)
    common_options(parser)
    common_options(shared)
    commands = parser.add_subparsers(dest="command", required=True)
    list_parser = commands.add_parser("list", parents=[shared], help="list a day of journal entries")
    list_parser.add_argument("day", type=date.fromisoformat, metavar="YYYY-MM-DD")
    list_parser.add_argument("journal_dir", type=Path, nargs="?")
    classification_options(list_parser)
    args = parser.parse_args(argv)

    journal = (getattr(args, "root", None) or args.journal_dir or configured_root()).expanduser()
    root = journal.parent if journal.suffix.lower() == ".jsonl" else journal
    try:
        thresholds = read_thresholds(root / ".triage" / "thresholds.json")
        rows, skipped = classify_day(journal, args.day, thresholds, use_jev=not args.no_jev,
                                     include_context=not args.no_context)
        result = listing(rows, args.day, skipped)
    except (OSError, ValueError, RecursionError):
        parser.error("could not read the journal or thresholds, or write the cache")
    print(list_text(result) if getattr(args, "format", "json") == "text" else json.dumps(result))


if __name__ == "__main__":
    main()
