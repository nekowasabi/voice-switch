#!/usr/bin/env bash
# Drives the Windows voice-switch.exe from WSL with synthetic WAV input. See SKILL.md next to this file.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/../../.." && pwd)"
SKILL="$REPO/.claude/skills/verify-voice-switch"
ROOT="${VS_VERIFY_ROOT:-/mnt/c/temp/voice-switch-verify}"
BUILD="$ROOT/build"
FIXTURES="$ROOT/fixtures"
EVIDENCE="$ROOT/evidence"
PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
DOTNET="${DOTNET:-dotnet}"

win() { wslpath -w "$1"; }
die() { echo "verify-voice-switch: $*" >&2; exit 1; }

build() {
  mkdir -p "$BUILD"
  "$DOTNET" publish "$REPO/dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj" -c Release -r win-x64 \
    --self-contained false -p:EnableWindowsTargeting=true -p:PublishSingleFile=false -p:DebugType=None \
    -p:CopyOutputSymbolsToPublishDirectory=false -o "$BUILD" >/dev/null
  # The verification config lives next to the exe so it never reads the user's installed config.json.
  cp "$REPO/config.example.windows.json" "$BUILD/config.json"
  git -C "$REPO" rev-parse --short HEAD > "$BUILD/built-from.txt"
  git -C "$REPO" status --porcelain --untracked-files=no | wc -l | sed 's/^/dirty-files=/' >> "$BUILD/built-from.txt"
  echo "built $BUILD/voice-switch.exe ($(tr '\n' ' ' < "$BUILD/built-from.txt"))"
}

fixtures() {
  mkdir -p "$FIXTURES"
  if [ -f "$FIXTURES/wake-body-separate-stop-clean.wav" ]; then echo "fixtures present in $FIXTURES"
  else
    "$PS" -NoProfile -ExecutionPolicy Bypass -File "$(win "$REPO/tests/windows/compose-fixtures.ps1")" -OutputDir "$(win "$FIXTURES")" >/dev/null
    ls "$FIXTURES"/*.wav | sed 's|.*/||'
  fi
  # Automatic-recording gates (features/automatic-gates.md): speech without a wake word, the same plus the stop word, and noise.
  if [ ! -f "$FIXTURES/auto-stop.wav" ]; then
    "$PS" -NoProfile -ExecutionPolicy Bypass -File "$(win "$REPO/scripts/windows-say.ps1")" -Text "今日の会議は三時から始めます。資料は共有フォルダに置きました。" -Output "$(win "$FIXTURES")\\say-body.wav" >/dev/null
    "$PS" -NoProfile -ExecutionPolicy Bypass -File "$(win "$REPO/scripts/windows-say.ps1")" -Text "入力ストップ" -Output "$(win "$FIXTURES")\\say-stop.wav" >/dev/null
    python3 "$SKILL/make-auto-fixtures.py" "$REPO" "$FIXTURES"
  fi
  sed 's/"autoIdle": false/"autoIdle": true/' "$REPO/config.example.windows.json" > "$FIXTURES/auto.json"
  grep -q '"autoIdle": true' "$FIXTURES/auto.json" || die "config.example.windows.json no longer has \"autoIdle\": false; fix auto.json"
}

doctor() {
  local ok=1
  [ -x "$BUILD/voice-switch.exe" ] || { echo "FAIL exe missing: run '$0 build'"; ok=0; }
  [ -f "$BUILD/config.json" ] || { echo "FAIL verification config missing in $BUILD"; ok=0; }
  ls "/mnt/c/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App" | grep -q '^8\.' || { echo "FAIL Windows Desktop runtime 8.x not installed"; ok=0; }
  [ -f "$FIXTURES/wake-body-separate-stop-clean.wav" ] || { echo "FAIL fixtures missing: run '$0 fixtures'"; ok=0; }
  if [ "$ok" = 1 ]; then
    (cd "$BUILD" && ./voice-switch.exe --self-test >/dev/null 2>&1) && echo "ok self-test" || { echo "FAIL --self-test"; ok=0; }
    echo "ok build $(tr '\n' ' ' < "$BUILD/built-from.txt")"
    echo "repo head $(git -C "$REPO" rev-parse --short HEAD)"
  fi
  [ "$ok" = 1 ]
}

