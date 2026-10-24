# Probe

The probe holds 40 labeled utterances and scores a confidence floor from answers. It does not reuse the pane floor of 0.8.

## Sub-features

- `probe-list` prints the case count and the edge tags.
- `probe-score` scores the synthetic answer file offline.
- `probe-live` calls TypeSafe only when an API key is present.

## How to get to it (user POV)

- Run `python3 scripts/tray_jev_probe.py --list`.
- Run `python3 scripts/tray_jev_probe.py --score tests/fixtures/ai/jev_score_synthetic.json`.
- Run `python3 scripts/tray_jev_probe.py` after placing `TYPESAFE_API_KEY` or `JEV_API_KEY` in the environment.

## Driving it with voice-switch-ai

Preconditions:

- The repository root is the working directory.
- For `--list` and `--score`, no API key is required.

- **List.** Run `python3 scripts/tray_jev_probe.py --list`. Exit code is 0. Stdout contains `cases=40` and `edge=backchannel`, `edge=rhetorical`, `edge=decision`, `edge=mixed`, and `edge=fragment`.
- **Score.** Run `python3 scripts/tray_jev_probe.py --score tests/fixtures/ai/jev_score_synthetic.json`. Exit code is 0. Stdout contains `source: synthetic`, `recommended_min_confidence: 0.92`, and `pane floor 0.8 is not used`.
- **Proof.** Append that stdout to `reports/verify-voice-switch-ai/proof.txt`. If the live command is not run, write `live calibration: not run` in the same file.

## Gotchas

- The 0.92 value comes from the synthetic file. Do not copy it into `jevMinConfidence` as if it were a live result.
- The live command exits with a message naming `TYPESAFE_API_KEY` or `JEV_API_KEY` when neither variable is set.
- Do not print the key. The probe sends utterance text only when the key is set and the live command is used.
