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
from datetime import date, datetime, timedelta, timezone
from pathlib import Path


QUESTIONS_PATH = Path(__file__).resolve().parents[1] / "Resources" / "triage_questions.json"


def load_questions():
    questions = json.loads(QUESTIONS_PATH.read_text(encoding="utf-8"))
    return {qid: questions[qid] for qid in (*QUESTION_IDS, *GATE_IDS)}


LOCAL_MARKERS = {
    "self_task": r"\b(?:task|todo)\s*[:：]|(?:タスク|やること)\s*[:：]|タスクに(?:追加|登録)",
    "look_up": r"\bresearch\s*[:：]|(?:調査|リサーチ)\s*[:：]|調べて",
    "write_text": r"\bdraft\s*[:：]|(?:下書き|ドラフト)(?:\s*[:：]|を(?:書いて|作って))",
    "computer_change": r"\bagent\s*[:：]|エージェント\s*[:：]|(?:エージェント|Claude|Codex|Grok|クロード)(?:に頼|に依頼)",
}
KINDS = (*LOCAL_MARKERS, "delegate")
LEGACY_QUESTION_IDS = ("wants_action", "is_filler", *(f"kind_{kind}" for kind in LOCAL_MARKERS))
QUESTION_IDS = (*LEGACY_QUESTION_IDS, "kind_delegate")
GATE_IDS = ("needs_local", "keep_note")  # asked with the table, not part of it (TriageClassifier.gateIDs)
DEFAULT_BAND = {"no": 0.3, "yes": 0.7}
STARTED = {"自動", "ウェイク", "手動"}
DESTINATIONS = {*(f"action:{kind}" for kind in KINDS), "thought", "filler",
                *(f"unjudged:{stage}" for stage in ("intent", "content", "kind", "error", "no-jev"))}


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
            if (day is None or timestamp.date() == day) and row.get("delivery") in (None, "history"):
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


def valid_answers(answers, *, legacy_label=False):
    if not isinstance(answers, dict):
        return False
    questions = LEGACY_QUESTION_IDS if legacy_label and "kind_delegate" not in answers else QUESTION_IDS
    return all(probability(answers.get(qid)) for qid in questions)


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


def decide(answers, thresholds, *, legacy_label=False):
    """Return the destinations for the Noul probabilities, or an error verdict."""
    if not valid_answers(answers, legacy_label=legacy_label):
        return ["unjudged:error"]
    intent = answers["wants_action"]
    intent_band = thresholds.get("wants_action", DEFAULT_BAND)
    if intent >= intent_band["yes"]:
        actions = [f"action:{kind}" for kind in KINDS
                   if f"kind_{kind}" in answers
                   and answers[f"kind_{kind}"] >= thresholds.get(f"kind_{kind}", DEFAULT_BAND)["yes"]]
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
        answers.update({qid: raw[qid]["noul"] for qid in GATE_IDS if qid in raw})  # the gates are optional here
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


def cache_directory():
    return Path.home() / "Library" / "Caches" / "voice-switch" / "triage"


def cache_path(device, day):
    return cache_directory() / device / f"{day}.jsonl"


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


def prune_cache(stored, ids, journal_hash):
    return {entry_id: row for entry_id, row in stored.items()
            if entry_id in ids or row.get("journal_hash") != journal_hash}


def prune_other_days(sources, journal_hash, day):
    for path in cache_directory().glob("*/*.jsonl"):
        try:
            cached_day = date.fromisoformat(path.stem)
        except ValueError:
            continue
        paths = sources.get(path.parent.name, [])
        if paths and cached_day == day:
            continue
        stored = {row["id"]: row for row in records(path)}
        if not any(row.get("journal_hash") == journal_hash for row in stored.values()):
            continue
        ids = {entry.id for source in paths for entry in journal_rows(source, cached_day) if entry}
        kept = prune_cache(stored, ids, journal_hash)
        if kept != stored:
            write_cache(path, kept)


