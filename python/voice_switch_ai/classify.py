"""Local markers, then an optional Jev choice. One primary class."""

from __future__ import annotations

import re
from dataclasses import dataclass

from voice_switch_ai.journal import JournalEntry
from voice_switch_ai.textutil import TASK_RE

CLASSES = ("task", "research", "draft", "agent", "none")
TRAY_CLASSES = ("task", "research", "draft", "agent")

MARKERS = {
    "task": TASK_RE,
    "research": re.compile(r"調べて|調べなきゃ|何だっけ|なんだっけ|なぜ|どうして|教えて|[?？]\s*$"),
    "draft": re.compile(r"下書き|文面|メール"),
    "agent": re.compile(r"クロードに|カーソルに|エージェントに|パソコンに"),
    "none": re.compile(r"ことにした|決めた|決定した|とは何"),
}
BACKCHANNEL = re.compile(r"^(うん|へえ|なるほど|そうだね|はいはい|はい)[。！!？?\s]*$")
MUSE_PREFIX = re.compile(r"^(ミューズ|みゅーず|Muse)[、,\s]+", re.IGNORECASE)
WRITE_INTENT = re.compile(r"送って|送信|貼って|ペースト|issue|イシュー|github|コメント|投稿|削除|プッシュ", re.IGNORECASE)
READ_SUMMARIZE = re.compile(r"要約|まとめ|ダイジェスト")
READ_RECALL = re.compile(r"調べ|検索|思い出|探し")


@dataclass(frozen=True)
class Config:
    cloud: bool = False
    jev_min_confidence: float | None = None
    allowed_executions: tuple[str, ...] = ("summarize", "recall")
    journal_dir: str | None = None
    derived_dir: str | None = None


@dataclass(frozen=True)
class Decision:
    entry_id: str
    cls: str
    confidence: float
    source: str
    route: str
    explicit: bool
    read_only: bool
    action: str | None
    log: str


def strip_muse(text: str) -> str | None:
    match = MUSE_PREFIX.match(text.strip())
    if not match:
        return None
    return text.strip()[match.end() :].strip()


def marker_hits(text: str) -> list[str]:
    hits = [name for name in CLASSES if MARKERS[name].search(text)]
    if BACKCHANNEL.match(text.strip()):
        if "none" not in hits:
            hits.append("none")
    return hits


def is_backchannel(text: str) -> bool:
    return BACKCHANNEL.match(text.strip()) is not None


def _decision(entry: JournalEntry, cls: str, confidence: float, source: str, route: str, log: str, explicit: bool = False, read_only: bool = False, action: str | None = None) -> Decision:
    return Decision(
        entry_id=entry.id,
        cls=cls,
        confidence=confidence,
        source=source,
        route=route,
        explicit=explicit,
        read_only=read_only,
        action=action,
        log=log,
    )


def _local(entry: JournalEntry, cls: str) -> Decision:
    if cls in TRAY_CLASSES:
        route = "tray"
    elif is_backchannel(entry.text):
        route = "memo"
    else:
        route = "journal"
    return _decision(entry, cls, 1.0, "local", route, f"triage: local={cls}@1.00 -> {route}")


def _memo(entry: JournalEntry, source: str, log: str) -> Decision:
    return _decision(entry, "none", 0.0, source, "memo", log)


def _muse_action(body: str) -> str | None:
    if WRITE_INTENT.search(body):
        return None
    if READ_SUMMARIZE.search(body):
        return "summarize"
    if READ_RECALL.search(body):
        return "recall"
    return None


def _classify_muse(entry: JournalEntry, body: str, config: Config) -> Decision:
    action = _muse_action(body)
    write = WRITE_INTENT.search(body) is not None
    if action and action in config.allowed_executions and not write:
        cls = "research"
        return _decision(
            entry,
            cls,
            1.0,
            "muse",
            "execute",
            f"triage: muse={cls}@1.00 -> execute",
            explicit=True,
            read_only=True,
            action=action,
        )
    cls = "agent"
    return _decision(
        entry,
        cls,
        1.0,
        "muse",
        "tray",
        f"triage: muse={cls}@1.00 -> tray",
        explicit=True,
        read_only=False,
    )


def classify(entry: JournalEntry, config: Config, jev=None) -> Decision:
    muse_body = strip_muse(entry.text)
    if muse_body is not None:
        return _classify_muse(entry, muse_body, config)
    hits = marker_hits(entry.text)
    if len(hits) == 1:
        return _local(entry, hits[0])
    if not config.cloud:
        return _memo(entry, "fallback", "triage: local=ambiguous jev=off -> memo")
    if config.jev_min_confidence is None:
        return _memo(entry, "uncalibrated", "triage: jev=uncalibrated -> memo")
    if jev is None:
        return _memo(entry, "error", "triage: jev=error -> memo")
    try:
        answer = jev.choose(entry.text)
        choice = answer.choice
        confidence = float(answer.confidence)
    except Exception:
        return _memo(entry, "error", "triage: jev=error -> memo")
    if choice not in CLASSES:
        return _memo(entry, "error", "triage: jev=error -> memo")
    if choice == "none" or confidence < config.jev_min_confidence:
        route = "memo"
    else:
        route = "tray"
    return _decision(entry, choice, confidence, "jev", route, f"triage: jev={choice}@{confidence:.2f} -> {route}")
