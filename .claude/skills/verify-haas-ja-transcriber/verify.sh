#!/usr/bin/env bash
# Acceptance drive for the opt-in Haas-ja body transcriber. No GPU, no model download.
set -u

SKILL_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd "$SKILL_DIR/../../.." && pwd)
ROOT=${VS_HAAS_VERIFY_ROOT:-/tmp/voice-switch-haas-verify}
RUN_ID=${VS_RUN_ID:-run}
EVIDENCE="$ROOT/evidence/$RUN_ID"
WORK="$ROOT/work"
LIB="$ROOT/lib"

export PATH="${HOME}/.dotnet:${HOME}/swift/usr/bin:${PATH}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

usage() {
  echo "usage: $0 doctor | drive [ITEM] | cleanup" >&2
  exit 2
}

prepare_swift_lib() {
  if [[ -e /usr/lib/x86_64-linux-gnu/libncursesw.so.6 ]]; then
    mkdir -p "$LIB"
    ln -sfn /usr/lib/x86_64-linux-gnu/libncursesw.so.6 "$LIB/libncurses.so.6"
    case ":${LD_LIBRARY_PATH:-}:" in
      *":$LIB:"*) ;;
      *) export LD_LIBRARY_PATH="$LIB${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" ;;
    esac
  fi
}

swiftc_status() {
  if [[ "${1:-}" == "setup" ]]; then
    prepare_swift_lib
  fi
  if ! command -v swiftc >/dev/null 2>&1; then
    echo "missing"
    return 0
  fi
  local err
  if err=$(swiftc --version 2>&1); then
    echo "ok"
    return 0
  fi
  echo "broken: $err"
}

doctor() {
  local ok=0
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "missing dotnet" >&2
    ok=1
  else
    local version
    version=$(dotnet --version)
    case "$version" in
      8.*) echo "ok dotnet $version" ;;
      *) echo "dotnet $version is not 8.x" >&2; ok=1 ;;
    esac
  fi
  if ! command -v python3 >/dev/null 2>&1; then
    echo "missing python3" >&2
    ok=1
  else
    echo "ok python3 $(python3 --version 2>&1)"
  fi
  if ! command -v git >/dev/null 2>&1; then
    echo "missing git" >&2
    ok=1
  else
    echo "ok git $(git -C "$REPO" rev-parse --abbrev-ref HEAD) $(git -C "$REPO" rev-parse --short HEAD)"
  fi
  echo "note swiftc $(swiftc_status)"
  if command -v nvidia-smi >/dev/null 2>&1 && nvidia-smi -L >/dev/null 2>&1; then
    echo "note nvidia: a GPU is visible; this drive still does not load Haas weights"
  else
    echo "note nvidia: none (expected; live inference is HJ-GPU SKIP)"
  fi
  [[ -f "$REPO/Makefile" ]] || { echo "missing Makefile under $REPO" >&2; ok=1; }
  if [[ "$ok" -eq 0 ]]; then
    echo "ok doctor"
  fi
  return "$ok"
}

record() {
  local id=$1
  local status=$2
  local rest=$3
  printf '%s %s %s\n' "$status" "$id" "$rest" | tee -a "$EVIDENCE/summary.txt"
  case "$status" in
    PASS) PASS_N=$((PASS_N + 1)) ;;
    SKIP) SKIP_N=$((SKIP_N + 1)) ;;
    FAIL) FAIL_N=$((FAIL_N + 1)) ;;
  esac
}

suite_log() {
  echo "$EVIDENCE/windows-tests.txt"
}

ensure_suite() {
  local log
  log=$(suite_log)
  if [[ -f "$log" ]]; then
    return 0
  fi
  mkdir -p "$EVIDENCE"
  dotnet run --project "$REPO/dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj" -c Release \
    >"$log" 2>&1
  echo $? >"$EVIDENCE/windows-tests.exit"
}

require_passes() {
  local log=$1
  shift
  local missing=0
  local name
  for name in "$@"; do
    if ! grep -F -x "PASS $name" "$log" >/dev/null 2>&1; then
      echo "missing PASS line: $name" >>"$EVIDENCE/item-detail.txt"
      missing=1
    fi
  done
  return "$missing"
}