wav() {
  local fixture="${1:?fixture name, e.g. wake-body-separate-stop-clean}" config="${2:-$BUILD/config.json}"
  local wavfile="$FIXTURES/$fixture.wav"
  [ -f "$wavfile" ] || die "no fixture $wavfile"
  local run="${VS_RUN_ID:-$(date +%Y%m%dT%H%M%S)}"
  local out="$EVIDENCE/$run/$fixture"
  mkdir -p "$out"
  cp "$config" "$out/config.json"
  set +e
  "$PS" -NoProfile -ExecutionPolicy Bypass -File "$(win "$SKILL/run-wav.ps1")" -Exe "$(win "$BUILD/voice-switch.exe")" \
    -Config "$(win "$out/config.json")" -Wav "$(win "$wavfile")" -OutDir "$(win "$out")"
  local code=$?
  set -e
  local count=0
  [ -d "$out/handoffs" ] && count=$(find "$out/handoffs" -name '*.wav' | wc -l)
  echo "exit=$code handoff_wavs=$count evidence=$out"
  grep -E "dictation (session|handoff|cancelled)|dictation recognition: complete .*(leadingWake=True|standaloneStop=True)" "$out/stdout.txt" | sed 's/ text=.*grammar=/ grammar=/' || true
}

phrases() {
  "$PS" -NoProfile -ExecutionPolicy Bypass -File "$(win "$SKILL/make-phrase-cases.ps1")" -Spec "$(win "$SKILL/phrase-cases.json")" -OutDir "$(win "$ROOT/recall/tts")"
}

# KPI: phrases missing from Superwhisper rawResult. Exit 0 only when none is missing.
recall() {
  local cases="${1:-$ROOT/recall/tts/cases.json}" pads="${2:-0}"
  local out="$ROOT/recall/${VS_RUN_ID:-$(date +%Y%m%dT%H%M%S)}"
  "$PS" -NoProfile -ExecutionPolicy Bypass -Command "& '$(win "$SKILL/superwhisper-recall.ps1")' -Cases '$(win "$cases")' -OutDir '$(win "$out")' -PadMs $pads"
}

cleanup() {
  for pidfile in "$EVIDENCE"/*/*/pid.txt; do
    [ -f "$pidfile" ] || continue
    # A run with an exit code already ended; its PID may now belong to the user's own voice-switch.
    [ -f "$(dirname "$pidfile")/exitcode.txt" ] && continue
    local pid; pid=$(tr -d '\r\n' < "$pidfile")
    # Kill only a process this skill started, and only while it is still voice-switch.
    if "$PS" -NoProfile -Command "\$p = Get-Process -Id $pid -ErrorAction SilentlyContinue; if (\$p -and \$p.ProcessName -eq 'voice-switch') { Stop-Process -Id $pid -Force; 'killed $pid' }" | grep -q killed; then
      echo "killed leftover pid $pid"
    fi
  done
  rm -rf "$BUILD"
  echo "removed $BUILD; kept evidence in $EVIDENCE and fixtures in $FIXTURES"
}

case "${1:-}" in
  build) build ;;
  fixtures) fixtures ;;
  doctor) doctor ;;
  wav) shift; wav "$@" ;;
  phrases) phrases ;;
  recall) shift; recall "$@" ;;
  cleanup) cleanup ;;
  *) echo "usage: $0 build | fixtures | doctor | wav <fixture> [config.json] | phrases | recall [cases.json] [padMs,...] | cleanup" >&2; exit 2 ;;
esac
