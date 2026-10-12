#!/usr/bin/env python3
"""Mac twin of superwhisper-recall.ps1: counts expected phrases missing from Superwhisper's rawResult.

Exit 0 only when no phrase is missing and no case timed out.

  python3 superwhisper_recall_mac.py --spec phrase-cases.json --out /tmp/recall --mode-key <key> [--pad-ms 0 500]
  python3 superwhisper_recall_mac.py --cases cases.json --out /tmp/recall --mode-key <key>
"""
import argparse
import json
import os
import subprocess
import sys
import time
import unicodedata
import wave
from pathlib import Path

SUPERWHISPER = Path.home() / "Documents/superwhisper"
RECORDINGS = SUPERWHISPER / "recordings"
PREFERENCES = SUPERWHISPER / "preferences.json"


def active_mode():
    # Same order as MacApp.swift readSuperwhisperActiveMode: the defaults domain first, then preferences.json.
    out = subprocess.run(["defaults", "read", "com.superduper.superwhisper", "activeModeKey"],
                         capture_output=True, text=True)
    if out.returncode == 0 and out.stdout.strip():
        return out.stdout.strip()
    try:
        return json.loads(PREFERENCES.read_text(encoding="utf-8")).get("activeMode")
    except (OSError, ValueError):
        return None


def set_mode(key):
    if active_mode() == key:
        return
    subprocess.run(["open", "-g", "superwhisper://mode?key=" + key], check=True)
    for _ in range(50):
        time.sleep(0.1)
        if active_mode() == key:
            return
    raise RuntimeError(f"Superwhisper did not switch to mode {key}")


def normalize(text):
    return "".join(c for c in (text or "") if not c.isspace() and not unicodedata.category(c).startswith("P"))


def speak_cases(spec_path, out_dir):
    cases = []
    for case in json.loads(Path(spec_path).read_text(encoding="utf-8")):
        script, phrases = ["[[slnc 300]]"], []
        for part in case["parts"]:
            if "pauseMs" in part:
                script.append(f"[[slnc {part['pauseMs']}]]")
            else:
                script.append(part["text"])
                phrases.append(part["expect"])
        script.append("[[slnc 600]]")
        wav = out_dir / f"{case['name']}.wav"
        subprocess.run(["say", "-v", "Kyoko", "--file-format=WAVE", "--data-format=LEI16@16000",
                        "-o", str(wav), " ".join(script)], check=True)
        cases.append({"name": case["name"], "wav": str(wav), "phrases": phrases})
    return cases


def padded(source, target, pad_ms):
    with wave.open(str(source), "rb") as src:
        params = src.getparams()
        frames = src.readframes(src.getnframes())
    silence = b"\x00" * (params.sampwidth * params.nchannels * (params.framerate * pad_ms // 1000))
    with wave.open(str(target), "wb") as dst:
        dst.setparams(params)
        dst.writeframes(silence + frames)


def transcribe(wav, timeout):
    before = set(os.listdir(RECORDINGS)) if RECORDINGS.exists() else set()
    subprocess.run(["open", "-g", "-a", "superwhisper", str(wav)], check=True)
    deadline = time.time() + timeout
    while time.time() < deadline:
        time.sleep(0.3)
        for name in set(os.listdir(RECORDINGS)) - before:
            meta = RECORDINGS / name / "meta.json"
            try:
                data = json.loads(meta.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if data.get("rawResult") or data.get("result"):
                return data
    return None


def main():
    ap = argparse.ArgumentParser()
    source = ap.add_mutually_exclusive_group(required=True)
    source.add_argument("--cases", help="JSON list of {name, wav, phrases: [[alt, ...], ...]}")
    source.add_argument("--spec", help="phrase-cases.json; spoken with `say -v Kyoko` first")
    ap.add_argument("--out", required=True)
    ap.add_argument("--mode-key", required=True, help="a Superwhisper mode key with autoPaste false")
    ap.add_argument("--pad-ms", type=int, nargs="+", default=[0])
    ap.add_argument("--timeout", type=int, default=90)
    args = ap.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    cases = speak_cases(args.spec, out_dir) if args.spec else json.loads(Path(args.cases).read_text(encoding="utf-8"))
    previous = active_mode()
    results = []
    try:
        set_mode(args.mode_key)
        for case in cases:
            for pad in args.pad_ms:
                wav = out_dir / f"{case['name']}-pad{pad}.wav"
                padded(case["wav"], wav, pad)
                meta = transcribe(wav, args.timeout)
                raw = meta.get("rawResult") if meta else None
                missing = ["|".join(alts) for alts in case["phrases"]
                           if not any(normalize(alt) in normalize(raw) for alt in alts)]
                results.append({"case": case["name"], "padMs": pad, "phrases": len(case["phrases"]),
                                 "dropped": len(missing), "missing": missing,
                                 "model": meta.get("modelKey") if meta else None, "rawResult": raw,
                                 "llmResult": meta.get("llmResult") if meta else None, "timedOut": meta is None})
                r = results[-1]
                print(f"{r['case']} pad={pad}ms model={r['model']} dropped={r['dropped']}/{r['phrases']} missing={','.join(missing)}")
    finally:
        if previous and active_mode() != previous:
            try:
                set_mode(previous)
            except RuntimeError as error:
                print(f"mode restore failed: {error}", file=sys.stderr)

    summary = []
    for pad in args.pad_ms:
        rows = [r for r in results if r["padMs"] == pad]
        summary.append({"padMs": pad, "cases": len(rows), "phrases": sum(r["phrases"] for r in rows),
                        "dropped": sum(r["dropped"] for r in rows), "timedOut": sum(r["timedOut"] for r in rows)})
    (out_dir / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    for row in summary:
        print(f"KPI pad={row['padMs']}ms dropped={row['dropped']}/{row['phrases']} timedOut={row['timedOut']}")
    return 1 if any(row["dropped"] or row["timedOut"] for row in summary) else 0


if __name__ == "__main__":
    sys.exit(main())
