"""Turn journal entries into notes, a tray, and digests. The journal stays untouched."""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from datetime import date, datetime
from pathlib import Path

from voice_switch_ai.classify import Decision, Config, classify, strip_muse
from voice_switch_ai.journal import JournalEntry, JournalReader, JsonlJournalReader
from voice_switch_ai.textutil import (
    clean,
    cosine,
    extract_deadline,
    extract_decisions,
    extract_tasks,
    summary_of,
    tags_for,
    title_of,
    tokens,
)

STUCK_RE = re.compile(r"困|不安|どうしよう|引っかか|わからない|モヤ|疲れた")
ISSUE_RE = re.compile(r"issue|イシュー|github", re.IGNORECASE)
ROUTE_RANK = {"memo": 1, "journal": 2, "tray": 3, "execute": 4}


@dataclass
class Note:
    id: str
    source_ids: list[str]
    timestamp: datetime
    title: str
    summary: str
    tags: list[str]
    cleaned: str
    tasks: list[dict]
    decisions: list[str]
    deadline: str | None
    route: str
    cls: str
    confidence: float
    source: str
    explicit: bool
    read_only: bool
    action: str | None
    kind: str | None
    related_ids: list[str] = field(default_factory=list)
    raw_texts: list[str] = field(default_factory=list)
    execution: str | None = None


@dataclass
class RunResult:
    logs: list[str]
    notes: list[Note]
    warnings: list[str]
    out: Path


def _norm(text: str) -> str:
    return re.sub(r"\s+", "", text)


def _kind(decision: Decision, text: str) -> str | None:
    if decision.route != "tray":
        return None
    if ISSUE_RE.search(text):
        return "issue"
    return decision.cls


def enrich(entry: JournalEntry, decision: Decision) -> Note:
    body = strip_muse(entry.text)
    source_text = body if body is not None else entry.text
    cleaned = clean(source_text)
    on = entry.timestamp.date()
    tasks = extract_tasks(cleaned, on)
    decisions = extract_decisions(cleaned)
    return Note(
        id=f"note-{entry.id}",
        source_ids=[entry.id],
        timestamp=entry.timestamp,
        title=title_of(cleaned),
        summary=summary_of(cleaned),
        tags=tags_for(cleaned, decision.cls, tasks, decisions),
        cleaned=cleaned,
        tasks=tasks,
        decisions=decisions,
        deadline=extract_deadline(cleaned, on),
        route=decision.route,
        cls=decision.cls,
        confidence=decision.confidence,
        source=decision.source,
        explicit=decision.explicit,
        read_only=decision.read_only,
        action=decision.action,
        kind=_kind(decision, entry.text),
        raw_texts=[cleaned],
    )


def _link(left: Note, right: Note) -> bool:
    if left.route == "execute" or right.route == "execute":
        return False
    if left.timestamp.date() != right.timestamp.date():
        return False
    gap = abs((left.timestamp - right.timestamp).total_seconds())
    if gap > 45 * 60:
        return False
    if _norm(left.cleaned) == _norm(right.cleaned) and left.cleaned:
        return True
    if tokens(left.cleaned) & tokens(right.cleaned):
        return True
    return cosine(left.cleaned, right.cleaned) >= 0.85


def _merge(group: list[Note]) -> Note:
    if len(group) == 1:
        return group[0]
    ordered = sorted(group, key=lambda note: (note.timestamp, note.source_ids[0]))
    winner = max(ordered, key=lambda note: (ROUTE_RANK[note.route], note.timestamp))
    source_ids: list[str] = []
    raw_texts: list[str] = []
    seen: set[str] = set()
    tags: list[str] = []
    tasks: list[dict] = []
    decisions: list[str] = []
    for note in ordered:
        for source_id in note.source_ids:
            if source_id not in source_ids:
                source_ids.append(source_id)
        for text in note.raw_texts:
            key = _norm(text)
            if key in seen:
                continue
            seen.add(key)
            raw_texts.append(text)
        for tag in note.tags:
            if tag not in tags:
                tags.append(tag)
        for task in note.tasks:
            if task not in tasks:
                tasks.append(task)
        for decision in note.decisions:
            if decision not in decisions:
                decisions.append(decision)
    cleaned = "。".join(raw_texts)
    deadline = next((task.get("deadline") for task in tasks if task.get("deadline")), None)
    if deadline is None:
        deadline = next((note.deadline for note in ordered if note.deadline), None)
    kind = "issue" if any(note.kind == "issue" for note in ordered) else winner.kind
    return Note(
        id="note-" + source_ids[0],
        source_ids=source_ids,
        timestamp=ordered[0].timestamp,
        title=title_of(cleaned),
        summary=summary_of(cleaned),
        tags=tags or ["アイデア"],
        cleaned=cleaned,
        tasks=tasks,
        decisions=decisions,
        deadline=deadline,
        route=winner.route,
        cls=winner.cls,
        confidence=winner.confidence,
        source=winner.source,
        explicit=any(note.explicit for note in ordered),
        read_only=winner.read_only,
        action=winner.action,
        kind=kind,
        raw_texts=raw_texts,
        execution=winner.execution,
    )


