# Append on accept

When dictation keeps an utterance, the same text is appended to the journal. Clearing the on-screen history, or waiting out the 30 minute cap, does not remove that line. Utterances the history rejects are not stored.

## Sub-features

- `history-hook` appends one JSONL line when Add accepts the utterance.
- `rejected-add-skips-journal` writes nothing when Add returns false.
- `fields` writes the seven required keys, with origin and an offset timestamp.
- `origin-automatic` stores `自動`.
- `origin-wake` stores `ウェイク`.
- `origin-manual` stores `手動`.
- `memory-bounds` keeps the 30 minute / 50 entry memory window.
- `memory-clear-keeps-journal` leaves the file in place after Clear.
- `memory-evict-keeps-journal` leaves the file in place after prune or eviction.
- `text-only` writes no audio.
- `flush-visible` makes the line readable as soon as Add returns.
- `both-sources` has the hook in Swift and C#.

## How to get to it (user POV)

- Dictate after a wake word, after the manual hotkey (Ctrl+Shift+F10), or via an automatic session, so the utterance is kept in history.
- Open the journal directory in a text editor. There is no in-app viewer.
- Clear the on-screen history. The file must still contain the line.

## Driving it with verify-journal

Preconditions:

- `$V doctor` printed `doctor: ready`.
- No scratch directory is being reused. `drive` creates a new one.

- **Accept an utterance.** Add a wake-word utterance with an end reason and a duration. Run `.cursor/skills/verify-dictation-journal/verify.sh drive append-on-add`. Stdout contains `PASS history-hook`, `PASS fields`, `PASS origin-wake`, and `PASS flush-visible`.
- **Reject an utterance.** The same run covers blank text, overlong text, duplicate ids, a stale generation, and a rejected automatic transcript. Stdout contains `PASS rejected-add-skips-journal`.
- **Classify origin.** The same run covers automatic, wake, and manual. Stdout contains `PASS origin-automatic`, `PASS origin-wake`, and `PASS origin-manual`.
- **Keep memory rules.** The same run checks capacity, retention, Clear, and eviction. Stdout contains `PASS memory-bounds`, `PASS memory-clear-keeps-journal`, and `PASS memory-evict-keeps-journal`.
- **Text only.** Stdout contains `PASS text-only`.
- **Proof.** The evidence directory named in the script output holds `stdout.txt` with those PASS lines and `exitcode.txt` containing `0`.

## Gotchas

- Add returns false for text the policy drops. A missing journal line there is success, not a lost utterance.
- Clear on the history window is the in-memory clear. It must not be wired to DeleteDay.
- `origin` values are the Japanese tokens from the issue, not the wake-rule names `strict` and `auto_b`.
- A unit test that writes the JSONL itself, without `DictationHistory.Add`, does not satisfy `history-hook`.
- The write stays inside `DictationHistory.Add` and `DictationHistory.add`. A write from `Finish` or a new `FinishReason` does not satisfy `history-hook`. Issue #48 owns the end-reason state machine.
