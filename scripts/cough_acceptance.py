#!/usr/bin/env python3
"""Evaluate local paired cough runs. Print counts and opaque IDs, never transcripts."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import unicodedata
import wave
from datetime import datetime
from pathlib import Path

PLATFORMS = ("windows", "macos")
VARIANTS = ("baseline", "candidate")
LABELS = ("cough", "throat_clear", "short_speech")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fields(value, names, where):
    require(type(value) is dict and set(value) == set(names.split()),
            f"{where}: missing or unknown fields")


def text(value, where):
    require(type(value) is str and bool(value.strip()), f"{where}: expected nonempty text")


def identifier(value, where):
    require(type(value) is str and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,79}", value),
            f"{where}: expected an opaque ID of at most 80 characters")


def choice(value, allowed, where):
    require(type(value) is str and value in allowed, f"{where}: unknown value")


def integer(value, minimum, where):
    require(type(value) is int and value >= minimum, f"{where}: expected integer >= {minimum}")


def timestamp(value, where):
    text(value, where)
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        require(parsed.utcoffset() is not None, f"{where}: timezone required")
        return parsed
    except ValueError:
        raise ValueError(f"{where}: expected an ISO 8601 timestamp with timezone") from None


def digest(value, where):
    require(type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value),
            f"{where}: expected lowercase SHA-256")


def verified_file(value, root, where):
    fields(value, "path sha256", where)
    text(value["path"], where)
    digest(value["sha256"], where)
    relative = Path(value["path"])
    require(not relative.is_absolute() and ".." not in relative.parts
            and "\\" not in value["path"] and ":" not in value["path"],
            f"{where}: use a relative path inside the input directory")
    try:
        path = (root / relative).resolve(strict=True)
        require(path.is_relative_to(root.resolve()) and path.is_file(),
                f"{where}: file must stay inside the input directory")
        require(0 < path.stat().st_size <= 64 * 1024 * 1024,
                f"{where}: file must contain 1 byte to 64 MiB")
        sha = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(65536), b""):
                sha.update(chunk)
        require(sha.hexdigest() == value["sha256"], f"{where}: SHA-256 mismatch")
    except (OSError, RuntimeError, UnicodeError):
        raise ValueError(f"{where}: cannot read local evidence file") from None
    return path


def normalized(value):
    return "".join(char for char in unicodedata.normalize("NFKC", value).casefold()
                   if not char.isspace() and not unicodedata.category(char).startswith("P"))


def evaluate(manifest, observations, corpus_root, runs_root):
    fields(manifest, "schema_version corpus_source label_source labeler labeled_at cases", "manifest")
    require(type(manifest["schema_version"]) is int and manifest["schema_version"] == 1,
            "manifest: unsupported schema_version")
    choice(manifest["corpus_source"], ("human_recording", "synthetic"), "corpus_source")
    choice(manifest["label_source"], ("human", "synthetic", "unlabeled"), "label_source")
    identifier(manifest["labeler"], "labeler")
    labeled_at = timestamp(manifest["labeled_at"], "labeled_at")
    require(type(manifest["cases"]) is list and manifest["cases"], "cases: expected a nonempty list")
    cases, hashes = {}, set()
    for index, case in enumerate(manifest["cases"]):
        where = f"case[{index}]"
        fields(case, "id label audio expected_transcripts", where)
        identifier(case["id"], where)
        require(case["id"] not in cases, f"{where}: duplicate case ID")
        choice(case["label"], LABELS, where)
        expected = case["expected_transcripts"]
        require(type(expected) is list and all(type(item) is str for item in expected),
                f"{where}: expected_transcripts must be a list of strings")
        require((case["label"] == "short_speech" and expected and all(normalized(item) for item in expected))
                or (case["label"] != "short_speech" and not expected),
                f"{where}: short speech needs references; non-speech needs an empty reference list")
        require(len({normalized(item) for item in expected}) == len(expected),
                f"{where}: duplicate normalized reference")
        path = verified_file(case["audio"], corpus_root, where)
        require(case["audio"]["sha256"] not in hashes, f"{where}: duplicate audio hash")
        try:
            with wave.open(str(path), "rb") as audio:
                require(audio.getnchannels() == 1 and audio.getsampwidth() == 2
                        and audio.getframerate() == 16000 and audio.getcomptype() == "NONE"
                        and audio.getnframes() > 0, f"{where}: expected nonempty mono 16 kHz PCM16 WAV")
                declared_bytes = audio.getnframes() * 2
                require(declared_bytes <= path.stat().st_size, f"{where}: WAV declares more data than the file contains")
                require(len(audio.readframes(audio.getnframes())) == declared_bytes,
                        f"{where}: truncated WAV data")
        except (wave.Error, EOFError, OSError, RuntimeError):
            raise ValueError(f"{where}: cannot read PCM WAV") from None
        cases[case["id"]] = case
        hashes.add(case["audio"]["sha256"])

    fields(observations, "schema_version runs", "observations")
    require(type(observations["schema_version"]) is int and observations["schema_version"] == 1,
            "observations: unsupported schema_version")
    require(type(observations["runs"]) is list, "runs: expected a list")
    runs, run_ids = {}, set()
    for index, run in enumerate(observations["runs"]):
        where = f"run[{index}]"
        fields(run, "id platform variant source revision recorded_at setup_id recognizer_id "
                    "fixed_settings_sha256 automatic_min_speech_ms interjection_filter_enabled evidence observations", where)
        for name in ("id", "setup_id", "recognizer_id"):
            identifier(run[name], f"{where}.{name}")
        choice(run["platform"], PLATFORMS, where)
        choice(run["variant"], VARIANTS, where)
        choice(run["source"], ("native_run", "synthetic"), where)
        require(type(run["revision"]) is str and re.fullmatch(r"[0-9a-f]{40}", run["revision"]),
                f"{where}: revision must be a full lowercase Git SHA")
        recorded_at = timestamp(run["recorded_at"], where)
        require(run["variant"] != "candidate" or labeled_at <= recorded_at,
                f"{where}: labels must be frozen no later than each candidate run")
        digest(run["fixed_settings_sha256"], where)
        integer(run["automatic_min_speech_ms"], 1, where)
        require(type(run["interjection_filter_enabled"]) is bool, f"{where}: filter flag must be boolean")
        verified_file(run["evidence"], runs_root, where)
        key = (run["platform"], run["variant"])
        require(key not in runs and run["id"] not in run_ids, f"{where}: duplicate run")
        require(type(run["observations"]) is list, f"{where}: observations must be a list")
        indexed = {}
        for offset, observation in enumerate(run["observations"]):
            item = f"{where}.observation[{offset}]"
            fields(observation, "case_id automatic_starts transcript retained", item)
            identifier(observation["case_id"], item)
            require(observation["case_id"] in cases and observation["case_id"] not in indexed,
                    f"{item}: unknown or duplicate case ID")
            integer(observation["automatic_starts"], 0, item)
            require(type(observation["transcript"]) is str and type(observation["retained"]) is bool,
                    f"{item}: transcript must be a string and retained must be boolean")
            require(observation["automatic_starts"] > 0 or
                    (not observation["transcript"] and not observation["retained"]),
                    f"{item}: transcript or retention without an automatic start")
            require(not observation["retained"] or bool(observation["transcript"].strip()),
                    f"{item}: retained transcript cannot be empty")
            indexed[observation["case_id"]] = observation
        require(set(indexed) == set(cases), f"{where}: missing case observations")
        runs[key] = (run, indexed)
        run_ids.add(run["id"])
    require(set(runs) == {(p, v) for p in PLATFORMS for v in VARIANTS},
            "runs: need exactly baseline and candidate for both windows and macos")
    for variant in VARIANTS:
        for name in ("revision", "automatic_min_speech_ms", "interjection_filter_enabled"):
            require(len({runs[p, variant][0][name] for p in PLATFORMS}) == 1,
                    "runs: each variant needs the same revision and candidate controls on both platforms")
    for platform in PLATFORMS:
        baseline, candidate = (runs[platform, v][0] for v in VARIANTS)
        require(not baseline["interjection_filter_enabled"], "runs: baseline interjection filter must be off")
        require(all(baseline[name] == candidate[name] for name in
                    ("setup_id", "recognizer_id", "fixed_settings_sha256")),
                "runs: baseline and candidate need the same setup, recognizer, and fixed settings")

    counts = {label: sum(case["label"] == label for case in cases.values()) for label in LABELS}
    blockers = []
    if manifest["corpus_source"] != "human_recording" or manifest["label_source"] != "human":
        blockers.append("human_recordings_and_labels_required")
    if any(run[0]["source"] != "native_run" for run in runs.values()):
        blockers.append("native_runs_required")
    if counts["cough"] + counts["throat_clear"] < 20 or not counts["cough"] or not counts["throat_clear"]:
        blockers.append("need_20_cough_or_throat_clear_cases_including_both_labels")
    if counts["short_speech"] < 20:
        blockers.append("need_20_short_speech_cases")
    results = []
    for platform in PLATFORMS:
        metrics = {}
        for variant in VARIANTS:
            run, observed = runs[platform, variant]
            cough = [key for key, case in cases.items() if case["label"] != "short_speech"]
            speech = [key for key, case in cases.items() if case["label"] == "short_speech"]
            correct = {key for key in speech if observed[key]["automatic_starts"] > 0 and
                       normalized(observed[key]["transcript"]) in
                       {normalized(item) for item in cases[key]["expected_transcripts"]}}
            metrics[variant] = {
                "run_id": run["id"],
                "cough_automatic_starts": sum(observed[key]["automatic_starts"] for key in cough),
                "cough_started_case_ids": [key for key in cough if observed[key]["automatic_starts"]],
                "cough_retained_cases": sum(observed[key]["retained"] for key in cough),
                "short_speech_start_misses": sum(observed[key]["automatic_starts"] == 0 for key in speech),
                "short_speech_transcript_hits": len(correct),
                "short_speech_transcript_misses": len(speech) - len(correct),
                "short_speech_retention_misses": len(speech) - sum(observed[key]["retained"] for key in correct),
            }
        baseline, candidate = metrics["baseline"], metrics["candidate"]
        gates = {"zero_cough_automatic_starts": candidate["cough_automatic_starts"] == 0}
        for name in ("start", "transcript", "retention"):
            metric = f"short_speech_{name}_misses"
            gates[f"{metric}_nonworse"] = candidate[metric] <= baseline[metric]
        results.append({"platform": platform, "metrics": metrics, "gates": gates})
    accepted = not blockers and all(all(row["gates"].values()) for row in results)
    return {"verdict": "pass" if accepted else "not_accepted", "evidence_blockers": blockers,
            "case_counts": counts, "results": results,
            "scope": "Supplied-observation evaluation. Human labels and native provenance still need review."}


def load_json(path):
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "JSON contains a duplicate object key")
            result[key] = value
        return result

    def reject_constant(value):
        raise ValueError("JSON contains a non-finite number")

    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object,
                          parse_constant=reject_constant)
    except (OSError, UnicodeError):
        raise ValueError("cannot read input JSON") from None
    except json.JSONDecodeError:
        raise ValueError("invalid JSON syntax") from None


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("observations", type=Path)
    args = parser.parse_args(argv)
    try:
        result = evaluate(load_json(args.manifest), load_json(args.observations),
                          args.manifest.parent, args.observations.parent)
    except (ValueError, RecursionError) as error:
        print(json.dumps({"verdict": "invalid", "error": str(error)}, ensure_ascii=True))
        return 2
    print(json.dumps(result, indent=2, ensure_ascii=True, allow_nan=False))
    return 0 if result["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
