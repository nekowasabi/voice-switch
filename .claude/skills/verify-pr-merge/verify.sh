#!/bin/sh
# Merge gate for one open PR: trial-merge it into origin/main in a throwaway worktree, build the Mac binary
# from that tree with verify-voice-switch-mac, drive the PR's feature, and print a verdict. Exit 0 = ready.
set -eu

REPO=$(cd "$(dirname "$0")/../../.." && pwd)
SKILL=$(cd "$(dirname "$0")" && pwd)
ROOT=${VS_PR_ROOT:-$HOME/Library/Caches/voice-switch-pr-verify}
SUPPORTED="58 60 67"

die() { echo "error: $*" >&2; exit 1; }
need_pr() { case " $SUPPORTED " in *" $1 "*) ;; *) die "pr $1 has no feature file; add features/pr-$1.md first" ;; esac; }

# Per-PR layout. The mac harness gets its own VS_VERIFY_ROOT so builds and doctor's commit check never mix PRs.
paths() {
  PR=$1
  WT=$ROOT/wt/pr-$PR
  MAC_ROOT=$ROOT/pr-$PR/mac
  RUN=${VS_RUN_ID:-$(cat "$ROOT/pr-$PR/run.txt" 2>/dev/null || date +%Y%m%d-%H%M%S)}
  OUT=$ROOT/evidence/$RUN/pr-$PR
  V=$WT/.claude/skills/verify-voice-switch-mac/verify.sh
}

report() { echo "$1" | tee -a "$OUT/result.txt"; }

cmd_prepare() {
  paths "$1"
  mkdir -p "$ROOT/pr-$PR" "$OUT"
  echo "$RUN" > "$ROOT/pr-$PR/run.txt"
  : > "$OUT/result.txt"
  git -C "$REPO" fetch -q origin main "+pull/$PR/head:refs/verify/pr-$PR"
  gh pr view "$PR" --json number,state,baseRefName,headRefName,headRefOid,mergeable > "$OUT/pr.json"
  base=$(gh pr view "$PR" --json baseRefName -q .baseRefName)
  state=$(gh pr view "$PR" --json state -q .state)
  [ "$state" = OPEN ] || report "BLOCK state=$state"
  # A ghstack PR targets gh/<user>/<n>/base; GitHub's merge button would land it there, not on main.
  [ "$base" = main ] || report "BLOCK base=$base (GitHub merge does not reach main; retarget with gh pr edit $PR --base main or land with ghstack)"
  [ -d "$WT" ] && git -C "$REPO" worktree remove --force "$WT"
  git -C "$REPO" worktree add -q --detach "$WT" origin/main
  git -C "$WT" rev-parse --short HEAD > "$OUT/main.txt"
  if git -C "$WT" merge -q --no-commit --no-ff "refs/verify/pr-$PR" > "$OUT/merge.txt" 2>&1; then
    echo merged > "$OUT/tree.txt"
    report "PASS merge: clean into origin/main $(cat "$OUT/main.txt")"
  else
    git -C "$WT" diff --name-only --diff-filter=U > "$OUT/conflicts.txt"
    report "BLOCK merge: conflicts with origin/main $(cat "$OUT/main.txt") in $(tr '\n' ' ' < "$OUT/conflicts.txt")"
    # Merge blocked: still build and drive something, but never call it merge-ready (the BLOCK line stays).
    if ! grep -qE '^(Sources|dotnet)/' "$OUT/conflicts.txt"; then
      # Only tests/docs conflict: the app source is the real merge result. Take main's side of the rest.
      # shellcheck disable=SC2046
      git -C "$WT" checkout --ours -- $(cat "$OUT/conflicts.txt")
      echo merged-source-only > "$OUT/tree.txt"
    else
      git -C "$WT" merge --abort
      git -C "$WT" checkout -q --detach "refs/verify/pr-$PR"
      # The PR heads sit on 2c28b7d, which does not compile on Swift 6.4; main fixed that in 27183c9.
      if git -C "$WT" cherry-pick -n 27183c9 > /dev/null 2>&1; then
        echo pr-head+27183c9 > "$OUT/tree.txt"
      else
        git -C "$WT" reset -q --hard
        echo pr-head > "$OUT/tree.txt"
      fi
    fi
  fi
  echo "tree=$(cat "$OUT/tree.txt") worktree=$WT evidence=$OUT"
}

