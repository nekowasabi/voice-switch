#!/usr/bin/env python3
"""Drive the 依頼 send path (docs/journal-triage-execution.md, plan A) in the built binary and check its side effects.

Usage: VS_BIN=<voice-switch binary> python3 triage.py [scenario ...]
       scenarios: unsure heavy sure restart failed disabled (default: all)

Each run is `voice-switch --simulate - --triage-fixture <fixture.json>` with an isolated HOME and VOICE_SWITCH_CONFIG in
the evidence folder, TYPESAFE_API_KEY and JEV_API_KEY removed, and Jev replaced by the fixture's answers. Dictations go
straight into DictationHistory (no audio, no Superwhisper). Routes are shell commands that append to files in the evidence
folder: starts.txt gets "start <text>" when a route begins, sent.txt gets the text when it ends. Nothing outside the
evidence folder is written; the script compares the user's real files before and after.
"""
import fcntl, json, os, re, signal, subprocess, sys, time
from pathlib import Path

ROOT = Path(os.environ.get("VS_VERIFY_ROOT", Path.home() / "Library/Caches/voice-switch-verify"))
BIN = os.environ.get("VS_BIN", str(ROOT / "build/release/voice-switch"))
EVID = ROOT / "evidence" / os.environ.get("VS_RUN_ID", "triage-" + time.strftime("%Y%m%d-%H%M%S"))
QUESTIONS = ["wants_action", "is_filler", "kind_self_task", "kind_look_up", "kind_write_text", "kind_computer_change"]
REAL = [Path.home() / p for p in (
    "Library/Application Support/voice-switch/proposals.jsonl", "Library/Caches/voice-switch/triage",
    "repos/changelog/changelogmemo")]
# The user's app may write its own journal lines meanwhile, so the journals are checked for this script's texts instead.
JOURNALS = [Path.home() / "Library/Application Support/voice-switch/journal", Path.home() / "repos/context-storage/voice-switch"]
MARKS = re.compile(r"(?:unsure|heavy|warm|sure|cancel|slow|fail|off)-\d")
REPO = Path(__file__).resolve().parents[3]


def leaked():
    found = []
    for root in JOURNALS:
        for f in root.rglob(time.strftime("%Y-%m-%d") + ".jsonl") if root.exists() else []:
            found += [str(f) for l in f.read_text(errors="replace").splitlines() if MARKS.search(l)]
    memo = Path.home() / "repos/changelog/changelogmemo"
    if memo.exists() and MARKS.search(memo.read_text(errors="replace")):
        found.append(str(memo))
    return sorted(set(found))


def sure(kind):
    return {q: 0.05 for q in QUESTIONS} | {"wants_action": 0.95, f"kind_{kind}": 0.95}


def stat(p):
    try:
        s = p.stat()
        return (s.st_mtime_ns, s.st_size)
    except FileNotFoundError:
        return None


