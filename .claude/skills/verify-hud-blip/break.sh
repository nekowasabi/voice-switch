#!/bin/sh
# Require the old Mac HUD rule to fail the noise invariant in a scratch copy.
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' 0
trap 'exit 1' HUP INT TERM
awk '
    /^HudMac ==/ {
        print "HudMac == dict # \"none\" \\/ (speechFrames > 0 /\\ phraseHAL)"
        changed++
        next
    }
    { print }
    END { if (changed != 1) exit 1 }
' "$dir/HudBlip.tla" > "$work/HudBlip.tla"
status=0
sh "$dir/check.sh" mac "$work/HudBlip.tla" > "$work/check.log" 2>&1 || status=$?
if [ "$status" -ne 0 ] && grep -Fq 'Invariant NoiseNeverShowsHud is violated' "$work/check.log"; then
    printf '%s\n' '旧規則で NoiseNeverShowsHud 違反を検出: pass'
    exit 0
fi
cat "$work/check.log"
printf '%s\n' '旧規則で必要な不変条件違反を検出できない: fail' >&2
exit 1
