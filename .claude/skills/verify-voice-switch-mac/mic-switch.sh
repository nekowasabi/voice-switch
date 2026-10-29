#!/bin/sh
# Switches the running VoiceSwitch.app to each microphone through its menu bar "マイク" submenu,
# the same path a user clicks, and judges each switch from ~/Library/Logs/voice-switch.log.
# Usage: VS_RUN_ID=<name> [VS_SPEAK="<Japanese text>"] mic-switch.sh [device title ...]   (no titles: every item in the submenu)
# VS_SPEAK plays the text with say after each passing switch and requires a recognition line (MS3).
set -u
LOG="$HOME/Library/Logs/voice-switch.log"
OUT="${VS_VERIFY_ROOT:-$HOME/Library/Caches/voice-switch-verify}/evidence/${VS_RUN_ID:-mic-switch-$(date +%Y%m%d-%H%M%S)}/mic-switch"
WAIT="${VS_SWITCH_WAIT:-4}"

pgrep -f '/VoiceSwitch.app/Contents/MacOS/voice-switch' >/dev/null || { echo "FAIL VoiceSwitch.app is not running (open ~/Applications/VoiceSwitch.app)"; exit 2; }
mkdir -p "$OUT"

as() { osascript -e "tell application \"System Events\" to tell process \"voice-switch\"" -e "$1" -e "end tell"; }
menu='menu 1 of menu bar item 1 of menu bar 1'
open_menu() { as 'click menu bar item 1 of menu bar 1' >/dev/null; sleep 0.5; }
close_menu() { as 'key code 53' >/dev/null 2>&1; }
# Opens the menu and clicks in one script; a menu opened by an earlier osascript ignores the click.
# "ignoring application responses" drops the click entirely, so it waits with a short timeout instead:
# a failed switch opens a modal NSAlert that can hold the click until take_alert closes it.
click_async() {
  osascript -e 'tell application "System Events" to tell process "voice-switch"' -e 'click menu bar item 1 of menu bar 1' -e 'delay 0.5' \
    -e "try" -e "with timeout of 5 seconds" -e "$1" -e "end timeout" -e "end try" -e "end tell" >/dev/null
}
# Returns the alert text and presses OK, or prints nothing when no alert is up.
take_alert() {
  as 'if (count of windows) > 0 then
        set t to value of static texts of window 1
        click button "OK" of window 1
        return t
      end if' 2>/dev/null
}
log_since() { tail -c +"$(($1 + 1))" "$LOG" | grep -vE '^(whisper_|ggml_)' | grep -E 'echo cancellation|input device|listening on|失敗|paused|convert'; }

open_menu
items=$(as "name of menu items of menu 1 of menu item \"マイク\" of $menu" | tr ',' '\n' | sed 's/^ *//')
selected=$(as "repeat with i in menu items of menu 1 of menu item \"マイク\" of $menu
  if value of attribute \"AXMenuItemMarkChar\" of i is not missing value then return name of i
end repeat" 2>/dev/null)
first=$(as "name of menu item 1 of $menu")
close_menu
printf '%s\n' "$items" > "$OUT/menu-items.txt"
echo "selected before: ${selected:-?}  top item: $first" | tee "$OUT/state-before.txt"

if [ "$first" = "再開" ]; then
  off=$(wc -c < "$LOG")
  click_async "click menu item \"再開\" of $menu"; sleep "$WAIT"
  alert=$(take_alert)
  { echo "== resume"; log_since "$off"; [ -n "$alert" ] && echo "ALERT: $alert"; } >> "$OUT/results.txt"
  [ -n "$alert" ] && { echo "FAIL resume: $alert"; cat "$OUT/results.txt"; exit 1; }
fi

[ $# -gt 0 ] && printf '%s\n' "$@" > "$OUT/targets.txt" || cp "$OUT/menu-items.txt" "$OUT/targets.txt"
fails=0
while IFS= read -r dev <&3; do
  [ -n "$dev" ] || continue
  off=$(wc -c < "$LOG")
  click_async "click menu item \"マイク\" of $menu
delay 0.3
click menu item \"$dev\" of menu 1 of menu item \"マイク\" of $menu"; sleep "$WAIT"
  alert=$(take_alert)
  lines=$(log_since "$off")
  { echo "== $dev"; printf '%s\n' "$lines"; [ -n "$alert" ] && echo "ALERT: $alert"; } >> "$OUT/results.txt"
  if [ -z "$alert" ] && printf '%s' "$lines" | grep -q 'listening on'; then
    echo "PASS $dev: $(printf '%s\n' "$lines" | grep 'listening on' | tail -1 | sed 's/ for \[.*//')"
    if [ -n "${VS_SPEAK:-}" ]; then
      off=$(wc -c < "$LOG")
      say -v Kyoko "$VS_SPEAK"; sleep 5
      heard=$(tail -c +"$((off + 1))" "$LOG" | grep -E 'heard:|dictation recognition: complete|dictation session: automatic')
      { echo "-- speech after switch"; printf '%s\n' "$heard"; } >> "$OUT/results.txt"
      if [ -n "$heard" ]; then echo "PASS $dev audio: $(printf '%s\n' "$heard" | head -1)"; else echo "FAIL $dev audio: nothing recognized"; fails=$((fails + 1)); fi
    fi
  else
    echo "FAIL $dev: ${alert:-no listening line}"; fails=$((fails + 1))
  fi
done 3< "$OUT/targets.txt"

if [ -n "$selected" ]; then
  click_async "click menu item \"マイク\" of $menu
delay 0.3
click menu item \"$selected\" of menu 1 of menu item \"マイク\" of $menu"; sleep "$WAIT"
  alert=$(take_alert); [ -n "$alert" ] && echo "WARN restoring $selected failed: $alert"
fi
[ "$first" = "再開" ] && { click_async "click menu item 1 of $menu"; sleep 1; take_alert >/dev/null; }
echo "evidence: $OUT"
[ "$fails" -eq 0 ]