class Home:
    """One isolated HOME with its config, route outputs and journal; runs keep it between launches."""

    def __init__(self, name, enabled=True):
        self.dir = EVID / name
        self.home = self.dir / "home"
        self.home.mkdir(parents=True, exist_ok=True)
        out = self.dir
        route = lambda extra="": (f't=$(cat); echo "start $t" >> {out}/starts.txt; {extra}'
                                  f'printf "%s\\n" "$t" >> {out}/sent.txt')
        self.config = {
            "wakeWords": ["音声入力"], "locale": "ja_JP", "command": f"touch {out}/wake.marker",
            "stopWords": ["入力ストップ"], "stopCommand": f"touch {out}/stop.marker",
            "dictation": {
                "superwhisperMode": "vs-verify-missing",
                "journalDir": str(out / "storage/private"),
                "triage": {
                    "enabled": enabled, "graceSeconds": 300,
                    "routes": {
                        "self_task": {"command": route(), "heavy": False, "graceSeconds": 2},
                        "look_up": {"command": route()},  # heavy by default
                        "write_text": {"command": route("sleep 20; "), "heavy": False},
                    },
                },
            },
        }
        (out / "config.json").write_text(json.dumps(self.config, ensure_ascii=False, indent=2))
        self.runs = 0

    def run(self, fixture, kill_on=None):
        """One launch. kill_on: a log regex; when it appears the process gets SIGKILL (the app dies mid-send)."""
        self.runs += 1
        tag = f"run{self.runs}"
        fx = self.dir / f"{tag}-fixture.json"
        fx.write_text(json.dumps(fixture, ensure_ascii=False))
        env = {k: v for k, v in os.environ.items() if k not in ("TYPESAFE_API_KEY", "JEV_API_KEY")}
        env |= {"HOME": str(self.home), "VOICE_SWITCH_CONFIG": str(self.dir / "config.json")}
        log = (self.dir / f"{tag}-stdout.txt").open("w")
        p = subprocess.Popen([BIN, "--simulate", "-", "--triage-fixture", str(fx)], env=env, stdout=log,
                             stderr=subprocess.STDOUT, cwd=self.dir)
        killed = False
        deadline = time.time() + 60
        while p.poll() is None and time.time() < deadline:
            time.sleep(0.2)
            if kill_on and re.search(kill_on, (self.dir / f"{tag}-stdout.txt").read_text()):
                p.send_signal(signal.SIGKILL); killed = True
        if p.poll() is None:
            p.kill()
        p.wait(); log.close()
        text = (self.dir / f"{tag}-stdout.txt").read_text()
        lines = [l for l in text.splitlines() if "triage" in l]
        for said in fixture.get("dictations") or []:
            SAID.append((f"{self.dir.name}/{tag}", said, any(said in l for l in text.splitlines())))
        return {"exit": p.returncode, "killed": killed, "triage": lines, "all": text}

    def lines(self, name):
        f = self.dir / name
        return f.read_text().splitlines() if f.exists() else []

    def proposals(self):
        f = self.home / "Library/Application Support/voice-switch/proposals.jsonl"
        last = {}
        for l in (f.read_text().splitlines() if f.exists() else []):
            row = json.loads(l); last[row["id"]] = row
        return last


results = []
SAID = []  # (run, dictated text, whether any log line of that run contains it)


def check(name, ok, evidence):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name)
    for e in evidence:
        print("    " + e)


def unsure():
    h = Home("unsure")
    r = h.run({"answers": {q: 0.05 for q in QUESTIONS} | {"wants_action": 0.5}, "dictations": ["unsure-1 来週の分"], "seconds": 5})
    rows = list(h.proposals().values())
    check("(a) an unsure request waits in 確認が要るもの and is not sent",
          len(rows) == 1 and rows[0]["stage"] == "review" and not h.lines("starts.txt")
          and any(" exception " in l for l in r["triage"]),
          r["triage"] + [f"proposals.jsonl last stage: {[x['stage'] for x in rows]}, starts.txt lines: {len(h.lines('starts.txt'))}"])


def heavy():
    h = Home("heavy")
    r = h.run({"answers": sure("look_up"), "verification": 0.4, "dictations": ["heavy-1 天気"], "seconds": 5})
    rows = list(h.proposals().values())
    check("(a') a heavy route whose extra check says 0.4 is an exception, not sent",
          len(rows) == 1 and rows[0]["stage"] == "review" and not h.lines("starts.txt")
          and any(" verify look_up 0.4" in l for l in r["triage"]),
          r["triage"] + [f"proposals.jsonl last stage: {[x['stage'] for x in rows]}, starts.txt lines: {len(h.lines('starts.txt'))}"])


def warm(h):
    """The first 5 sends to a route are exceptions (rule 4). The user presses 送る on 5 requests, then waits out rule 5."""
    r = h.run({"answers": sure("self_task"), "action": "send", "dictations": [f"warm-{i}" for i in range(5)], "seconds": 6})
    time.sleep(61)
    return r


