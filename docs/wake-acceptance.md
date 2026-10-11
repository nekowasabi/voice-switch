# Wake acceptance gates

These gates are fixed before the no-wake-word implementation and any new evaluation. They are proposed engineering defaults for issue #17, not measured results. Automatic wake remains disabled by default.

## Safety gate

Every automatic wake must remain local. It must never paste, change the clipboard without an explicit selection, invoke an action URL, route to tmux, call Jev, or send audio to a cloud transcription model. A later strict wake or finish hotkey must not upgrade an automatic session. A failed input-idle query must fail closed.

Manual paste must first retain the result in history. It must stop when focus cannot be confirmed, the target may be a password field, IME composition cannot be ruled out, clipboard contents cannot be restored, or input resumed. Unknown safety state means history only.

## Accuracy gate

Evaluate `strict`, `relaxed_d2`, `followup`, and `auto_b` separately. Keep the baseline configuration and model versions fixed. For each candidate rule and each input context, collect at least 10 hours of labeled negative exposure and 50 intended attempts. Keep a held-out session separate from tuning.

- False wakes per hour must be at most 0.5, and no more than 0.1 above the strict baseline on the same labeled exposure.
- MISS percentage must fall by at least 30% relative to the strict baseline, without increasing the absolute number of misses in either context.
- Any unintended paste or external transmission fails immediately, regardless of accuracy.
- Report insufficient exposure, missing labels, and unavailable input-idle samples as inconclusive. Never infer intended speech from recognized text.

The two contexts are `after_input`, from 0 through less than 3 seconds since keyboard or mouse activity, and `idle`, from 3 seconds onward. `unknown` is not assigned to either. These are coarse input-activity proxies, not proof that the user was typing. Windows GetLastInputInfo does not distinguish keyboard from mouse.

The MISS shortcut records an operator-provided label for the most recent eligible recognition, rather than guessing a missed intent. False-wake labels and exposure durations must also come from a reviewed evaluation manifest. Recognition counts alone are not FW/h or MISS%.

## Native gate

Before enabling automatic wake in daily use, complete `docs/no-wake-word/native-validation.md`. Linux Swift checks do not verify the macOS app. No real audio or synthesized personal text is used by the VM checks. S1b and S6 remain open until a human supplies native results and labeled exposure.

## Windows MISS shortcut

Press Ctrl+Shift+F8 within 10 seconds of a missed recognition in the tray app. The shortcut logs `wake label: MISS id=N rule=... inputIdleSeconds=...` once for that recognition. It uses RegisterHotKey, does not monitor arbitrary keys, and reports a registration conflict instead of stealing another application's shortcut. A MISS label marks intent only. It neither starts recording nor authorizes delivery.

`inputIdleSeconds=-1` means unavailable. Old log lines remain readable and are counted in `missing_fields`. Do not count those as idle observations.

## Evaluate a reviewed manifest

Run `python3 scripts/wake_acceptance.py manifest.json --out report.json`. Exit 0 means the supplied labels meet the numeric gates. Exit 1 means failure. Exit 3 means insufficient evidence. A numeric pass does not replace native checks or a review of label quality.

The manifest has `rules`, `negativeExposureHours`, `safetyIncidents`, and `attempts`. Rules are selected from `relaxed_d2`, `followup`, and `auto_b`. Exposure has `after_input` and `idle` hours. Every attempt has a unique `id`, a `context`, a human-reviewed boolean `intent`, the baseline boolean `strictWake`, and a `wakes` object with one boolean per candidate rule. No transcript or audio belongs in the manifest. Evaluate both contexts on the same paired exposure. Keep failed candidates disabled rather than changing the acceptance thresholds.

An empty evaluation is intentionally inconclusive. The repository's synthetic test manifests test the evaluator, not real-world wake accuracy.
