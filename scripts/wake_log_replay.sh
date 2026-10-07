#!/usr/bin/env bash
# Track A wake log replay wrapper. Measurement only — restores DictationCore after H2b run.
# H4b: Core swap always restored via EXIT trap (even if build/run fails).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
CORE="dotnet/VoiceSwitch.Windows.Core/DictationCore.cs"
PROJ="dotnet/VoiceSwitch.WakeReplay/VoiceSwitch.WakeReplay.csproj"
H2B_SHA="${H2B_SHA:-3a6322a}"
export PATH="${HOME}/.dotnet:${PATH}"

CORE_BAK=""
RESTORE_NEEDED=0

restore_core() {
  if [[ "${RESTORE_NEEDED}" -eq 1 && -n "${CORE_BAK}" && -f "${CORE_BAK}" ]]; then
    mv "${CORE_BAK}" "$CORE"
    RESTORE_NEEDED=0
    CORE_BAK=""
    touch "$CORE"
    echo "Restored $CORE to tree version (trap)."
  fi
}
trap restore_core EXIT

# Prove: trap restores Core when the swapped-Core path fails mid-run.
# Manual proof: corrupt after swap → exit 1 → sha256(Core) matches pre-swap.
self_test_restore() {
  if [[ ! -f "$CORE" ]]; then
    echo "FAIL self-test-restore: missing $CORE" >&2
    return 1
  fi
  local sum_before sum_after
  sum_before=$(sha256sum "$CORE" | awk '{print $1}')
  local bak
  bak=$(mktemp)
  cp "$CORE" "$bak"
  # Nested bash so this function's caller is not aborted by set -e on intentional failure.
  if bash -c '
    set -euo pipefail
    CORE="$1"
    BAK="$2"
    restore() {
      if [[ -f "$BAK" ]]; then
        mv "$BAK" "$CORE"
        touch "$CORE"
      fi
    }
    trap restore EXIT
    printf "H4b-corrupt-marker\n" > "$CORE"
    # Intentional failure after swap — trap must still restore.
    exit 1
  ' _ "$CORE" "$bak"; then
    echo "FAIL self-test-restore: expected failure after swap" >&2
    git checkout -- "$CORE" 2>/dev/null || true
    return 1
  fi
  sum_after=$(sha256sum "$CORE" | awk '{print $1}')
  if [[ "$sum_before" != "$sum_after" ]]; then
    echo "FAIL self-test-restore: Core not restored (before=$sum_before after=$sum_after)" >&2
    git checkout -- "$CORE" 2>/dev/null || true
    return 1
  fi
  # Also require the production script declares an EXIT trap (static guard).
  if ! grep -q 'trap restore_core EXIT' "$ROOT/scripts/wake_log_replay.sh"; then
    echo "FAIL self-test-restore: scripts/wake_log_replay.sh missing trap restore_core EXIT" >&2
    return 1
  fi
  echo "OK wake_log_replay restore trap self-test"
  return 0
}

if [[ "${1:-}" == "--self-test-restore" ]]; then
  self_test_restore
  exit $?
fi

LOG="${1:-/workspace/voice-switch-overnight/voice-switch.log}"
OUT_DIR="${2:-/workspace/voice-switch-overnight}"

mkdir -p "$OUT_DIR"
dotnet restore "$PROJ" -v q
dotnet build "$PROJ" -c Release -v q

echo "=== main (tree HEAD $(git rev-parse --short HEAD)) ==="
dotnet run --project "$PROJ" -c Release --no-build -- \
  --log "$LOG" --label "main-$(git rev-parse --short HEAD)" \
  --json-out "$OUT_DIR/h4_replay_main.json" | tee "$OUT_DIR/h4_replay_main.txt"

if git cat-file -e "${H2B_SHA}^{commit}" 2>/dev/null; then
  echo "=== H2b (${H2B_SHA}) temporary Core swap ==="
  CORE_BAK=$(mktemp)
  cp "$CORE" "$CORE_BAK"
  RESTORE_NEEDED=1
  git show "${H2B_SHA}:dotnet/VoiceSwitch.Windows.Core/DictationCore.cs" > "$CORE"
  dotnet build "$PROJ" -c Release -v q
  dotnet run --project "$PROJ" -c Release --no-build -- \
    --log "$LOG" --label "h2b-${H2B_SHA}" \
    --json-out "$OUT_DIR/h4_replay_h2b.json" | tee "$OUT_DIR/h4_replay_h2b.txt"
  # Explicit restore before EXIT (trap is the safety net if anything above failed).
  restore_core
  dotnet build "$PROJ" -c Release -v q
  echo "Restored $CORE to tree version."
else
  echo "WARN: H2B_SHA=${H2B_SHA} not in this clone; skipped H2b run" >&2
fi
