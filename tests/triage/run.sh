#!/bin/sh
# Swift triage classifier against the shared truth table and Python's thresholds reader.
# Usage: sh tests/triage/run.sh   (from anywhere; needs swiftc and python3)
set -eu
repo=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/triage-swift.XXXXXX")
trap 'rm -rf "$work"' EXIT
python3 - "$repo/scripts" "$work/thresholds.json" <<'EOF'
import json, sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import journal_triage as jt
files = {
    "missing file": None,
    "empty object": "{}",
    "one question": '{"wants_action": {"no": 0.2, "yes": 0.6}}',
    "integer edges": '{"is_filler": {"no": 0, "yes": 1}}',
    "unknown question": '{"wants_lunch": {"no": 0.2, "yes": 0.6}}',
    "no above yes": '{"wants_action": {"no": 0.7, "yes": 0.3}}',
    "no equals yes": '{"wants_action": {"no": 0.5, "yes": 0.5}}',
    "out of range": '{"wants_action": {"no": 0.2, "yes": 1.2}}',
    "extra key": '{"wants_action": {"no": 0.2, "yes": 0.6, "maybe": 0.4}}',
    "missing yes": '{"wants_action": {"no": 0.2}}',
    "boolean edge": '{"wants_action": {"no": false, "yes": 0.6}}',
    "string edge": '{"wants_action": {"no": "0.2", "yes": 0.6}}',
    "band not an object": '{"wants_action": 0.5}',
    "not an object": "[]",
    "not json": "{",
}
out = []
for name, text in files.items():
    path = Path(sys.argv[2])
    if text is None:
        path.unlink(missing_ok=True)
    else:
        path.write_text(text, encoding="utf-8")
    try:
        bands = jt.read_thresholds(path)
    except (ValueError, json.JSONDecodeError):
        bands = None
    out.append({"name": name, "text": text, "bands": bands})
Path(sys.argv[2]).write_text(json.dumps(out), encoding="utf-8")
EOF
mv "$work/thresholds.json" "$work/expected.json"
swiftc -O -o "$work/triage" "$repo/tests/triage/main.swift" \
  "$repo/Sources/voice-switch/TriageClassifier.swift" "$repo/Sources/voice-switch/TriageExecution.swift"
"$work/triage" "$repo/tests/parity/fixtures/triage_decide.json" "$work/expected.json" "$work"
