#!/bin/sh
# Each model action rests on one guard in Sources/voice-switch/MacApp.swift or PaneRoute.swift (anchors.tsv: name<TAB>exact text).
# A guard that is gone, edited, or duplicated fails here, so the model is not trusted for code it no longer describes.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
srcdir="$dir/../../../Sources/voice-switch"
bad=0
tab=$(printf '\t')
while IFS="$tab" read -r name text; do
  [ -n "$name" ] || continue
  n=$(cat "$srcdir/MacApp.swift" "$srcdir/PaneRoute.swift" | grep -cF -- "$text" || true)
  if [ "$n" = 1 ]; then echo "ok   anchor $name"; else echo "FAIL anchor $name found $n times: $text"; bad=1; fi
done < "$dir/anchors.tsv"
exit "$bad"
