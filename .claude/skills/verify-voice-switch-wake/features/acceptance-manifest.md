# Acceptance manifest

Acceptance manifest runs `scripts/wake_acceptance.py` on a synthetic labeled manifest. The tool checks exposure, safety incidents, and a relative miss reduction for one rule. It does not read audio and it does not infer intent from text.

## Sub-features

- `pass-manifest` is the scaffolding file that meets the engineering gate for `auto_b`.
- `inconclusive-empty` is an empty manifest the tool refuses to call a pass.
- `safety-incident` is a manifest with one safety incident the tool calls a fail.

## How to get to it (user POV)

- Run `verify.sh drive acceptance` for the pass fixture.
- The other two fixtures are files next to it. Drive them with `python3 scripts/wake_acceptance.py <file>` when you need to see exit 3 or exit 1. They are not part of `verify.sh drive`.

## Driving it with verify-wake

Preconditions:

- `verify.sh doctor` prints `doctor: ok`.
- `python3` can import the standard library `json` module. No package install.

- **Pass.** Run `VS_RUN_ID=acceptance verify.sh drive acceptance`. Exit code 0. Evidence stdout contains `"verdict": "pass"`.
- **Empty exposure.** Run `python3 scripts/wake_acceptance.py .claude/skills/verify-voice-switch-wake/fixtures/inconclusive-manifest.json`. Exit code 3. Stdout contains `"verdict": "inconclusive"`.
- **Safety incident.** Run `python3 scripts/wake_acceptance.py .claude/skills/verify-voice-switch-wake/fixtures/safety-incident-manifest.json`. Exit code 1. Stdout contains `"verdict": "fail"`.

## Gotchas

- Exit 0 here means the evaluator accepted the synthetic manifest. It does not mean more wake words were recognized.
- Exit 3 is inconclusive on purpose (empty exposure, too few intended wakes, or an unknown context). Do not treat it as a broken script.
- A safety incident is exit 1 even when the attempt list is empty. Do not weaken the manifest to get exit 0.
- The tool never edits config. A drive that changes `config.example.windows.json` is not this feature.
- Product logs are not checked in. Do not point the tool at an ad-hoc file and call that the gate.