item_cfg_cs() {
  ensure_suite
  require_passes "$(suite_log)" \
    "haas config absent transcriber stays superwhisper" \
    "haas config decodes command timeout and vocabulary" \
    "haas config rejects an unknown transcriber" \
    "haas config rejects an empty command"
}

item_route_ok() {
  ensure_suite
  require_passes "$(suite_log)" \
    "haas runner sends mock text to tmux and does not launch superwhisper" \
    "haas runner sends mock text to the action url and not to tmux" \
    "haas runner pastes when tmux does not take the text"
}

item_route_fail() {
  ensure_suite
  require_passes "$(suite_log)" \
    "haas runner non-zero exit does not send and does not fall back" \
    "haas runner empty stdout does not send and does not fall back" \
    "haas runner timeout does not send and does not fall back"
}

item_default() {
  ensure_suite
  require_passes "$(suite_log)" \
    "haas default factory keeps the superwhisper handoff"
}

item_reg() {
  ensure_suite
  local code
  code=$(cat "$EVIDENCE/windows-tests.exit")
  if [[ "$code" != "0" ]]; then
    echo "windows tests exit $code" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  if grep -E '^FAIL ' "$(suite_log)" >/dev/null 2>&1; then
    echo "windows tests printed FAIL" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  return 0
}

item_runner() {
  local src=$REPO/scripts/haas_transcribe.py
  local out=$EVIDENCE/HJ-RUNNER.txt
  mkdir -p "$EVIDENCE"
  if [[ ! -f "$src" ]]; then
    echo "missing $src" | tee "$out" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  if ! python3 -m py_compile "$src" >"$out" 2>&1; then
    echo "py_compile failed" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  echo "py_compile exit 0" >>"$out"
  if ! python3 "$src" --help >>"$out" 2>&1; then
    echo "--help failed" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  if ! grep -q -- '--vocab' "$out"; then
    echo "--help did not mention --vocab" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  if ! grep -E -q -i 'wav' "$out"; then
    echo "--help did not mention wav" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  if python3 - "$src" <<'PY'
import ast, sys
tree = ast.parse(open(sys.argv[1], encoding="utf-8").read())
for node in tree.body:
    if isinstance(node, (ast.Import, ast.ImportFrom)):
        names = []
        if isinstance(node, ast.Import):
            names = [a.name for a in node.names]
        else:
            names = [node.module or ""]
        if any(name == "haas_asr" or name.startswith("haas_asr.") for name in names):
            raise SystemExit("haas_asr is imported at module level")
text = open(sys.argv[1], encoding="utf-8").read()
for needle in ("transcribe_longform", "haas_asr.transcribe", "Vocabulary"):
    if needle not in text and needle.replace("haas_asr.", "") not in text:
        raise SystemExit("runner is missing " + needle)
print("ast ok")
PY
  then
    echo "ast ok" >>"$out"
  else
    echo "runner static check failed" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  local importable
  importable=$(python3 -c 'import importlib.util,sys; sys.exit(0 if importlib.util.find_spec("haas_asr") else 1)' && echo yes || echo no)
  if [[ "$importable" == "yes" ]]; then
    echo "SKIP-SUB haas_asr is installed; missing-package stderr was not checked" >>"$out"
  else
    python3 "$src" "$EVIDENCE/no-such-haas.wav" >"$EVIDENCE/runner-missing.out" 2>"$EVIDENCE/runner-missing.err"
    local run_code=$?
    if [[ "$run_code" == "0" ]]; then
      echo "runner exit 0 without haas_asr" >>"$EVIDENCE/item-detail.txt"
      return 1
    fi
    if ! grep -q 'haas_asr' "$EVIDENCE/runner-missing.err"; then
      echo "stderr did not name haas_asr" >>"$EVIDENCE/item-detail.txt"
      cat "$EVIDENCE/runner-missing.err" >>"$EVIDENCE/item-detail.txt"
      return 1
    fi
    if ! grep -E -q 'haas-setup|pip install' "$EVIDENCE/runner-missing.err"; then
      echo "stderr did not say how to install haas_asr" >>"$EVIDENCE/item-detail.txt"
      return 1
    fi
  fi
  return 0
}