cmd_build() {
  paths "$1"
  [ -x "$V" ] || die "no worktree for pr $PR; run prepare $PR"
  rm -f "$MAC_ROOT/build/release/voice-switch"
  # The mac harness pipes swift build into tail, so its exit code is 0 even on a compile error. Judge by the binary.
  VS_VERIFY_ROOT=$MAC_ROOT sh "$V" build > "$OUT/build.txt" 2>&1 || true
  if [ -x "$MAC_ROOT/build/release/voice-switch" ]; then
    report "PASS swift-build ($(cat "$OUT/tree.txt"))"
  else
    swift build -c release --package-path "$WT" --build-path "$MAC_ROOT/build" > "$OUT/swift-build-full.txt" 2>&1 || true
    report "FAIL swift-build ($(cat "$OUT/tree.txt")): $(grep -m 3 -E '\.swift:[0-9]+.*error|error.*\.swift:[0-9]+' "$OUT/swift-build-full.txt" | tr -d '\033' | sed 's/\[[0-9;]*m//g' | tr '\n' ' ')"
    return 1
  fi
}

cmd_doctor() {
  paths "$1"
  ok=0
  [ -d "$WT" ] && echo "ok   worktree $WT tree=$(cat "$OUT/tree.txt" 2>/dev/null || echo unknown)" || { echo "FAIL worktree missing: run prepare $PR"; ok=1; }
  [ -f "$OUT/result.txt" ] && grep '^BLOCK' "$OUT/result.txt" || true
  if [ -x "$V" ]; then VS_VERIFY_ROOT=$MAC_ROOT sh "$V" doctor || ok=1; fi
  if command -v dotnet > /dev/null 2>&1; then echo "ok   dotnet $(dotnet --version)"; else echo "warn dotnet absent: Windows items stay UNVERIFIED on this host"; fi
  exit $ok
}

# Fails when the given fixed string is absent / present in a file.
has() { if grep -qF -- "$2" "$1"; then report "PASS $3"; else report "FAIL $3: missing '$2' in $1"; fi; }
lacks() { if grep -qF -- "$2" "$1"; then report "FAIL $3: '$2' leaked into $1"; else report "PASS $3"; fi; }

drive_58() {
  VS_VERIFY_ROOT=$MAC_ROOT sh "$V" say wake "音声入力 [[slnc 1500]]" > /dev/null
  # A set secret-named variable whose value also appears in the command, plus a missing one with a shell default.
  VS_VERIFY_API_TOKEN=vsverify-supersecret-42 VS_RUN_ID=$RUN VS_VERIFY_ROOT=$MAC_ROOT \
    sh "$V" run wake "$SKILL/config.secret.json" > "$OUT/drive.txt" 2>&1 || true
  ev=$MAC_ROOT/evidence/$RUN/wake
  cp "$ev/stdout.txt" "$ev/config.json" "$OUT/"
  [ -f "$ev/wake.marker" ] && report "PASS sr-wake-ran: wake.marker" || report "FAIL sr-wake-ran: no wake.marker (command never ran)"
  has "$OUT/stdout.txt" 'command exited 3:' sr-logged
  has "$OUT/stdout.txt" '$VS_VERIFY_MISSING_TOKEN' sr-missing-name
  has "$OUT/stdout.txt" '$VS_VERIFY_API_TOKEN' sr-set-name
  lacks "$OUT/stdout.txt" 's3cret-value' sr-default-hidden
  lacks "$OUT/stdout.txt" 'vsverify-supersecret-42' sr-value-hidden
}

