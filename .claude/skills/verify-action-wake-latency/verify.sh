#!/usr/bin/env bash
# Linux checks for action wake words on the early-wake probe. No microphone.
set -euo pipefail

export PATH="${HOME}/.dotnet:${HOME}/swift/usr/bin:${PATH}"
export DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

REPO="$(cd "$(dirname "$0")/../../.." && pwd)"
SKILL="$(cd "$(dirname "$0")" && pwd)"
ROOT="${VS_AWL_VERIFY_ROOT:-/tmp/voice-switch-action-wake-verify}"
EVIDENCE="$ROOT/evidence"
ITEMS="$SKILL/items.md"
TESTS_PROJ="$REPO/dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj"

die() { echo "verify-action-wake-latency: $*" >&2; exit 1; }

usage() {
  echo "usage: $0 doctor | drive [id] | cleanup" >&2
  exit 2
}

item_rows() {
  python3 - "$ITEMS" <<'PY'
import pathlib, re, sys
text = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
blocks = re.split(r"(?m)^## ", text)[1:]
for block in blocks:
    lines = block.splitlines()
    item_id = lines[0].strip()
    kind = ""
    needle = ""
    for line in lines[1:]:
        if line.startswith("kind:"):
            kind = line.split(":", 1)[1].strip()
        elif line.startswith("needle:"):
            needle = line.split(":", 1)[1].strip()
    if not item_id or not kind:
        sys.exit(f"bad item block: {item_id!r}")
    print(f"{item_id}\t{kind}\t{needle}")
PY
}

doctor() {
  local ok=1
  if ! command -v dotnet >/dev/null; then
    echo "FAIL dotnet missing"
    ok=0
  elif ! dotnet --version | grep -Eq '^8\.'; then
    echo "FAIL dotnet is not 8.x ($(dotnet --version))"
    ok=0
  fi
  if ! command -v python3 >/dev/null; then
    echo "FAIL python3 missing"
    ok=0
  fi
  local path
  for path in \
    "$ITEMS" \
    "$TESTS_PROJ" \
    "$REPO/dotnet/VoiceSwitch.Windows.Core/DictationCore.cs" \
    "$REPO/dotnet/VoiceSwitch.Windows/DictationRuntime.cs" \
    "$REPO/dotnet/VoiceSwitch.Windows/Program.cs" \
    "$REPO/Sources/voice-switch/Segmenter.swift" \
    "$REPO/Sources/voice-switch/MacApp.swift" \
    "$REPO/Sources/voice-switch/Transcript.swift" \
    "$REPO/README.md" \
    "$REPO/README_ja.md" \
    "$REPO/tests/url-actions/run.py" \
    "$REPO/tests/parity/run_parity.py"
  do
    if [ ! -f "$path" ]; then
      echo "FAIL missing $path"
      ok=0
    fi
  done
  if [ "$ok" = 1 ]; then
    echo "repo $REPO"
    echo "head $(git -C "$REPO" rev-parse HEAD)"
    echo "sdk $(dotnet --version)"
    echo "scratch $ROOT"
    if swiftc --version >/dev/null 2>&1; then
      echo "swiftc: ok"
    else
      echo "swiftc: unavailable"
    fi
    echo "doctor: ok"
  fi
  [ "$ok" = 1 ]
}

run_logged() {
  local id="$1"
  shift
  local out="$RUN_DIR/$id"
  mkdir -p "$out"
  printf '%s\n' "$@" > "$out/command.txt"
  set +e
  "$@" >"$out/stdout.txt" 2>"$out/stderr.txt" &
  local pid=$!
  echo "$pid" > "$ROOT/active.pid"
  echo "$pid" > "$out/pid.txt"
  wait "$pid"
  LAST_CODE=$?
  set -e
  rm -f "$ROOT/active.pid"
  echo "$LAST_CODE" > "$out/exitcode.txt"
  LAST_OUT="$out"
}

