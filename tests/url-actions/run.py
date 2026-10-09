#!/usr/bin/env python3
"""Check macOS URL wiring; optionally require executable Swift regressions."""
import argparse
import pathlib
import shutil
import subprocess
import tempfile
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "parity"))
from run_parity import SourceTree, extract_braced_body

root = pathlib.Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument("--require-swift", action="store_true")
args = parser.parse_args()
repo = SourceTree(root)
mac = repo.code("Sources/voice-switch/MacApp.swift")
main = repo.code("Sources/voice-switch/main.swift")
platform = repo.code("Sources/voice-switch/Platform.swift")
assert "Platform.runMacAction(config.cfg)" in mac, "wake must dispatch URL action"
assert "Platform.runMacAction(config.cfg, stop: true)" in mac, "stop must dispatch URL action"
assert "Platform.runMacAction(cfg, wait: true)" in main, "--fire must wait for URL result"
body = extract_braced_body(platform, r"static func runMacAction[^\{]*\{")
assert "stop ? cfg.macOS?.stopURL : cfg.macOS?.wakeURL" in body
assert "runURLProcess(urlProcess(url), wait: wait)" in body
assert "stop ? (cfg.stopCommand ?? defaultSuperwhisperToggle) : cfg.command" in body
url_body = extract_braced_body(platform, r"static func urlProcess[^\{]*\{")
assert '"/usr/bin/open"' in url_body and "url.openArguments" in url_body
assert "runCommand" not in url_body and '"/bin/sh"' not in url_body
print("URL action static call-chain checks passed", flush=True)
if not shutil.which("swiftc"):
    print("SKIP: swiftc unavailable; Swift behavior tests were NOT run")
    sys.exit(1 if args.require_swift else 0)
with tempfile.TemporaryDirectory(prefix="voice-switch-url-test-") as temp:
    binary = pathlib.Path(temp) / "url-actions"
    subprocess.run(["swiftc", str(root / "Sources/voice-switch/Config.swift"),
                    str(root / "Sources/voice-switch/Platform.swift"),
                    str(root / "tests/url-actions/main.swift"), "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True)