item_make() {
  local out=$EVIDENCE/HJ-MAKE.txt
  mkdir -p "$EVIDENCE"
  python3 - "$REPO/Makefile" >"$out" <<'PY'
import re, sys
text = open(sys.argv[1], encoding="utf-8").read()
targets = {}
for match in re.finditer(r"^([A-Za-z0-9_.-]+)\s*:([^=\n]*)$", text, re.M):
    name = match.group(1)
    prereq = match.group(2)
    prereq = re.sub(r"\\.*", "", prereq)
    targets.setdefault(name, [])
    targets[name].append(prereq)
for name in ("haas-setup", "haas-model"):
    if name not in targets:
        raise SystemExit("missing target " + name)
banned = {"build", "app", "install", "model", "relaunch", "win", "win-build", "win-test", "win-publish", "parity-test", "url-test"}
for name in sorted(banned):
    blob = " ".join(targets.get(name, []))
    for bad in ("haas-setup", "haas-model"):
        if re.search(r"(^|\s)" + bad + r"($|\s)", blob):
            raise SystemExit(f"{name} depends on {bad}")
print("makefile graph ok")
PY
  local code=$?
  if [[ "$code" != "0" ]]; then
    echo "makefile graph check failed" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  local target
  for target in build win-test parity-test; do
    make -C "$REPO" -n "$target" >"$EVIDENCE/make-n-$target.txt" 2>&1 || true
    if grep -E -q 'haas-setup|haas-model|haas-asr|huggingface' "$EVIDENCE/make-n-$target.txt"; then
      echo "make -n $target mentions haas" >>"$EVIDENCE/item-detail.txt"
      return 1
    fi
  done
  return 0
}

item_docs() {
  mkdir -p "$EVIDENCE"
  python3 - "$REPO" >"$EVIDENCE/HJ-DOCS.txt" <<'PY'
import json, sys
from pathlib import Path
repo = Path(sys.argv[1])
errors = []

def load_json(path: Path):
    text = path.read_text(encoding="utf-8")
    out = []
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
    try:
        return json.loads("".join(out))
    except json.JSONDecodeError as exc:
        errors.append(f"{path.name} is not JSON: {exc}")
        return {}
def need(path, needles):
    file = repo / path
    if not file.is_file():
        errors.append("missing " + path)
        return
    text = file.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            errors.append(path + " missing " + needle)
need("README.md", [
    "Haas-ja-v1.0 (Japanese local option)",
    "third-intelligence/Haas-ja-v1.0",
    "NVIDIA",
    "dictation.transcriber",
    '"haas"',
])
need("README_ja.md", [
    "Haas-ja-v1.0 (Japanese local option)",
    "third-intelligence/Haas-ja-v1.0",
    "NVIDIA",
    "dictation.transcriber",
])
for path, banned in (("README.md", "in-process"), ("README_ja.md", "プロセス内")):
    text = (repo / path).read_text(encoding="utf-8") if (repo / path).is_file() else ""
    if banned not in text and "macOS" not in text and "Mac" not in text:
        errors.append(path + " does not say Mac in-process is unsupported")
    if path == "README.md" and "not supported" not in text.lower() and "unsupported" not in text.lower():
        errors.append("README.md does not say Mac in-process is unsupported")
    if path == "README_ja.md" and "非対応" not in text and "できません" not in text:
        errors.append("README_ja.md does not say Mac in-process is unsupported")
memo = repo / "MEMO.md"
if not memo.is_file():
    errors.append("missing MEMO.md")
else:
    text = memo.read_text(encoding="utf-8")
    for needle in ("Haas-ja-v1.0", "GPU"):
        if needle not in text:
            errors.append("MEMO.md missing " + needle)
    if "Cohere" not in text and "Whisper" not in text:
        errors.append("MEMO.md does not keep Cohere or Whisper as the CPU/Mac alternative")
example = repo / "config.example.haas.json"
if not example.is_file():
    errors.append("missing config.example.haas.json")
else:
    data = load_json(example)
    transcriber = data.get("dictation", {}).get("transcriber")
    command = data.get("dictation", {}).get("haas", {}).get("command")
    if transcriber != "haas":
        errors.append("config.example.haas.json transcriber is not haas")
    if not isinstance(command, list) or not command or not all(isinstance(part, str) and part for part in command):
        errors.append("config.example.haas.json command is not a non-empty string array")
for sample in ("config.example.json", "config.example.windows.json"):
    data = load_json(repo / sample)
    if "transcriber" in data.get("dictation", {}):
        errors.append(sample + " must not set dictation.transcriber")
if errors:
    print("\n".join(errors))
    raise SystemExit(1)
print("docs ok")
PY
}

