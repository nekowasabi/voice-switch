#!/bin/sh
# Model-check StickyDelivery.tla with the pinned TLC (the same jar as verify-mic-fallback).
# Usage: check.sh [Config.cfg]   (default: StickyDelivery.cfg beside this script)
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
ver=v1.7.4
sha=936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88
jar="$HOME/.cache/tla2tools-$ver/tla2tools.jar"
if [ ! -f "$jar" ]; then
  mkdir -p "$(dirname "$jar")"
  gh release download -R tlaplus/tlaplus "$ver" -p tla2tools.jar -D "$(dirname "$jar")"
fi
shasum -a 256 -c - <<EOF >/dev/null
$sha  $jar
EOF
cfg=${1:-$dir/StickyDelivery.cfg}
work=$(mktemp -d)
cp "$dir/StickyDelivery.tla" "$work/StickyDelivery.tla"
cp "$cfg" "$work/StickyDelivery.cfg"
cd "$work"
exec java -XX:+UseParallelGC -cp "$jar" tlc2.TLC -workers auto -config StickyDelivery.cfg StickyDelivery.tla
