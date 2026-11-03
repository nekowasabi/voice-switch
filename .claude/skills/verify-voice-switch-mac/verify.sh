#!/bin/sh
# Drive the macOS voice-switch binary with synthetic speech through --simulate and keep the log as evidence.
set -eu

REPO=$(cd "$(dirname "$0")/../../.." && pwd)
ROOT=${VS_VERIFY_ROOT:-$HOME/Library/Caches/voice-switch-verify}
BUILD=$ROOT/build
BIN=$BUILD/release/voice-switch
FIX=$ROOT/fixtures
EVID=$ROOT/evidence
SKILL=$(cd "$(dirname "$0")" && pwd)

die() { echo "error: $*" >&2; exit 1; }

cmd_build() {
  mkdir -p "$ROOT"
  # A separate build path: `make` would also install and relaunch the user's VoiceSwitch.app.
  swift build -c release --package-path "$REPO" --build-path "$BUILD" 2>&1 | tail -n 3
  git -C "$REPO" rev-parse --short HEAD > "$BUILD/built-from.txt"
  git -C "$REPO" status --porcelain | wc -l | tr -d ' ' > "$BUILD/built-dirty.txt"
  echo "built $BIN"
  enroll_own
}

# Speaker gate fixture: the model download and the user's enrollment are idempotent and never fail the build
# (a missing model is the fail-open case, SG7). The enrollment is the user's 5 newest superwhisper recordings
# with a transcript and 3 s or more (TTS voices calibrated the gate wrongly; --enroll-speaker rejects the whole set when one
# clip is under 3 s); speaker/own.txt lists them so a test clip can avoid them.
enroll_own() {
  spk=$ROOT/speaker
  mkdir -p "$spk"
  make -C "$REPO" speaker-model >/dev/null 2>&1 || echo "warn make speaker-model failed; speaker gate stays unavailable"
  [ -f "$spk/own.json" ] && return 0
  : > "$spk/own.txt"
  # Newest first, which a glob cannot order; read line by line so a name with spaces stays whole.
  ls -t "$HOME/Documents/superwhisper/recordings" | while IFS= read -r d; do
    r=$HOME/Documents/superwhisper/recordings/$d
    [ -f "$r/output.wav" ] && [ -n "$(plutil -extract result raw "$r/meta.json" 2>/dev/null)" ] || continue
    [ "$(plutil -extract duration raw "$r/meta.json" 2>/dev/null | cut -d. -f1)" -ge 3000 ] 2>/dev/null || continue
    echo "$r/output.wav" >> "$spk/own.txt"
    [ "$(wc -l < "$spk/own.txt")" -ge 5 ] && break
  done
  # shellcheck disable=SC2046
  "$BIN" --enroll-speaker "$spk/own.json" $(cat "$spk/own.txt") || echo "warn enrollment failed; speaker gate stays unavailable"
}

cmd_doctor() {
  ok=0
  if [ -x "$BIN" ]; then echo "ok   binary $BIN"; else echo "FAIL binary missing: run build"; ok=1; fi
  if [ -f "$BUILD/built-from.txt" ]; then
    echo "info built-from $(cat "$BUILD/built-from.txt") dirty=$(cat "$BUILD/built-dirty.txt") | repo HEAD $(git -C "$REPO" rev-parse --short HEAD) dirty=$(git -C "$REPO" status --porcelain | wc -l | tr -d ' ')"
  fi
  if say -v '?' | grep -q '^Kyoko '; then echo "ok   say voice Kyoko"; else echo "FAIL Japanese voice Kyoko missing"; ok=1; fi
  if [ -x "$BIN" ]; then
    # It also fails on a missing Silero resource or Whisper model; its last line says which.
    if st=$("$BIN" --vad-selftest 2>&1); then echo "ok   --vad-selftest"; else echo "FAIL --vad-selftest: $(printf '%s\n' "$st" | tail -n 1)"; ok=1; fi
  fi
  # Only VS_BUSY_INPUT=1 runs need this, so it warns instead of failing.
  if swift "$SKILL/busy-input.swift" 0 >/dev/null 2>&1; then echo "ok   busy-input can post events"; else echo "warn busy-input cannot post events: grant Accessibility to this terminal or VS_BUSY_INPUT runs are not busy"; fi
  for p in "$EVID"/*/*/pid.txt; do
    [ -f "$p" ] || continue
    d=$(dirname "$p")
    [ -f "$d/exitcode.txt" ] || echo "warn unfinished run $d (pid $(cat "$p")); run cleanup"
  done
  exit $ok
}