item_swift() {
  mkdir -p "$EVIDENCE" "$WORK"
  prepare_swift_lib
  local status
  status=$(swiftc_status)
  if [[ "$status" != ok ]]; then
    echo "$status" >"$EVIDENCE/HJ-SWIFT.txt"
    record HJ-SWIFT SKIP "$status"
    return 2
  fi
  local bin=$WORK/haas-swift
  local log=$EVIDENCE/HJ-SWIFT.txt
  if ! swiftc -module-name voice_switch -module-cache-path "$WORK/module-cache" \
    "$REPO/Sources/voice-switch/Config.swift" \
    "$REPO/Sources/voice-switch/DictationJournal.swift" \
    "$REPO/Sources/voice-switch/WakeSafety.swift" \
    "$REPO/Sources/voice-switch/Transcript.swift" \
    "$REPO/Sources/voice-switch/Platform.swift" \
    "$REPO/Sources/voice-switch/HaasTranscriber.swift" \
    "$REPO/tests/haas-transcriber/main.swift" \
    -o "$bin" >"$log" 2>&1; then
    echo "swiftc compile failed" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  if ! "$bin" >>"$log" 2>&1; then
    echo "swift test failed" >>"$EVIDENCE/item-detail.txt"
    return 1
  fi
  require_passes "$log" \
    "haas swift absent transcriber stays superwhisper" \
    "haas swift decodes command timeout and vocabulary" \
    "haas swift rejects an unknown transcriber" \
    "haas swift mock text" \
    "haas swift mock non-zero does not return text" \
    "haas swift mock empty does not return text" \
    "haas swift mock timeout"
}

item_swift_wire() {
  mkdir -p "$EVIDENCE"
  python3 - "$REPO" >"$EVIDENCE/HJ-SWIFT-WIRE.txt" <<'PY'
import sys
from pathlib import Path
repo = Path(sys.argv[1])
errors = []
mac = (repo / "Sources/voice-switch/MacApp.swift").read_text(encoding="utf-8")
start = mac.find("func handoff(")
if start < 0:
    errors.append("MacApp.swift has no func handoff")
else:
    body = mac[start:]
    open_at = body.find('"/usr/bin/open"')
    haas_at = body.find("handoffHaas")
    if haas_at < 0:
        haas_at = body.find("HaasRunner.run")
    if haas_at < 0 or open_at < 0 or haas_at > open_at:
        errors.append("Haas branch is not before /usr/bin/open in handoff")
haas_fn = mac.find("func handoffHaas")
if haas_fn < 0:
    errors.append("MacApp.swift has no func handoffHaas")
else:
    nxt = mac.find("\nfunc ", haas_fn + 1)
    chunk = mac[haas_fn: nxt if nxt > 0 else haas_fn + 8000]
    if "dictation: haas runner failed" not in chunk and "dictation: haas runner failed" not in mac:
        errors.append("Mac Haas path does not log dictation: haas runner failed")
    if ".noResult" not in chunk:
        errors.append("handoffHaas does not return .noResult")
    if ".notDispatched" in chunk:
        errors.append("handoffHaas returns .notDispatched")
win = "\n".join(path.read_text(encoding="utf-8") for path in (repo / "dotnet").rglob("*.cs"))
if "dictation: haas runner failed" not in win:
    errors.append("Windows does not log dictation: haas runner failed")
program = (repo / "dotnet/VoiceSwitch.Windows/Program.cs").read_text(encoding="utf-8")
if "DictationHandoffs.Create" not in program:
    errors.append("Program.cs does not call DictationHandoffs.Create")
factory = repo / "dotnet/VoiceSwitch.Windows/DictationHandoffs.cs"
if not factory.is_file():
    errors.append("missing DictationHandoffs.cs")
else:
    text = factory.read_text(encoding="utf-8")
    haas = text.find("new HaasRunnerHandoff")
    superwhisper = text.find("new RegisteredSuperwhisperHandoff")
    wrap = text.find("LocalHistoryHandoff.Wrap")
    if min(haas, superwhisper, wrap) < 0:
        errors.append("DictationHandoffs.cs is missing Haas, Superwhisper, or the local wrap")
    elif not (haas < superwhisper < wrap):
        errors.append("DictationHandoffs.cs order is not Haas, then Superwhisper, then Wrap")
    elif "LocalHistoryHandoff.Wrap" in text[haas:superwhisper]:
        errors.append("local history wrap sits on the Haas branch")
    if '"haas"' not in text:
        errors.append("DictationHandoffs.cs does not mention \"haas\"")
if errors:
    print("\n".join(errors))
    raise SystemExit(1)
print("wire ok")
PY
}

