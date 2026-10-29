#!/usr/bin/env bash
# Drive the Linux wake-recognition surrogates. No microphone, no Windows desktop, no Mac build.
set -euo pipefail

export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${PATH}"
export DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

REPO="$(cd "$(dirname "$0")/../../.." && pwd)"
SKILL="$(cd "$(dirname "$0")" && pwd)"
ROOT="${VS_WAKE_VERIFY_ROOT:-/tmp/voice-switch-wake-verify}"
EVIDENCE="$ROOT/evidence"
BUILD="$ROOT/build"
CORPUS_DLL="$BUILD/wake-corpus.dll"
REPLAY_PROJ="$REPO/dotnet/VoiceSwitch.WakeReplay/VoiceSwitch.WakeReplay.csproj"
REPLAY_DLL="$REPO/dotnet/VoiceSwitch.WakeReplay/bin/Release/net8.0/VoiceSwitch.WakeReplay.dll"
TESTS_PROJ="$REPO/dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj"
FIXTURE="$REPO/tests/fixtures/wake_log_replay_sample.log"
CORPUS="$SKILL/corpus/corpus.json"

die() { echo "verify-voice-switch-wake: $*" >&2; exit 1; }

LAST_OUT=""

need_lines() {
  local file="$1"
  shift
  local missing=0
  local needle
  for needle in "$@"; do
    if ! grep -q -F -- "$needle" "$file"; then
      echo "FAIL missing line: $needle" >&2
      missing=1
    fi
  done
  [ "$missing" -eq 0 ]
}

rule_line() {
  local file="$1"
  local label="$2"
  if ! grep -Eq "^rule=${label} hits=[0-9]+ misses=[0-9]+ false_wakes=0 rejects=[0-9]+ rows=47$" "$file"; then
    echo "FAIL missing ${label} summary with false_wakes=0 and rows=47" >&2
    return 1
  fi
}

capture() {
  local feature="$1"
  shift
  local run="${VS_RUN_ID:-$(date +%Y%m%dT%H%M%S)}"
  local out="$EVIDENCE/$run/$feature"
  mkdir -p "$out"
  git -C "$REPO" rev-parse HEAD > "$out/git-head.txt"
  printf '%s\n' "$@" > "$out/command.txt"
  set +e
  "$@" >"$out/stdout.txt" 2>"$out/stderr.txt" &
  local pid=$!
  echo "$pid" > "$ROOT/active.pid"
  echo "$pid" > "$out/pid.txt"
  wait "$pid"
  local code=$?
  set -e
  rm -f "$ROOT/active.pid"
  echo "$code" > "$out/exitcode.txt"
  LAST_OUT="$out"
  printf '%s\n' "$out" > "$ROOT/last-evidence-path"
  echo "exit: $code"
  echo "evidence: $out"
  return "$code"
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
    "$CORPUS" \
    "$SKILL/corpus/Program.cs" \
    "$SKILL/corpus/WakeCorpus.csproj" \
    "$REPLAY_PROJ" \
    "$FIXTURE" \
    "$REPO/scripts/wake_acceptance.py" \
    "$REPO/dotnet/VoiceSwitch.Windows.Core/TextMatching.cs" \
    "$REPO/dotnet/VoiceSwitch.Windows.Core/DictationCore.cs" \
    "$REPO/dotnet/VoiceSwitch.Windows.Core/NoWakePolicy.cs" \
    "$SKILL/fixtures/pass-manifest.json"
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
    if command -v swiftc >/dev/null; then
      echo "note swiftc=$(command -v swiftc)"
    else
      echo "note swiftc unavailable; Mac wake behavior is unverified"
    fi
    echo "doctor: ok"
  fi
  [ "$ok" = 1 ]
}

build() {
  mkdir -p "$BUILD" "$EVIDENCE"
  # Rebuild every time. An up-to-date check trusts timestamps and will run a
  # leftover DLL whose sources were replaced by an older checkout.
  dotnet build "$SKILL/corpus/WakeCorpus.csproj" -c Release -t:Rebuild -o "$BUILD" --ignore-failed-sources >"$EVIDENCE/build-corpus.log"
  dotnet build "$REPLAY_PROJ" -c Release -t:Rebuild --ignore-failed-sources >"$EVIDENCE/build-replay.log"
  need_lines "$EVIDENCE/build-corpus.log" "0 Error(s)"
  need_lines "$EVIDENCE/build-replay.log" "0 Error(s)"
  [ -f "$CORPUS_DLL" ] || die "missing $CORPUS_DLL"
  [ -f "$REPLAY_DLL" ] || die "missing $REPLAY_DLL"
  echo "built $CORPUS_DLL"
  echo "built $REPLAY_DLL"
}