def sure_and_cancel():
    h = Home("sure")
    w = warm(h)
    warmed = len([l for l in h.lines("sent.txt") if l.startswith("warm-")])
    r = h.run({"answers": sure("self_task"), "dictations": ["sure-1 牛乳を買う"], "seconds": 8})
    starts = [l for l in h.lines("starts.txt") if l == "start sure-1 牛乳を買う"]
    sent = [l for l in h.lines("sent.txt") if l == "sure-1 牛乳を買う"]
    pend = [l for l in r["triage"] if " pending self_task until " in l]
    sending = [l for l in r["triage"] if " sending self_task" in l]
    row = [x for x in h.proposals().values() if x["text"] == "sure-1 牛乳を買う"]
    check("(b) a sure request waits graceSeconds 2 in まもなく送る, then goes out exactly once",
          warmed == 5 and len(pend) == 1 and len(sending) == 1 and len(starts) == 1 and len(sent) == 1
          and row and row[0]["stage"] == "done" and row[0].get("outcome") == "sent",
          [f"warm-up: 5 requests sent by 送る, sent.txt has {warmed}"] + r["triage"]
          + [f"starts.txt 'sure-1' lines: {len(starts)}, sent.txt 'sure-1' lines: {len(sent)}, last stage: {row[0]['stage'] if row else None}"])
    time.sleep(61)
    r = h.run({"answers": sure("self_task"), "action": "cancel", "dictations": ["cancel-1 取り消す依頼"], "seconds": 6})
    row = [x for x in h.proposals().values() if x["text"] == "cancel-1 取り消す依頼"]
    hist = [json.loads(l)["stage"] for l in (h.home / "Library/Application Support/voice-switch/proposals.jsonl").read_text().splitlines()
            if json.loads(l)["text"] == "cancel-1 取り消す依頼"]
    check("(c) a request cancelled in まもなく送る is never sent, even after its grace period",
          hist == ["pending", "dismissed"] and not [l for l in h.lines("starts.txt") if "cancel-1" in l],
          r["triage"] + [f"proposals.jsonl stages for it: {hist}, starts.txt 'cancel-1' lines: {len([l for l in h.lines('starts.txt') if 'cancel-1' in l])}"])


def restart():
    h = Home("restart")
    r1 = h.run({"answers": sure("write_text"), "action": "send", "dictations": ["slow-1 下書き"], "seconds": 30},
               kill_on=r"triage: \S+ sending write_text")
    time.sleep(1)
    r2 = h.run({"answers": sure("write_text"), "dictations": [], "seconds": 4})
    time.sleep(22)  # the orphaned route finishes its sleep 20; a resend would add a second start line
    starts = [l for l in h.lines("starts.txt") if "slow-1" in l]
    rows = list(h.proposals().values())
    check("(d) the app dies while a slow route runs; on relaunch the request is 送ったか不明 and is not sent again",
          r1["killed"] and any("restored unknown" in l for l in r2["triage"]) and not any("sending" in l for l in r2["triage"])
          and len(starts) == 1 and rows and rows[0]["stage"] == "executing",
          ["run 1 (SIGKILL after the sending line):"] + r1["triage"] + ["run 2 (relaunch, no new dictation):"] + r2["triage"]
          + [f"starts.txt 'slow-1' lines after 22 s: {len(starts)}, proposals.jsonl last line stage: {[x['stage'] for x in rows]}"])


