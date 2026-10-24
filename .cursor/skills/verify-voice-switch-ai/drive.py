#!/usr/bin/env python3
"""Run the voice-switch-ai checklist and leave a proof file."""

from __future__ import annotations

import json
import os
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PROOF = ROOT / "reports" / "verify-voice-switch-ai" / "proof.txt"
SCRATCH_LIST = ROOT / "reports" / "verify-voice-switch-ai" / "scratch-path.txt"
JOURNAL = ROOT / "tests" / "fixtures" / "ai" / "journal"
PYTHON = sys.executable


def run(args, env=None, timeout=60):
    return subprocess.run(
        args,
        cwd=ROOT,
        env=env,
        check=False,
        capture_output=True,
        text=True,
        timeout=timeout,
    )


def cleanup() -> int:
    if not SCRATCH_LIST.exists():
        print("cleanup: no scratch list")
        return 0
    for line in SCRATCH_LIST.read_text(encoding="utf-8").splitlines():
        path = Path(line.strip())
        if not path.exists():
            continue
        if path.is_dir():
            import shutil

            shutil.rmtree(path)
        else:
            path.unlink()
        print(f"cleanup: removed {path}")
    print(f"cleanup: kept {PROOF}")
    return 0


def main() -> int:
    if "--cleanup" in sys.argv:
        return cleanup()
    results = []

    def check(item: str, ok: bool, detail: str) -> None:
        results.append((item, ok, detail))
        print(f"{item} {'PASS' if ok else 'FAIL'} {detail}")

    unit = run([PYTHON, "-m", "unittest", "discover", "-s", "tests/ai"], timeout=60)
    check("V00", unit.returncode == 0, f"unittest exit {unit.returncode}")

    doctor = run([PYTHON, "scripts/voice-switch-ai", "doctor"])
    doctor_ok = (
        doctor.returncode == 0
        and "cloud=off" in doctor.stdout
        and "jevMinConfidence=unset" in doctor.stdout
        and "ready=yes" in doctor.stdout
    )
    check("V16", doctor_ok, "doctor cloud=off jevMinConfidence=unset ready=yes")

    env = os.environ.copy()
    env["TYPESAFE_API_KEY"] = "fake-not-a-real-key"
    trial = run([PYTHON, "scripts/voice-switch-ai", "try"], env=env)
    stdout = trial.stdout
    try_ok = trial.returncode == 0
    check("V01", try_ok and "journal_unchanged=yes" in stdout, "journal_unchanged=yes")
    check("V19", try_ok and "jev=off" in stdout and "jev=error" not in stdout, "fake key stays jev=off")
    check("V03", "triage: local=task@1.00 -> tray" in stdout, "local task line")
    check("V04", "triage: local=ambiguous jev=off -> memo" in stdout, "ambiguous memo line")
    check("V05", "triage: muse=research@1.00 -> execute" in stdout, "muse execute line")
    check("V06", "triage: muse=agent@1.00 -> tray" in stdout, "muse tray line")
    check("V10", "recall: conclusion-change e6 e16" in stdout and "digest: week=2026-W43" in stdout, "week and conclusion change")
    check("V12", "recall: e16 related=e6" in stdout, "related past")
    check("V13", "search: conclusion-change" in stdout, "search conclusion change")

    out_line = next((line for line in stdout.splitlines() if line.startswith("out=")), "out=")
    out = Path(out_line.split("=", 1)[1]) if "=" in out_line else Path("")
    notes = []
    tray = []
    if out.is_dir():
        mode_dir = stat.S_IMODE(out.stat().st_mode)
        mode_file = stat.S_IMODE((out / "tray.jsonl").stat().st_mode)
        check("V02", mode_dir == 0o700 and mode_file == 0o600, f"dir {oct(mode_dir)} file {oct(mode_file)}")
        notes = [json.loads(line) for line in (out / "notes.jsonl").read_text(encoding="utf-8").splitlines()]
        tray = [json.loads(line) for line in (out / "tray.jsonl").read_text(encoding="utf-8").splitlines()]
        report = next(note for note in notes if "e2" in note["sourceIds"])
        note_ok = (
            "えーと" not in report["cleaned"]
            and report["title"]
            and report["summary"]
            and "タスク" in report["tags"]
            and report["deadline"] == "2026-10-24"
        )
        check("V07", note_ok, "e2 title summary tags deadline cleanup")
        invoice = next(row for row in tray if "e10" in row["sourceIds"])
        merge_ok = invoice["sourceIds"] == ["e9", "e10", "e11"] and invoice["body"].count("やっぱり請求書は金曜までに出さないと") == 1
        check("V08", merge_ok, "merged e9 e10 e11")
        issue = next(row for row in tray if row["kind"] == "issue")
        issue_ok = issue["status"] == "draft" and issue["externalWrite"] is False and issue["explicitIntent"] is True
        check("V06b", issue_ok, "issue draft externalWrite false")
        daily = json.loads((out / "digests" / "2026-10-23.json").read_text(encoding="utf-8"))
        daily_ok = all(key in daily for key in ("themes", "決めたこと", "引っかかっていること", "openLoops", "handoff"))
        daily_ok = daily_ok and any(loop["theme"] == "請求書" for loop in daily["openLoops"]) and daily["決めたこと"] and daily["引っかかっていること"]
        check("V09", daily_ok, "daily digest sections")
        weekly = json.loads((out / "digests" / "2026-W43.json").read_text(encoding="utf-8"))
        check("V10b", weekly.get("kind") == "weekly" and weekly.get("結論の変化"), "weekly conclusion change")
        research = (out / "research.jsonl").read_text(encoding="utf-8")
        agent = (out / "agent-staging.jsonl").read_text(encoding="utf-8")
        check("V11", "何だっけ" in research and "クロード" in agent and "needsApproval" in agent, "research queue and agent staging")
        blob = "\n".join(path.read_text(encoding="utf-8") for path in out.rglob("*") if path.is_file())
        check("V18", "api.github.com" not in blob, "no GitHub URL in derived files")
    else:
        for item in ("V02", "V06b", "V07", "V08", "V09", "V10b", "V11", "V18"):
            check(item, False, "try output missing")

    listed = run([PYTHON, "scripts/tray_jev_probe.py", "--list"])
    list_ok = listed.returncode == 0 and "cases=40" in listed.stdout
    for edge in ("backchannel", "rhetorical", "decision", "mixed", "fragment"):
        list_ok = list_ok and f"edge={edge}" in listed.stdout
    check("V14", list_ok, "probe cases=40 with edges")
    scored = run(
        [PYTHON, "scripts/tray_jev_probe.py", "--score", "tests/fixtures/ai/jev_score_synthetic.json"]
    )
    score_ok = (
        scored.returncode == 0
        and "source: synthetic" in scored.stdout
        and "recommended_min_confidence: 0.92" in scored.stdout
        and "pane floor 0.8 is not used" in scored.stdout
    )
    check("V15", score_ok, "synthetic floor 0.92, pane floor not used")

    scratch = Path(tempfile.mkdtemp(prefix="voice-switch-ai-verify-"))
    first = run(
        [PYTHON, "scripts/voice-switch-ai", "triage", "--journal", str(JOURNAL), "--out", str(scratch / "out")]
    )
    tray_path = scratch / "out" / "tray.jsonl"
    first_text = tray_path.read_text(encoding="utf-8") if tray_path.exists() else ""
    second = run(
        [PYTHON, "scripts/voice-switch-ai", "triage", "--journal", str(JOURNAL), "--out", str(scratch / "out")]
    )
    second_text = tray_path.read_text(encoding="utf-8") if tray_path.exists() else ""
    check(
        "V17",
        first.returncode == 0 and second.returncode == 0 and first_text == second_text and first_text != "",
        "second triage keeps tray bytes",
    )

    empty = run(
        [PYTHON, "scripts/voice-switch-ai", "search", "--journal", str(JOURNAL), "--out", str(scratch / "empty"), ""]
    )
    check("V13b", empty.returncode == 2, f"empty search exit {empty.returncode}")

    PROOF.parent.mkdir(parents=True, exist_ok=True)
    lines = [
        "skill: verify-voice-switch-ai",
        "source: create-verification-skill",
        "live calibration: not run",
        "Mac 未検証",
        "Windows 未検証",
        "",
        "doctor:",
        doctor.stdout.rstrip(),
        "",
        "try:",
        stdout.rstrip(),
        "",
        "probe-list:",
        listed.stdout.rstrip(),
        "",
        "probe-score:",
        scored.stdout.rstrip(),
        "",
        "items:",
    ]
    failed = [item for item, ok, _detail in results if not ok]
    for item, ok, detail in results:
        lines.append(f"{item} {'PASS' if ok else 'FAIL'} {detail}")
    lines.append("")
    lines.append("failed: " + (", ".join(failed) if failed else "none"))
    PROOF.write_text("\n".join(lines) + "\n", encoding="utf-8")
    SCRATCH_LIST.write_text(str(scratch) + "\n" + str(out) + "\n", encoding="utf-8")
    print(f"proof: {PROOF}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
