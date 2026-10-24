# Delete a day or an entry

The user can drop one calendar day or one utterance without a viewer. Surviving lines stay byte-identical. Clearing the on-screen history is not this operation.

## Sub-features

- `delete-day` removes one offset-date in directory mode and in single-file mode. An unknown day removes nothing.
- `delete-entry` removes one id. A missing id leaves the file bytes unchanged.

## How to get to it (user POV)

- There is no journal window and no new menu item.
- Day deletion removes that day's `yyyy-MM-dd.jsonl`, or the matching lines when `journalDir` is a single `.jsonl` file.
- Entry deletion removes the line with that `id`.
- The on-screen history Clear control does not call these operations.

## Driving it with verify-journal

Preconditions:

- `$V doctor` printed `doctor: ready`.
- The scratch journal already has two days and two ids. `drive` builds that inside the test.

- **Delete a day.** Run `.cursor/skills/verify-dictation-journal/verify.sh drive delete-journal`. Stdout contains `PASS delete-day`. The other day's line is byte-identical to what it was before the call. An unknown day removes nothing and returns 0.
- **Delete an entry.** The same run contains `PASS delete-entry`. A missing id returns false and leaves the file bytes unchanged.
- **Proof.** `stdout.txt` contains those two PASS lines and `exitcode.txt` is `0`.

## Gotchas

- Directory mode deletes the day's file. Single-file mode filters lines. Both must keep other days byte-identical.
- The date is the timestamp's own offset date, not the UTC date and not the host's local zone.
- A missing id that still rewrites the file fails, even if the JSON is equivalent.
- Do not add a HUD button to make this recipe easier to click. The issue leaves the viewer undecided.
