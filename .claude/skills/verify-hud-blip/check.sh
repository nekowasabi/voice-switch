#!/bin/sh
# Model-check HudBlip.tla with a pinned TLC. Usage: check.sh mac|windows [Model.tla] (default: HudBlip.tla next to this script).
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
platform=${1:?usage: check.sh mac|windows [Model.tla]}
ver=v1.7.4
sha=936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88
jar="$HOME/.cache/tla2tools-$ver/tla2tools.jar"
if [ ! -f "$jar" ]; then
  gh release download -R tlaplus/tlaplus "$ver" -p tla2tools.jar -D "$(dirname "$jar")"
fi
shasum -a 256 -c - <<EOF >/dev/null
$sha  $jar
EOF
spec=${2:-$dir/HudBlip.tla}
work=$(mktemp -d)
cp "$spec" "$work/HudBlip.tla"
cp "$dir/HudBlip-$platform.cfg" "$work/HudBlip.cfg"
cd "$work"
exec java -XX:+UseParallelGC -cp "$jar" tlc2.TLC -workers auto -config HudBlip.cfg HudBlip.tla
