#!/usr/bin/env python3
"""Parse voice-switch.log into Track A/B/C overnight metrics JSON.

No behavior change to the product — harness only.
Redacts: never prints API keys; default JSON is counts only (no text bodies).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any, Iterable, Optional

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "tests" / "fixtures" / "overnight_metrics_sample.log"

RE_STT = re.compile(r"dictation recognition: stt\b")
RE_STT_MS = re.compile(r"\bms=(\d+)\b")
RE_COMPLETE = re.compile(r"dictation recognition: complete\b")
RE_LEADING_WAKE = re.compile(r"\bleadingWake=(True|False)\b")
RE_TEXT = re.compile(r'\btext="([^"]*)"')
RE_WAKE_SESSION = re.compile(r"dictation session: wake")
RE_TMUX = re.compile(r"tmux: hits=")
RE_TMUX_JEV = re.compile(r"\bjev=(\S+)")
RE_TMUX_DISP = re.compile(r"->\s+(send|skip)\b(?:\s+%\d+)?\s*\(([^)]*)\)")
RE_TIMING = re.compile(r"dictation timing:")
RE_END_SILENCE = re.compile(r"\bendSilenceMs=(\d+)\b")
RE_START_TIMEOUT = re.compile(r"\bstartTimeoutMs=(\d+)\b")


def percentile(sorted_vals: list[int], p: float) -> Optional[int]:
    if not sorted_vals:
        return None
    if len(sorted_vals) == 1:
        return sorted_vals[0]
    # nearest-rank, 1-indexed style used in the plan's measured p50
    k = int(round((p / 100.0) * (len(sorted_vals) - 1)))
    return sorted_vals[k]


def iter_lines(path: Path, tail: Optional[int]) -> Iterable[str]:
    if tail is None or tail <= 0:
        with path.open("r", encoding="utf-8", errors="replace") as f:
            for line in f:
                yield line.rstrip("\n")
        return
    # efficient-ish: for large files read all then slice (24MB is fine)
    with path.open("r", encoding="utf-8", errors="replace") as f:
        lines = f.readlines()
    for line in lines[-tail:]:
        yield line.rstrip("\n")


def parse_metrics(path: Path, tail: Optional[int] = None) -> dict[str, Any]:
    stt_ms: list[int] = []
    complete_count = 0
    leading_wake_true = 0
    text_scored = 0
    empty_text_count = 0
    wake_session_count = 0
    tmux_count = 0
    tmux_send_count = 0
    tmux_disposition: Counter[str] = Counter()
    jev_hist: Counter[str] = Counter()
    end_silence: Optional[int] = None
    start_timeout: Optional[int] = None
    parse_errors = 0
    lines_scanned = 0

    for line in iter_lines(path, tail):
        lines_scanned += 1

        if RE_STT.search(line):
            m = RE_STT_MS.search(line)
            if m:
                stt_ms.append(int(m.group(1)))
            else:
                parse_errors += 1
            continue

        if RE_COMPLETE.search(line):
            complete_count += 1
            lw = RE_LEADING_WAKE.search(line)
            if lw:
                if lw.group(1) == "True":
                    leading_wake_true += 1
            else:
                parse_errors += 1
            tm = RE_TEXT.search(line)
            if tm:
                text_scored += 1
                if tm.group(1).strip() == "":
                    empty_text_count += 1
            continue

        if RE_WAKE_SESSION.search(line):
            wake_session_count += 1
            continue

        if RE_TMUX.search(line):
            tmux_count += 1
            jev_m = RE_TMUX_JEV.search(line)
            if jev_m:
                jev_hist[jev_m.group(1)] += 1
            else:
                parse_errors += 1
            disp_m = RE_TMUX_DISP.search(line)
            if disp_m:
                kind, reason = disp_m.group(1), disp_m.group(2).strip()
                if kind == "send":
                    tmux_send_count += 1
                key = f"{kind} ({reason})" if reason else kind
                tmux_disposition[key] += 1
            else:
                parse_errors += 1
            continue

        if RE_TIMING.search(line):
            es = RE_END_SILENCE.search(line)
            st = RE_START_TIMEOUT.search(line)
            if es:
                end_silence = int(es.group(1))
            else:
                parse_errors += 1
            if st:
                start_timeout = int(st.group(1))
            else:
                parse_errors += 1
            continue

    stt_sorted = sorted(stt_ms)
    stt_count = len(stt_sorted)
    leading_wake_rate = (
        (leading_wake_true / complete_count) if complete_count else None
    )
    empty_text_rate = (
        (empty_text_count / text_scored) if text_scored else None
    )
    tmux_send_rate = (tmux_send_count / tmux_count) if tmux_count else None

    return {
        "path": str(path),
        "tail": tail,
        "lines_scanned": lines_scanned,
        "parse_errors": parse_errors,
        # Track A
        "stt_count": stt_count,
        "stt_ms_min": stt_sorted[0] if stt_sorted else None,
        "stt_ms_p50": percentile(stt_sorted, 50),
        "stt_ms_p90": percentile(stt_sorted, 90),
        "stt_ms_max": stt_sorted[-1] if stt_sorted else None,
        "complete_count": complete_count,
        "leadingWake_true": leading_wake_true,
        "leadingWake_rate": leading_wake_rate,
        "text_scored_count": text_scored,
        "empty_text_count": empty_text_count,
        "empty_text_rate": empty_text_rate,
        "wake_session_count": wake_session_count,
        # Track B
        "tmux_count": tmux_count,
        "tmux_send_count": tmux_send_count,
        "tmux_send_rate": tmux_send_rate,
        "tmux_disposition": dict(sorted(tmux_disposition.items())),
        "jev": dict(sorted(jev_hist.items())),
        # Track C
        "endSilenceMs": end_silence,
        "startTimeoutMs": start_timeout,
    }



def self_test() -> int:
    if not FIXTURE.is_file():
        print(f"FAIL: missing fixture {FIXTURE}", file=sys.stderr)
        return 1
    m = parse_metrics(FIXTURE, tail=None)
    failures: list[str] = []

    # Recompute p50/p90 from known stt ms in fixture: 100,200,300,400
    stt = [100, 200, 300, 400]
    expect_p50 = percentile(stt, 50)
    expect_p90 = percentile(stt, 90)

    checks = {
        "stt_count": 4,
        "stt_ms_min": 100,
        "stt_ms_p50": expect_p50,
        "stt_ms_p90": expect_p90,
        "stt_ms_max": 400,
        "complete_count": 5,
        "leadingWake_true": 2,
        "text_scored_count": 4,
        "empty_text_count": 2,
        "wake_session_count": 2,
        "tmux_count": 3,
        "tmux_send_count": 1,
        "endSilenceMs": 24000,
        "startTimeoutMs": 30000,
        "parse_errors": 0,
    }
    for k, want in checks.items():
        got = m.get(k)
        if got != want:
            failures.append(f"{k}: got={got!r} want={want!r}")

    # disposition / jev
    if m["tmux_disposition"].get("send (unique hit)") != 1:
        failures.append(f"disp send unique: {m['tmux_disposition']}")
    if m["tmux_disposition"].get("skip (no pane matched)") != 2:
        failures.append(f"disp skip no-pane: {m['tmux_disposition']}")
    if m["jev"].get("off") != 2:
        failures.append(f"jev off: {m['jev']}")
    if m["jev"].get("none@1.00") != 1:
        failures.append(f"jev none: {m['jev']}")

    # rates
    if abs((m["leadingWake_rate"] or 0) - 0.4) > 1e-9:
        failures.append(f"leadingWake_rate: {m['leadingWake_rate']}")
    if abs((m["empty_text_rate"] or 0) - 0.5) > 1e-9:
        failures.append(f"empty_text_rate: {m['empty_text_rate']}")
    if abs((m["tmux_send_rate"] or 0) - (1 / 3)) > 1e-9:
        failures.append(f"tmux_send_rate: {m['tmux_send_rate']}")

    if failures:
        print("SELF-TEST FAIL:", file=sys.stderr)
        for f in failures:
            print(f"  {f}", file=sys.stderr)
        print(json.dumps(m, ensure_ascii=False, indent=2), file=sys.stderr)
        return 1
    print("SELF-TEST OK")
    print(json.dumps({k: m[k] for k in checks}, ensure_ascii=False, indent=2))
    return 0


def main(argv: Optional[list[str]] = None) -> int:
    p = argparse.ArgumentParser(description="voice-switch overnight metrics harness")
    p.add_argument(
        "--path",
        default=os.environ.get("VOICE_SWITCH_LOG", ""),
        help="path to voice-switch.log (or env VOICE_SWITCH_LOG)",
    )
    p.add_argument(
        "--tail",
        type=int,
        default=None,
        help="only scan last N lines",
    )
    p.add_argument(
        "--json",
        action="store_true",
        default=True,
        help="print JSON summary to stdout (default)",
    )
    p.add_argument(
        "--self-test",
        action="store_true",
        help="run fixture assertions and exit",
    )
    args = p.parse_args(argv)

    if args.self_test:
        return self_test()

    if not args.path:
        print(
            "error: --path or VOICE_SWITCH_LOG required (or use --self-test)",
            file=sys.stderr,
        )
        return 2
    path = Path(args.path)
    if not path.is_file():
        print(f"error: log not found: {path}", file=sys.stderr)
        return 2

    metrics = parse_metrics(path, tail=args.tail)
    print(json.dumps(metrics, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
