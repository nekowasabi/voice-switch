#!/bin/sh
# Mutants for check items 6, 7 and 9 of SKILL.md: each breaks one guard in a copy of the app sources or of
# scripts/memo-inbox-add or scripts/mail-send; the named check must FAIL on the copy. \n in a field is a newline.
# Usage: sh .claude/skills/verify-triage-execution/code-mutants.sh   (about 3 min)
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$dir/../../.." && pwd)
work="${TMPDIR:-/tmp}/verify-triage-execution/code-mutants-$$"
mkdir -p "$work"
trap 'rm -rf "$work"' EXIT
i=0
while IFS='|' read -r name want file old new; do
  i=$((i + 1))
  copy="$work/$i"
  mkdir -p "$copy"
  case $file in
  scripts/*) cp "$repo/$file" "$copy/target" ;;
  *) cp -R "$repo/Sources/voice-switch/." "$copy/"; mv "$copy/$(basename "$file")" "$copy/target" ;;
  esac
  if [ -n "$old" ] && ! OLD="$old" NEW="$new" perl -0pi -e 'BEGIN { ($o, $n) = map { s/\\n/\n/gr } @ENV{qw(OLD NEW)} } s/\Q$o\E/$n/ or die "anchor missing\n"' "$copy/target"; then
    echo "MUTANT $name: anchor missing (update code-mutants.sh)"; continue
  fi
  case $file in
  scripts/memo-inbox-add) out=$(cd "$repo" && MEMO_INBOX_ADD="$copy/target" python3 -m unittest tests.test_memo_inbox_add 2>&1 || true) ;;
  scripts/mail-send) out=$(cd "$repo" && MAIL_SEND="$copy/target" python3 -m unittest tests.test_mail_send 2>&1 || true) ;;
  *) mv "$copy/target" "$copy/$(basename "$file")"
     out=$(TRIAGE_SOURCES="$copy" sh "$repo/tests/triage/run.sh" 2>&1 || true) ;;
  esac
  if printf '%s\n' "$out" | grep -Eq "^(FAIL|ERROR):? $want"; then echo "CAUGHT $name by $want"
  else echo "MISSED $name (want FAIL $want)"; printf '%s\n' "$out" | tail -n 20; fi
done <<'EOF' | tee "$work/out"
inbox-glues-cut-line|test_a_cut_last_line_is_kept_and_the_new_line_starts_fresh|scripts/memo-inbox-add|                line = b"\n" + line|                pass
inbox-fails-silently|test_an_unwritable_inbox_fails_and_writes_nothing|scripts/memo-inbox-add|        sys.exit(f"memo-inbox-add: {error.strerror}: {path}")|        return
inbox-truncates|test_earlier_bytes_never_change|scripts/memo-inbox-add|os.O_APPEND|os.O_TRUNC
mail-embeds-text|test_quotes_in_the_text_or_recipient_never_become_applescript|scripts/mail-send|exec osascript - "$1" "$subject" "$body" <<'APPLESCRIPT'\non run argv\n    set {recipientAddress, subjectLine, messageBody} to argv|exec osascript - <<APPLESCRIPT\non run argv\n    set {recipientAddress, subjectLine, messageBody} to {"$1", "$subject", "$body"}
mail-subject-is-body|test_the_first_line_is_the_subject_and_the_whole_text_is_the_body|scripts/mail-send|"$1" "$subject" "$body"|"$1" "$body" "$body"
mail-hides-failure|test_a_send_osascript_refuses_exits_non_zero|scripts/mail-send|exec osascript -|trap 'exit 0' EXIT; osascript -
mail-any-recipient|test_without_exactly_one_recipient_nothing_is_sent|scripts/mail-send|if [ "$#" -ne 1 ]|if false
previous-window-61|request|Sources/voice-switch/TriageRequest.swift|age <= 60|age <= 61
previous-three|request|Sources/voice-switch/TriageRequest.swift|.suffix(2)|.suffix(3)
previous-other-device|request|Sources/voice-switch/TriageRequest.swift|row.device == device && |
previous-pasted|request|Sources/voice-switch/TriageRequest.swift|row.entry.eligibleForTriage && |
send-bundle-id|request|Sources/voice-switch/TriageRequest.swift|app: note.focus?.name.flatMap|app: note.focus?.bundleId.flatMap
verify-route-in-batch|request|Sources/voice-switch/TriageRequest.swift|let batch = questions.filter { TriageClassifier.questionIDs.contains($0.key) }|let batch = questions
verify-without-route|request|Sources/voice-switch/TriageRequest.swift|state.route = route|_ = route
hash-escapes-slash|question hash|Sources/voice-switch/TriageFiles.swift|encoder.outputFormatting = .withoutEscapingSlashes|encoder.outputFormatting = []
routes-any-key|routes|Sources/voice-switch/Config.swift|guard routes.keys.allSatisfy(kinds.contains) else {|guard true else {
EOF
! grep -qv '^CAUGHT ' "$work/out"
