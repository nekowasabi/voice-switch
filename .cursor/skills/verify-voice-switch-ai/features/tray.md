# Tray

The tray is a list of drafts. Approval changes a local status field. It does not call GitHub, paste, or send.

## Sub-features

- `tray-list` prints the draft rows.
- `tray-issue` keeps the GitHub wording as `kind` `issue` and `status` `draft`.
- `tray-approve` sets `approved` and prints `approved locally; no write API called`.
- `tray-rerun` keeps that approval after another triage of the same journal.

## How to get to it (user POV)

- Run `python3 scripts/voice-switch-ai try`, then `python3 scripts/voice-switch-ai tray list --out <the printed out path>`.
- Run `python3 scripts/voice-switch-ai tray approve --out <dir> --id <tray id>`.

## Driving it with voice-switch-ai

Preconditions:

- `python3 scripts/voice-switch-ai try` has just exited 0, or `triage` has written the `--out` directory.

- **List.** Run `python3 scripts/voice-switch-ai tray list --out /tmp/voice-switch-ai-try`. Exit code is 0. Stdout contains `"externalWrite": false` and `tray: count=`.
- **Issue draft.** In that JSON, the row with `"kind": "issue"` has `"status": "draft"` and `"explicitIntent": true`.
- **Approve.** Run `python3 scripts/voice-switch-ai tray approve --out /tmp/voice-switch-ai-try --id tray-e2`. Exit code is 0. Stdout is `approved locally; no write API called`. A second `tray list` shows `tray-e2` as `approved` and `externalWrite` false.
- **Proof.** Save both stdout transcripts. Read `approvals.jsonl` and confirm the tray id is there.

## Gotchas

- `tray list` does not read the journal. Run `triage` or `try` first.
- Approving an unknown id exits 1 and prints `tray: missing`.
- Approval does not create a GitHub issue. There is no write client in this CLI.
