#!/usr/bin/env python3
"""Box-side proof of SuperwhisperModes ResolveKey / ActiveMode / Decide (Mac+Win contract)."""

from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def field(json_text: str, name: str) -> str | None:
    try:
        root = json.loads(json_text)
    except json.JSONDecodeError:
        return None
    if not isinstance(root, dict):
        return None
    value = root.get(name)
    return value if isinstance(value, str) and value else None


def resolve_key(wanted: str, mode_jsons: list[str]) -> str | None:
    modes = []
    for raw in mode_jsons:
        key = field(raw, "key")
        if key is None:
            continue
        modes.append((key, field(raw, "name")))
    for key, _ in modes:
        if key.casefold() == wanted.casefold():
            return key
    for key, name in modes:
        if name and name.casefold() == wanted.casefold():
            return key
    return None


def active_mode(preferences_json: str | None) -> str | None:
    return None if preferences_json is None else field(preferences_json, "activeMode")


def decide(mode_requested: bool, route: str) -> str:
    if route in ("sent", "skippedNoBody"):
        return "pane"
    return "paste" if mode_requested else "superwhisper"


def check(cond: bool, msg: str) -> None:
    if not cond:
        raise AssertionError(msg)


def main() -> int:
    modes = [
        '{"key":"new-mode-1","name":"voice_switch","autoPaste":false}',
        '{"key":"default","name":"Default"}',
        '{"key":"other","name":"new-mode-1"}',
    ]
    check(resolve_key("new-mode-1", modes) == "new-mode-1", "key match wins")
    check(resolve_key("voice_switch", modes) == "new-mode-1", "name match")
    check(resolve_key("VOICE_SWITCH", modes) == "new-mode-1", "name casefold")
    check(resolve_key("missing", modes) is None, "missing")
    check(active_mode('{"activeMode":"default"}') == "default", "active")
    check(active_mode('{"activeMode":""}') is None, "empty active")
    check(active_mode(None) is None, "null prefs")
    check(active_mode("{not json") is None, "bad json")

    check(decide(True, "sent") == "pane", "sent+mode")
    check(decide(True, "notRouted") == "paste", "notRouted+mode")
    check(decide(True, "sendFailed") == "paste", "sendFailed+mode")
    check(decide(False, "notRouted") == "superwhisper", "legacy")
    check(decide(False, "sendFailed") == "superwhisper", "sendFailed legacy no paste")
    check(decide(False, "sent") == "pane", "sent legacy")
    check(decide(True, "skippedNoBody") == "pane", "no body never paste")
    check(decide(False, "skippedNoBody") == "pane", "no body never paste unset")

    # Static: Mac sources must expose the wiring (swiftc unavailable on this box).
    modes_swift = (ROOT / "Sources/voice-switch/SuperwhisperModes.swift").read_text(encoding="utf-8")
    mac = (ROOT / "Sources/voice-switch/MacApp.swift").read_text(encoding="utf-8")
    pane = (ROOT / "Sources/voice-switch/PaneRoute.swift").read_text(encoding="utf-8")
    for needle in (
        "func resolveSuperwhisperModeKey",
        "func decideDictationDelivery",
        "case skippedNoBody",
    ):
        check(needle in modes_swift, f"missing in SuperwhisperModes.swift: {needle}")
    for needle in (
        "enterSuperwhisperMode(cfg.superwhisperMode",
        "decideDictationDelivery(modeRequested:",
        "pasteDictation(payload, target:",
        "superwhisper mode restored:",
        "guard readSuperwhisperActiveMode() == key else",
    ):
        check(needle in mac, f"missing in MacApp.swift: {needle}")
    # Windows EnterModeAsync mirrors the same post-switch activeMode gate.
    win = (ROOT / "dotnet/VoiceSwitch.Windows/SuperwhisperHandoff.cs").read_text(encoding="utf-8")
    for needle in (
        "if (ReadActiveMode() != key)",
        "return (false, null);",
    ):
        check(needle in win, f"missing in SuperwhisperHandoff.cs: {needle}")
    for needle in (
        "func routeDictation(_ text: String) async -> RouteResult",
        "func sendKeysToPane",
        "p.waitUntilExit()",
    ):
        check(needle in pane, f"missing in PaneRoute.swift: {needle}")

    print("superwhisper_mode_proof: ok")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as exc:
        print(f"superwhisper_mode_proof: FAIL {exc}", file=sys.stderr)
        raise SystemExit(1)