source_checks() {
  python3 - "$REPO" "$1" <<'PY'
import pathlib, sys
repo = pathlib.Path(sys.argv[1])
item = sys.argv[2]

def read(rel):
    return (repo / rel).read_text(encoding="utf-8")

def strip_line_comments(text):
    return "\n".join(line.split("//", 1)[0] for line in text.splitlines())

def fail(message):
    print(message)
    sys.exit(1)

if item == "awl-url-static":
    segmenter = strip_line_comments(read("Sources/voice-switch/Segmenter.swift"))
    start = segmenter.find("static func earlyWake")
    if start < 0:
        fail("missing Segmenter.earlyWake")
    rest = segmenter[start:]
    nxt = rest.find("\n    static func ", 1)
    if nxt < 0:
        nxt = rest.find("\n    mutating func ", 1)
    body = rest if nxt < 0 else rest[:nxt]
    if ".actions" not in body or "prefixGapFrames" not in body:
        fail("earlyWake body missing .actions or prefixGapFrames")
    run_py = read("tests/url-actions/run.py")
    if "not in early" in run_py:
        fail("url-actions still asserts actions are absent from earlyWake")
    print("url-static markers ok")
elif item == "awl-mac-early":
    mac = strip_line_comments(read("Sources/voice-switch/MacApp.swift"))
    marker = "cfg: config.cfg, tailSilentFrames"
    start = mac.find(marker)
    if start < 0:
        fail("missing listener earlyWake call")
    end = mac.find("guard let event else", start)
    if end < 0:
        fail("missing guard let event else after the early-wake block")
    block = mac[start:end]
    for token in ("actionWakeChime", "handoffBusy", "deafFrames", "action:", "action="):
        if token not in block:
            fail(f"early-wake block missing {token}")
    check = mac.find("func check(")
    if check < 0:
        fail("missing func check")
    load = mac.find("func loadSamples", check)
    check_body = mac[check:] if load < 0 else mac[check:load]
    if "action=" not in check_body:
        fail("check() does not name the action")
    print("mac early markers ok")
elif item == "awl-readme":
    en = read("README.md")
    ja = read("README_ja.md")
    if "early wake probes use only the default wake words" in en:
        fail("README.md still says early probes use only default wake words")
    if "early wake probes use the default wake words and each action" not in en:
        fail("README.md missing the action early-wake sentence")
    if "早期ウェイクの判定は既定のウェイクワードだけを使う。" in ja:
        fail("README_ja.md still says early probes use only default wake words")
    if "早期ウェイクの判定は既定のウェイクワードと各アクション" not in ja:
        fail("README_ja.md missing the action early-wake sentence")
    print("readme markers ok")
else:
    fail(f"unknown source item {item}")
PY
}

record() {
  local status="$1" id="$2" detail="${3:-}"
  if [ -n "$detail" ]; then
    echo "$status $id $detail"
    printf '%s %s %s\n' "$status" "$id" "$detail" >> "$RUN_DIR/items.txt"
  else
    echo "$status $id"
    printf '%s %s\n' "$status" "$id" >> "$RUN_DIR/items.txt"
  fi
  case "$status" in
    PASS) PASS_N=$((PASS_N + 1)) ;;
    SKIP) SKIP_N=$((SKIP_N + 1)) ;;
    FAIL) FAIL_N=$((FAIL_N + 1)) ;;
  esac
}

