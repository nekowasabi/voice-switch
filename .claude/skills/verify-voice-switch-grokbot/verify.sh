#!/usr/bin/env bash
# Prove the Windows URL a grokbot wake action would open, on Linux, without launching Grok Bot.
# See SKILL.md next to this file.
set -euo pipefail

export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

REPO="$(cd "$(dirname "$0")/../../.." && pwd)"
SKILL="$REPO/.claude/skills/verify-voice-switch-grokbot"
ROOT="${VS_GROKBOT_VERIFY_ROOT:-/tmp/voice-switch-grokbot-verify}"
BUILD="$ROOT/build"
EVIDENCE="$ROOT/evidence"
PROBE_DLL="$BUILD/url-probe.dll"
DOTNET="${DOTNET:-dotnet}"
probe() { "$DOTNET" exec "$PROBE_DLL" "$@"; }
TESTS="$REPO/dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj"
CORE="$REPO/dotnet/VoiceSwitch.Windows.Core/VoiceSwitch.Windows.Core.csproj"
WIN="$REPO/dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj"

die() { echo "verify-voice-switch-grokbot: $*" >&2; exit 1; }

evidence_dir() {
  local feature="$1"
  local run="${VS_RUN_ID:-$(date +%Y%m%dT%H%M%S)}"
  local out="$EVIDENCE/$run/$feature"
  mkdir -p "$out"
  echo "$out"
}

build() {
  mkdir -p "$BUILD"
  "$DOTNET" restore "$SKILL/url-probe/UrlProbe.csproj" >/dev/null
  "$DOTNET" publish "$SKILL/url-probe/UrlProbe.csproj" -c Release -o "$BUILD" --no-restore >/dev/null
  "$DOTNET" build "$CORE" -c Release --no-restore >/dev/null
  "$DOTNET" build "$TESTS" -c Release --no-restore >/dev/null
  git -C "$REPO" rev-parse HEAD > "$BUILD/built-from.txt"
  git -C "$REPO" status --porcelain --untracked-files=no | wc -l | sed 's/^/dirty-files=/' >> "$BUILD/built-from.txt"
  echo "built $PROBE_DLL ($(tr '\n' ' ' < "$BUILD/built-from.txt"))"
}

doctor() {
  local ok=1
  command -v "$DOTNET" >/dev/null || { echo "FAIL dotnet missing"; ok=0; }
  "$DOTNET" --version | grep -q '^8\.' || { echo "FAIL dotnet is not 8.x ($("$DOTNET" --version))"; ok=0; }
  [ -f "$PROBE_DLL" ] || { echo "FAIL probe missing: run '$0 build'"; ok=0; }
  if [ "$ok" = 1 ]; then
    local cfg
    cfg="$(mktemp)"
    cat > "$cfg" <<'JSON'
{"wakeWords":["音声入力"],"command":"wake","windows":{"actions":[{"name":"progress","wakeWords":["progress"],"url":"alter://action/26B00BAB-5655-4581-BCA0-F3880E746CC9"}]}}
JSON
    if probe load-config --config "$cfg" >/dev/null; then
      echo "ok probe $PROBE_DLL"
      echo "ok build $(tr '\n' ' ' < "$BUILD/built-from.txt")"
      echo "repo head $(git -C "$REPO" rev-parse HEAD)"
      if command -v swiftc >/dev/null; then echo "note swiftc=$(command -v swiftc)"
      else echo "note swiftc unavailable; Swift is source-grep only (Mac-unverified)"
      fi
    else
      echo "FAIL probe cannot load a windows.actions config"
      ok=0
    fi
    rm -f "$cfg"
  fi
  [ "$ok" = 1 ]
}

tests() {
  local only="${1:-}"
  if [ -n "$only" ]; then
    "$DOTNET" run --project "$TESTS" -c Release --no-restore -- --only "$only"
  else
    "$DOTNET" run --project "$TESTS" -c Release --no-restore
  fi
}

swift_mirror() {
  python3 "$SKILL/swift_mirror.py" "${1:-baseline}"
}

win_build() {
  "$DOTNET" build "$WIN" -c Release -r win-x64 --no-restore
}

drive_wake_action_url() {
  local out
  out="$(evidence_dir wake-action-url)"
  cat > "$out/config.json" <<'JSON'
{"wakeWords":["音声入力"],"command":"wake","windows":{"actions":[{"name":"progress","wakeWords":["progress","プログレス"],"url":"alter://action/26B00BAB-5655-4581-BCA0-F3880E746CC9"}]}}
JSON
  local want="alter://action/26B00BAB-5655-4581-BCA0-F3880E746CC9?input=hello%20world"
  set +e
  probe expect --config "$out/config.json" --name progress --text "hello world" --want "$want" > "$out/stdout.txt" 2> "$out/stderr.txt"
  local code=$?
  set -e
  echo "$code" > "$out/exitcode.txt"
  echo "$want" > "$out/want.txt"
  python3 "$SKILL/swift_mirror.py" baseline > "$out/swift-mirror.txt" 2>&1
  echo "exit=$code evidence=$out"
  cat "$out/stdout.txt"
  [ "$code" -eq 0 ] || die "wake-action-url URL mismatch (see $out)"
}