def cluster_notes(notes: list[Note]) -> list[Note]:
    ordered = sorted(notes, key=lambda note: (note.timestamp, note.id))
    parent = list(range(len(ordered)))

    def find(index: int) -> int:
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    for left in range(len(ordered)):
        for right in range(left + 1, len(ordered)):
            if _link(ordered[left], ordered[right]):
                parent[find(right)] = find(left)
    groups: dict[int, list[Note]] = {}
    for index, note in enumerate(ordered):
        groups.setdefault(find(index), []).append(note)
    merged = [_merge(group) for group in groups.values()]
    merged.sort(key=lambda note: (note.timestamp, note.id))
    return merged


def fill_executions(notes: list[Note]) -> None:
    for note in notes:
        if note.route != "execute" or not note.action:
            continue
        prior = [other for other in notes if other.timestamp <= note.timestamp and other.id != note.id]
        if note.action == "summarize":
            decisions = [decision for other in prior for decision in other.decisions]
            note.execution = "。".join(decisions) if decisions else "決定はまだありません"
            continue
        ranked = sorted(prior, key=lambda other: cosine(note.cleaned, other.cleaned), reverse=True)
        hits = [other.cleaned for other in ranked if cosine(note.cleaned, other.cleaned) > 0][:3]
        note.execution = "。".join(hits) if hits else "関連する過去の発話はありません"


def conclusion_changes(notes: list[Note]) -> list[dict]:
    found = []
    seen: list[dict] = []
    for note in sorted(notes, key=lambda item: item.timestamp):
        for text in note.decisions:
            for prev in seen:
                shared = tokens(prev["text"]) & tokens(text)
                if prev["text"] != text and shared:
                    found.append(
                        {
                            "theme": sorted(shared)[0],
                            "before": prev["text"],
                            "after": text,
                            "beforeId": prev["id"],
                            "afterId": note.source_ids[0],
                        }
                    )
            seen.append({"text": text, "id": note.source_ids[0]})
    return found


def attach_related(notes: list[Note]) -> list[str]:
    logs = []
    ordered = sorted(notes, key=lambda note: note.timestamp)
    for index, note in enumerate(ordered):
        best = None
        best_rank = (-1, -1.0)
        for prev in ordered[:index]:
            shared = tokens(note.cleaned) & tokens(prev.cleaned)
            score = cosine(note.cleaned, prev.cleaned)
            rank = (len(shared), score)
            if rank > best_rank and (shared or score >= 0.35):
                best = prev
                best_rank = rank
        if best is not None:
            note.related_ids = [best.source_ids[0]]
            logs.append(f"recall: {note.source_ids[0]} related={best.source_ids[0]}")
    for change in conclusion_changes(ordered):
        logs.append(f"recall: conclusion-change {change['beforeId']} {change['afterId']}")
    return logs


def _theme_clusters(notes: list[Note]) -> list[dict]:
    if not notes:
        return []
    parent = list(range(len(notes)))

    def find(index: int) -> int:
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    token_sets = [tokens(" ".join(note.raw_texts)) for note in notes]
    for left in range(len(notes)):
        for right in range(left + 1, len(notes)):
            if token_sets[left] & token_sets[right]:
                parent[find(right)] = find(left)
    groups: dict[int, list[int]] = {}
    for index in range(len(notes)):
        groups.setdefault(find(index), []).append(index)
    themes = []
    for indexes in groups.values():
        freq: dict[str, int] = {}
        ids: list[str] = []
        texts: list[str] = []
        has_decision = False
        for index in indexes:
            note = notes[index]
            ids.extend(note.source_ids)
            texts.extend(note.raw_texts)
            if note.decisions:
                has_decision = True
            for token in tokens(" ".join(note.raw_texts)):
                freq[token] = freq.get(token, 0) + len(note.source_ids)
        if freq:
            label = sorted(freq, key=lambda token: (-freq[token], -len(token), token))[0]
        else:
            label = title_of(texts[0] if texts else "")
        themes.append(
            {
                "label": label,
                "utteranceIds": ids,
                "summary": summary_of("。".join(texts)),
                "mentions": len(ids),
                "hasDecision": has_decision,
            }
        )
    themes.sort(key=lambda theme: (-theme["mentions"], theme["label"]))
    return themes


