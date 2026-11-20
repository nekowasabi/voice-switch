#!/usr/bin/env python3
"""Same transitions as StickyMode.swift, checked without swiftc. A drift fails this file or the Swift BFS."""

from __future__ import annotations

Phase = str
Kind = str


def initial(on: bool) -> dict:
    return {
        "on": on,
        "phase": "idle",
        "kind": "none",
        "alive": False,
        "switched": False,
        "deliveries": 0,
        "entered": False,
        "age": 0,
        "focus": "user",
        "bad": False,
    }


def capturable(kind: Kind) -> bool:
    return kind in ("field", "tmux")


def may_paste(s: dict) -> bool:
    return (
        s["on"]
        and s["phase"] == "delivering"
        and s["deliveries"] == 0
        and not s["entered"]
        and s["alive"]
        and capturable(s["kind"])
        and s["age"] < 3
        and not s["bad"]
    )


def step(s: dict, event: tuple) -> dict:
    name = event[0]
    n = dict(s)
    active = s["phase"] in ("armed", "recording", "delivering")
    opened = s["phase"] in ("armed", "recording")
    if name == "toggleOn":
        if (not s["on"]) and s["phase"] in ("idle", "delivered", "fellback"):
            n["on"] = True
        return n
    if name == "toggleOff":
        if not s["on"]:
            return s
        n["on"] = False
        if active:
            n["phase"] = "fellback"
            n["entered"] = False
        return n
    if name == "start":
        kind = event[1]
        if not (s["on"] and s["phase"] == "idle" and kind in ("field", "tmux", "secure", "nofield")):
            return s
        n.update(kind=kind, alive=kind in ("field", "tmux", "secure"), switched=False,
                 deliveries=0, entered=False, age=0, focus="user")
        n["phase"] = "armed" if capturable(kind) else "recording"
        return n
    if name == "speech":
        if s["on"] and s["phase"] == "armed":
            n["phase"] = "recording"
        return n
    if name == "switch":
        if s["on"] and active:
            n["switched"] = True
        return n
    if name == "gone":
        if s["on"] and s["alive"] and active:
            n["alive"] = False
        return n
    if name == "tick":
        if s["on"] and opened and s["age"] < 3:
            n["age"] = s["age"] + 1
            if n["age"] == 3:
                n["phase"] = "fellback"
        return n
    if name == "stop":
        if not (s["on"] and opened):
            return s
        if not event[1]:
            kept = initial(s["on"])
            kept["bad"] = s["bad"]
            return kept
        n["phase"] = "delivering" if s["alive"] and capturable(s["kind"]) and s["age"] < 3 else "fellback"
        return n
    if name == "pasteOk":
        if not may_paste(s):
            return s
        n["deliveries"] = 1
        n["focus"] = "target"
        return n
    if name == "pasteFail":
        if s["on"] and s["phase"] == "delivering" and s["deliveries"] == 0 and not s["entered"]:
            n["phase"] = "fellback"
        return n
    if name == "enterOk":
        if s["on"] and s["phase"] == "delivering" and s["deliveries"] == 1 and not s["entered"] and s["alive"] and capturable(s["kind"]):
            n["entered"] = True
            n["phase"] = "delivered"
            n["focus"] = "user" if s["switched"] else "target"
        return n
    if name == "enterFail":
        if s["on"] and s["phase"] == "delivering" and s["deliveries"] == 1 and not s["entered"]:
            n["phase"] = "fellback"
        return n
    if name == "reset":
        if s["phase"] in ("delivered", "fellback"):
            n = initial(s["on"])
            n["bad"] = s["bad"]
        return n
    raise AssertionError(name)


def key(s: dict) -> tuple:
    return tuple(s[k] for k in ("on", "phase", "kind", "alive", "switched", "deliveries", "entered", "age", "focus", "bad"))


EVENTS = [
    ("toggleOn",),
    ("toggleOff",),
    ("start", "field"),
    ("start", "tmux"),
    ("start", "secure"),
    ("start", "nofield"),
    ("start", "none"),
    ("speech",),
    ("switch",),
    ("gone",),
    ("tick",),
    ("stop", False),
    ("stop", True),
    ("pasteOk",),
    ("pasteFail",),
    ("enterOk",),
    ("enterFail",),
    ("reset",),
]


def explore() -> int:
    seen = {key(initial(False)), key(initial(True))}
    queue = [initial(False), initial(True)]
    q = 0
    while q < len(queue):
        current = queue[q]
        q += 1
        assert not current["bad"]
        if current["entered"]:
            assert current["deliveries"] == 1 and capturable(current["kind"]) and current["phase"] == "delivered"
        if current["kind"] == "secure":
            assert current["deliveries"] == 0 and not current["entered"]
        if current["phase"] in ("armed", "recording", "delivering"):
            assert current["on"]
        assert current["deliveries"] <= 1
        if current["phase"] == "delivered" and current["entered"]:
            assert current["focus"] == ("user" if current["switched"] else "target")
        for event in EVENTS:
            nxt = step(current, event)
            mark = key(nxt)
            if mark not in seen:
                seen.add(mark)
                queue.append(nxt)
    return len(seen)


def scenario() -> None:
    on = initial(True)
    armed = step(on, ("start", "field"))
    assert armed["phase"] == "armed"
    away = step(step(armed, ("speech",)), ("switch",))
    delivered = step(step(step(away, ("stop", True)), ("pasteOk",)), ("enterOk",))
    assert delivered["focus"] == "user" and delivered["entered"]
    secure = step(step(on, ("start", "secure")), ("stop", True))
    assert secure["phase"] == "fellback" and secure["deliveries"] == 0
    stale = step(on, ("start", "field"))
    for _ in range(3):
        stale = step(stale, ("tick",))
    assert stale["phase"] == "fellback"
    off = step(initial(False), ("start", "field"))
    assert off["phase"] == "idle"


if __name__ == "__main__":
    scenario()
    count = explore()
    print(f"PASS python sticky {count} states")
