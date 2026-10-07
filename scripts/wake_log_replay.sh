#!/usr/bin/env bash
# Track A wake log replay wrapper. Measurement only — restores DictationCore after H2b run.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
LOG="${1:-/workspace/voice-switch-overnight/voice-switch.log}"
OUT_DIR="${2:-/workspace/voice-switch-overnight}"
H2B_SHA="${H2B_SHA:-3a6322a}"
CORE="dotnet/VoiceSwitch.Windows.Core/DictationCore.cs"
PROJ="dotnet/VoiceSwitch.WakeReplay/VoiceSwitch.WakeReplay.csproj"
export PATH="${HOME}/.dotnet:${PATH}"

mkdir -p "$OUT_DIR"
dotnet restore "$PROJ" -v q
dotnet build "$PROJ" -c Release -v q

echo "=== main (tree HEAD $(git rev-parse --short HEAD)) ==="
dotnet run --project "$PROJ" -c Release --no-build -- \
  --log "$LOG" --label "main-$(git rev-parse --short HEAD)" \
  --json-out "$OUT_DIR/h4_replay_main.json" | tee "$OUT_DIR/h4_replay_main.txt"

if git cat-file -e "${H2B_SHA}^{commit}" 2>/dev/null; then
  echo "=== H2b (${H2B_SHA}) temporary Core swap ==="
  cp "$CORE" /tmp/DictationCore.main.h4.bak
  git show "${H2B_SHA}:dotnet/VoiceSwitch.Windows.Core/DictationCore.cs" > "$CORE"
  dotnet build "$PROJ" -c Release -v q
  dotnet run --project "$PROJ" -c Release --no-build -- \
    --log "$LOG" --label "h2b-${H2B_SHA}" \
    --json-out "$OUT_DIR/h4_replay_h2b.json" | tee "$OUT_DIR/h4_replay_h2b.txt"
  mv /tmp/DictationCore.main.h4.bak "$CORE"
  touch "$CORE"
  dotnet build "$PROJ" -c Release -v q
  echo "Restored $CORE to tree version."
else
  echo "WARN: H2B_SHA=${H2B_SHA} not in this clone; skipped H2b run" >&2
fi
