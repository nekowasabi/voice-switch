#!/bin/sh
# Checks for the 2026-11-28 audit fixes. baseline: behaviour that must stay. <n>: a check that fails while finding n
# is still present and passes once it is fixed. Evidence stays under $ROOT/<run>/ after every run.
set -u
dir=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$dir/../../.." && pwd)
ROOT=${VS_AUDIT_ROOT:-$HOME/Library/Caches/voice-switch-audit}
RUN=$ROOT/$(date -u +%Y%m%dT%H%M%SZ)-$$
mkdir -p "$RUN"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

result() { # name exit-code fixed-text open-text
  if [ "$2" -eq 0 ]; then echo "PASS $1: $3" | tee -a "$RUN/summary.txt"; return 0; fi
  echo "FAIL $1: $4 (log $RUN/$1.log)" | tee -a "$RUN/summary.txt"; return 1
}

cmd_doctor() {
  for t in swift swiftc java python3 git; do
    if command -v "$t" >/dev/null 2>&1; then echo "ok   $t"; else echo "FAIL $t missing"; fi
  done
  if command -v dotnet >/dev/null 2>&1; then echo "ok   dotnet"; else echo "SKIP dotnet missing: findings 1 6 7 16 (and the C# half of 8 18) cannot run here; run make win-test on the WSL machine"; fi
  pgrep -x voice-switch >/dev/null 2>&1 && echo "note the user's VoiceSwitch.app is running; this skill never starts, stops or installs it" || true
  echo "evidence root $ROOT"
}

cmd_baseline() {
  (cd "$repo" && sh tests/check.sh) > "$RUN/baseline.log" 2>&1
  result baseline $? "tests/check.sh passed ($(tail -n 1 "$RUN/baseline.log"))" "tests/check.sh failed: $(grep -m 1 -E 'FAIL|error' "$RUN/baseline.log")"
}

# 2: the mac harness build must fail when swift build fails. A fake swift fails the compile; a fake make keeps
# enroll_own from downloading the speaker model.
cmd_2() {
  # A build from an earlier run is already there, as on any machine that ran this skill before.
  mkdir -p "$work/bin2" "$work/root2/build/release"
  printf '#!/bin/sh\necho "stale binary from an earlier build"\n' > "$work/root2/build/release/voice-switch"
  chmod +x "$work/root2/build/release/voice-switch"
  printf '#!/bin/sh\necho "error: audit shim: compile failed" >&2\nexit 1\n' > "$work/bin2/swift"
  printf '#!/bin/sh\nexit 0\n' > "$work/bin2/make"
  chmod +x "$work/bin2/swift" "$work/bin2/make"
  VS_VERIFY_ROOT=$work/root2 PATH="$work/bin2:$PATH" sh "$repo/.claude/skills/verify-voice-switch-mac/verify.sh" build > "$RUN/2.log" 2>&1
  code=$?
  echo "exit=$code" >> "$RUN/2.log"
  [ "$code" -ne 0 ]
  result 2 $? "a failed swift build fails verify.sh build (exit $code)" "verify.sh build exited 0 after swift build failed; the stale binary stays and built-from.txt says $(cat "$work/root2/build/built-from.txt" 2>/dev/null)"
}

# 3: the sticky drive must fail when its Swift harness fails. Fake sh skips the TLC steps; fake swiftc builds a
# harness that dies like fatalError.
cmd_3() {
  mkdir -p "$work/bin3"
  cat > "$work/bin3/sh" <<'EOF'
#!/bin/sh
case "${1:-}" in */anchors.sh|*/check.sh|*/break.sh|*/finding.sh) echo "audit shim: skipped $1"; exit 0 ;; esac
exec /bin/sh "$@"
EOF
  cat > "$work/bin3/swiftc" <<'EOF'
#!/bin/sh
while [ $# -gt 0 ]; do [ "$1" = -o ] && out=$2; shift; done
printf '#!/bin/sh\necho "Fatal error: audit shim check failed" >&2\nexit 134\n' > "$out"; chmod +x "$out"
EOF
  chmod +x "$work/bin3/sh" "$work/bin3/swiftc"
  PATH="$work/bin3:$PATH" /bin/sh "$repo/.claude/skills/verify-sticky-mode/verify.sh" drive > "$RUN/3.log" 2>&1
  code=$?
  echo "exit=$code" >> "$RUN/3.log"
  grep -q "audit shim check failed" "$RUN/3.log" && [ "$code" -ne 0 ]
  result 3 $? "a failing stickyStep harness fails the drive (exit $code)" "the drive exited $code although the Swift harness failed"
}

# 10, 11: the triage CLI with HTTP replaced and HOME in a temp folder.
cmd_10() { python3 "$dir/triage_checks.py" cache > "$RUN/10.log" 2>&1; result 10 $? "--no-context answers are not reused with context" "$(tail -n 1 "$RUN/10.log")"; }
cmd_11() { python3 "$dir/triage_checks.py" reason > "$RUN/11.log" 2>&1; result 11 $? "a Jev failure shows its reason on stderr" "$(tail -n 1 "$RUN/11.log")"; }

# 13: budget.sh must refuse each model that grows past its 2026-11-22 size.
cmd_13() {
  : > "$RUN/13.log"; ok=0
  for pair in verify-hud-blip/HudBlip.tla:104 verify-hud-noise/HudNoise.tla:92 verify-meet-pause/MeetPause.tla:120; do
    rel=.claude/skills/${pair%%:*}; max=${pair##*:}; w=$work/b13-$max
    mkdir -p "$w/tests"; git -C "$w" init -q
    cp "$repo/tests/budget.sh" "$w/tests/"
    (cd "$repo" && git ls-files "*.tla" ".claude/skills/*/SKILL.md") | while IFS= read -r f; do
      mkdir -p "$w/$(dirname "$f")"; cp "$repo/$f" "$w/$f"; done
    while [ "$(wc -l < "$w/$rel")" -le "$max" ]; do echo '\* audit pad' >> "$w/$rel"; done
    if (cd "$w" && sh tests/budget.sh) >> "$RUN/13.log" 2>&1; then echo "accepted $rel at $(wc -l < "$w/$rel" | tr -d ' ') lines" >> "$RUN/13.log"; ok=1; fi
  done
  result 13 $ok "budget.sh refuses HudBlip, HudNoise and MeetPause past their 2026-11-22 size" "$(grep -m 1 accepted "$RUN/13.log")"
}

cmd_all() {
  fail=0
  for n in baseline 2 3 10 11 13; do "cmd_$n" || fail=1; done
  return $fail
}

case "${1:-}" in
  doctor) cmd_doctor ;;
  baseline|2|3|10|11|13|all) "cmd_$1"; code=$?; echo "evidence $RUN"; exit $code ;;
  *) echo "usage: check.sh doctor|baseline|2|3|10|11|13|all" >&2; exit 2 ;;
esac
