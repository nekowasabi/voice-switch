"""Compile standalone Swift harnesses and check their inputs against the SwiftPM Core target."""

import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE_DIR = ROOT / "Sources/voice-switch"

# These are harness inputs. Package.swift owns membership in VoiceSwitchCore.
JOURNAL = ("SecretLog", "Config", "Platform", "DictationJournal", "WakeSafety", "Transcript")
SEGMENTER = (*JOURNAL, "Segmenter", "SuperwhisperModes", "PaneRoute")
TRIAGE = ("TriageClassifier", "TriageExecution")
HARNESSES = {
    "journal": JOURNAL,
    "segmenter": SEGMENTER,
    "sticky": ("StickyMode",),
    "triage": TRIAGE,
    "triage-request": (*JOURNAL, *TRIAGE, "TriageRequest", "TriageFiles"),
}

ALLOWED_IMPORTS = {
    "Foundation", "CoreFoundation", "CryptoKit", "Darwin", "os",
    "OnnxRuntimeBindings", "whisper", "FoundationNetworking",
}
IMPORT = re.compile(
    r"(?m)(?:^|;)[ \t]*(?:@\w+(?:\([^\n)]*\))?[ \t\n]+|(?:public|package|internal|private|fileprivate)[ \t]+)*"
    r"import\s+(?:(?:typealias|struct|class|enum|protocol|let|var|func)\s+)?`?(\w+)"
)


def harness_sources(harness, source_dir=SOURCE_DIR):
    return [str(source_dir / f"{name}.swift") for name in HARNESSES[harness]]


def check():
    manifest = json.loads(subprocess.check_output(["swift", "package", "dump-package"], cwd=ROOT))
    target = next(target for target in manifest["targets"] if target["name"] == "VoiceSwitchCore")
    core = {(ROOT / target["path"] / name) for name in target["sources"]}

    errors = []
    for path in sorted(core):
        source = path.read_text()
        for match in IMPORT.finditer(source):
            if match[1] not in ALLOWED_IMPORTS:
                line = source.count("\n", 0, match.start(1)) + 1
                errors.append(f"{path.relative_to(ROOT)}:{line}: import {match[1]} is outside the VoiceSwitchCore allowlist")
    for harness in HARNESSES:
        for path in map(Path, harness_sources(harness)):
            if path not in core:
                errors.append(f"{path.relative_to(ROOT)}: {harness} harness input is missing from VoiceSwitchCore")
    if errors:
        for error in errors:
            print(f"FAIL {error}", file=sys.stderr)
        return 1
    print(f"PASS Swift Core imports and harness inputs ({len(core)} sources)")
    return 0


if __name__ == "__main__":
    if sys.argv[1:] == ["check"]:
        raise SystemExit(check())
    harness, source_dir, *command = sys.argv[1:]
    raise SystemExit(subprocess.call([*command, *harness_sources(harness, Path(source_dir))]))
