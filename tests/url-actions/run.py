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
def check_wiring(repo):
    mac = repo.code("Sources/voice-switch/MacApp.swift")
    main = repo.code("Sources/voice-switch/main.swift")
    platform = repo.code("Sources/voice-switch/Platform.swift")
    config = repo.code("Sources/voice-switch/Config.swift")
    assert "Platform.runMacAction(config.cfg)" in mac, "wake must dispatch URL action"
    assert "Platform.runMacAction(config.cfg, stop: true)" in mac, "stop must dispatch URL action"
    assert "guard Platform.runMacAction(cfg, wait: true) else { exit(1) }" in main, "--fire must report URL failure"
    body = extract_braced_body(platform, r"static func runMacAction[^\{]*\{")
    assert "stop ? cfg.macOS?.stopURL : cfg.macOS?.wakeURL" in body
    assert "return runURLProcess(urlProcess(url), wait: wait)" in body, "URL branch must return without fallback"
    assert "runCommand(stop ? (cfg.stopCommand ?? defaultSuperwhisperToggle) : cfg.command)" in body
    url_body = extract_braced_body(platform, r"static func urlProcess[^\{]*\{")
    assert '"/usr/bin/open"' in url_body and "url.openArguments" in url_body
    assert "let process = Process()" in url_body, "each dispatch needs a fresh Process"
    assert "runCommand" not in url_body and '"/bin/sh"' not in url_body
    assert 'var openArguments: [String] { ["-g", "--", rawValue] }' in config
    runner = extract_braced_body(platform, r"static func runURLProcess[^\{]*\{")
    assert "return process.terminationStatus == 0" in runner
    assert "let hit = !isHead && config.cfg.wakeWords.map(normalize).contains(t)" in mac
    consume = extract_braced_body(mac, r"private func consume[^\{]*\{")
    for marker in ("config.cfg.macOS?.actions", "handoffBusy", "if !hit && start == nil",
                   "dictationStart(transcript, wakeWords: action.wakeWords)"):
        assert marker in consume, "action selection missing: " + marker
    assert "let start =" in consume and "if isHead && start == nil" in consume
    assert consume.index("let start =") < consume.index("config.cfg.macOS?.actions") < consume.index("if isHead && start == nil")
    assert "var action: WakeAction? = nil" in mac
    submit = extract_braced_body(mac, r"private func submit[^\{]*\{")
    assert "action: d.action" in submit
    handoff = extract_braced_body(mac, r"func handoff[^\{]*\{")
    assert "if action == nil, Macrowhisper.shouldPrepareHandoff(macrowhisper)" in handoff
    mode_override = "if let mode = action?.superwhisperMode { cfg.superwhisperMode = mode }"
    enter_mode = "enterSuperwhisperMode(cfg.superwhisperMode, target: target)"
    assert mode_override in handoff and enter_mode in handoff
    assert handoff.index(mode_override) < handoff.index(enter_mode)
    assert "Platform.urlProcess(action.url)" in handoff
    assert "Platform.runURLProcess(p, wait: false)" in handoff
    assert "openArguments(input:" in handoff and "let route = await routeDictation(result)" in handoff
    action_pos = handoff.index("openArguments(input:")
    route_pos = handoff.index("let route = await routeDictation(result)")
    assert action_pos < route_pos and "return" in handoff[action_pos:route_pos]
    segmenter = repo.code("Sources/voice-switch/Segmenter.swift")
    early = extract_braced_body(segmenter, r"static func earlyWake[^\{]*\{")
    assert "cfg.wakeWords" in early and ".actions" not in early

repo = SourceTree(root)
check_wiring(repo)
print("URL action static call-chain checks passed", flush=True)
mutations = [
    ("MacApp.swift", "Platform.runMacAction(config.cfg)"),
    ("MacApp.swift", "Platform.runMacAction(config.cfg, stop: true)"),
    ("main.swift", "else { exit(1) }"),
    ("Platform.swift", "stop ? cfg.macOS?.stopURL : cfg.macOS?.wakeURL"),
    ("Platform.swift", "return runURLProcess(urlProcess(url), wait: wait)"),
    ("Platform.swift", "runCommand(stop ? (cfg.stopCommand ?? defaultSuperwhisperToggle) : cfg.command)"),
    ("Platform.swift", "return process.terminationStatus == 0"),
    ("Platform.swift", '"/usr/bin/open"'),
    ("Config.swift", '["-g", "--", rawValue]'),
    ("MacApp.swift", "let hit = !isHead && config.cfg.wakeWords.map(normalize).contains(t)"),
    ("MacApp.swift", "config.cfg.macOS?.actions"),
    ("MacApp.swift", "handoffBusy"),
    ("MacApp.swift", "if !hit && start == nil"),
    ("MacApp.swift", "dictationStart(transcript, wakeWords: action.wakeWords)"),
    ("MacApp.swift", "var action: WakeAction? = nil"),
    ("MacApp.swift", "action: d.action"),
    ("MacApp.swift", "if action == nil, Macrowhisper.shouldPrepareHandoff(macrowhisper)"),
    ("MacApp.swift", "if let mode = action?.superwhisperMode { cfg.superwhisperMode = mode }"),
    ("MacApp.swift", "Platform.urlProcess(action.url)"),
    ("MacApp.swift", "Platform.runURLProcess(p, wait: false)"),
    ("MacApp.swift", "openArguments(input:"),
    ("MacApp.swift", "Platform.runURLProcess(p, wait: false)\n        return"),
    ("MacApp.swift", "if isHead && start == nil"),
    ("MacApp.swift", "let route = await routeDictation(result)"),
]
for filename, marker in mutations:
    path = "Sources/voice-switch/" + filename
    original = repo.read(path)
    assert marker in original, "mutation setup failed"
    changed = original.replace(marker, "REMOVED_FOR_TEST")
    try:
        check_wiring(SourceTree(root, {path: changed}))
    except AssertionError:
        continue
    raise AssertionError("mutation was not detected: " + marker)
print(f"URL static mutation checks passed: {len(mutations)} (source copies only)", flush=True)
if not shutil.which("swiftc"):
    print("SKIP: swiftc unavailable; Swift behavior tests were NOT run")
    sys.exit(1 if args.require_swift else 0)
with tempfile.TemporaryDirectory(prefix="voice-switch-url-test-") as temp:
    binary = pathlib.Path(temp) / "url-actions"
    subprocess.run(["swiftc", str(root / "Sources/voice-switch/Config.swift"),
                    str(root / "Sources/voice-switch/Platform.swift"),
                    str(root / "tests/url-actions/main.swift"), "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True)