drive() {
  local only="${1:-}"
  local run="${VS_RUN_ID:-$(date +%Y%m%dT%H%M%S)}"
  RUN_DIR="$EVIDENCE/$run"
  mkdir -p "$RUN_DIR"
  git -C "$REPO" rev-parse HEAD > "$RUN_DIR/git-head.txt"
  printf '%s\n' "$RUN_DIR" > "$ROOT/last-evidence-path"
  : > "$RUN_DIR/items.txt"
  PASS_N=0
  SKIP_N=0
  FAIL_N=0

  local rows id kind needle
  local -a ids=() kinds=() needles=()
  while IFS=$'\t' read -r id kind needle; do
    if [ -n "$only" ] && [ "$id" != "$only" ]; then
      continue
    fi
    ids+=("$id")
    kinds+=("$kind")
    needles+=("$needle")
  done < <(item_rows)
  [ "${#ids[@]}" -gt 0 ] || die "no item named ${only:-<all>}"

  local want_full=0 want_dotnet=0 i
  for i in "${!ids[@]}"; do
    if [ "${kinds[$i]}" = "dotnet-pass" ]; then
      want_dotnet=1
    fi
    if [ "${ids[$i]}" = "awl-suite" ]; then
      want_full=1
    fi
  done
  if [ "$want_dotnet" = 1 ] && [ "${#ids[@]}" -gt 1 ]; then
    want_full=1
  fi

  local suite_out="" suite_code=0
  if [ "$want_full" = 1 ]; then
    run_logged suite dotnet run --project "$TESTS_PROJ" -c Release --no-restore
    suite_code=$LAST_CODE
    suite_out="$LAST_OUT/stdout.txt"
    local pass_lines fail_lines
    pass_lines="$(grep -c '^PASS ' "$suite_out" || true)"
    fail_lines="$(grep -c '^FAIL ' "$suite_out" || true)"
    if [ "$suite_code" -eq 0 ] && [ "$fail_lines" -eq 0 ] && [ "$pass_lines" -ge 243 ]; then
      record PASS awl-suite "pass_lines=$pass_lines"
    else
      record FAIL awl-suite "exit=$suite_code pass_lines=$pass_lines fail_lines=$fail_lines"
    fi
  fi

  for i in "${!ids[@]}"; do
    id="${ids[$i]}"
    kind="${kinds[$i]}"
    needle="${needles[$i]}"
    case "$kind" in
      dotnet-pass)
        local out_file="" code=0
        if [ "$want_full" = 1 ]; then
          out_file="$suite_out"
          code=$suite_code
          mkdir -p "$RUN_DIR/$id"
          printf '%s\n' "shared suite log $suite_out" > "$RUN_DIR/$id/command.txt"
          cp "$suite_out" "$RUN_DIR/$id/stdout.txt"
          echo "$code" > "$RUN_DIR/$id/exitcode.txt"
        else
          local only_text="${needle#PASS }"
          run_logged "$id" dotnet run --project "$TESTS_PROJ" -c Release --no-restore -- --only "$only_text"
          code=$LAST_CODE
          out_file="$LAST_OUT/stdout.txt"
        fi
        if grep -q -F -- "$needle" "$out_file"; then
          record PASS "$id"
        else
          record FAIL "$id" "missing needle"
        fi
        ;;
      builtin)
        case "$id" in
          awl-suite)
            [ "$want_full" = 1 ] || record FAIL awl-suite "full suite was not run"
            ;;
          awl-url-static)
            run_logged awl-url-static python3 "$REPO/tests/url-actions/run.py"
            code=$LAST_CODE
            local url_ok=0
            if [ "$code" -eq 0 ]; then
              url_ok=1
            elif [ "$code" -eq 1 ] && grep -q 'libncurses.so.6' "$LAST_OUT/stderr.txt"; then
              url_ok=1
            fi
            if [ "$url_ok" != 1 ]; then
              record FAIL awl-url-static "run.py exit $code"
            elif ! grep -q 'URL action static call-chain checks passed' "$LAST_OUT/stdout.txt" \
              || ! grep -q 'URL static mutation checks passed' "$LAST_OUT/stdout.txt"; then
              record FAIL awl-url-static "static checks did not pass"
            elif source_checks awl-url-static >"$RUN_DIR/awl-url-static/markers.txt" 2>&1; then
              record PASS awl-url-static
            else
              record FAIL awl-url-static "markers"
            fi
            ;;
          awl-mac-early|awl-readme)
            mkdir -p "$RUN_DIR/$id"
            printf '%s\n' "source check $id" > "$RUN_DIR/$id/command.txt"
            if source_checks "$id" >"$RUN_DIR/$id/stdout.txt" 2>"$RUN_DIR/$id/stderr.txt"; then
              echo 0 > "$RUN_DIR/$id/exitcode.txt"
              record PASS "$id"
            else
              echo 1 > "$RUN_DIR/$id/exitcode.txt"
              record FAIL "$id" "markers"
            fi
            ;;
          awl-swift-exec)
            mkdir -p "$RUN_DIR/awl-swift-exec"
            set +e
            swiftc --version >"$RUN_DIR/awl-swift-exec/stdout.txt" 2>"$RUN_DIR/awl-swift-exec/stderr.txt"
            code=$?
            set -e
            echo "$code" > "$RUN_DIR/awl-swift-exec/exitcode.txt"
            printf '%s\n' swiftc --version > "$RUN_DIR/awl-swift-exec/command.txt"
            if [ "$code" -eq 0 ]; then
              record SKIP awl-swift-exec "swiftc runs; Mac listener is not compiled here"
            else
              local why
              why="$(head -n 1 "$RUN_DIR/awl-swift-exec/stderr.txt" | tr '\t' ' ')"
              record SKIP awl-swift-exec "swiftc exit $code ${why}"
            fi
            ;;
          awl-parity)
            run_logged awl-parity python3 "$REPO/tests/parity/run_parity.py"
            code=$LAST_CODE
            if python3 - "$LAST_OUT/stdout.txt" "$LAST_OUT/stderr.txt" <<'PY'
