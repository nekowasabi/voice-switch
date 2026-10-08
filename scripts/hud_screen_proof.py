#!/usr/bin/env python3
"""実際の HUD をコンパイルし、複数画面で表示先を検証する。"""

import argparse
import platform
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true", help="実ウィンドウと HUD の表示先を検証する")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    sources = sorted((root / "Sources/voice-switch").glob("*.swift"))
    sources = [str(path) for path in sources if path.name != "main.swift"]
    with tempfile.TemporaryDirectory(prefix="voice-switch-hud-proof-") as directory:
        executable = Path(directory) / "hud-screen-proof"
        subprocess.run([
            "xcrun", "swiftc", "-swift-version", "5", "-target",
            f"{platform.machine()}-apple-macosx26.0", *sources,
            str(root / "tests/macos/HUDScreen/main.swift"), "-o", str(executable),
        ], check=True, timeout=120)
        subprocess.run([str(executable), *(["--live"] if args.live else [])], check=True, timeout=30)


if __name__ == "__main__":
    main()
