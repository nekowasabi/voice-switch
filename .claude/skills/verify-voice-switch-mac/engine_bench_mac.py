#!/usr/bin/env python3
"""LT0: run a local engine on Superwhisper's own recordings and compare with its rawResult.

Superwhisper's recordings/<id>/meta.json holds rawResult (the transcript before LLM cleanup),
modelName and processingTime, so its past recordings are a ready-made reference set.
"""
import argparse, json, pathlib, statistics, subprocess, time, unicodedata

HOME = pathlib.Path.home()
p = argparse.ArgumentParser()
p.add_argument("--cli", default=str(HOME / "Library/Caches/voice-switch-verify/transcribe.cpp/build/bin/transcribe-cli"))
p.add_argument("--gguf", default=str(HOME / "Library/Application Support/voice-switch/models/cohere-transcribe-03-2026-Q4_K_M.gguf"))
p.add_argument("--recordings", default=str(HOME / "Documents/superwhisper/recordings"))
p.add_argument("--model", default="Cohere", help="only recordings whose modelName contains this")
p.add_argument("--max-ms", type=int, default=15000, help="skip recordings longer than this")
p.add_argument("--limit", type=int, default=10)
p.add_argument("--out", required=True)
a = p.parse_args()


def norm(s):
    # Compare characters, not words: Japanese has no spaces, and punctuation differs between engines.
    return "".join(c for c in unicodedata.normalize("NFKC", s) if not (c.isspace() or unicodedata.category(c).startswith("P")))


def cer(ref, hyp):
    prev = list(range(len(hyp) + 1))
    for i, r in enumerate(ref, 1):
        cur = [i]
        for j, h in enumerate(hyp, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (r != h)))
        prev = cur
    return prev[-1] / max(1, len(ref))


out = pathlib.Path(a.out)
out.mkdir(parents=True, exist_ok=True)
picked = []
for d in sorted(pathlib.Path(a.recordings).iterdir(), key=lambda d: d.name, reverse=True):
    meta = d / "meta.json"
    audio = next((d / n for n in ("output.wav", "output.ogg") if (d / n).exists()), None)
    if not meta.exists() or audio is None:
        continue
    m = json.loads(meta.read_text())
    if a.model not in (m.get("modelName") or "") or not (m.get("rawResult") or "").strip() or m.get("duration", 0) > a.max_ms:
        continue
    picked.append((d.name, audio, m))
    if len(picked) == a.limit:
        break

rows = []
for rid, audio, m in picked:
    wav = out / f"{rid}.wav"
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", str(audio), "-ar", "16000", "-ac", "1", str(wav)], check=True)
    t = time.monotonic()
    r = subprocess.run([a.cli, "-m", a.gguf, "-l", "ja", str(wav)], capture_output=True, text=True)
    ms = int((time.monotonic() - t) * 1000)
    # stdout is a diagnostic report; the transcript is its `text: ` line.
    local = next((l[len("text: "):] for l in r.stdout.splitlines() if l.startswith("text: ")), "")
    rows.append({"id": rid, "durationMs": m.get("duration"), "superwhisperModel": m.get("modelName"),
                 "superwhisperMs": m.get("processingTime"), "localMs": ms, "exit": r.returncode,
                 "cer": round(cer(norm(m["rawResult"]), norm(local)), 3),
                 "rawResult": m["rawResult"], "local": local, "stderrTail": r.stderr.strip().splitlines()[-3:]})
    print(f"{rid}\tdur={m.get('duration')}ms\tsw={m.get('processingTime')}ms\tlocal={ms}ms\tcer={rows[-1]['cer']}\texit={r.returncode}")

(out / "results.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1))
ok = [r for r in rows if r["exit"] == 0]
if not ok:
    raise SystemExit(f"KPI n=0 failed={len(rows)} (no recording ran; see {out}/results.json)")
summary = {"n": len(ok), "failed": len(rows) - len(ok), "meanCer": round(statistics.mean(r["cer"] for r in ok), 3),
           "p50LocalMs": statistics.median(r["localMs"] for r in ok),
           "p50SuperwhisperMs": statistics.median(r["superwhisperMs"] or 0 for r in ok),
           "gguf": a.gguf}
(out / "summary.json").write_text(json.dumps(summary, indent=1))
print(f"KPI n={summary['n']} failed={summary['failed']} meanCER={summary['meanCer']} p50local={summary['p50LocalMs']}ms p50superwhisper={summary['p50SuperwhisperMs']}ms")
