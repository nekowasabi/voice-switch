"""Local text helpers. No model and no network."""

from __future__ import annotations

import math
import re
from datetime import date, timedelta

FILLERS = ("えーと", "えっと", "あのー", "そのー", "なんか")
TASK_RE = re.compile(r"(?<!調べ)(?:なきゃ|なくちゃ|ないと)|TODO|todo|やること")
TOKEN_RE = re.compile(r"[一-龥]{2,}|[ァ-ヴー]{3,}|[A-Za-z]{3,}")
STOP = {
    "今日",
    "明日",
    "昨日",
    "これ",
    "それ",
    "あの",
    "こと",
    "もの",
    "ため",
    "よう",
    "ところ",
    "まだ",
    "やっぱり",
}


def clean(text: str) -> str:
    cleaned = text
    for filler in FILLERS:
        cleaned = cleaned.replace(filler, "")
    cleaned = re.sub(r"\s+", " ", cleaned)
    cleaned = re.sub(r"^[、,\s]+|[、,\s]+$", "", cleaned)
    cleaned = re.sub(r"、{2,}", "、", cleaned)
    return cleaned.strip()


def tokens(text: str) -> set[str]:
    return {token for token in TOKEN_RE.findall(text) if token not in STOP}


def bigrams(text: str) -> dict[str, int]:
    compact = re.sub(r"\s+", "", text)
    if len(compact) < 2:
        return {compact: 1} if compact else {}
    counts: dict[str, int] = {}
    for index in range(len(compact) - 1):
        gram = compact[index : index + 2]
        counts[gram] = counts.get(gram, 0) + 1
    return counts


def cosine(left: str, right: str) -> float:
    left_counts = bigrams(left)
    right_counts = bigrams(right)
    if not left_counts or not right_counts:
        return 0.0
    keys = set(left_counts) | set(right_counts)
    dot = sum(left_counts.get(key, 0) * right_counts.get(key, 0) for key in keys)
    left_norm = math.sqrt(sum(value * value for value in left_counts.values()))
    right_norm = math.sqrt(sum(value * value for value in right_counts.values()))
    if left_norm == 0 or right_norm == 0:
        return 0.0
    return dot / (left_norm * right_norm)


def title_of(text: str) -> str:
    head = re.split(r"[。、]", text, maxsplit=1)[0].strip()
    if len(head) > 24:
        head = head[:24]
    return head or "（無題）"


def summary_of(text: str) -> str:
    one_line = re.sub(r"\s+", " ", text).strip()
    if len(one_line) > 80:
        return one_line[:80]
    return one_line or "（空）"


def next_weekday(on: date, weekday: int) -> date:
    delta = (weekday - on.weekday()) % 7
    return on + timedelta(days=delta)


def extract_deadline(text: str, on: date) -> str | None:
    match = re.search(r"(\d{1,2})月(\d{1,2})日", text)
    if match:
        month = int(match.group(1))
        day = int(match.group(2))
        return f"{on.year:04d}-{month:02d}-{day:02d}"
    if "明日" in text:
        return (on + timedelta(days=1)).isoformat()
    if "今日" in text and "まで" in text:
        return on.isoformat()
    if "来週" in text:
        return (on + timedelta(days=7)).isoformat()
    if "金曜" in text:
        return next_weekday(on, 4).isoformat()
    return None


def extract_tasks(text: str, on: date) -> list[dict]:
    if TASK_RE.search(text):
        return [{"text": text, "deadline": extract_deadline(text, on)}]
    deadline = extract_deadline(text, on)
    if deadline and re.search(r"まで", text):
        return [{"text": text, "deadline": deadline}]
    return []


def extract_decisions(text: str) -> list[str]:
    found = []
    for part in re.split(r"[。！？?!]", text):
        piece = part.strip()
        if piece and re.search(r"ことにした|決めた|決定した", piece):
            found.append(piece)
    return found


def tags_for(text: str, cls: str, tasks: list[dict], decisions: list[str]) -> list[str]:
    tags: list[str] = []
    if tasks or cls == "task":
        tags.append("タスク")
    if cls == "research" or re.search(r"[?？]|何だっけ|なんだっけ|なぜ|どうして", text):
        tags.append("疑問")
    if decisions:
        tags.append("決定")
    if re.search(r"うれしい|悲しい|疲れた|不安|イライラ|困る|モヤ", text):
        tags.append("感情")
    if re.search(r"アイデア|思いつ|かもしれ", text):
        tags.append("アイデア")
    if not tags:
        tags.append("アイデア")
    return tags