def build_digest(all_notes: list[Note], focus: list[Note], kind: str, when: str) -> dict:
    themes = _theme_clusters(focus)
    decisions = [
        {"text": text, "utteranceIds": list(note.source_ids)}
        for note in focus
        for text in note.decisions
    ]
    stuck = []
    for note in focus:
        blob = " ".join(note.raw_texts)
        if "感情" in note.tags or STUCK_RE.search(blob):
            stuck.append({"text": note.summary, "utteranceIds": list(note.source_ids)})
    open_loops = [
        {"theme": theme["label"], "mentions": theme["mentions"], "utteranceIds": theme["utteranceIds"]}
        for theme in themes
        if theme["mentions"] >= 2 and not theme["hasDecision"]
    ]
    focus_ids = {source_id for note in focus for source_id in note.source_ids}
    changes = [change for change in conclusion_changes(all_notes) if change["afterId"] in focus_ids]
    deadlines = sorted(
        {
            *(note.deadline for note in focus if note.deadline),
            *(task["deadline"] for note in focus for task in note.tasks if task.get("deadline")),
        }
    )
    next_deadline = deadlines[0] if deadlines else "なし"
    names = "、".join(loop["theme"] for loop in open_loops) or "なし"
    lead = "翌週へ" if kind == "weekly" else "明日へ"
    body = {
        "kind": kind,
        "themes": [
            {"label": theme["label"], "summary": theme["summary"], "utteranceIds": theme["utteranceIds"]}
            for theme in themes
        ],
        "決めたこと": decisions,
        "引っかかっていること": stuck,
        "openLoops": open_loops,
        "結論の変化": changes,
        "handoff": f"{lead}: 未完了 {len(open_loops)}件。直近の期限 {next_deadline}。開いているテーマ: {names}",
    }
    if kind == "daily":
        body["date"] = when
    else:
        body["week"] = when
    return body


def search_notes(notes: list[Note], query: str) -> list[dict]:
    ranked = []
    for note in notes:
        score = max(cosine(query, note.cleaned), cosine(query, note.title))
        if tokens(query) & tokens(note.cleaned + note.title):
            score = max(score, 0.5)
        if score <= 0:
            continue
        ranked.append((score, note))
    ranked.sort(key=lambda item: (-item[0], item[1].timestamp, item[1].id))
    changes = conclusion_changes(notes)
    hits = []
    for score, note in ranked[:8]:
        related = [other.source_ids[0] for _score, other in ranked if other.id != note.id][:3]
        change = None
        for item in changes:
            if set(note.source_ids) & {item["beforeId"], item["afterId"]}:
                change = f"{item['theme']}: 「{item['before']}」から「{item['after']}」へ"
                break
        hits.append(
            {
                "id": note.source_ids[0],
                "score": round(score, 2),
                "text": note.cleaned,
                "relatedIds": related,
                "conclusionChange": change,
            }
        )
    return hits


def _inside(child: Path, parent: Path) -> bool:
    try:
        child.resolve().relative_to(parent.resolve())
    except ValueError:
        return False
    return True


def ensure_private_dir(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)
    if path.is_symlink():
        raise OSError(f"refusing symlink {path}")
    os.chmod(path, 0o700)


def write_private(path: Path, text: str) -> None:
    if path.exists() and path.is_symlink():
        raise OSError(f"refusing symlink {path}")
    ensure_private_dir(path.parent)
    temporary = path.with_name(path.name + ".tmp")
    flags = os.O_CREAT | os.O_TRUNC | os.O_WRONLY
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    descriptor = os.open(temporary, flags, 0o600)
    try:
        os.write(descriptor, text.encode("utf-8"))
    finally:
        os.close(descriptor)
    os.chmod(temporary, 0o600)
    os.replace(temporary, path)
    os.chmod(path, 0o600)


def _dump(obj) -> str:
    return json.dumps(obj, ensure_ascii=False, sort_keys=True) + "\n"


def _dump_lines(rows: list[dict]) -> str:
    return "".join(_dump(row) for row in rows)


def read_approvals(out: Path) -> set[str]:
    path = out / "approvals.jsonl"
    if not path.exists():
        return set()
    approved = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        obj = json.loads(line)
        if obj.get("status") == "approved" and obj.get("trayId"):
            approved.add(obj["trayId"])
    return approved


def _note_row(note: Note) -> dict:
    return {
        "id": note.id,
        "sourceIds": note.source_ids,
        "timestamp": note.timestamp.isoformat(),
        "title": note.title,
        "summary": note.summary,
        "tags": note.tags,
        "cleaned": note.cleaned,
        "tasks": note.tasks,
        "decisions": note.decisions,
        "deadline": note.deadline,
        "route": note.route,
        "class": note.cls,
        "relatedIds": note.related_ids,
    }


