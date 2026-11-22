#!/bin/sh
# Model-check TriageExec.tla, one invariant per TLC run so each gets its own verdict and trace.
# Usage: check.sh <design|risk> [Model.tla]   (failures print the path to the retained TLC log)
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
ver=v1.7.4
sha=936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88
jar="$HOME/.cache/tla2tools-$ver/tla2tools.jar"
if [ ! -f "$jar" ]; then
  gh release download -R tlaplus/tlaplus "$ver" -p tla2tools.jar -D "$(dirname "$jar")"
fi
shasum -a 256 -c - <<EOF >/dev/null
$sha  $jar
EOF
cfg=${1:?usage: check.sh <design|risk> [Model.tla]}
spec=${2:-$dir/TriageExec.tla}
out=$(mktemp -d "${TMPDIR:-/tmp}/verify-triage-execution-$cfg.XXXXXX")
cp "$spec" "$out/TriageExec.tla"
invariants=$(sed -n 's/^INVARIANTS //p' "$dir/$cfg.cfg")
status=0
for inv in $invariants; do
  sed '/^INVARIANTS /d' "$dir/$cfg.cfg" > "$out/$inv.cfg"
  echo "INVARIANT $inv" >> "$out/$inv.cfg"
  if (cd "$out" && java -XX:+UseParallelGC -cp "$jar" tlc2.TLC -workers auto -config "$inv.cfg" TriageExec.tla > "$inv.out" 2>&1); then
    echo "PASS $inv ($(sed -n 's/.* \([0-9][0-9]*\) distinct states found.*/\1/p' "$out/$inv.out" | tail -n 1) distinct states)"
  else
    if grep -q "is violated" "$out/$inv.out"; then echo "FAIL $inv (trace: $out/$inv.out)"; else echo "ERROR $inv (see $out/$inv.out)"; fi
    status=1
  fi
done
exit $status
