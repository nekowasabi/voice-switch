#!/bin/sh
# Model-check MeetPause.tla with a pinned TLC. Usage: check.sh [Model.tla] (default: MeetPause.tla next to this script).
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
spec=${1:-$dir/MeetPause.tla}
work=$(mktemp -d)
cp "$spec" "$work/MeetPause.tla"
cp "$dir/MeetPause.cfg" "$work/"
cd "$work"
exec java -XX:+UseParallelGC -cp "$jar" tlc2.TLC -workers auto -config MeetPause.cfg MeetPause.tla
