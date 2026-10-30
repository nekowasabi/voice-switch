#!/usr/bin/env bash
# Drive the dictation-journal checklist. See SKILL.md in this directory.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SKILL_DIR="$(cd "$(dirname "$0")" && pwd)"
BASE="${JOURNAL_VERIFY_BASE:-/tmp/voice-switch-journal-verify}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
if [[ -x "$DOTNET_ROOT/dotnet" ]]; then
  export PATH="$DOTNET_ROOT:$PATH"
fi

die() { printf 'verify: fail: %s\n' "$1" >&2; exit 1; }

check_item_map() {
  local items="$SKILL_DIR/items.md"
  local id file base
  local -A known=() listed=()
  while IFS= read -r id; do
    [[ -n "$id" ]] || continue
    known["$id"]=1
  done < <(sed -n 's/^- id: \([a-z0-9-]*\)$/\1/p' "$items")
  [[ ${#known[@]} -gt 0 ]] || die "items.md listed no ids"
  for file in "$SKILL_DIR"/features/*.md; do
    base="$(basename "$file" .md)"
    [[ "$base" == "README" ]] && continue
    while IFS= read -r id; do
      [[ -n "$id" ]] || continue
      [[ -n "${known[$id]+x}" ]] || die "feature ${base} lists ${id} which is not in items.md"
      listed["$id"]=1
    done < <(feature_ids "$base")
  done
  for id in "${!known[@]}"; do
    [[ -n "${listed[$id]+x}" ]] || die "items.md id ${id} is not in any feature file"
  done
  local expected actual
  expected=$'## Sub-features\n## How to get to it (user POV)\n## Driving it with verify-journal\n## Gotchas'
  for file in "$SKILL_DIR"/features/*.md; do
    base="$(basename "$file")"
    [[ "$base" == "README.md" ]] && continue
    actual="$(grep '^## ' "$file")"
    [[ "$actual" == "$expected" ]] || die "feature ${base} H2 headings drifted"
  done
}

doctor() {
  check_item_map
  command -v dotnet >/dev/null 2>&1 || die "dotnet not on PATH and not at $DOTNET_ROOT"
  dotnet --list-sdks | grep -E '^8\.' >/dev/null || die "no .NET 8 SDK (dotnet --list-sdks)"
  [[ -f "$ROOT/Sources/voice-switch/WakeSafety.swift" ]] || die "missing WakeSafety.swift"
  grep -q 'class DictationHistory' "$ROOT/Sources/voice-switch/WakeSafety.swift" || die "DictationHistory missing from WakeSafety.swift"
  [[ -f "$ROOT/dotnet/VoiceSwitch.Windows.Core/DictationHistory.cs" ]] || die "missing DictationHistory.cs"
  grep -q 'class DictationHistory' "$ROOT/dotnet/VoiceSwitch.Windows.Core/DictationHistory.cs" || die "DictationHistory missing from DictationHistory.cs"
  [[ -f "$ROOT/config.example.json" && -f "$ROOT/config.example.windows.json" ]] || die "missing config examples"
  [[ -x "$SKILL_DIR/verify.sh" ]] || die "verify.sh is not executable"
  printf 'doctor: ready\n'
  printf 'doctor: root %s\n' "$ROOT"
  printf 'doctor: dotnet %s\n' "$(dotnet --version)"
  if command -v swiftc >/dev/null 2>&1; then
    printf 'doctor: swiftc %s\n' "$(command -v swiftc)"
  else
    printf 'doctor: swiftc absent (swift-harness will SKIP, Mac 未検証)\n'
  fi
  if [[ "$(uname -s)" == "Linux" ]]; then
    printf 'doctor: host linux (windows-acl will SKIP, Windows ACL 未検証)\n'
  fi
}

cleanup() {
  local stamp pid
  if [[ -d "$BASE/evidence" ]]; then
    for pidfile in "$BASE"/evidence/*/pid.txt; do
      [[ -f "$pidfile" ]] || continue
      pid="$(tr -cd '0-9' <"$pidfile" || true)"
      if [[ -n "$pid" ]] && [[ -d "/proc/$pid" ]]; then
        # Only the harness we started, and only if it is still dotnet.
        if tr '\0' ' ' <"/proc/$pid/cmdline" | grep -q 'JournalTests'; then
          kill "$pid" 2>/dev/null || true
        fi
      fi
    done
  fi
  rm -rf "$BASE/scratch"
  printf 'cleanup: removed %s\n' "$BASE/scratch"
  printf 'cleanup: evidence kept at %s\n' "$BASE/evidence"
}

feature_ids() {
  local feature="$1"
  local file="$SKILL_DIR/features/${feature}.md"
  [[ -f "$file" ]] || die "unknown feature ${feature}"
  # Ids are the backticked tokens in the Sub-features list.
  awk '/^## Sub-features$/{f=1;next} /^## /{f=0} f && /`- `/{next} f' "$file" \
    | sed -n 's/.*`\([a-z0-9-]*\)`.*/\1/p'
}

drive() {
  local feature="${1:-}"
  local ids=""
  if [[ -n "$feature" ]]; then
    ids="$(feature_ids "$feature" | paste -sd, -)"
    [[ -n "$ids" ]] || die "feature ${feature} listed no ids"
  fi
  [[ -f "$ROOT/tests/journal/JournalTests.csproj" ]] || die "missing tests/journal/JournalTests.csproj (journal harness not built yet)"
  local stamp scratch evidence
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  scratch="$BASE/scratch/$stamp"
  evidence="$BASE/evidence/$stamp"
  mkdir -p "$scratch" "$evidence"
  printf '%s\n' "$ids" >"$evidence/items.txt"
  local cmd=(dotnet run --project "$ROOT/tests/journal/JournalTests.csproj" -c Release -- "$ROOT" "$scratch")
  if [[ -n "$ids" ]]; then
    cmd+=("$ids")
  fi
  printf '%q ' "${cmd[@]}" >"$evidence/command.txt"
  printf '\n' >>"$evidence/command.txt"
  set +e
  JOURNAL_VERIFY_FEATURE="$feature" "${cmd[@]}" >"$evidence/stdout.txt" 2>"$evidence/stderr.txt" &
  local pid=$!
  printf '%s\n' "$pid" >"$evidence/pid.txt"
  wait "$pid"
  local code=$?
  set -e
  printf '%s\n' "$code" >"$evidence/exitcode.txt"
  rm -f "$evidence/pid.txt"
  cat "$evidence/stdout.txt"
  if [[ -s "$evidence/stderr.txt" ]]; then
    printf '--- stderr ---\n' >&2
    cat "$evidence/stderr.txt" >&2
  fi
  printf 'evidence: %s\n' "$evidence"
  require_passes "$evidence" "$feature" "$code"
}

# Every requested id must print PASS. The two host skips are the only exception.
require_passes() {
  local evidence="$1"
  local feature="$2"
  local code="$3"
  local id
  local -a want=() missing=()
  if [[ -n "$feature" ]]; then
    while IFS= read -r id; do
      [[ -n "$id" ]] || continue
      want+=("$id")
    done < <(feature_ids "$feature")
  else
    while IFS= read -r id; do
      [[ -n "$id" ]] || continue
      want+=("$id")
    done < <(sed -n 's/^- id: \([a-z0-9-]*\)$/\1/p' "$SKILL_DIR/items.md")
  fi
  for id in "${want[@]}"; do
    if grep -qx "PASS ${id}" "$evidence/stdout.txt"; then
      continue
    fi
    if [[ "$id" == "swift-harness" || "$id" == "windows-acl" ]] && grep -qx "SKIP ${id}" "$evidence/stdout.txt"; then
      continue
    fi
    missing+=("$id")
  done
  if [[ ${#missing[@]} -gt 0 ]]; then
    printf 'verify: fail: missing PASS for %s\n' "${missing[*]}" >&2
  fi
  if [[ "$code" -ne 0 || ${#missing[@]} -gt 0 ]]; then
    exit 1
  fi
}

cmd="${1:-doctor}"
case "$cmd" in
  doctor) doctor ;;
  drive) drive "${2:-}" ;;
  cleanup) cleanup ;;
  *) die "usage: verify.sh doctor|drive [feature]|cleanup" ;;
esac
