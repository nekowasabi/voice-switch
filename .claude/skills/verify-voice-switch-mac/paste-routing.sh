#!/bin/sh
# Counts how automatic recordings are routed by the real Superwhisper: one ended by silence must not paste, one ended by
# the stop word must paste. Each run is one --simulate session with TextEdit in front as the paste target.
# usage: paste-routing.sh <runs per case> <config.json>
# The config must name a Superwhisper mode with "autoPaste": false in dictation.superwhisperMode (the user's real mode).
# It drives the user's Superwhisper and TextEdit; nobody may touch the keyboard or mouse while it runs (~45 s per run).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
V=$here/verify.sh
n=${1:?runs per case}; config=${2:?config.json}
fix=${VS_VERIFY_ROOT:-$HOME/Library/Caches/voice-switch-verify}/fixtures
for f in sg-own-auto sg-own-stop; do [ -f "$fix/$f.aiff" ] || { echo "missing fixture $fix/$f.aiff (see features/paste-routing.md)"; exit 2; }; done
# A document of its own, so the user's TextEdit documents are never cleared or read; it is closed unsaved at the end.
doc=$(osascript -e 'tell application "TextEdit"' -e 'activate' -e 'make new document' -e 'name of document 1' -e 'end tell')
te() { osascript -e "tell application \"TextEdit\" to $1"; }
trap 'te "close document \"$doc\" saving no" >/dev/null 2>&1 || true' EXIT
run_id=paste-routing-$(date +%Y%m%d-%H%M%S)
wrong=0; total=0
for i in $(seq 1 "$n"); do
  for case in silence stop; do
    fixture=sg-own-auto; want=no-paste
    [ $case = stop ] && { fixture=sg-own-stop; want=paste; }
    te "set text of document \"$doc\" to \"\"" >/dev/null
    osascript -e 'tell application "TextEdit" to activate' -e "tell application \"TextEdit\" to set index of window \"$doc\" to 1" >/dev/null
    SIMULATE_REALTIME=1 VS_RUN_ID=$run_id/$case-$i "$V" run "$fixture" "$config" >/dev/null
    sleep 2
    text=$(te "get text of document \"$doc\"")
    got=no-paste; [ -n "$text" ] && got=paste
    total=$((total + 1)); [ $got = $want ] || wrong=$((wrong + 1))
    echo "$case-$i want=$want got=$got text=[$text]"
  done
done
echo "routed correctly $((total - wrong))/$total (run $run_id)"
[ $wrong -eq 0 ]
