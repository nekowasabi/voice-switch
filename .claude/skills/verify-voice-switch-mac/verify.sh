#!/bin/sh
# Drive the macOS voice-switch binary with synthetic speech through --simulate and keep the log as evidence.
set -eu

REPO=$(cd "$(dirname "$0")/../../.." && pwd)
ROOT=${VS_VERIFY_ROOT:-$HOME/Library/Caches/voice-switch-verify}
BUILD=$ROOT/build
BIN=$BUILD/release/voice-switch
FIX=$ROOT/fixtures
EVID=$ROOT/evidence
SKILL=$(cd "$(dirname "$0")" && pwd)

die() { echo "error: $*" >&2; exit 1; }

cmd_build() {
  mkdir -p "$ROOT"
  # A separate build path: `make` would also install and relaunch the user's VoiceSwitch.app.
  swift build -c release --package-path "$REPO" --build-path "$BUILD" 2>&1 | tail -n 3
  git -C "$REPO" rev-parse --short HEAD > "$BUILD/built-from.txt"
  git -C "$REPO" status --porcelain | wc -l | tr -d ' ' > "$BUILD/built-dirty.txt"
  echo "built $BIN"
}

cmd_doctor() {
  ok=0
  if [ -x "$BIN" ]; then echo "ok   binary $BIN"; else echo "FAIL binary missing: run build"; ok=1; fi
  if [ -f "$BUILD/built-from.txt" ]; then
    echo "info built-from $(cat "$BUILD/built-from.txt") dirty=$(cat "$BUILD/built-dirty.txt") | repo HEAD $(git -C "$REPO" rev-parse --short HEAD) dirty=$(git -C "$REPO" status --porcelain | wc -l | tr -d ' ')"
  fi
  if say -v '?' | grep -q '^Kyoko '; then echo "ok   say voice Kyoko"; else echo "FAIL Japanese voice Kyoko missing"; ok=1; fi
  if [ -x "$BIN" ]; then
    if "$BIN" --vad-selftest >/dev/null 2>&1; then echo "ok   --vad-selftest"; else echo "FAIL --vad-selftest"; ok=1; fi
  fi
  for p in "$EVID"/*/*/pid.txt; do
    [ -f "$p" ] || continue
    d=$(dirname "$p")
    [ -f "$d/exitcode.txt" ] || echo "warn unfinished run $d (pid $(cat "$p")); run cleanup"
  done
  exit $ok
}

# say <name> <text>: Kyoko speech. [[slnc 800]] inserts 800 ms of silence.
cmd_say() {
  [ $# -ge 2 ] || die "usage: say <name> <text>"
  mkdir -p "$FIX"
  name=$1; shift
  say -v Kyoko -o "$FIX/$name.aiff" "$*"
  echo "$FIX/$name.aiff"
}

# run <fixture> [config.json]
cmd_run() {
  [ $# -ge 1 ] || die "usage: run <fixture> [config.json]"
  fixture=$1
  wav=$FIX/$fixture.aiff
  [ -f "$wav" ] || die "no fixture $wav; create it with: say $fixture <text>"
  run=${VS_RUN_ID:-$(date +%Y%m%d-%H%M%S)}
  out=$EVID/$run/$fixture
  mkdir -p "$out"
  cp "${2:-$SKILL/config.safe.json}" "$out/config.json"
  # MARK lets a config's command prove its side effect inside the evidence folder.
  sed -i '' "s#__EVIDENCE__#$out#g" "$out/config.json"
  cp "$wav" "$out/input.aiff"
  VOICE_SWITCH_CONFIG=$out/config.json "$BIN" --simulate "$out/input.aiff" > "$out/stdout.txt" 2> "$out/stderr.txt" &
  pid=$!
  echo "$pid" > "$out/pid.txt"
  # --simulate exits by itself after 40 s; 90 s means it hung.
  i=0
  while kill -0 "$pid" 2>/dev/null && [ $i -lt 90 ]; do sleep 1; i=$((i + 1)); done
  if kill -0 "$pid" 2>/dev/null; then kill "$pid"; echo timeout > "$out/exitcode.txt"; else wait "$pid" && echo 0 > "$out/exitcode.txt" || echo $? > "$out/exitcode.txt"; fi
  echo "exit=$(cat "$out/exitcode.txt") evidence=$out"
  for m in "$out"/*.marker; do [ -f "$m" ] && basename "$m"; done
  grep -E 'heard|dictation|phase|fatal|command' "$out/stdout.txt" || true
}

cmd_cleanup() {
  for p in "$EVID"/*/*/pid.txt; do
    [ -f "$p" ] || continue
    d=$(dirname "$p"); pid=$(cat "$p")
    # Only a run that never finished, and only if that PID is still our binary.
    if [ ! -f "$d/exitcode.txt" ] && ps -p "$pid" -o command= 2>/dev/null | grep -q "$BIN --simulate"; then
      kill "$pid" && echo "killed $pid ($d)"
      echo killed > "$d/exitcode.txt"
    fi
  done
  rm -rf "$BUILD"
  echo "removed $BUILD; kept $EVID and $FIX"
}

[ $# -ge 1 ] || die "usage: verify.sh build|doctor|say|run|cleanup"
c=$1; shift
case $c in
  build) cmd_build ;;
  doctor) cmd_doctor ;;
  say) cmd_say "$@" ;;
  run) cmd_run "$@" ;;
  cleanup) cmd_cleanup ;;
  *) die "unknown command $c" ;;
esac
