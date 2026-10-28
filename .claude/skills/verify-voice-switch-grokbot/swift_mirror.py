#!/usr/bin/env python3
"""Static Swift (and C# twin) markers for Grok Bot wake-action URL composition."""
from __future__ import annotations

import argparse
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[3]
SWIFT = ROOT / "Sources" / "voice-switch" / "Config.swift"
MAC = ROOT / "Sources" / "voice-switch" / "MacApp.swift"
CORE = ROOT / "dotnet" / "VoiceSwitch.Windows.Core"
HANDOFF = ROOT / "dotnet" / "VoiceSwitch.Windows" / "SuperwhisperHandoff.cs"

BASELINE = [
    (SWIFT, 'func openArguments(input: String) -> [String]'),
    (SWIFT, 'filter { $0.name != "input" }'),
    (SWIFT, 'URLQueryItem(name: "input", value: value)'),
    (CORE / "VoiceSwitchConfig.cs", "public string InputUrl(string input)"),
    (CORE / "VoiceSwitchConfig.cs", '.Where(item => item.Split(\'=\', 2)[0] != "input")'),
    (CORE / "VoiceSwitchConfig.cs", '.Append("input=" + EncodeUnreserved(input))'),
    (MAC, "openArguments(input:"),
    (HANDOFF, "LaunchUrl(action.InputUrl(text))"),
    (MAC, "if controlled { return .delivered }"),
    (HANDOFF, "if (controlled)"),
]


def core_text() -> str:
    return "\n".join(p.read_text(encoding="utf-8") for p in sorted(CORE.glob("*.cs")))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("baseline", "grokbot"))
    args = parser.parse_args()
    if args.mode == "baseline":
        missing: list[str] = []
        for path, marker in BASELINE:
            if marker not in path.read_text(encoding="utf-8"):
                missing.append(f"{path.relative_to(ROOT)}: {marker}")
        if missing:
            print(f"FAIL baseline missing {len(missing)} markers", file=sys.stderr)
            for item in missing:
                print(item, file=sys.stderr)
            return 1
        print(f"ok baseline {len(BASELINE)} markers")
        return 0

    swift = SWIFT.read_text(encoding="utf-8")
    mac = MAC.read_text(encoding="utf-8")
    cs = core_text()
    handoff = HANDOFF.read_text(encoding="utf-8")
    checks = [
        ("Config.swift inputParam", "inputParam" in swift),
        ("Config.swift GrokBotPrompt", "GrokBotPrompt" in swift),
        ("Config.swift /v1/prompt", "/v1/prompt" in swift),
        ("Windows.Core InputParam", "InputParam" in cs),
        ("Windows.Core GrokBotPrompt", "GrokBotPrompt" in cs),
        ("Windows.Core /v1/prompt", "/v1/prompt" in cs),
        ("MacApp.swift grokbot prompt text truncated", "grokbot prompt text truncated" in mac),
        ("SuperwhisperHandoff.cs grokbot prompt text truncated", "grokbot prompt text truncated" in handoff),
    ]
    missing = [name for name, ok in checks if not ok]
    if missing:
        print(f"FAIL grokbot missing {len(missing)} markers", file=sys.stderr)
        for item in missing:
            print(item, file=sys.stderr)
        return 1
    print(f"ok grokbot {len(checks)} markers")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
