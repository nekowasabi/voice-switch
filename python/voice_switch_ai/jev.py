"""TypeSafe Jev choice client. Imported for real only when cloud triage is on and calibrated."""

from __future__ import annotations

import json
from dataclasses import dataclass

ENDPOINT = "https://api.typesafe.ai/v1/systemone"

CRITERIA = {
    "task": "The speaker owes a concrete action.",
    "research": "The speaker wants an answer or a lookup.",
    "draft": "The utterance is material for a message to send later.",
    "agent": "The speaker asks a computer or an AI to do something.",
    "none": "A thought, feeling, decision record, backchannel, or rhetorical question.",
}


def question() -> dict:
    return {
        "type": "choice",
        "instructions": (
            "dictation is speech-to-text. Pick exactly one primary tray. "
            "Priority: agent, then task, then research, then draft, else none. "
            "Rhetorical questions and backchannels are none. "
            "Classify only. Do not extract fields and do not draft text."
        ),
        "criteria": CRITERIA,
    }


def build_body(text: str) -> dict:
    return {
        "state": {"dictation": text},
        "model": "jev-latest",
        "questions": {"tray": question()},
    }


@dataclass(frozen=True)
class JevAnswer:
    choice: str
    confidence: float


class LiveJev:
    def __init__(self, key: str, timeout: float = 10) -> None:
        self.key = key
        self.timeout = timeout

    def choose(self, text: str) -> JevAnswer:
        import urllib.request

        payload = json.dumps(build_body(text)).encode()
        request = urllib.request.Request(
            ENDPOINT,
            data=payload,
            method="POST",
            headers={"Authorization": f"Bearer {self.key}", "Content-Type": "application/json"},
        )
        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            data = json.load(response)
        answer = data["answers"]["tray"]
        return JevAnswer(str(answer["choice"]), float(answer["confidence"]))
