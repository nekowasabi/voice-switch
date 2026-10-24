#!/usr/bin/env python3
"""Run the production C# and Swift transcript policies against shared fixtures."""

from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
FIXTURE = ROOT / "tests/parity/fixtures/cough_policy.json"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--language", choices=("both", "csharp", "swift"), default="both")
    parser.add_argument("--regression-source", type=Path, help="run only the config-to-local regression against this source tree")
    args = parser.parse_args()
    required = {"csharp": "dotnet", "swift": "swiftc"}
    selected = required if args.language == "both" else {args.language: required[args.language]}
    if args.regression_source is not None:
        selected = {"csharp": "dotnet"}
    missing = [tool for tool in selected.values() if not shutil.which(tool)]
    if missing:
        print("BLOCKED missing SDK tools: " + ", ".join(missing))
        return 2
    if "csharp" in selected:
        if args.regression_source is None:
            subprocess.run(["dotnet", "run", "--project", str(HERE / "csharp/PolicyTests.csproj"), "--", str(FIXTURE)], cwd=ROOT, check=True)
        command = ["dotnet", "run", "--project", str(HERE / "csharp/ConfigRegression.csproj")]
        if args.regression_source is not None:
            command.append("-p:VoiceSwitchSourceRoot=" + str(args.regression_source.resolve()))
        subprocess.run(command, cwd=ROOT, check=True)
    if "swift" in selected:
        with tempfile.TemporaryDirectory(prefix="voice-policy-") as temp:
            binary = Path(temp) / "policy-tests"
            subprocess.run([
                "swiftc", "-module-cache-path", str(Path(temp) / "module-cache"),
                str(ROOT / "Sources/voice-switch/Config.swift"),
                str(ROOT / "Sources/voice-switch/Platform.swift"),
                str(ROOT / "Sources/voice-switch/DictationJournal.swift"),
                str(ROOT / "Sources/voice-switch/WakeSafety.swift"),
                str(ROOT / "Sources/voice-switch/Transcript.swift"),
                str(HERE / "swift/main.swift"), "-o", str(binary),
            ], cwd=ROOT, check=True)
            subprocess.run([str(binary), str(FIXTURE)], cwd=ROOT, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
