#!/usr/bin/env python3
"""Mechanical checks for issue #48 Phase 0. Prints one PASS/FAIL/DEFERRED/UNVERIFIED line per item."""

from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
ITEMS = Path(__file__).resolve().parent / "items.json"
EVIDENCE = Path("/tmp/voice-switch-verify-end-model/evidence")


def load_items():
    data = json.loads(ITEMS.read_text())
    return data["items"]


def read(rel: str) -> str | None:
    path = ROOT / rel
    if not path.is_file():
        return None
    return path.read_text()


def strip_json_line_comments(text: str) -> str:
    """Drop // comments outside strings. Sample configs allow them, and json.loads does not."""
    out: list[str] = []
    in_string = False
    escape = False
    index = 0
    while index < len(text):
        char = text[index]
        if in_string:
            out.append(char)
            if escape:
                escape = False
            elif char == "\\":
                escape = True
            elif char == '"':
                in_string = False
            index += 1
            continue
        if char == '"':
            in_string = True
            out.append(char)
            index += 1
            continue
        if char == "/" and index + 1 < len(text) and text[index + 1] == "/":
            while index < len(text) and text[index] not in "\r\n":
                index += 1
            continue
        out.append(char)
        index += 1
    return "".join(out)


def dig(value, pointer: str):
    for part in pointer.split("."):
        value = value[part]
    return value


def evaluate(item: dict) -> tuple[str, str]:
    kind = item["kind"]
    if kind == "device":
        return "UNVERIFIED", item["reason"]
    if kind == "deferred":
        return "DEFERRED", item["reason"]
    if kind == "contains_all":
        text = read(item["path"])
        if text is None:
            return "FAIL", f"{item['path']} missing"
        missing = [needle for needle in item["needles"] if needle not in text]
        if missing:
            return "FAIL", f"{item['path']} missing {missing}"
        return "PASS", item["path"]
    if kind == "absent":
        missing_files = []
        hits = []
        for rel in item["paths"]:
            text = read(rel)
            if text is None:
                missing_files.append(rel)
            elif item["needle"] in text:
                hits.append(rel)
        if missing_files:
            return "FAIL", f"missing files {missing_files}"
        if hits:
            return "FAIL", f"{item['needle']!r} still in {hits}"
        return "PASS", f"absent from {len(item['paths'])} files"
    if kind == "present":
        missing = []
        for rel in item["paths"]:
            text = read(rel)
            if text is None or item["needle"] not in text:
                missing.append(rel)
        if missing:
            return "FAIL", f"{item['needle']!r} missing from {missing}"
        return "PASS", f"present in {len(item['paths'])} files"
    if kind == "json_equals":
        bad = []
        for rel in item["paths"]:
            text = read(rel)
            if text is None:
                bad.append(f"{rel} missing")
                continue
            try:
                actual = dig(json.loads(strip_json_line_comments(text)), item["pointer"])
            except (json.JSONDecodeError, KeyError, TypeError) as error:
                bad.append(f"{rel} ({error})")
                continue
            if actual != item["value"]:
                bad.append(f"{rel}={actual}")
        if bad:
            return "FAIL", f"expected {item['value']}: {bad}"
        return "PASS", f"{item['pointer']}={item['value']}"
    return "FAIL", f"unknown kind {kind}"


def doctor() -> int:
    problems = []
    if not (ROOT / "Sources/voice-switch/MacApp.swift").is_file():
        problems.append("MacApp.swift missing")
    if not (ROOT / "dotnet/VoiceSwitch.Windows/DictationRuntime.cs").is_file():
        problems.append("DictationRuntime.cs missing")
    try:
        items = load_items()
    except Exception as error:  # noqa: BLE001
        problems.append(f"items.json: {error}")
        items = []
    print(f"doctor root={ROOT} items={len(items)}")
    if problems:
        for problem in problems:
            print(f"doctor FAIL {problem}")
        return 1
    print("doctor PASS")
    return 0


def run(feature: str | None) -> int:
    items = load_items()
    if feature:
        items = [item for item in items if item["feature"] == feature]
        if not items:
            print(f"no items for feature {feature}")
            return 1
    lines = []
    failed = 0
    for item in items:
        status, detail = evaluate(item)
        line = f"{status} {item['id']} {detail}"
        print(line)
        lines.append(line)
        if status == "FAIL" and item.get("required", True):
            failed += 1
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    name = feature or "all"
    (EVIDENCE / f"{name}.txt").write_text("\n".join(lines) + "\n")
    print(f"evidence {EVIDENCE / (name + '.txt')}")
    return 1 if failed else 0


def main(argv: list[str]) -> int:
    if len(argv) < 2 or argv[1] not in {"doctor", "drive"}:
        print("usage: check.py doctor | check.py drive [feature]", file=sys.stderr)
        return 2
    if argv[1] == "doctor":
        return doctor()
    feature = argv[2] if len(argv) > 2 else None
    code = doctor()
    if code != 0:
        return code
    return run(feature)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