drive_grokbot_prompt_link() {
  local out
  out="$(evidence_dir grokbot-prompt-link)"
  cat > "$out/config.json" <<'JSON'
{"wakeWords":["音声入力"],"command":"wake","windows":{"actions":[{"name":"grokbot","wakeWords":["グロック","grok bot"],"url":"grokbot://app/v1/prompt?id=YOUR_BOT_ID","inputParam":"text"}]}}
JSON
  local want="grokbot://app/v1/prompt?id=YOUR_BOT_ID&text=hello%20world"
  set +e
  probe expect --config "$out/config.json" --name grokbot --text "hello world" --want "$want" > "$out/stdout.txt" 2> "$out/stderr.txt"
  local code=$?
  set -e
  echo "$code" > "$out/exitcode.txt"
  echo "$want" > "$out/want.txt"
  echo "exit=$code evidence=$out"
  cat "$out/stdout.txt"
  cat "$out/stderr.txt" >&2 || true
  [ "$code" -eq 0 ] || die "grokbot-prompt-link URL mismatch (see $out)"
}

drive_grokbot_text_limits() {
  local out
  out="$(evidence_dir grokbot-text-limits)"
  cat > "$out/config.json" <<'JSON'
{"wakeWords":["音声入力"],"command":"wake","windows":{"actions":[{"name":"grokbot","wakeWords":["グロック"],"url":"grokbot://app/v1/prompt?id=YOUR_BOT_ID","inputParam":"text"},{"name":"progress","wakeWords":["progress"],"url":"alter://action/26B00BAB-5655-4581-BCA0-F3880E746CC9"}]}}
JSON
  python3 - "$DOTNET" "$PROBE_DLL" "$out" <<'PY'
import pathlib, subprocess, sys
dotnet, dll, out = sys.argv[1], sys.argv[2], pathlib.Path(sys.argv[3])
cfg = out / "config.json"

def run(name, text):
    p = subprocess.run([dotnet, "exec", dll, "load", "--config", str(cfg), "--name", name, "--text", text],
                       capture_output=True, text=True)
    (out / "last-stderr.txt").write_text(p.stderr)
    if p.returncode != 0:
        raise SystemExit(f"probe failed: {p.stderr}")
    return p.stdout.strip()

def query_value(url, key):
    _, _, query = url.partition("?")
    query, _, _ = query.partition("#")
    for item in query.split("&"):
        name, _, value = item.partition("=")
        if name == key:
            return value
    return None

failures = []
url = run("grokbot", "hello\r\nworld\t\tnow\n")
value = query_value(url, "text")
if value != "hello%0Aworld%20now":
    failures.append(f"line breaks: {url}")
# 1000-char cap, no split of %E4%BB%8A (UTF-8 for 今) and no split of %XX.
long_text = "今" * 400
url = run("grokbot", long_text)
value = query_value(url, "text")
if value is None:
    failures.append(f"missing text= param: {url}")
    value = ""
if len(value) > 1000:
    failures.append(f"encoded longer than 1000: {len(value)}")
if value and len(value) % 9 != 0:
    failures.append(f"cut inside %E4%BB%8A: len={len(value)} url={url}")
i = 0
while i < len(value):
    if value[i] == "%":
        if i + 2 >= len(value) or any(c not in "0123456789ABCDEFabcdef" for c in value[i+1:i+3]):
            failures.append(f"split percent-escape at {i}: {value[max(0,i-4):i+4]}")
            break
        i += 3
    else:
        i += 1
# Cap is grokbot-only: the same 400 × 今 on a non-grokbot URL stays longer than 1000.
other = run("progress", long_text)
other_value = query_value(other, "input")
if other_value is None:
    failures.append(f"non-grokbot missing input=: {other}")
elif len(other_value) <= 1000:
    failures.append(f"non-grokbot was capped: encoded_len={len(other_value)} url={other}")
(out / "stdout.txt").write_text(
    url + "\n" + f"encoded_len={len(value)}\n" + other + "\n" + f"non_grokbot_encoded_len={len(other_value or '')}\n"
)
if failures:
    (out / "fail.txt").write_text("\n".join(failures) + "\n")
    print("FAIL " + " | ".join(failures), file=sys.stderr)
    raise SystemExit(1)
print(f"ok encoded_len={len(value)} non_grokbot_encoded_len={len(other_value)}")
PY
  echo "exit=0 evidence=$out"
}

