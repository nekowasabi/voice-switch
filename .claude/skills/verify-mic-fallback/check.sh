#!/bin/sh
# Model-check MicFallback.tla with a pinned TLC, for the current code and the option-2 design.
# Usage: check.sh [Model.tla] [Current|Design] (default: MicFallback.tla next to this script, both configs).
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
spec=${1:-$dir/MicFallback.tla}
cfgs=${2:-Current Design}
status=0
for c in $cfgs; do
  work=$(mktemp -d)
  cp "$spec" "$work/MicFallback.tla"
  cp "$dir/$c.cfg" "$work/"
  echo "== $c =="
  (cd "$work" && java -XX:+UseParallelGC -cp "$jar" tlc2.TLC -workers auto -config "$c.cfg" MicFallback.tla) || status=1
done
exit $status
