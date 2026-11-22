#!/usr/bin/env python3
"""Write tests/parity/fixtures/triage_decide.json: Noul answers -> destinations, from the truth table in
docs/journal-triage-design.md, written here independently of any implementation. Swift, C# and Python read the file.

Rerun after a change to the truth table: python3 tests/parity/make_triage_decide.py
"""
import itertools
import json
from pathlib import Path

KINDS = ("self_task", "look_up", "write_text", "computer_change", "delegate")
QUESTIONS = ("wants_action", "is_filler", *(f"kind_{kind}" for kind in KINDS))
DEFAULT = {"no": 0.3, "yes": 0.7}
# Each band takes its edge values in turn, so 0.7 and 0.3 sit on both sides of every case: 0.69 and 0.31 are unsure.
VALUES = {"yes": (0.7, 0.9, 1), "unsure": (0.31, 0.5, 0.69), "no": (0, 0.1, 0.3)}


def band(value, edges):
    return "yes" if value >= edges["yes"] else "no" if value <= edges["no"] else "unsure"


def decide(answers, thresholds):
    if not isinstance(answers, dict) or any(
            type(answers.get(q)) not in (int, float) or not 0 <= answers[q] <= 1 for q in QUESTIONS):
        return ["unjudged:error"]
    edge = lambda q: thresholds.get(q, DEFAULT)
    intent = band(answers["wants_action"], edge("wants_action"))
    if intent == "yes":
        kinds = [f"action:{k}" for k in KINDS if band(answers[f"kind_{k}"], edge(f"kind_{k}")) == "yes"]
        return kinds or ["unjudged:kind"]
    if intent == "unsure":
        return ["unjudged:intent"]
    return {"yes": ["filler"], "unsure": ["unjudged:content"], "no": ["thought"]}[band(answers["is_filler"], edge("is_filler"))]


def cases():
    out = []
    for n, bands in enumerate(itertools.product(VALUES, repeat=len(QUESTIONS))):
        answers = {q: VALUES[b][(n + i) % 3] for i, (q, b) in enumerate(zip(QUESTIONS, bands))}
        out.append({"name": "bands " + " ".join(f"{q}={b}" for q, b in zip(QUESTIONS, bands)), "answers": answers})

    custom = {**{q: dict(DEFAULT) for q in QUESTIONS}, "wants_action": {"no": 0.2, "yes": 0.6},
              "is_filler": {"no": 0.4, "yes": 0.8}, "kind_look_up": {"no": 0.1, "yes": 0.95}}
    for q in QUESTIONS:
        # Kinds are read only when the speaker wants something done; is_filler only when they do not.
        base = {p: 0.05 for p in QUESTIONS} | ({"wants_action": 0.9} if q.startswith("kind_") else {})
        for edge in custom[q].values():
            for value in sorted({round(edge - 0.01, 2), edge, round(edge + 0.01, 2)} & {x / 100 for x in range(101)}):
                out.append({"name": f"thresholds of its own: {q}={value}", "answers": base | {q: value}, "thresholds": custom})

    filler = {q: 0.05 for q in QUESTIONS} | {"is_filler": 0.95}  # would be filler if the answer were read
    out.append({"name": "no answer (request failed)", "answers": None})
    out += [{"name": f"answers not an object: {json.dumps(bad)}", "answers": bad} for bad in ([], "filler", 0.95)]
    for q in QUESTIONS:
        out.append({"name": f"{q} missing", "answers": {p: v for p, v in filler.items() if p != q}})
        for bad in ("0.95", None, True, -0.01, 1.01, [0.95]):
            out.append({"name": f"{q} is {json.dumps(bad)}", "answers": filler | {q: bad}})
    for case in out:
        case["want"] = decide(case["answers"], case.get("thresholds", {}))
    return out


def main():
    path = Path(__file__).parent / "fixtures" / "triage_decide.json"
    lines = ",\n    ".join(json.dumps(case, ensure_ascii=False) for case in cases())
    path.write_text('{\n  "default": {"no": 0.3, "yes": 0.7},\n  "cases": [\n    ' + lines + "\n  ]\n}\n", encoding="utf-8")
    print(f"{path}: {len(cases())} cases")


if __name__ == "__main__":
    main()