# say <name> <text>: Kyoko speech, or VS_SAY_VOICE (another ja_JP voice stands in for a second speaker).
# [[slnc 800]] inserts 800 ms of silence.
cmd_say() {
  [ $# -ge 2 ] || die "usage: say <name> <text>"
  mkdir -p "$FIX"
  name=$1; shift
  say -v "${VS_SAY_VOICE:-Kyoko}" -o "$FIX/$name.aiff" "$*"
  echo "$FIX/$name.aiff"
}

# run <fixture> [config.json]
cmd_run() {
  [ $# -ge 1 ] || die "usage: run <fixture> [config.json]"
  fixture=$1
  wav=$FIX/$fixture.aiff
  [ -f "$wav" ] || die "no fixture $wav; create it with: say $fixture <text>"
  run=${VS_RUN_ID:-$(date +%Y%m%d-%H%M%S)}
  out=$EVID/$run/$fixture
  mkdir -p "$out"
  cp "${2:-$SKILL/config.safe.json}" "$out/config.json"
  # MARK lets a config's command prove its side effect inside the evidence folder.
  sed -i '' "s#__EVIDENCE__#$out#g; s#__CACHE__#$ROOT#g" "$out/config.json"
  cp "$wav" "$out/input.aiff"
  # VS_BUSY_INPUT=1 holds input idle near zero for the whole run, i.e. "the user is operating the Mac".
  if [ -n "${VS_BUSY_INPUT:-}" ]; then
    swift "$SKILL/busy-input.swift" 45 > "$out/busy-input.txt" 2>&1 &
    echo $! > "$out/busy-pid.txt"
    sleep 3
  fi
  VOICE_SWITCH_CONFIG=$out/config.json "$BIN" --simulate "$out/input.aiff" > "$out/stdout.txt" 2> "$out/stderr.txt" &
  pid=$!
  echo "$pid" > "$out/pid.txt"
  # --simulate exits by itself after 40 s; 90 s means it hung.
  i=0
  while kill -0 "$pid" 2>/dev/null && [ $i -lt 90 ]; do sleep 1; i=$((i + 1)); done
  if kill -0 "$pid" 2>/dev/null; then kill "$pid"; echo timeout > "$out/exitcode.txt"; else wait "$pid" && echo 0 > "$out/exitcode.txt" || echo $? > "$out/exitcode.txt"; fi
  # The helper ends by itself after 45 s; waiting keeps its idle report in the evidence.
  [ -f "$out/busy-pid.txt" ] && { wait "$(cat "$out/busy-pid.txt")" || true; cat "$out/busy-input.txt"; }
  echo "exit=$(cat "$out/exitcode.txt") evidence=$out"
  for m in "$out"/*.marker; do [ -f "$m" ] && basename "$m"; done
  grep -E 'heard|dictation|phase|fatal|command|whisper' "$out/stdout.txt" || true
}

# hud: shows every glyph of the built binary's real HUD (no microphone, no Superwhisper), then compares
# the glyph names of the Mac and Windows HUD sources. Exit 0 only when every check passes.
cmd_hud() {
  out=$EVID/${VS_RUN_ID:-$(date +%Y%m%d-%H%M%S)}/hud
  mkdir -p "$out"
  rc=0
  "$BIN" --hud-labels "$out" || rc=1
  # Glyph names cover Windows, which this Mac cannot render. Both HUDs draw vector paths now, so a shared
  # name contract is what keeps the two platforms showing the same nine states.
  awk '/^enum HUDLabel/ { inside = 1 } inside && /case / { gsub(/^[ \t]*case[ \t]+/, ""); gsub(/[ \t]*\{.*/, ""); n = split($0, names, /,[ \t]*/); for (i = 1; i <= n; i++) print names[i] } inside && /^}/ { inside = 0 }' \
    "$REPO/Sources/voice-switch/MacApp.swift" | tr 'A-Z' 'a-z' | LC_ALL=C sort > "$out/mac-glyphs.txt"
  awk '/^public enum HudGlyph/ { inside = 1; next } inside && /^}/ { inside = 0 } inside { print }' \
    "$REPO/dotnet/VoiceSwitch.Windows/Tray/DictationHud.cs" | grep -v '^[[:space:]]*{' | sed 's/[[:space:],]//g' | sed '/^$/d' \
    | tr 'A-Z' 'a-z' | LC_ALL=C sort > "$out/windows-glyphs.txt"
  if diff "$out/mac-glyphs.txt" "$out/windows-glyphs.txt" > "$out/parity.diff"; then
    echo "ok   Mac and Windows HUD glyph names match"
  else
    echo "FAIL Mac and Windows HUD glyph names differ (parity.diff)"; rc=1
  fi
  [ "$(wc -l < "$out/mac-glyphs.txt")" -eq 9 ] || { echo "FAIL expected 9 Mac HUD glyphs; narrow the awk"; rc=1; }
  # Regression guard: the HUD must not go back to emoji or text labels.
  if grep -nE 'label\.Text|labelWithString' "$REPO/Sources/voice-switch/MacApp.swift" "$REPO/dotnet/VoiceSwitch.Windows/Tray/DictationHud.cs" > "$out/text-labels.txt"; then
    echo "FAIL the HUD still draws text labels (text-labels.txt)"; rc=1
  else
    echo "ok   both HUDs draw vector glyphs, not text"
  fi
  echo "exit=$rc evidence=$out"
  return $rc
}

