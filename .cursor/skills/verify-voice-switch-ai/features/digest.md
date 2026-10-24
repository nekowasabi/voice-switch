# Digest

A digest groups one day or one ISO week. It lists themes, decisions, stuck points, open loops, conclusion changes, and a handoff line.

## Sub-features

- `digest-daily` writes `digests/2026-10-23.json` for the sample.
- `digest-weekly` writes `digests/2026-W43.json`.
- `digest-open-loop` names `請求書` on 2026-10-23.
- `digest-handoff` includes the handoff sentence.

## How to get to it (user POV)

- Run `python3 scripts/voice-switch-ai try`.
- Run `python3 scripts/voice-switch-ai digest daily --journal tests/fixtures/ai/journal --out <dir> --date 2026-10-23`.
- Run `python3 scripts/voice-switch-ai digest weekly --journal tests/fixtures/ai/journal --out <dir> --date 2026-10-23`.

## Driving it with voice-switch-ai

Preconditions:

- The sample journal contains `2026-10-23.jsonl` and `2026-10-24.jsonl`.
- `--out` is outside that directory.

- **Daily file.** Run `python3 scripts/voice-switch-ai digest daily --journal tests/fixtures/ai/journal --out /tmp/voice-switch-ai-digest --date 2026-10-23`. Exit code is 0. The printed JSON has `決めたこと`, `引っかかっていること`, `openLoops`, `themes`, `結論の変化`, and `handoff`.
- **Open loop.** `openLoops` includes an object whose `theme` is `請求書`.
- **Weekly file.** Run the weekly command with the same `--out` and `--date 2026-10-23`. Exit code is 0. `kind` is `weekly` and `week` is `2026-W43`. `結論の変化` is non-empty.
- **Proof.** Save the JSON documents next to the command transcript in `reports/verify-voice-switch-ai/proof.txt`.

## Gotchas

- `digest` runs triage again and rewrites the derived directory.
- An unknown `--date` exits 1 with `digest: missing`.
- The handoff is text in the JSON file, not a message sent anywhere.