item_parity() {
  mkdir -p "$EVIDENCE"
  local log=$EVIDENCE/HJ-PARITY.txt
  python3 "$REPO/tests/parity/run_parity.py" >"$log" 2>&1
  echo "parity exit $?" >>"$log"
  python3 - "$log" <<'PY'
import re, sys
text = open(sys.argv[1], encoding="utf-8").read()
match = re.search(r"SUMMARY pass=(\d+) allow=(\d+) fail=(\d+)", text)
if not match:
    raise SystemExit("no SUMMARY line")
fail = int(match.group(3))
if fail > 2:
    raise SystemExit(f"parity fail={fail} is worse than baseline 2")
allowed = (
    "Swift pure harness failed to compile",
    "libncurses.so.6",
    "automatic transcript policy checks failed",
)
for line in text.splitlines():
    if line.startswith("FAIL ") or line.startswith("FAIL"):
        if not any(token in line for token in allowed):
            # The policy failure's traceback follows; only the FAIL lines count.
            if line.startswith("FAIL"):
                raise SystemExit("new parity failure: " + line)
print(f"parity ok fail={fail}")
PY
}

item_gpu() {
  record HJ-GPU SKIP "live Haas inference needs Linux + NVIDIA GPU + CUDA/Triton; this box does not run it (human/GPU-owned)"
  return 2
}

run_item() {
  local id=$1
  local detail=$EVIDENCE/item-detail.txt
  : >"$detail"
  local rc=0
  case "$id" in
    HJ-CFG-CS) item_cfg_cs ;;
    HJ-ROUTE-OK) item_route_ok ;;
    HJ-ROUTE-FAIL) item_route_fail ;;
    HJ-DEFAULT) item_default ;;
    HJ-REG) item_reg ;;
    HJ-RUNNER) item_runner ;;
    HJ-MAKE) item_make ;;
    HJ-DOCS) item_docs ;;
    HJ-SWIFT) item_swift ;;
    HJ-SWIFT-WIRE) item_swift_wire ;;
    HJ-PARITY) item_parity ;;
    HJ-GPU) item_gpu ;;
    *) echo "unknown item $id" >&2; return 2 ;;
  esac
  rc=$?
  if [[ "$rc" -eq 2 ]]; then
    return 0
  fi
  if [[ "$rc" -eq 0 ]]; then
    record "$id" PASS "see $EVIDENCE"
  else
    local why
    why=$(tr '\n' ' ' <"$detail" | cut -c1-400)
    record "$id" FAIL "${why:-see $EVIDENCE}"
  fi
}

drive() {
  local only=${1:-}
  mkdir -p "$EVIDENCE"
  : >"$EVIDENCE/summary.txt"
  PASS_N=0
  SKIP_N=0
  FAIL_N=0
  local ids=(
    HJ-DOCS HJ-MAKE HJ-RUNNER HJ-SWIFT-WIRE
    HJ-CFG-CS HJ-ROUTE-OK HJ-ROUTE-FAIL HJ-DEFAULT HJ-REG
    HJ-SWIFT HJ-PARITY HJ-GPU
  )
  if [[ -n "$only" ]]; then
    ids=("$only")
  fi
  local id
  for id in "${ids[@]}"; do
    run_item "$id" || true
  done
  echo "SUMMARY pass=$PASS_N skip=$SKIP_N fail=$FAIL_N" | tee -a "$EVIDENCE/summary.txt"
  [[ "$FAIL_N" -eq 0 ]]
}

cleanup() {
  rm -rf "$WORK" "$LIB"
  echo "removed $WORK and $LIB; evidence remains under $ROOT/evidence"
}

main() {
  local cmd=${1:-}
  case "$cmd" in
    doctor) doctor ;;
    drive) drive "${2:-}" ;;
    cleanup) cleanup ;;
    *) usage ;;
  esac
}

main "$@"