def failed():
    """The route is the real scripts/changelogmemo-add on a memo in the evidence folder. While this script holds the memo's
    lock, the add gives up after CHANGELOGMEMO_TIMEOUT_SECONDS 0.5 with exit 75; the request must land in 確認が要るもの as
    failed with its reason, and neither the rest of the run nor a relaunch (with the lock free again) sends it again."""
    h = Home("failed")
    memo = h.dir / "changelogmemo"
    add = (f'CHANGELOG_FILE={memo} CHANGELOGMEMO_TIMEOUT_SECONDS=0.5 {sys.executable} {REPO}/scripts/changelogmemo-add task')
    h.config["dictation"]["triage"]["routes"]["self_task"] = {
        "command": f't=$(cat); echo "start $t" >> {h.dir}/starts.txt; printf "%s\\n" "$t" | {add} && printf "%s\\n" "$t" >> {h.dir}/sent.txt',
        "heavy": False, "graceSeconds": 2}
    (h.dir / "config.json").write_text(json.dumps(h.config, ensure_ascii=False, indent=2))
    warm(h)  # five 送る through the real script, so the next request goes out on its own after the grace period
    warmed = memo.read_text().count("  - 内容: warm-") if memo.exists() else 0
    said = "fail-1 牛乳を買う"
    with open(str(memo) + ".lock", "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        r = h.run({"answers": sure("self_task"), "dictations": [said], "seconds": 8})
    r2 = h.run({"answers": sure("self_task"), "dictations": [], "seconds": 6})
    starts = [l for l in h.lines("starts.txt") if l == f"start {said}"]
    row = [x for x in h.proposals().values() if x["text"] == said]
    failed_lines = [l for l in r["triage"] if re.search(r"triage: \S+ failed self_task command exit 75$", l)]
    sending = [l for l in r["triage"] + r2["triage"] if " sending self_task" in l]
    in_memo = memo.read_text().count(f"  - 内容: {said}") if memo.exists() else 0
    check("(e) a route that exits 75 (changelogmemo-add out of time) leaves the request failed with its reason; "
          "nothing sends it again, not even a relaunch with the lock free",
          warmed == 5 and len(failed_lines) == 1 and len(sending) == 1 and len(starts) == 1 and in_memo == 0
          and not [l for l in h.lines("sent.txt") if l == said]
          and row and row[0]["stage"] == "failed" and row[0].get("failureReason") == "command exit 75",
          [f"warm-up: changelogmemo has {warmed} warm- entries"] + ["run 1 (lock held):"] + r["triage"]
          + ["run 2 (relaunch, lock free, no new dictation):"] + r2["triage"]
          + [f"starts.txt 'fail-1' lines: {len(starts)}, changelogmemo 'fail-1' entries: {in_memo}, "
             f"last stage: {row[0]['stage'] if row else None}, failureReason: {row[0].get('failureReason') if row else None}"])


def disabled():
    h = Home("disabled", enabled=False)
    r = h.run({"answers": sure("self_task"), "dictations": ["off-1 送らない"], "seconds": 4})
    f = h.home / "Library/Application Support/voice-switch/proposals.jsonl"
    check("E7 with triage.enabled false nothing is classified, filed or sent",
          not r["triage"] and not f.exists() and not h.lines("starts.txt"),
          [f"triage log lines: {len(r['triage'])}, proposals.jsonl exists: {f.exists()}, starts.txt lines: {len(h.lines('starts.txt'))}"])


SCENARIOS = {"unsure": unsure, "heavy": heavy, "sure": sure_and_cancel, "restart": restart, "failed": failed,
             "disabled": disabled}

if __name__ == "__main__":
    if not os.access(BIN, os.X_OK):
        sys.exit(f"no binary at {BIN}; run verify.sh build or set VS_BIN")
    before = {p: stat(p) for p in REAL}
    for name in sys.argv[1:] or SCENARIOS:
        SCENARIOS[name]()
    shown = [f"{run}: {said}" for run, said, found in SAID if found]
    check("no log line carries the dictated text (the triage lines give ids, kinds and fixed reasons only)",
          bool(SAID) and not shown, shown or [f"{len(SAID)} dictated texts, none in the runs' stdout"])
    changed = [str(p) for p in REAL if stat(p) != before[p]] + leaked()
    check("the user's real proposals.jsonl, triage cache, changelogmemo and journals are untouched",
          not changed, [f"changed: {changed}" if changed else f"unchanged: {len(REAL)} files; no test text in today's journals or changelogmemo"])
    print(f"evidence={EVID}")
    sys.exit(0 if all(results) else 1)