drive() {
  local feature="${1:-}"
  [ -n "$feature" ] || die "usage: $0 drive <reading-match|fixture-replay|near-miss|acceptance|safety>"
  build
  case "$feature" in
    reading-match)
      capture reading-match dotnet exec "$CORPUS_DLL" --corpus "$CORPUS"
      need_lines "$LAST_OUT/stdout.txt" \
        "corpus_sha256=97fbaf6f615971d59eb0b509560c4939027d7b813a24f9eaf46765bacc8da593" \
        "rows=47" \
        "UNVERIFIED real-audio mac windows-hardware" \
        "floor configured hits>=15 misses<=8 false_wakes=0" \
        "floor surface hits>=7 misses<=16 false_wakes=0"
      rule_line "$LAST_OUT/stdout.txt" configured
      rule_line "$LAST_OUT/stdout.txt" surface
      ;;
    fixture-replay)
      # --self-test also runs scripts/wake_log_replay.sh --self-test-restore, which
      # writes DictationCore.cs and restores it on EXIT. One fixture-replay at a time.
      capture fixture-replay dotnet exec "$REPLAY_DLL" --self-test
      need_lines "$LAST_OUT/stdout.txt" "OK wake_log_replay self-test" "in_band=False"
      ;;
    near-miss)
      capture near-miss dotnet exec "$REPLAY_DLL" --log "$FIXTURE" --label verify-near-miss
      # Whole fields, not prefixes. "gained=1" is also inside "gained=10".."gained=19".
      need_lines "$LAST_OUT/stdout.txt" \
        "agreed_true=2 agreed_false=7 gained=1 lost=0" \
        "near_miss_examples_long_wake_d1_2_misses_whole (shown 0, wake.Reading.Length>=10, !hit, whole-utterance):" \
        "near_miss_examples_short_wake_d1_misses_whole (shown 0, wake.Reading.Length<10, !hit, whole-utterance, d=1):" \
        "near_miss_examples_long_wake_d3_misses_whole (shown 1, wake.Reading.Length>=10, !hit, whole-utterance, d=3):" \
        "d3_near_miss_intentional=1 d3_near_miss_ambient=0" \
        "text=\"法政入力\" reading=\"ほうぜいにゅうりょく\" wake=おんせいにゅうりょく d=3 intentional=True"
      ;;
    acceptance)
      capture acceptance python3 "$REPO/scripts/wake_acceptance.py" "$SKILL/fixtures/pass-manifest.json"
      need_lines "$LAST_OUT/stdout.txt" '"verdict": "pass"'
      ;;
    safety)
      local name slug
      for name in \
        "automatic session never opens an action URL" \
        "manual paste requires every safety observation" \
        "automatic delivery tries Superwhisper first and keeps only history; wake delivery skips history"
      do
        slug="$(printf '%s' "$name" | tr ' ;' '--')"
        capture "safety-$slug" dotnet run --project "$TESTS_PROJ" -c Release --no-restore -- --only "$name"
        if ! grep -qxF -- "PASS $name" "$LAST_OUT/stdout.txt"; then
          echo "FAIL missing exact PASS line: $name" >&2
          exit 1
        fi
      done
      ;;
    *)
      die "unknown feature $feature"
      ;;
  esac
}

cleanup() {
  if [ -f "$ROOT/active.pid" ]; then
    local pid
    pid="$(cat "$ROOT/active.pid")"
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
      local cmd=""
      if [ -r "/proc/$pid/cmdline" ]; then
        cmd="$(tr '\0' ' ' < "/proc/$pid/cmdline")"
      fi
      case "$cmd" in
        *wake-corpus*|*VoiceSwitch.WakeReplay*|*VoiceSwitch.Windows.Tests*)
          kill "$pid" 2>/dev/null || true
          wait "$pid" 2>/dev/null || true
          ;;
        *)
          echo "cleanup left pid $pid alone"
          ;;
      esac
    fi
    rm -f "$ROOT/active.pid"
  fi
  rm -rf "$BUILD"
  echo "cleanup removed $BUILD"
  echo "evidence kept at $EVIDENCE"
}

case "${1:-}" in
  doctor) doctor ;;
  build) build ;;
  drive) shift; drive "$@" ;;
  cleanup) cleanup ;;
  *)
    die "usage: $0 doctor|build|drive <feature>|cleanup"
    ;;
esac