drive_automatic_no_action_url() {
  local out
  out="$(evidence_dir automatic-no-action-url)"
  set +e
  "$DOTNET" run --project "$TESTS" -c Release --no-restore -- --only "automatic session never opens an action URL" > "$out/stdout.txt" 2> "$out/stderr.txt"
  local code=$?
  set -e
  echo "$code" > "$out/exitcode.txt"
  echo "exit=$code evidence=$out"
  grep -E '^(PASS|FAIL|SKIP) ' "$out/stdout.txt" "$out/stderr.txt" || true
  grep -q '^PASS automatic session never opens an action URL$' "$out/stdout.txt" || die "automatic-no-action-url test did not PASS (see $out)"
}

drive_grokbot_config_docs() {
  local out
  out="$(evidence_dir grokbot-config-docs)"
  local example="$REPO/config.example.grokbot.json"
  [ -f "$example" ] || die "missing $example"
  cp "$example" "$out/config.example.grokbot.json"
  set +e
  probe load-config --config "$example" > "$out/stdout.txt" 2> "$out/stderr.txt"
  local code=$?
  set -e
  echo "$code" > "$out/exitcode.txt"
  echo "exit=$code evidence=$out"
  cat "$out/stdout.txt"
  [ "$code" -eq 0 ] || die "config.example.grokbot.json did not load (see $out)"
  grep -q 'name=grokbot' "$out/stdout.txt" || die "example did not list a grokbot action"
  python3 - "$example" <<'PY'
import json, sys
from pathlib import Path
cfg = json.loads(Path(sys.argv[1]).read_text())

def check(block, label):
    actions = (block or {}).get("actions") or []
    grok = next((a for a in actions if a.get("name") == "grokbot"), None)
    if grok is None:
        raise SystemExit(f"{label} missing name=grokbot")
    if grok.get("inputParam") != "text":
        raise SystemExit(f'{label} missing "inputParam": "text"')
    url = grok.get("url") or ""
    if "YOUR_BOT_ID" not in url:
        raise SystemExit(f"{label} URL missing YOUR_BOT_ID")
    if "grokbot://app/v1/prompt" not in url:
        raise SystemExit(f"{label} URL is not grokbot://app/v1/prompt")

check(cfg.get("macOS"), "macOS.actions")
check(cfg.get("windows"), "windows.actions")
print("ok both action blocks")
PY
  [ -f "$REPO/docs/grokbot-message.md" ] || die "missing docs/grokbot-message.md"
  grep -q 'documented, not live-tested' "$REPO/docs/grokbot-message.md" || die "docs/grokbot-message.md missing documented, not live-tested"
  grep -q 'Grok Bot' "$REPO/README.md" || die "README.md missing Grok Bot"
  grep -q 'Grok Bot' "$REPO/README_ja.md" || die "README_ja.md missing Grok Bot"
  grep -q 'invoke an action URL' "$REPO/docs/wake-acceptance.md" || die "wake-acceptance.md lost the action-URL safety gate"
}

drive() {
  local feature="${1:?feature file stem, e.g. wake-action-url}"
  [ -f "$PROBE_DLL" ] || die "probe missing: run '$0 build'"
  case "$feature" in
    wake-action-url) drive_wake_action_url ;;
    grokbot-prompt-link) drive_grokbot_prompt_link ;;
    grokbot-text-limits) drive_grokbot_text_limits ;;
    automatic-no-action-url) drive_automatic_no_action_url ;;
    grokbot-config-docs) drive_grokbot_config_docs ;;
    *) die "unknown feature $feature" ;;
  esac
}

cleanup() {
  if [ -d "$BUILD" ]; then
    rm -rf "$BUILD"
    echo "removed $BUILD"
  else
    echo "no build to remove"
  fi
  echo "kept evidence under $EVIDENCE"
}

usage() {
  echo "usage: $0 {build|doctor|drive <feature>|tests [only]|swift-mirror [baseline|grokbot]|win-build|cleanup}"
}

cmd="${1:-}"
shift || true
case "$cmd" in
  build) build ;;
  doctor) doctor ;;
  drive) drive "${1:?feature}" ;;
  tests) tests "${1:-}" ;;
  swift-mirror) swift_mirror "${1:-baseline}" ;;
  win-build) win_build ;;
  cleanup) cleanup ;;
  -h|--help|"") usage; [ -n "$cmd" ] ;;
  *) usage; die "unknown command $cmd" ;;
esac