def classify_day(journal, day, thresholds, *, use_jev, include_context):
    key = (os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY")) if use_jev else None
    questions = load_questions() if key else None
    digest = question_hash(questions) if questions else None
    output = []
    skipped = 0
    journal_hash = hashlib.sha256(os.fsencode(journal.resolve())).hexdigest()
    sources = sources_by_device(journal, configured_device())
    for device, paths in sources.items():
        rows = [entry for source in paths for entry in journal_rows(source, day)]
        entries = [entry for entry in rows if entry is not None]
        skipped += len(rows) - len(entries)
        history, instants = context_history(paths, day, entries) if key and include_context else ([], [])
        path = cache_path(device, day)
        stored = {row["id"]: row for row in records(path)}
        ids = {entry.id for entry in entries}
        kept = prune_cache(stored, ids, journal_hash)
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
                        kept[entry.id] = {"id": entry.id, "answers": answers, "question_hash": digest,
                                          "journal_hash": journal_hash}
                destinations = decide(answers, thresholds)
            output.append((entry, device, destinations, answers))
        if kept != stored:
            write_cache(path, kept)
    prune_other_days(sources, journal_hash, day)
    output.sort(key=lambda item: item[0].instant)
    return output, skipped


OTHER_ASSISTANT = re.compile(r"^\s*(?:アレクサ|alexa|ok[、,]?\s*(?:グーグル|google)|hey\s*siri|ヘイ\s*シリ)", re.IGNORECASE)


def gate(text, destinations, answers):
    """What the app lets leave the journal (TriageClassifier.sessionTalk / worthKeeping): "session" for talk to the
    work session on screen or another voice assistant, "memo" for a note worth keeping, else None."""
    if destinations == ["filler"] or answers is None:
        return None
    if answers.get("needs_local", 0) >= 0.7 or OTHER_ASSISTANT.search(text):
        return "session"
    return "memo" if answers.get("keep_note", 0) >= 0.7 else None


def result_row(entry, device, destinations, answers):
    return {"id": entry.id, "device": device, "timestamp": entry.timestamp.isoformat(), "text": entry.text,
            "origin": entry.origin, "app": entry.focus_app, "task": entry.task_name,
            "destinations": destinations, "answers": answers, "gate": gate(entry.text, destinations, answers)}


def describe_row(row):
    destinations = ", ".join(row["destinations"])
    question = {"unjudged:intent": "wants_action", "unjudged:content": "is_filler"}.get(destinations)
    if question is not None:
        destinations += f'({row["answers"][question]:.2f})'
    return (f'- {datetime.fromisoformat(row["timestamp"]):%H:%M:%S} · {markdown(row["device"])} · '
            f'{destinations} · {markdown(row["app"])} · {markdown(row["task"])} — {markdown(row["text"])}')


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


def review(rows, day, skipped, thresholds):
    candidates = []
    for entry, device, destinations, answers in rows:
        distance = None
        if answers is not None:
            distance = min(abs(answers[qid] - boundary) for qid in QUESTION_IDS
                           for boundary in thresholds[qid].values())
        row = result_row(entry, device, destinations, answers)
        row["distance"] = distance
        candidates.append(row)
    candidates.sort(key=lambda row: (
        not row["destinations"][0].startswith("unjudged:"), row["distance"] is None, row["distance"] or 0,
    ))
    return {"day": str(day), "candidates": candidates, "skipped": skipped}


def journal_days(path):
    if path.suffix.lower() == ".jsonl":
        yield None
        return
    for daily in sorted(path.glob("*.jsonl"), reverse=True):
        try:
            yield date.fromisoformat(daily.stem)
        except ValueError:
            continue


def find_utterance(journal, entry_id, local_device):
    for device, paths in sources_by_device(journal, local_device).items():
        for path in paths:
            for day in journal_days(path):
                for entry in journal_rows(path, day):
                    if entry is not None and entry.id == entry_id:
                        return entry, device
    raise ValueError("entry not found")


def valid_destinations(destinations):
    return (isinstance(destinations, list) and bool(destinations)
            and all(isinstance(value, str) and value in DESTINATIONS for value in destinations)
            and len(set(destinations)) == len(destinations)
            and (len(destinations) == 1 or all(value.startswith("action:") for value in destinations)))


def add_label(journal, directory, entry_id, destinations, thresholds, *, use_jev):
    local_device = configured_device()
    entry, device = find_utterance(journal, entry_id, local_device)
    saved = {}
    if use_jev:
        digest = question_hash(load_questions())
        saved = next((row for row in records(cache_path(device, entry.timestamp.date()))
                      if row["id"] == entry_id and row.get("question_hash") == digest), {})
    answers = saved.get("answers")
    if not valid_answers(answers):
        answers = None
    row = {"id": entry_id, "target": destinations,
           "observed": decide(answers, thresholds) if answers is not None else local_category(entry.text),
           "answers": answers, "question_hash": saved.get("question_hash") if answers is not None else None,
           "device": device, "timestamp": datetime.now(timezone.utc).isoformat()}
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"{local_device}-cli.jsonl"
    fd = os.open(path, os.O_WRONLY | os.O_APPEND | os.O_CREAT, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write((json.dumps(row) + "\n").encode("utf-8"))
    return row


def read_labels(directory):
    latest = {}
    for path in sorted(directory.glob("*.jsonl")):
        for row in records(path):
            timestamp = row.get("timestamp")
            if not valid_destinations(row.get("target")) or not isinstance(timestamp, str):
                continue
            if row["id"] not in latest or timestamp >= latest[row["id"]]["timestamp"]:
                latest[row["id"]] = row
    return latest.values()


def score(labels, thresholds):
    weight = 0
    filler_misses = 0
    for label in labels:
        expected = label["target"]
        actual = decide(label["answers"], thresholds, legacy_label=True)
        if set(expected) == set(actual):
            continue
        if actual == ["filler"]:
            filler_misses += 1
            weight += 10
        elif actual == ["thought"] and expected[0].startswith("action:"):
            weight += 3
        elif actual[0].startswith("unjudged:"):
            weight += 0.5
        else:
            weight += 1
    return {"count": len(labels), "weight": weight, "filler_misses": filler_misses}


def split_labels(labels):
    training, holdout = [], []
    for label in labels:
        bucket = int(hashlib.sha256(label["id"].encode("utf-8")).hexdigest(), 16) % 10
        (training if bucket < 7 else holdout).append(label)
    return training, holdout


def find_thresholds(training, thresholds):
    # Among candidates of equal weight, the one nearest the current thresholds wins: the labels pick how far an edge
    # moves, and a tie must not push it to the smallest value tried (a wants_action yes edge of 0.025 once came out).
    def key(candidate):
        weight = score(training, candidate)["weight"]
        return weight, sum(abs(candidate[q][b] - thresholds[q][b]) for q in QUESTION_IDS for b in ("no", "yes"))

    best = dict(thresholds)
    best_key = key(best)
    while True:
        before = best_key
        for qid in QUESTION_IDS:
            current = best[qid]
            points = sorted({0, 1, *current.values(),
                             *(label["answers"][qid] for label in training if qid in label["answers"])})
            boundaries = sorted(set(points) | {(a + b) / 2 for a, b in zip(points, points[1:])})
            lower = [current["no"]] if qid.startswith("kind_") else boundaries
            for no in lower:
                for yes in boundaries:
                    if no >= yes:
                        continue
                    candidate = {**best, qid: {"no": no, "yes": yes}}
                    candidate_key = key(candidate)
                    if candidate_key < best_key:
                        best, best_key = candidate, candidate_key
        if best_key == before:
            return best


def tune(labels, thresholds):
    training, holdout = split_labels(labels)
    result = {"training_count": len(training), "holdout_count": len(holdout), "proposal": None}
    if len(labels) < 30 or not training or not holdout:
        return result
    candidate = find_thresholds(training, thresholds)
    baseline = score(holdout, thresholds)
    adjusted = score(holdout, candidate)
    result.update(baseline=baseline, candidate=adjusted)
    if adjusted["weight"] < baseline["weight"] and adjusted["filler_misses"] <= baseline["filler_misses"]:
        result["proposal"] = candidate
    return result


def eval_text(result):
    return (f'採点 {result["count"]} 件 / 答えなし {result["unscored"]} 件\n'
            f'誤りの重さ {result["weight"]} / フィラーへの取りこぼし {result["filler_misses"]} 件')


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
    label_parser = commands.add_parser("label", parents=[shared], help="record the expected destinations for an entry")
    label_parser.add_argument("id")
    label_parser.add_argument("destinations", nargs="+", metavar="destination")
    label_parser.add_argument("--no-jev", action="store_true", help="record a label for a local-marker result")
    review_parser = commands.add_parser("review", parents=[shared], help="rank candidates for review")
    review_parser.add_argument("day", type=date.fromisoformat, nargs="?", default=date.today(), metavar="YYYY-MM-DD")
    classification_options(review_parser)
    commands.add_parser("eval", parents=[shared], help="score the answers saved with labels")
    tune_parser = commands.add_parser("tune", parents=[shared], help="propose thresholds using saved labels")
    tune_parser.add_argument("--apply", action="store_true", help="write an accepted proposal")
    args = parser.parse_args(argv)

    journal = (getattr(args, "root", None) or getattr(args, "journal_dir", None) or configured_root()).expanduser()
    root = journal.parent if journal.suffix.lower() == ".jsonl" else journal
    directory = root / ".triage"
    threshold_path = directory / "thresholds.json"
    try:
        thresholds = read_thresholds(threshold_path)
        if args.command in ("list", "review"):
            rows, skipped = classify_day(journal, args.day, thresholds, use_jev=not args.no_jev,
                                         include_context=not args.no_context)
            if args.command == "list":
                result = listing(rows, args.day, skipped)
                text_output = list_text(result)
            else:
                result = review(rows, args.day, skipped, thresholds)
                lines = [f'{describe_row(row)} · ID {markdown(row["id"])}' for row in result["candidates"]]
                text_output = "\n".join([f'## {result["day"]}', *lines])
        elif args.command == "label":
            if not valid_destinations(args.destinations):
                parser.error("invalid destination")
            result = add_label(journal, directory / "labels", args.id, args.destinations, thresholds,
                               use_jev=not args.no_jev)
            text_output = f'{result["id"]} · {", ".join(result["target"])}'
        else:
            labels = read_labels(directory / "labels")
            scored = [row for row in labels if valid_answers(row.get("answers"), legacy_label=True)]
            if args.command == "eval":
                result = score(scored, thresholds)
                result["unscored"] = len(labels) - len(scored)
                text_output = eval_text(result)
                stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
                report = directory / "reports" / f"{stamp}.md"
                write_atomic(report, f"# {stamp}\n\n{text_output}\n")
                result["report"] = str(report)
            else:
                result = tune(scored, thresholds)
                result["applied"] = bool(args.apply and result["proposal"])
                if result["applied"]:
                    write_atomic(threshold_path, json.dumps(result["proposal"], indent=2) + "\n")
                if result["proposal"] is None:
                    text_output = "採用条件を満たすしきい値の案はありません。"
                else:
                    title = "しきい値を保存しました。" if result["applied"] else "しきい値の案"
                    text_output = title + "\n" + json.dumps(result["proposal"], indent=2)
    except (OSError, ValueError, RecursionError):
        parser.error("could not read or write triage data, or the requested entry was not found")
    print(text_output if getattr(args, "format", "json") == "text" else json.dumps(result))


if __name__ == "__main__":
    main()
