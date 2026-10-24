"""Threshold sweep for tray choices. The pane floor is not an input."""

from __future__ import annotations

TRAY = {"task", "research", "draft", "agent"}


def apply_threshold(choice: str, confidence: float, threshold: float) -> str:
    if choice in TRAY and confidence >= threshold:
        return choice
    return "none"


def score_at(rows: list[tuple[str, float, str]], threshold: float) -> dict:
    predicted_none = 0
    true_none = 0
    tray_predictions = 0
    tray_but_none = 0
    correct_tray_kept = 0
    for choice, confidence, expected in rows:
        predicted = apply_threshold(choice, confidence, threshold)
        if predicted == "none":
            predicted_none += 1
            if expected == "none":
                true_none += 1
        else:
            tray_predictions += 1
            if expected == "none":
                tray_but_none += 1
            elif predicted == expected:
                correct_tray_kept += 1
    return {
        "none_precision": (true_none / predicted_none) if predicted_none else None,
        "tray_noise": (tray_but_none / tray_predictions) if tray_predictions else 0.0,
        "correct_tray_kept": correct_tray_kept,
    }


def recommend(rows: list[tuple[str, float, str]]) -> dict:
    raw_none = [row for row in rows if row[0] == "none"]
    raw_none_hit = sum(1 for choice, _confidence, expected in raw_none if expected == "none")
    raw_tray = [row for row in rows if row[0] in TRAY]
    raw_noise = sum(1 for _choice, _confidence, expected in raw_tray if expected == "none")
    thresholds = sorted({round(confidence, 2) for _choice, confidence, _expected in rows})
    chosen = None
    chosen_stats = None
    for threshold in thresholds:
        stats = score_at(rows, threshold)
        if stats["tray_noise"] == 0 and stats["correct_tray_kept"] > 0:
            chosen = threshold
            chosen_stats = stats
            break
    return {
        "recommended_min_confidence": chosen,
        "at_recommendation": chosen_stats,
        "raw_none_precision": (raw_none_hit / len(raw_none)) if raw_none else None,
        "raw_tray_noise": (raw_noise / len(raw_tray)) if raw_tray else 0.0,
        "rows": len(rows),
    }


def format_report(result: dict, source: str) -> str:
    value = result["recommended_min_confidence"]
    shown = "none" if value is None else f"{value:.2f}"
    stats = result["at_recommendation"] or {}
    none_precision = stats.get("none_precision")
    none_shown = "n/a" if none_precision is None else f"{none_precision:.2f}"
    noise = stats.get("tray_noise")
    noise_shown = "n/a" if noise is None else f"{noise:.2f}"
    raw_none = result["raw_none_precision"]
    raw_none_shown = "n/a" if raw_none is None else f"{raw_none:.2f}"
    lines = [
        f"source: {source}",
        f"rows: {result['rows']}",
        f"raw_none_precision: {raw_none_shown}",
        f"raw_tray_noise: {result['raw_tray_noise']:.2f}",
        f"recommended_min_confidence: {shown}",
        f"none_precision_at_recommendation: {none_shown}",
        f"tray_noise_at_recommendation: {noise_shown}",
        "pane floor 0.8 is not used. Set dictation.ai.jevMinConfidence from this report after a live run.",
    ]
    return "\n".join(lines)