def _week_key(day: date) -> str:
    iso = day.isocalendar()
    return f"{iso.year}-W{iso.week:02d}"


def write_outputs(out: Path, notes: list[Note], decisions: list[Decision], logs: list[str], warnings: list[str]) -> None:
    ensure_private_dir(out)
    approved = read_approvals(out)
    triage_rows = [
        {
            "id": decision.entry_id,
            "class": decision.cls,
            "confidence": round(decision.confidence, 2),
            "source": decision.source,
            "route": decision.route,
            "log": decision.log,
        }
        for decision in decisions
    ]
    tray_rows = []
    research_rows = []
    agent_rows = []
    execution_rows = []
    decision_rows = []
    for note in notes:
        if note.route == "tray":
            tray_id = "tray-" + note.source_ids[0]
            status = "approved" if tray_id in approved else "draft"
            tray_rows.append(
                {
                    "id": tray_id,
                    "sourceIds": note.source_ids,
                    "kind": note.kind or note.cls,
                    "title": note.title,
                    "body": note.cleaned,
                    "status": status,
                    "needsApproval": status != "approved",
                    "explicitIntent": note.explicit,
                    "externalWrite": False,
                }
            )
        if note.kind == "research":
            hits = search_notes([other for other in notes if other.id != note.id], note.cleaned)[:3]
            research_rows.append(
                {
                    "id": "research-" + note.source_ids[0],
                    "sourceIds": note.source_ids,
                    "question": note.cleaned,
                    "status": "queued",
                    "needsApproval": True,
                    "externalWrite": False,
                    "localRecall": [{"id": hit["id"], "text": hit["text"]} for hit in hits],
                }
            )
        if note.kind == "agent":
            agent_rows.append(
                {
                    "id": "agent-" + note.source_ids[0],
                    "sourceIds": note.source_ids,
                    "instruction": note.cleaned,
                    "status": "staged",
                    "needsApproval": True,
                    "externalWrite": False,
                }
            )
        if note.route == "execute":
            execution_rows.append(
                {
                    "id": "exec-" + note.source_ids[0],
                    "sourceIds": note.source_ids,
                    "mode": "read-only",
                    "action": note.action,
                    "result": note.execution or "",
                    "externalWrite": False,
                }
            )
        for text in note.decisions:
            decision_rows.append(
                {
                    "id": note.source_ids[0],
                    "text": text,
                    "timestamp": note.timestamp.isoformat(),
                    "utteranceIds": note.source_ids,
                }
            )
    write_private(out / "triage.jsonl", _dump_lines(triage_rows))
    write_private(out / "notes.jsonl", _dump_lines([_note_row(note) for note in notes]))
    write_private(out / "tray.jsonl", _dump_lines(tray_rows))
    write_private(out / "research.jsonl", _dump_lines(research_rows))
    write_private(out / "agent-staging.jsonl", _dump_lines(agent_rows))
    write_private(out / "executions.jsonl", _dump_lines(execution_rows))
    write_private(out / "decisions.jsonl", _dump_lines(decision_rows))
    write_private(out / "triage.log", "\n".join(logs) + ("\n" if logs else ""))
    if warnings:
        write_private(out / "warnings.log", "\n".join(warnings) + "\n")
    by_day: dict[str, list[Note]] = {}
    for note in notes:
        by_day.setdefault(note.timestamp.date().isoformat(), []).append(note)
    digest_dir = out / "digests"
    ensure_private_dir(digest_dir)
    for day, focus in sorted(by_day.items()):
        write_private(digest_dir / f"{day}.json", _dump(build_digest(notes, focus, "daily", day)))
    by_week: dict[str, list[Note]] = {}
    for note in notes:
        by_week.setdefault(_week_key(note.timestamp.date()), []).append(note)
    for week, focus in sorted(by_week.items()):
        write_private(digest_dir / f"{week}.json", _dump(build_digest(notes, focus, "weekly", week)))


def run(journal: Path, out: Path, config: Config, reader: JournalReader | None = None, jev=None) -> RunResult:
    journal_root = journal if journal.is_dir() else journal.parent
    if journal_root.exists() and _inside(out, journal_root):
        raise OSError("derived dir must not be inside the journal")
    active = reader or JsonlJournalReader()
    entries = active.read(journal)
    warnings = list(getattr(active, "warnings", []))
    decisions = [classify(entry, config, jev) for entry in entries]
    notes = [enrich(entry, decision) for entry, decision in zip(entries, decisions)]
    fill_executions(notes)
    notes = cluster_notes(notes)
    logs = [decision.log for decision in decisions]
    logs.extend(attach_related(notes))
    write_outputs(out, notes, decisions, logs, warnings)
    return RunResult(logs=logs, notes=notes, warnings=warnings, out=out)