import pathlib, re, sys
text = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace")
text += "\n" + pathlib.Path(sys.argv[2]).read_text(encoding="utf-8", errors="replace")
summary = re.search(r"SUMMARY pass=(\d+) allow=\d+ fail=(\d+)", text)
if not summary:
    print("missing SUMMARY")
    sys.exit(1)
passed, failed = int(summary.group(1)), int(summary.group(2))
known = (
    "FAIL Swift pure harness failed to compile:",
    "FAIL automatic transcript policy checks failed:",
)
bad = [line for line in text.splitlines() if line.startswith("FAIL ") and not line.startswith(known)]
print(f"pass={passed} fail={failed} unknown={len(bad)}")
if bad:
    print(bad[0])
sys.exit(0 if passed >= 126 and failed <= 2 and not bad else 1)
PY
            then
              record PASS awl-parity
            else
              record FAIL awl-parity "exit=$code"
            fi
            ;;
          *)
            record FAIL "$id" "no handler"
            ;;
        esac
        ;;
      *)
        record FAIL "$id" "unknown kind $kind"
        ;;
    esac
  done

  echo "items pass=$PASS_N skip=$SKIP_N fail=$FAIL_N"
  echo "evidence: $RUN_DIR"
  [ "$FAIL_N" -eq 0 ]
}

cleanup() {
  local pidfile="$ROOT/active.pid"
  if [ -f "$pidfile" ]; then
    local pid
    pid="$(cat "$pidfile")"
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
      local cmd
      cmd="$(ps -p "$pid" -o args= 2>/dev/null || true)"
      case "$cmd" in
        *VoiceSwitch.Windows.Tests*|*run_parity.py*|*url-actions/run.py*)
          kill "$pid" 2>/dev/null || true
          wait "$pid" 2>/dev/null || true
          ;;
      esac
    fi
    rm -f "$pidfile"
  fi
  echo "cleanup: evidence kept at $EVIDENCE"
}

cmd="${1:-}"
case "$cmd" in
  doctor) doctor ;;
  drive) drive "${2:-}" ;;
  cleanup) cleanup ;;
  *) usage ;;
esac
