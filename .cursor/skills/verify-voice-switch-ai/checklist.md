# Issue 50 checklist

Each item is checked by `drive.py` or by `python3 -m unittest discover -s tests/ai -v`. The unittest process is item V00.

| ID | Spec | Proof |
| --- | --- | --- |
| V00 | Unit tests cover the cascade, permissions, and safety | `unittest` exits 0 |
| V01 | `#49` JSONL is read and not modified | `try` prints `journal_unchanged=yes` |
| V02 | Derived directory is 0700 and a derived file is 0600 | `stat` on the `try` output |
| V03 | One local marker routes to the tray without Jev | stdout contains `triage: local=task@1.00 -> tray` |
| V04 | Zero or several markers, cloud off, stay a memo | stdout contains `triage: local=ambiguous jev=off -> memo` |
| V05 | `ミューズ、` plus a read-only verb runs locally | stdout contains `triage: muse=research@1.00 -> execute` |
| V06 | `ミューズ、` plus a write stays a draft | stdout contains `triage: muse=agent@1.00 -> tray` and the issue row has `externalWrite` false |
| V07 | Title, summary, tags, filler cleanup, deadline | `notes.jsonl` for `e2` |
| V08 | Fragment merge and duplicate collapse | tray source ids `e9`,`e10`,`e11` and one copy of the sentence |
| V09 | Daily digest sections | `digests/2026-10-23.json` keys and a `請求書` open loop |
| V10 | Weekly digest and conclusion change | `digests/2026-W43.json` and `recall: conclusion-change e6 e16` |
| V11 | Research queue and agent staging | `research.jsonl` and `agent-staging.jsonl` |
| V12 | Related past | `recall: e16 related=e6` |
| V13 | Search | `try` prints `search: conclusion-change` |
| V14 | Probe has 30 to 40 labeled cases, including edges | `tray_jev_probe.py --list` |
| V15 | Threshold is computed, and 0.8 is not the floor | `--score` prints `recommended_min_confidence: 0.92` and `pane floor 0.8 is not used` |
| V16 | Doctor shows cloud off and no floor | `doctor` stdout |
| V17 | Second triage keeps the same tray bytes | two `triage` runs |
| V18 | No GitHub write URL in derived files | grep of the `try` output directory |
| V19 | Fake API key does not turn into `jev=error` | `try` with `TYPESAFE_API_KEY=fake-not-a-real-key` |

Live TypeSafe calibration is not a checklist failure of the CLI. This environment has no `TYPESAFE_API_KEY` and no `JEV_API_KEY`. `dictation.ai.jevMinConfidence` stays unset, so triage does not call Jev. The 0.92 figure is the synthetic score fixture, not a live floor.

Mac app wiring and Windows app wiring are out of this CLI. Mark those surfaces Mac 未検証 and Windows 未検証.

## Spec review

Reviewed against issue 50, and against the safety rules cited from issues 48 and 49.

- Pipeline destinations memo, journal, tray, and execute are the `route` field.
- Local marker, then Jev, then memo. Jev choices are task, research, draft, agent, and none.
- The log shape `triage: jev=task@0.87 -> tray` is produced by the unit test, not by `try` (cloud is off).
- Secondary tags stay on the note. Jev's request body has no title and no tags.
- Read-only muse actions are `summarize` and `recall`. Paste, send, and GitHub are rejected at config load.
- `tray approve` writes `approved` and prints `approved locally; no write API called`.
- Original journal bytes are not rewritten. Derived files are a sibling directory.
- No open pull request for issue 49 was found, so the reader is `JsonlJournalReader` on main's tree.

No spec bullet was dropped in this review. Live Jev and the two desktop apps remain unrun.
