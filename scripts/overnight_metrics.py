#!/usr/bin/env python3
"""Parse voice-switch.log into Track A/B/C overnight metrics JSON.

No behavior change to the product — harness only.
Redacts: never prints API keys; default JSON is counts only (no text bodies).

Notes on semantics (M1b):
- empty_text_rate = empty among complete_count (missing text= OR blank strip).
  empty_text_rate_scored keeps the older scored-only denom for comparison.
- tmux_send_count / tmux_send_rate are decision-to-send (`-> send` on the
  `tmux: hits=` line), NOT proof the pane received keys. Delivery failures
  are counted separately when product logs them (pane-route builds):
  `tmux: send-keys exited`, `tmux: send-keys failed to start`,
  `dictation not delivered: send failed…`, `dictation not delivered: no target…`.
  Older deployments may never emit those lines (counts stay 0).
- Track C timing: hist + mode over all `dictation timing:` lines; *_last is
  the final line only (not used as the sole reported value).
- stt_ms p50/p90 use nearest-rank on sorted samples.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
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
# Delivery / post-decision (pane-route product logs; may be absent on older builds)
RE_SEND_KEYS_EXITED = re.compile(r"tmux: send-keys exited\b")
RE_SEND_KEYS_FAIL_START = re.compile(r"tmux: send-keys failed to start\b")
RE_NOT_DELIVERED_SEND_FAILED = re.compile(
    r"dictation not delivered: send failed"
)
RE_NOT_DELIVERED_NO_WINDOW = re.compile(
    r"dictation not delivered: no target window"
)

NOTES = (
    "tmux_send_count is decision-to-send (-> send on tmux: hits= line), "
    "not arrived-at-pane. Delivery counters parse pane-route log lines "
    "(send-keys exited / failed to start / not delivered); older logs may "
    "have zeros. empty_text_rate uses complete_count; empty_text_rate_scored "
    "uses text_scored_count. Timing hist/mode cover all timing lines; "
    "endSilenceMs/startTimeoutMs alias *_last."
)


def percentile(sorted_vals: list[int], p: float) -> Optional[int]:
    if not sorted_vals:
        return None
    if len(sorted_vals) == 1:
        return sorted_vals[0]
    # nearest-rank, 1-indexed style used in the plan's measured p50
    k = int(round((p / 100.0) * (len(sorted_vals) - 1)))
    return sorted_vals[k]


def mode_of(hist: Counter[str]) -> Optional[int]:
    """Most common value; on ties, first-seen wins (not last-wins)."""
    if not hist:
        return None
    key, _ = hist.most_common(1)[0]
    return int(key)


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
    empty_text_scored_count = 0
    wake_session_count = 0
    tmux_count = 0
    tmux_send_count = 0
    tmux_disposition: Counter[str] = Counter()
    jev_hist: Counter[str] = Counter()
    end_silence_hist: Counter[str] = Counter()
    start_timeout_hist: Counter[str] = Counter()
    end_silence_last: Optional[int] = None
    start_timeout_last: Optional[int] = None
    send_keys_exited = 0
    send_keys_failed_start = 0
    not_delivered_send_failed = 0
    not_delivered_no_window = 0
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
                    empty_text_scored_count += 1
            else:
                # missing text= counts as empty among complete (plan Track A)
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

        if RE_SEND_KEYS_EXITED.search(line):
            send_keys_exited += 1
            continue

        if RE_SEND_KEYS_FAIL_START.search(line):
            send_keys_failed_start += 1
            continue

        if RE_NOT_DELIVERED_SEND_FAILED.search(line):
            not_delivered_send_failed += 1
            continue

        if RE_NOT_DELIVERED_NO_WINDOW.search(line):
            not_delivered_no_window += 1
            continue

        if RE_TIMING.search(line):
            es = RE_END_SILENCE.search(line)
            st = RE_START_TIMEOUT.search(line)
            if es:
                val = int(es.group(1))
                end_silence_last = val
                end_silence_hist[str(val)] += 1
            else:
                parse_errors += 1
            if st:
                val = int(st.group(1))
                start_timeout_last = val
                start_timeout_hist[str(val)] += 1
            else:
                parse_errors += 1
            continue

    stt_sorted = sorted(stt_ms)
    stt_count = len(stt_sorted)
    leading_wake_rate = (
        (leading_wake_true / complete_count) if complete_count else None
    )
    empty_text_rate = (
        (empty_text_count / complete_count) if complete_count else None
    )
    empty_text_rate_scored = (
        (empty_text_scored_count / text_scored) if text_scored else None
    )
    tmux_send_rate = (tmux_send_count / tmux_count) if tmux_count else None
    end_silence_mode = mode_of(end_silence_hist)
    start_timeout_mode = mode_of(start_timeout_hist)

    return {
        "path": str(path),
        "tail": tail,
        "lines_scanned": lines_scanned,
        "parse_errors": parse_errors,
        "notes": NOTES,
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
        "empty_text_scored_count": empty_text_scored_count,
        "empty_text_rate_scored": empty_text_rate_scored,
        "wake_session_count": wake_session_count,
        # Track B — decision-to-send + delivery failure counters
        "tmux_count": tmux_count,
        "tmux_send_count": tmux_send_count,
        "tmux_send_rate": tmux_send_rate,
        "tmux_disposition": dict(sorted(tmux_disposition.items())),
        "jev": dict(sorted(jev_hist.items())),
        "tmux_send_keys_exited_count": send_keys_exited,
        "tmux_send_keys_failed_start_count": send_keys_failed_start,
        "dictation_not_delivered_send_failed_count": not_delivered_send_failed,
        "dictation_not_delivered_no_window_count": not_delivered_no_window,
        # Track C — hist + mode; *_last / bare aliases are last-wins only
        "endSilenceMs_hist": dict(sorted(end_silence_hist.items(), key=lambda kv: int(kv[0]))),
        "startTimeoutMs_hist": dict(sorted(start_timeout_hist.items(), key=lambda kv: int(kv[0]))),
        "endSilenceMs_mode": end_silence_mode,
        "startTimeoutMs_mode": start_timeout_mode,
        "endSilenceMs_last": end_silence_last,
        "startTimeoutMs_last": start_timeout_last,
        "endSilenceMs": end_silence_last,
        "startTimeoutMs": start_timeout_last,
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
        # empty = 2 blank text= + 1 missing text= → 3 among complete
        "empty_text_count": 3,
        "empty_text_scored_count": 2,
        "wake_session_count": 2,
        "tmux_count": 3,
        "tmux_send_count": 1,
        "tmux_send_keys_exited_count": 1,
        "tmux_send_keys_failed_start_count": 1,
        "dictation_not_delivered_send_failed_count": 1,
        "dictation_not_delivered_no_window_count": 1,
        "endSilenceMs_last": 24000,
        "startTimeoutMs_last": 30000,
        "endSilenceMs": 24000,
        "startTimeoutMs": 30000,
        # mode: first-seen on tie → 1200 / 3000 (proves not last-wins-only)
        "endSilenceMs_mode": 1200,
        "startTimeoutMs_mode": 3000,
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

    # hist must include both timing values (not collapsed to last)
    if m["endSilenceMs_hist"] != {"1200": 1, "24000": 1}:
        failures.append(f"endSilenceMs_hist: {m['endSilenceMs_hist']}")
    if m["startTimeoutMs_hist"] != {"3000": 1, "30000": 1}:
        failures.append(f"startTimeoutMs_hist: {m['startTimeoutMs_hist']}")
    # mode must not equal last when hist has an earlier value with equal count
    if m["endSilenceMs_mode"] == m["endSilenceMs_last"] and m["endSilenceMs_hist"].get("1200") == 1:
        # only fail if mode wrongly picked last while 1200 also has 1 —
        # with first-seen tie-break mode should be 1200 ≠ 24000
        if m["endSilenceMs_mode"] == 24000:
            failures.append("endSilenceMs_mode last-wins (want first-seen on tie)")

    # rates
    if abs((m["leadingWake_rate"] or 0) - 0.4) > 1e-9:
        failures.append(f"leadingWake_rate: {m['leadingWake_rate']}")
    # empty_text_rate among complete: 3/5
    if abs((m["empty_text_rate"] or 0) - 0.6) > 1e-9:
        failures.append(f"empty_text_rate: {m['empty_text_rate']}")
    # scored-only: 2/4
    if abs((m["empty_text_rate_scored"] or 0) - 0.5) > 1e-9:
        failures.append(f"empty_text_rate_scored: {m['empty_text_rate_scored']}")
    if abs((m["tmux_send_rate"] or 0) - (1 / 3)) > 1e-9:
        failures.append(f"tmux_send_rate: {m['tmux_send_rate']}")

    if not m.get("notes"):
        failures.append("notes missing")

    # warn lines must not share the Track C `dictation timing:` prefix (parse poison)
    baseline = {
        "parse_errors": m["parse_errors"],
        "endSilenceMs_hist": dict(m["endSilenceMs_hist"]),
        "startTimeoutMs_hist": dict(m["startTimeoutMs_hist"]),
        "endSilenceMs_mode": m["endSilenceMs_mode"],
        "startTimeoutMs_mode": m["startTimeoutMs_mode"],
        "endSilenceMs_last": m["endSilenceMs_last"],
        "startTimeoutMs_last": m["startTimeoutMs_last"],
        "endSilenceMs": m["endSilenceMs"],
        "startTimeoutMs": m["startTimeoutMs"],
    }
    with tempfile.TemporaryDirectory() as tmp:
        polluted = Path(tmp) / "overnight_metrics_warn.log"
        polluted.write_text(
            FIXTURE.read_text(encoding="utf-8")
            + "dictation timing warn: endSilenceMs unusually high (24000); example is 2400\n"
            + "dictation timing warn: startTimeoutMs unusually high (30000); example is 3000\n",
            encoding="utf-8",
        )
        w = parse_metrics(polluted, tail=None)
    for k, want in baseline.items():
        got = w.get(k)
        if got != want:
            failures.append(f"warn-prefix poison {k}: got={got!r} want={want!r}")

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
    p = argparse.ArgumentParser(
        description=(
            "voice-switch overnight metrics harness. "
            "tmux_send_* = decision-to-send; delivery failure counters are separate. "
            "empty_text_rate uses complete_count. Timing reports hist+mode."
        )
    )
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
