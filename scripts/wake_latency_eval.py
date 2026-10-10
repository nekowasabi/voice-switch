#!/usr/bin/env python3
"""Wake-word latency eval (macOS only). No network, no API spend.

Builds a synthetic Japanese speech set with `say` (Kyoko is the only voice `say -o` renders for ja_JP; the
other ja voices fall back to it byte-for-byte, so variety comes from rate and [[pbas]] pitch), runs
`voice-switch --check` under one config, and writes the hillclimb layout:

  <variant-dir>/results.jsonl   one row per (case, rep)
  <variant-dir>/summary.json
  <variant-dir>/traces/<id>_rep<k>.json

Grading (deterministic, no LLM):
  wake-*   : pass iff a wake-ish verdict exists and (decision - wake_end) <= target ms, where
             decision = audio position the verdict fired at + measured STT ms (both printed by --check) and
             wake_end = end of the wake word in *that file* (last 30 ms frame with RMS > 0.005 before the first
             gap >= 30 ms nearest the lone rendering's end; measured on the clean twin).
  nonwake  : pass iff no wake-ish verdict at all (a dictate verdict opens a dictation, so it is a false wake).

Usage:
  scripts/wake_latency_eval.py --config cfg.json --variant-dir .claude/hillclimb/wake/baseline [--reps 3]
"""
import argparse
import json
import math
import os
import random
import re
import struct
import subprocess
import sys
import wave
import zlib

RATE = 16000
FRAME = 480
LEAD_MS = 500
NOISE_RMS = 0.003

# (id, say rate or None, embedded pitch command)
PROSODY = [
    ("p0", None, ""), ("p1", 150, ""), ("p2", 240, ""), ("p3", 280, ""),
    ("p4", None, "[[pbas 30]]"), ("p5", None, "[[pbas 70]]"), ("p6", 200, "[[pbas 40]]"), ("p7", 260, "[[pbas 60]]"),
]
WAKE = [("w0", "音声入力"), ("w1", "音声に入る"), ("w2", "おんせい")]
# continuation sentences: s0-s2 go with train prosodies, s3-s5 with test prosodies (see _state.json split)
SENT = ["明日の会議の議事録をまとめてください", "このメールに返信して", "テストを全部実行してください",
        "来週の予定を確認したい", "さっきの関数名を変えて", "ログの最後を見せて"]
NONWAKE = [("n0", "音声認識の精度を上げたい"), ("n1", "今日はいい天気ですね"), ("n2", "温泉に入りたい"), ("n3", "入力ストップ"),
           ("n4", "音声ファイルを送ってください"), ("n5", "おんせんたまごを食べたい"), ("n6", "会議の資料を作ってください"),
           ("n7", "音声合成を試したい")]
TRAIN_PROSODY = {"p0", "p2", "p4", "p6"}

VERDICT = re.compile(r"^(early-wake|wake|early-dictate|dictate)@(\d+)ms\+stt(\d+)")


def read_wav(path):
    with wave.open(path) as w:
        n = w.getnframes()
        return list(struct.unpack("<%dh" % n, w.readframes(n)))


def write_wav(path, samples):
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE)
        w.writeframes(struct.pack("<%dh" % len(samples), *samples))


def say(text, rate, pitch, path):
    tmp = path + ".aiff"
    cmd = ["say", "-v", "Kyoko", "-o", tmp] + (["-r", str(rate)] if rate else []) + [pitch + text]
    subprocess.run(cmd, check=True)
    subprocess.run(["afconvert", "-f", "WAVE", "-d", "LEI16@16000", "-c", "1", tmp, path + ".raw.wav"], check=True)
    os.remove(tmp)
    s = read_wav(path + ".raw.wav")
    os.remove(path + ".raw.wav")
    return s


def segments(samples, floor=0.005):
    on = []
    for i in range(0, len(samples) - FRAME + 1, FRAME):
        r = math.sqrt(sum(v * v for v in samples[i:i + FRAME]) / FRAME) / 32768
        on.append(r > floor)
    segs, st = [], None
    for k, s in enumerate(on + [False]):
        if s and st is None:
            st = k
        if not s and st is not None:
            segs.append((st * 30, k * 30)); st = None
    return segs


