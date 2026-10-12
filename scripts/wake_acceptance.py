#!/usr/bin/env python3
"""Evaluate paired, explicitly labeled wake attempts. Never infer intent from text."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

RULES = {"relaxed_d2", "followup", "auto_b"}
CONTEXTS = ("after_input", "idle")


def evaluate(manifest: dict) -> dict:
    rules = manifest.get("rules", [])
    if not rules or len(set(rules)) != len(rules) or not set(rules) <= RULES:
        raise ValueError("rules must contain distinct candidate rules")
    exposure = manifest.get("negativeExposureHours", {})
    for context, hours in exposure.items():
        if context not in CONTEXTS or isinstance(hours, bool) or not isinstance(hours, (int, float)) or not math.isfinite(hours) or hours < 0:
            raise ValueError("negativeExposureHours requires finite nonnegative hours for each known context")
    safety = manifest.get("safetyIncidents")
    if isinstance(safety, bool) or not isinstance(safety, int) or safety < 0:
        raise ValueError("safetyIncidents must be an explicit nonnegative integer")
    attempts = manifest.get("attempts", [])
    seen = set()
    unknown = 0
    for attempt in attempts:
        identity = attempt.get("id")
        if not isinstance(identity, str) or not identity or identity in seen:
            raise ValueError("each attempt needs a unique nonempty id")
        seen.add(identity)
        if attempt.get("context") not in (*CONTEXTS, "unknown"):
            raise ValueError("attempt context must be after_input, idle, or unknown")
        unknown += attempt["context"] == "unknown"
        if type(attempt.get("intent")) is not bool or type(attempt.get("strictWake")) is not bool:
            raise ValueError("intent and strictWake require explicit boolean labels")
        decisions = attempt.get("wakes", {})
        if set(decisions) != set(rules) or any(type(value) is not bool for value in decisions.values()):
            raise ValueError("every attempt must have one boolean wake decision per candidate rule")
    rows = []
    for rule in rules:
        for context in CONTEXTS:
            selected = [item for item in attempts if item["context"] == context]
            intended = sum(item["intent"] for item in selected)
            strict_miss = sum(item["intent"] and not item["strictWake"] for item in selected)
            candidate_miss = sum(item["intent"] and not item["wakes"][rule] for item in selected)
            strict_false = sum(not item["intent"] and item["strictWake"] for item in selected)
            candidate_false = sum(not item["intent"] and item["wakes"][rule] for item in selected)
            hours = exposure.get(context, 0)
            strict_rate = strict_false / hours if hours else None
            candidate_rate = candidate_false / hours if hours else None
            reasons = []
            if safety:
                verdict = "fail"
                reasons.append("unintended delivery or transmission")
            elif hours < 10 or intended < 50 or unknown:
                verdict = "inconclusive"
                reasons.append("need 10 negative hours and 50 intended attempts per context, with no unknown contexts")
            elif strict_miss == 0:
                verdict = "inconclusive"
                reasons.append("strict baseline has no misses; relative reduction cannot be established")
            else:
                accepted = candidate_rate <= 0.5 and candidate_rate <= strict_rate + 0.1 and candidate_miss <= strict_miss * 0.7
                verdict = "pass" if accepted else "fail"
                if not accepted:
                    reasons.append("candidate does not meet the frozen false-wake or MISS reduction gate")
            rows.append({
                "rule": rule, "context": context, "verdict": verdict, "reasons": reasons,
                "negative_hours": hours, "intended_attempts": intended,
                "strict_false_wakes": strict_false, "candidate_false_wakes": candidate_false,
                "strict_fw_per_hour": strict_rate, "candidate_fw_per_hour": candidate_rate,
                "strict_misses": strict_miss, "candidate_misses": candidate_miss,
                "strict_miss_percent": strict_miss * 100 / intended if intended else None,
                "candidate_miss_percent": candidate_miss * 100 / intended if intended else None,
                "relative_miss_reduction": 1 - candidate_miss / strict_miss if strict_miss else None,
            })
    verdict = "fail" if any(row["verdict"] == "fail" for row in rows) else "inconclusive" if any(row["verdict"] == "inconclusive" for row in rows) else "pass"
    return {"verdict": verdict, "unknown_context_attempts": unknown, "safety_incidents": safety, "results": rows,
            "note": "Paired manifest evaluation only. Native checks and label quality require separate review. Failed candidates remain disabled; this tool never edits configuration."}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    try:
        result = evaluate(json.loads(args.manifest.read_text(encoding="utf-8")))
    except (ValueError, TypeError, KeyError, OSError) as error:
        parser.error(str(error))
    text = json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    else:
        print(text, end="")
    return 0 if result["verdict"] == "pass" else 1 if result["verdict"] == "fail" else 3


if __name__ == "__main__":
    raise SystemExit(main())