drive_67() {
  mkdir -p "$MAC_ROOT"
  printf '\n  PRマージ検証タスク\n二行目は使わない\n' > "$MAC_ROOT/task.txt"
  VS_VERIFY_ROOT=$MAC_ROOT sh "$V" say busy-speech "[[slnc 1500]] 今日の会議は三時からです [[slnc 3000]]" > /dev/null
  SIMULATE_REALTIME=1 VS_RUN_ID=$RUN VS_VERIFY_ROOT=$MAC_ROOT \
    sh "$V" run busy-speech "$SKILL/config.journal.json" > "$OUT/drive.txt" 2>&1 || true
  ev=$MAC_ROOT/evidence/$RUN/busy-speech
  cp "$ev/stdout.txt" "$ev/config.json" "$MAC_ROOT/task.txt" "$OUT/"
  has "$OUT/stdout.txt" 'dictation recognition: complete' jc-history
  : > "$OUT/journal.jsonl"
  for f in "$ev"/journal/*.jsonl; do [ -f "$f" ] && cat "$f" >> "$OUT/journal.jsonl"; done
  [ -s "$OUT/journal.jsonl" ] && report "PASS jc-journal-written" || { report "FAIL jc-journal-written: no line under $ev/journal"; return 0; }
  has "$OUT/journal.jsonl" '"schemaVersion":1' jc-schema
  has "$OUT/journal.jsonl" '"taskchute":{"name":"PRマージ検証タスク","id":null,"startedAt":null,"source":"file"' jc-task-plain
  lacks "$OUT/journal.jsonl" '"focusApp":null' jc-focus-present
  has "$OUT/journal.jsonl" '"pid":' jc-focus-pid
  lacks "$OUT/journal.jsonl" 'superwhisper' jc-focus-not-superwhisper
}

drive_60() {
  # --simulate never builds AppDelegate, so the hotkey cannot be pressed here. Prove the wiring exists in the tree.
  if grep -q 'MacWakeShortcuts.registerActions' "$WT/Sources/voice-switch/MacApp.swift"; then report "PASS as-registered-at-launch"; else report "FAIL as-registered-at-launch"; fi
  if grep -q 'action-hotkey' "$WT/Sources/voice-switch/MacApp.swift"; then report "PASS as-session-log"; else report "FAIL as-session-log"; fi
  report "MANUAL as-live-press: see features/pr-60.md (real key press on Mac and Windows)"
}

cmd_drive() {
  paths "$1"
  [ -x "$V" ] || die "no worktree for pr $PR; run prepare $PR"
  "drive_$PR"
  command -v dotnet > /dev/null 2>&1 || report "UNVERIFIED windows: dotnet absent (see features/pr-$PR.md for the Windows commands)"
}

cmd_check() {
  need_pr "$1"
  paths "$1"
  rm -f "$ROOT/pr-$PR/run.txt"
  cmd_prepare "$PR"
  paths "$PR"
  if cmd_build "$PR"; then cmd_drive "$PR"; fi
  echo "--- result ($OUT/result.txt)"
  cat "$OUT/result.txt"
  if grep -q '^FAIL' "$OUT/result.txt"; then echo "verdict: fail"; return 1; fi
  if grep -q '^BLOCK' "$OUT/result.txt"; then echo "verdict: blocked"; return 2; fi
  echo "verdict: ready (MANUAL / UNVERIFIED lines still need a human)"
}

# Removes the worktree, the build and the verify ref of each PR. Evidence under $ROOT/evidence stays.
cmd_cleanup() {
  for pr in ${1:-$SUPPORTED}; do
    paths "$pr"
    [ -x "$V" ] && VS_VERIFY_ROOT=$MAC_ROOT sh "$V" cleanup > /dev/null 2>&1 || true
    [ -d "$WT" ] && git -C "$REPO" worktree remove --force "$WT"
    git -C "$REPO" update-ref -d "refs/verify/pr-$pr" 2> /dev/null || true
    rm -rf "$ROOT/pr-$pr"
  done
  git -C "$REPO" worktree prune
  echo "cleanup: evidence kept at $ROOT/evidence"
}

cmd=${1:-}
[ $# -gt 0 ] && shift
case "$cmd" in
  check) [ $# -eq 1 ] || die "usage: check <pr>"; cmd_check "$1" ;;
  prepare) need_pr "$1"; cmd_prepare "$1" ;;
  build) cmd_build "$1" ;;
  doctor) cmd_doctor "$1" ;;
  drive) cmd_drive "$1" ;;
  cleanup) cmd_cleanup "${1:-}" ;;
  *) die "usage: verify.sh check <pr> | prepare|build|doctor|drive <pr> | cleanup [pr]  (pr: $SUPPORTED)" ;;
esac
