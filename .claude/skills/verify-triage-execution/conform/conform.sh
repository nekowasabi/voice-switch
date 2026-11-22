#!/bin/sh
# Exhaustive conformance of the Swift reducer (Sources/voice-switch/TriageExecution.swift) to TriageExec.tla.
# Usage: conform.sh gen                      rewrite transitions.json from the model (after any model or design.cfg change)
#        conform.sh check [Reducer.swift]    feed every model transition and every refused event to the reducer
#        conform.sh mutants                  each mutant breaks the reducer in one place; check must FAIL
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
skill=$(dirname "$dir")
repo=$(cd "$skill/../../.." && pwd)
jar="$HOME/.cache/tla2tools-v1.7.4/tla2tools.jar"
fixture="$dir/transitions.json"
work="${TMPDIR:-/tmp}/verify-triage-execution/conform-$$"
mkdir -p "$work"
trap 'rm -rf "$work"' EXIT

stale() {
  python3 - "$fixture" "$skill/TriageExec.tla" "$skill/design.cfg" <<'EOF'
import hashlib, json, sys
doc = json.load(open(sys.argv[1]))
model = hashlib.sha256(open(sys.argv[2], "rb").read()).hexdigest()
cfg = hashlib.sha256(open(sys.argv[3], "rb").read()).hexdigest()
if (model, cfg) != (doc["model_sha256"], doc["config_sha256"]):
    sys.exit("FAIL fixture is stale: TriageExec.tla or design.cfg changed since gen; run conform.sh gen")
EOF
}

check() {
  src=${1:-$repo/Sources/voice-switch/TriageExecution.swift}
  stale
  cp "$dir/main.swift" "$work/main.swift"
  swiftc -O -o "$work/conform" "$work/main.swift" "$src" 2> "$work/build.txt" || { cat "$work/build.txt"; echo "ERROR build"; return 2; }
  "$work/conform" "$fixture"
}

case ${1:?usage: conform.sh gen|check|mutants} in
gen)
  [ -f "$jar" ] || sh "$skill/check.sh" design > /dev/null  # check.sh fetches and pins the jar
  python3 "$dir/gen.py" "$jar" "$fixture" ;;
check)
  check "${2:-}" ;;
mutants)
  bad=0
  tab=$(printf '\t')
  grep -v '^#' "$dir/mutants.txt" | while IFS="$tab" read -r name want old new; do
    copy="$work/$name.swift"
    if ! OLD="$old" NEW="$new" perl -0pe 'BEGIN { ($o, $n) = map { s/\\n/\n/gr } @ENV{qw(OLD NEW)} } s/\Q$o\E/$n/ or die "anchor missing\n"' "$repo/Sources/voice-switch/TriageExecution.swift" > "$copy" 2>/dev/null; then
      echo "MUTANT $name: anchor missing (update conform.sh)"; bad=1; continue
    fi
    out=$(check "$copy" || true)
    if printf '%s\n' "$out" | grep -q "^FAIL $want"; then echo "CAUGHT $name by $want"
    else echo "MISSED $name (want FAIL $want)"; printf '%s\n' "$out"; fi
  done | tee "$work/mutants.out"
  ! grep -qv '^CAUGHT ' "$work/mutants.out" ;;
*) echo "usage: conform.sh gen|check|mutants"; exit 2 ;;
esac
