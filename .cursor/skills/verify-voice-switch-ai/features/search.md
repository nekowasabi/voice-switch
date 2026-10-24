# Search

Search ranks past notes for a query and shows a conclusion change when two decisions about the same theme disagree.

## Sub-features

- `search-hit` finds the sample notes that mention `保存先`.
- `search-change` prints `search: conclusion-change`.
- `search-related` records `recall: e16 related=e6` during triage.

## How to get to it (user POV)

- Run `python3 scripts/voice-switch-ai try`.
- Run `python3 scripts/voice-switch-ai search --journal tests/fixtures/ai/journal --out <dir> 保存先`.

## Driving it with voice-switch-ai

Preconditions:

- The sample journal is present.
- `--out` is outside the journal directory.

- **Query.** Run `python3 scripts/voice-switch-ai search --journal tests/fixtures/ai/journal --out /tmp/voice-switch-ai-search 保存先`. Exit code is 0. Stdout contains `search: hits=` and `search: conclusion-change`. The JSON array includes an object whose `conclusionChange` contains `保存先`.
- **Empty query.** Run the same command with an empty query string. Exit code is 2.
- **Proof.** Save stdout. Confirm the journal directory's file bytes are unchanged by hashing `tests/fixtures/ai/journal` before and after.

## Gotchas

- Search is local character overlap. It does not call an embedding API.
- `search` rewrites the derived directory, same as triage.
- An empty query is an error. A query with no overlap prints `search: hits=0` and exits 0.