def wake_end(samples, lone_end):
    """End of the wake word inside a continuation: the segment end followed by a gap, nearest the lone end."""
    ends = [e for (_, e) in segments(samples)]
    best = min(ends, key=lambda e: abs(e - lone_end)) if ends else lone_end
    return (best, "gap") if abs(best - lone_end) <= 200 else (lone_end, "lone")


def add_lead_and_noise(samples, noisy, seed):
    lead = [0] * (LEAD_MS * RATE // 1000)
    out = lead + samples
    if noisy:
        rnd = random.Random(seed)
        out = [max(-32768, min(32767, int(v + rnd.gauss(0, NOISE_RMS * 32768)))) for v in out]
    return out


def build_set(audio_dir):
    os.makedirs(audio_dir, exist_ok=True)
    manifest_path = os.path.join(audio_dir, "manifest.json")
    if os.path.exists(manifest_path):
        return json.load(open(manifest_path))
    cases = []
    for pid, rate, pitch in PROSODY:
        split = "train" if pid in TRAIN_PROSODY else "test"
        sents = SENT[:3] if split == "train" else SENT[3:]
        for wid, word in WAKE:
            lone = say(word, rate, pitch, os.path.join(audio_dir, "tmp"))
            lone_end = segments(lone)[-1][1]
            variants = [("lone", word, lone, lone_end, "lone")]
            for ci, (kind, sep) in enumerate([("comma", "、"), ("pause", " [[slnc 800]] ")]):
                text = word + sep + sents[(PROSODY.index((pid, rate, pitch)) + ci) % 3]
                s = say(text, rate, pitch, os.path.join(audio_dir, "tmp"))
                end, src = wake_end(s, lone_end)
                variants.append((kind, text, s, end, src))
            for kind, text, s, end, src in variants:
                for noisy in (False, True):
                    cid = f"{wid}-{kind}-{pid}-{'noisy' if noisy else 'clean'}"
                    path = os.path.join(audio_dir, cid + ".wav")
                    write_wav(path, add_lead_and_noise(s, noisy, zlib.crc32(cid.encode())))
                    cases.append({"id": cid, "path": path, "kind": "wake-" + kind, "text": text, "split": split,
                                  "prosody": pid, "noisy": noisy, "wake_end_ms": end + LEAD_MS, "wake_end_src": src})
        for nid, text in NONWAKE:
            nsplit = "train" if nid in ("n0", "n1", "n2", "n3") else "test"
            if nsplit != split:
                continue
            s = say(text, rate, pitch, os.path.join(audio_dir, "tmp"))
            for noisy in (False, True):
                cid = f"{nid}-{pid}-{'noisy' if noisy else 'clean'}"
                path = os.path.join(audio_dir, cid + ".wav")
                write_wav(path, add_lead_and_noise(s, noisy, zlib.crc32(cid.encode())))
                cases.append({"id": cid, "path": path, "kind": "nonwake", "text": text, "split": split,
                              "prosody": pid, "noisy": noisy})
    json.dump(cases, open(manifest_path, "w"), ensure_ascii=False, indent=1)
    return cases


def run_check(binary, config, paths):
    env = dict(os.environ, VOICE_SWITCH_CONFIG=config)
    out = subprocess.run([binary, "--check", *paths], env=env, capture_output=True, text=True)
    verdicts = {}
    for line in out.stdout.splitlines():
        if "\t" in line:
            path, rest = line.split("\t", 1)
            verdicts[path] = json.loads(rest)
    return verdicts, out.stderr, out.returncode


def grade(case, verdicts, target):
    hits = [m for m in (VERDICT.match(v) for v in verdicts) if m]
    if case["kind"] == "nonwake":
        return {"pass": 0 if hits else 1}
    if not hits:
        return {"pass": 0, "detected": 0}
    m = hits[0]
    latency = int(m.group(2)) + int(m.group(3)) - case["wake_end_ms"]
    # detected: a late wake still records the speech; a missed one loses it, so they are tracked apart
    return {"pass": 1 if latency <= target else 0, "detected": 1, "latency_ms": latency}


def regrade(vdir, cases, target):
    """Re-grade stored verdicts in place with the current grader (no re-run)."""
    by_id = {c["id"]: c for c in cases}
    path = os.path.join(vdir, "results.jsonl")
    rows = [json.loads(l) for l in open(path)]
    for r in rows:
        r["grade"] = grade(by_id[r["prompt_id"]], r["meta"]["verdicts"], target)
    with open(path, "w") as out:
        for r in rows:
            out.write(json.dumps(r, ensure_ascii=False) + "\n")
    print(f"regraded {len(rows)} rows in {path}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--variant-dir", required=True)
    ap.add_argument("--reps", type=int, default=3)
    ap.add_argument("--target-ms", type=int, default=300)
    here = os.path.dirname(os.path.abspath(__file__))
    ap.add_argument("--audio-dir", default=os.path.join(here, "..", ".build", "wake-eval", "audio"))
    ap.add_argument("--binary", default=os.path.join(here, "..", ".build", "release", "voice-switch"))
    ap.add_argument("--regrade", action="store_true", help="re-grade stored verdicts in place and exit")
    a = ap.parse_args()
    cases = build_set(os.path.abspath(a.audio_dir))
    vdir = os.path.abspath(a.variant_dir)
    if a.regrade:
        regrade(vdir, cases, a.target_ms)
        return 0
    os.makedirs(os.path.join(vdir, "traces"), exist_ok=True)
    res_path = os.path.join(vdir, "results.jsonl")
    done = set()
    if os.path.exists(res_path):
        for line in open(res_path):
            r = json.loads(line); done.add((r["prompt_id"], r["rep"]))
    print(f"resolved: {len(cases)} cases x {a.reps} reps, config={a.config}, target<={a.target_ms}ms", flush=True)
    for rep in range(a.reps):
        todo = [c for c in cases if (c["id"], rep) not in done]
        if not todo:
            continue
        verdicts, err, code = run_check(os.path.abspath(a.binary), os.path.abspath(a.config), [c["path"] for c in todo])
        if code != 0 or len(verdicts) != len(todo):
            print(f"runner error rep {rep}: exit {code}, {len(verdicts)}/{len(todo)} verdicts\n{err[-500:]}", file=sys.stderr)
            return 2
        with open(res_path, "a") as out:
            for c in todo:
                v = verdicts[c["path"]]
                g = grade(c, v, a.target_ms)
                row = {"prompt_id": c["id"], "rep": rep, "prompt": c["text"],
                       "tags": [c["kind"], c["prosody"], "noisy" if c["noisy"] else "clean"],
                       "grade": g, "model": "apple-speech-ja_JP", "usage": {"input_tokens": 0, "output_tokens": 0},
                       "meta": {"verdicts": v, "wake_end_ms": c.get("wake_end_ms"), "wake_end_src": c.get("wake_end_src")}}
                out.write(json.dumps(row, ensure_ascii=False) + "\n")
                trace = [{"role": "user", "content": f"{c['text']}  ({c['kind']}, {c['prosody']}, {'noisy' if c['noisy'] else 'clean'}, wake_end={c.get('wake_end_ms')}ms)"},
                         {"role": "assistant", "content": json.dumps(v, ensure_ascii=False)}]
                json.dump(trace, open(os.path.join(vdir, "traces", f"{c['id']}_rep{rep}.json"), "w"), ensure_ascii=False)
    rows = [json.loads(l) for l in open(res_path)]
    by = {}
    for r in rows:
        by.setdefault(r["tags"][0], []).append(r["grade"]["pass"])
    print("  ".join(f"{k} {sum(v)}/{len(v)}" for k, v in sorted(by.items())))
    print(f"all {sum(r['grade']['pass'] for r in rows)}/{len(rows)}  -> {res_path}")
    sp = os.path.join(vdir, "summary.json")
    if not os.path.exists(sp):
        json.dump({"description": os.path.basename(a.config), "target": "code"}, open(sp, "w"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