# latency sim [word]: a lone action wake word through --simulate at each hangoverMs in VS_HANGOVERS, then the
# audio-time breakdown of "word end -> HUD waiting". Log timestamps are whole seconds, so nothing here uses wall time.
# --simulate has no HAL voice detector (energy VAD only), so this is the floor; `latency log` reads the real mic.
cmd_latency_sim() {
  word=${1:-プログレス}
  lrun=${VS_RUN_ID:-latency-$(date +%Y%m%d-%H%M%S)}
  mkdir -p "$FIX" "$EVID/$lrun"
  # 5 s after the word: startTimeoutMs (3 s) cancels the waiting action, so its command never runs.
  say -v Kyoko -o "$FIX/latency-action.aiff" "[[slnc 1000]] $word [[slnc 5000]]"
  table=$EVID/$lrun/latency.tsv
  printf 'hangoverMs\theard\tvoiced_ms\thal_ms\tstt_ms\test_word_end_to_hud_ms\tphase_waiting\taction_marker\n' > "$table"
  for h in ${VS_HANGOVERS:-300 150 60}; do
    cfg=$EVID/$lrun/config-h$h.json
    sed "s#__HANGOVER__#$h#; s#__WORD__#$word#" "$SKILL/config.action-latency.json" > "$cfg"
    VS_RUN_ID=$lrun/h$h SIMULATE_REALTIME=1 cmd_run latency-action "$cfg" > /dev/null
    out=$EVID/$lrun/h$h/latency-action
    # heard: <text>  [utt N ms, stt M ms]; the first one is the action word.
    line=$(grep -m 1 'heard: ' "$out/stdout.txt" || true)
    heard=$(printf '%s' "$line" | sed -n 's/.*heard: \(.*\)  \[utt.*/\1/p')
    stt=$(printf '%s' "$line" | sed -n 's/.*stt \([0-9]*\) ms\].*/\1/p')
    phrase=$(grep -m 1 'voice: phrase' "$out/stdout.txt" || true)
    voiced=$(printf '%s' "$phrase" | sed -n 's/.*voicedMs=\([0-9]*\).*/\1/p')
    hal=$(printf '%s' "$phrase" | sed -n 's/.*halMs=\([0-9]*\).*/\1/p')
    waiting=$(grep -c 'phase: waiting' "$out/stdout.txt" || true)
    marker=no; [ -f "$out/action.marker" ] && marker=yes
    # The utterance closes hangoverMs after the last voiced frame, the frame loop adds at most one 30 ms frame, then
    # STT. HUD dispatch to the main thread is not measured (log time is whole seconds). With no HAL detector here the
    # VAD tail past the word is about one frame, so this is the floor of the real app's delay.
    total=-; [ -n "$stt" ] && [ "$waiting" -gt 0 ] && total=$((h + 30 + stt))
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$h" "${heard:--}" "${voiced:--}" "${hal:--}" "${stt:--}" "$total" "$waiting" "$marker" >> "$table"
    printf '%s\n' "${line:-h=$h: no heard line}" > "$out/heard.txt"
  done
  column -t -s "$(printf '\t')" "$table"
  echo "evidence=$EVID/$lrun"
}

