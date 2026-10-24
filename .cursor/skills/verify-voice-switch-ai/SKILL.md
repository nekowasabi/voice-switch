---
name: verify-voice-switch-ai
description: "Drive voice-switch-ai, the offline CLI that triages a JSONL journal into a proposal tray, digests, and search. Use when checking triage, tray, digest, search, or tray_jev_probe behavior."
---

# Verify voice-switch-ai

voice-switch-ai is a short-lived CLI. There is no server. One command reads a journal and writes a derived directory. The journal files stay unchanged. Cloud calls stay off unless a config file sets `dictation.ai.cloud` to true and `dictation.ai.jevMinConfidence` to a number.

The Mac menu-bar app and the Windows tray app do not call this CLI yet. A run here does not prove those apps. Say Mac 未検証 and Windows 未検証 for any check that needs those apps or those operating systems.

## Launch

From the repository root:

```sh
python3 scripts/voice-switch-ai doctor
```

Ready when the process exits 0 and stdout contains both `cloud=off` and `ready=yes`. The sample journal is `tests/fixtures/ai/journal`. No migration step exists. Each later command gets its own `--out` directory. Do not point `--out` inside the journal directory. The CLI rejects that.

`python3 scripts/voice-switch-ai try` is the one-shot path. It deletes and rewrites only `$TMPDIR/voice-switch-ai-try` (on this Linux host, `/tmp/voice-switch-ai-try`).

## Doctor

```sh
python3 scripts/voice-switch-ai doctor
```

Stdout must include `cloud=off`, `jevMinConfidence=unset`, `fixture=yes`, and `ready=yes`. This reports the built-in default, not a user's config file. If `ready=no`, the sample journal is missing and the drive stops.

## Drive

The harness is the CLI plus `python3 .cursor/skills/verify-voice-switch-ai/drive.py`. Prefer `drive.py` for the whole checklist. For one feature, run the command in that feature file with a fresh `--out`.

Stable handles are the subcommands `doctor`, `try`, `triage`, `digest`, `tray`, `search`, and `scripts/tray_jev_probe.py --list`. Log lines start with `triage:` or `recall:`. Derived files are `tray.jsonl`, `notes.jsonl`, `executions.jsonl`, `research.jsonl`, `agent-staging.jsonl`, `decisions.jsonl`, and `digests/*.json`.

## Evidence

Write the proof to `reports/verify-voice-switch-ai/proof.txt`. Capture the command, the exit code, and the stdout lines the checklist names. Also record `journal_unchanged=yes` and one `externalWrite` field from `tray.jsonl`. A passing screenshot is not the proof. The proof is the CLI transcript plus the derived files.

`try` must not open a socket. The checklist sets `TYPESAFE_API_KEY` to a fake value and requires `jev=off` rather than `jev=error`.

Mocks are allowed only for the Jev client inside `python3 -m unittest discover -s tests/ai -v`. The `try` path uses the real CLI and the real sample journal.

## Cleanup

```sh
python3 .cursor/skills/verify-voice-switch-ai/drive.py --cleanup
```

Cleanup deletes the scratch directory recorded in `reports/verify-voice-switch-ai/scratch-path.txt`. It does not delete `reports/verify-voice-switch-ai/proof.txt`. It does not delete the sample journal. Kill only processes this drive started. There is no long-lived process after the CLI exits.

## Helpers

Run the whole checklist:

```sh
python3 .cursor/skills/verify-voice-switch-ai/drive.py
```

The script prints one line per item, `PASS` or `FAIL`, and exits 0 only when every item passes. Feature recipes live in `features/`. The spec map is `checklist.md`.

After a product change, update the feature file in the same change. Use `/maintain-verification-skill` to keep the map aligned with the CLI.