# latency log [file]: every action launch in the real app's log with the phrase line that opened it.
# hal_only_ms = frames voiced only by the HAL detector, i.e. how long it held the phrase open past the energy VAD.
cmd_latency_log() {
  log=${1:-$HOME/Library/Logs/voice-switch.log}
  [ -f "$log" ] || die "no log $log"
  awk -v hang="${VS_HANGOVER:-300}" '
    function msOfDay(ts,   c) { c = substr(ts, 12); sub(/Z$/, "", c); split(c, p, ":"); return int((p[1] * 3600 + p[2] * 60 + p[3]) * 1000) }
    function field(name,   v) { v = $0; sub(".*" name "=", "", v); sub(/ .*/, "", v); return v }
    /heard: .*\[utt [0-9]+ ms, stt [0-9]+ ms\]/ { s = $0; sub(/.*stt /, "", s); sub(/ ms\].*/, "", s); stt = s }
    # Builds that log milliseconds and a "wake probe:" line give a measured row:
    # energy end -> HUD = energySilentMs + sttMs + (hud log time - probe log time); the probe line is written after STT.
    /wake probe: .*hit=true/ && $1 ~ /\./ { pts = msOfDay($1); pstt = field("sttMs"); pes = field("energySilentMs"); psil = field("silentMs"); psv = /sileroSilentMs=[0-9]/ ? field("sileroSilentMs") : ""; probe = 1 }
    /dictation started early.*action=/ && probe { wantHud = 1; ha = $NF; sub(/^action=/, "", ha); hts0 = $1 }
    wantHud && /hud: waiting/ {
      d = msOfDay($1) - pts
      # Silero voice end -> HUD: the nearest signal to the word end (Silero falls 32-64 ms after it on real recordings).
      printf "measured\t%s\t%s\tvoice_end_to_hud=%s\tenergy_end_to_hud=%d\tsilence_end_to_hud=%d\tstt=%d\tdispatch=%d\tenergySilentMs=%d\n", hts0, ha, (psv == "" ? "-" : psv + pstt + d), pes + pstt + d, psil + pstt + d, pstt, d, pes
      m++; mv[m] = psv == "" ? pes + pstt + d : psv + pstt + d; wantHud = 0; probe = 0
    }
    # main logs "dictation started ... -> action <name>" after the utterance closed (hangoverMs). The early probe
    # (PR #71) logs "dictation started early ... action=<name>" at a 60 ms silence gap (150 ms for a prefix word).
    /dictation started.*(-> action |action=)/ {
      t = $1; a = $NF; sub(/^action=/, "", a); bound = /started early/ ? 60 : hang; pending = 1; next
    }
    pending && /voice: phrase .*-> dictation opened/ {
      v = $0; sub(/.*voicedMs=/, "", v); sub(/ .*/, "", v)
      e = $0; sub(/.*energyMs=/, "", e); sub(/ .*/, "", e)
      h = $0; sub(/.*halMs=/, "", h); sub(/ .*/, "", h)
      only = v - e
      # stt is logged only while dictation.noWakeWord is off; otherwise the estimate leaves it out.
      printf "%s\t%s\tvoiced=%s\tenergy=%s\thal=%s\thal_only=%d\tboundary=%d\tstt=%s\test_word_end_to_hud=%d%s\n", t, a, v, e, h, only, bound, (stt == "" ? "-" : stt), only + bound + stt, (stt == "" ? "+stt" : "")
      n++; sum += only; vals[n] = only; pending = 0; stt = ""; next
    }
    pending && /dictation started|dictation session/ { pending = 0 }
    END {
      if (m > 0) {
        for (i = 2; i <= m; i++) { x = mv[i]; j = i - 1; while (j > 0 && mv[j] > x) { mv[j + 1] = mv[j]; j-- } mv[j + 1] = x }
        printf "measured=%d end_to_hud_ms (voice_end, else energy_end) min=%d median=%d max=%d\n", m, mv[1], mv[int((m + 1) / 2)], mv[m]
      }
      if (n == 0) { print "no action launch with a voice: phrase line"; exit }
      # insertion sort for the median; n is small
      for (i = 2; i <= n; i++) { x = vals[i]; j = i - 1; while (j > 0 && vals[j] > x) { vals[j + 1] = vals[j]; j-- } vals[j + 1] = x }
      printf "launches=%d hal_only_ms median=%d max=%d (boundary added per row: hangoverMs=%d, or 60 for an early start)\n", n, vals[int((n + 1) / 2)], vals[n], hang
    }' "$log"
}

cmd_cleanup() {
  for p in "$EVID"/*/*/pid.txt; do
    [ -f "$p" ] || continue
    d=$(dirname "$p"); pid=$(cat "$p")
    # Only a run that never finished, and only if that PID is still our binary.
    if [ ! -f "$d/exitcode.txt" ] && ps -p "$pid" -o command= 2>/dev/null | grep -q "$BIN --simulate"; then
      kill "$pid" && echo "killed $pid ($d)"
      echo killed > "$d/exitcode.txt"
    fi
  done
  rm -rf "$BUILD"
  echo "removed $BUILD; kept $EVID and $FIX"
}

[ $# -ge 1 ] || die "usage: verify.sh build|doctor|say|run|hud|latency sim|log|cleanup"
c=$1; shift
case $c in
  build) cmd_build ;;
  hud) cmd_hud ;;
  latency)
    [ $# -ge 1 ] || die "usage: latency sim [word] | latency log [file]"
    sub=$1; shift
    case $sub in sim) cmd_latency_sim "$@" ;; log) cmd_latency_log "$@" ;; *) die "unknown latency $sub" ;; esac ;;
  doctor) cmd_doctor ;;
  say) cmd_say "$@" ;;
  run) cmd_run "$@" ;;
  cleanup) cmd_cleanup ;;
  *) die "unknown command $c" ;;
esac
