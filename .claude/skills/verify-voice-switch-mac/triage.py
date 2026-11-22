#!/usr/bin/env python3
"""Drive the 依頼 send path (docs/journal-triage-execution.md, plan A) in the built binary and check its side effects.

Usage: VS_BIN=<voice-switch binary> python3 triage.py [scenario ...]
       scenarios: unsure heavy sure restart failed destinations disabled (default: all)

Each run is `voice-switch --simulate - --triage-fixture <fixture.json>` with an isolated HOME and VOICE_SWITCH_CONFIG in
the evidence folder, TYPESAFE_API_KEY and JEV_API_KEY removed, and Jev replaced by the fixture's answers. Dictations go
straight into DictationHistory (no audio, no Superwhisper). Routes are shell commands that append to files in the evidence
folder: starts.txt gets "start <text>" when a route begins, sent.txt gets the text when it ends. Nothing outside the
evidence folder is written; the script compares the user's real files before and after.
"""
import json, os, re, signal, subprocess, sys, time
from pathlib import Path

ROOT = Path(os.environ.get("VS_VERIFY_ROOT", Path.home() / "Library/Caches/voice-switch-verify"))
BIN = os.environ.get("VS_BIN", str(ROOT / "build/release/voice-switch"))
EVID = ROOT / "evidence" / os.environ.get("VS_RUN_ID", "triage-" + time.strftime("%Y%m%d-%H%M%S"))
QUESTIONS = ["wants_action", "is_filler", "kind_self_task", "kind_look_up", "kind_write_text", "kind_computer_change", "kind_delegate"]
REAL = [Path.home() / p for p in (
    "Library/Application Support/voice-switch/proposals.jsonl", "Library/Caches/voice-switch/triage",
    "repos/changelog/changelogmemo", "repos/changelog/voice-switch-inbox.jsonl")]
# The user's app may write its own journal lines meanwhile, so the journals are checked for this script's texts instead.
JOURNALS = [Path.home() / "Library/Application Support/voice-switch/journal", Path.home() / "repos/context-storage/voice-switch"]
MARKS = re.compile(r"(?:unsure|heavy|warm|sure|cancel|slow|fail|off|gate)-\d|dest-[a-z]+-\d")
REPO = Path(__file__).resolve().parents[3]


def leaked():
    found = []
    for root in JOURNALS:
        for f in root.rglob(time.strftime("%Y-%m-%d") + ".jsonl") if root.exists() else []:
            found += [str(f) for l in f.read_text(errors="replace").splitlines() if MARKS.search(l)]
    for memo in (Path.home() / "repos/changelog/changelogmemo", Path.home() / "repos/changelog/voice-switch-inbox.jsonl",
                 Path.home() / "Library/Application Support/voice-switch/proposals.jsonl"):
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

    def run(self, fixture, kill_on=None, kill_when=None):
        """One launch. kill_on: a log regex; when it appears the process gets SIGKILL (the app dies mid-send).
        kill_when: the same with a test on the whole log so far."""
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
            so_far = (self.dir / f"{tag}-stdout.txt").read_text()
            if (kill_on and re.search(kill_on, so_far)) or (kill_when and kill_when(so_far)):
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
    # A request whose kind is unsure (wants_action yes, no kind yes). An unsure intent is kept as a memo instead (2026-11-22).
    r = h.run({"answers": {q: 0.05 for q in QUESTIONS} | {"wants_action": 0.9}, "dictations": ["unsure-1 来週の分"], "seconds": 5})
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
    labels = h.dir / "storage/.triage/labels/private-app.jsonl"
    rows = [json.loads(l) for l in labels.read_text().splitlines()] if labels.exists() else []
    by_text = {x["id"]: x["text"] for x in h.proposals().values()}
    got = sorted((by_text.get(r["id"], "?")[:7], r.get("outcome"), r.get("target")[0]) for r in rows)
    want = sorted([(f"warm-{i}", "memo", "action:self_task") for i in range(5)] + [("cancel-", "drop", "thought")])
    check("(o) every 送る and 送らない writes one label with its outcome and day; a send nobody touched writes none",
          [(t[:6], o, g) for t, o, g in got] == [(t[:6], o, g) for t, o, g in want]
          and all(r.get("day") == time.strftime("%Y-%m-%d") and r.get("answers") for r in rows),
          [f"labels: {got}"])


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
    """The route is the real scripts/memo-inbox-add, pointed at an inbox whose folder is a file, so it exits 1; the
    request must land in 確認が要るもの as failed with its reason, and neither the rest of the run nor a relaunch sends it
    again. The warm-up goes through the same script into a writable inbox first."""
    h = Home("failed")
    inbox = h.dir / "inbox.jsonl"
    blocked = h.dir / "blocked"
    target = h.dir / "target"
    add = f'VOICE_SWITCH_MEMO_INBOX="$(cat {target})" {sys.executable} {REPO}/scripts/memo-inbox-add task'
    h.config["dictation"]["triage"]["routes"]["self_task"] = {
        "command": f't=$(cat); echo "start $t" >> {h.dir}/starts.txt; printf "%s\\n" "$t" | {add} && printf "%s\\n" "$t" >> {h.dir}/sent.txt',
        "heavy": False, "graceSeconds": 2}
    (h.dir / "config.json").write_text(json.dumps(h.config, ensure_ascii=False, indent=2))
    target.write_text(str(inbox))
    warm(h)  # five 送る through the real script, so the next request goes out on its own after the grace period
    texts = lambda: [json.loads(l)["text"] for l in inbox.read_text().splitlines()] if inbox.exists() else []
    warmed = sum(t.startswith("warm-") for t in texts())
    blocked.write_text("a file where the inbox folder should be")
    target.write_text(str(blocked / "inbox.jsonl"))
    said = "fail-1 牛乳を買う"
    r = h.run({"answers": sure("self_task"), "dictations": [said], "seconds": 8})
    target.write_text(str(inbox))
    r2 = h.run({"answers": sure("self_task"), "dictations": [], "seconds": 6})
    starts = [l for l in h.lines("starts.txt") if l == f"start {said}"]
    row = [x for x in h.proposals().values() if x["text"] == said]
    failed_lines = [l for l in r["triage"] if re.search(r"triage: \S+ failed self_task command exit 1$", l)]
    sending = [l for l in r["triage"] + r2["triage"] if " sending self_task" in l]
    check("(e) a route that exits 1 (memo-inbox-add cannot write its inbox) leaves the request failed with its reason; "
          "nothing sends it again, not even a relaunch with the inbox writable",
          warmed == 5 and len(failed_lines) == 1 and len(sending) == 1 and len(starts) == 1 and said not in texts()
          and not [l for l in h.lines("sent.txt") if l == said]
          and row and row[0]["stage"] == "failed" and row[0].get("failureReason") == "command exit 1",
          [f"warm-up: inbox has {warmed} warm- lines"] + ["run 1 (inbox unwritable):"] + r["triage"]
          + ["run 2 (relaunch, inbox writable, no new dictation):"] + r2["triage"]
          + [f"starts.txt 'fail-1' lines: {len(starts)}, inbox 'fail-1' lines: {texts().count(said)}, "
             f"last stage: {row[0]['stage'] if row else None}, failureReason: {row[0].get('failureReason') if row else None}"])


def destinations():
    """delegate has two candidates, A (the default, graceSeconds 2) and B (graceSeconds 5), each a command that appends
    to its own file, and C (graceSeconds 2), which is never sent to. (g) The user presses [B に送る] on 確認が要るもの.
    (h) A sure request shows A in まもなく送る, the user switches it to B, and only B gets it. (f) A sure request nobody
    touches goes to A. (i) The app dies after a switch to B, before B's grace period ends; on relaunch the request still
    waits for B and goes only to B. (j) A switch to C lands in 確認が要るもの with C's warm-up reason although A and B are
    past theirs: rules 4 and 5 count per destination."""
    h = Home("destinations")
    dest = lambda name: (f't=$(cat); echo "start $t" >> {h.dir}/starts.txt; printf "%s\\n" "$t" >> {h.dir}/{name}.txt; '
                         f'printf "%s\\n" "$t" >> {h.dir}/sent.txt')
    h.config["dictation"]["triage"]["routes"]["delegate"] = [
        {"name": "A（既定の送り先）", "command": dest("dest-a"), "heavy": False, "graceSeconds": 2},
        {"name": "B（2 番目の送り先）", "command": dest("dest-b"), "heavy": False, "graceSeconds": 5},
        {"name": "C（送ったことのない重い送り先）", "command": dest("dest-c"), "heavy": True, "graceSeconds": 2}]
    (h.dir / "config.json").write_text(json.dumps(h.config, ensure_ascii=False, indent=2))
    at = lambda name, text: h.lines(f"{name}.txt").count(text)
    rows = lambda prefix: [x for x in h.proposals().values() if x["text"].startswith(prefix)]

    ra = h.run({"answers": sure("delegate"), "verification": 0.9, "action": "send", "dictations": [f"dest-a-{i}" for i in range(5)], "seconds": 6})
    rb = h.run({"answers": sure("delegate"), "verification": 0.9, "action": "send", "routeIndex": 1, "dictations": [f"dest-b-{i}" for i in range(5)],
                "seconds": 6})
    b_rows = rows("dest-b-")
    check("(g) on 確認が要るもの [B に送る] sends to the second candidate only, and proposals.jsonl records index 1 and B's name",
          all(at("dest-a", f"dest-a-{i}") == 1 for i in range(5)) and all(at("dest-b", f"dest-b-{i}") == 1 for i in range(5))
          and not [l for l in h.lines("dest-a.txt") if l.startswith("dest-b-")]
          and len(b_rows) == 5 and all(r["routeIndex"] == 1 and r["routeName"] == "B（2 番目の送り先）" and r["stage"] == "done"
                                       for r in b_rows),
          ["run A (送る on the default):"] + ra["triage"] + ["run B (送る with candidate 1):"] + rb["triage"]
          + [f"dest-a.txt: {h.lines('dest-a.txt')}", f"dest-b.txt: {h.lines('dest-b.txt')}",
             f"B rows (routeIndex, routeName, stage, reasons): {[(r['routeIndex'], r['routeName'], r['stage'], r['reasons']) for r in b_rows]}"])
    time.sleep(61)  # rule 5: no request to the same destination within 60 s of the warm-up

    said = "dest-switch-1 会議室を予約して"
    r = h.run({"answers": sure("delegate"), "verification": 0.9, "action": "switch", "routeIndex": 1, "dictations": [said], "seconds": 9})
    pend = [l for l in r["triage"] if " pending delegate until " in l]
    until = [time.mktime(time.strptime(re.search(r"until (\S+)Z", l).group(1), "%Y-%m-%dT%H:%M:%S")) for l in pend]
    row = rows("dest-switch-")
    check("(h) a sure request shows A in まもなく送る; switched to B in its grace period, it waits B's graceSeconds and goes to B only",
          len(pend) == 2 and until[1] - until[0] >= 2 and at("dest-b", said) == 1 and at("dest-a", said) == 0
          and len([l for l in r["triage"] if " sending delegate" in l]) == 1
          and row and row[0]["routeIndex"] == 1 and row[0]["routeName"] == "B（2 番目の送り先）" and row[0]["stage"] == "done",
          r["triage"] + [f"deadlines A then B: {pend and [l.split(' until ')[1] for l in pend]}",
                         f"dest-a.txt has it: {at('dest-a', said)}, dest-b.txt has it: {at('dest-b', said)}",
                         f"last line: routeIndex {row[0]['routeIndex'] if row else None}, stage {row[0]['stage'] if row else None}"])

    said = "dest-auto-1 来週の予定を登録して"
    r = h.run({"answers": sure("delegate"), "verification": 0.9, "dictations": [said], "seconds": 6})
    row = rows("dest-auto-")
    check("(f) a sure request nobody touches goes to the default candidate A after its grace period (B's request 10 s earlier "
          "is not a repeat for A)",
          at("dest-a", said) == 1 and at("dest-b", said) == 0 and len([l for l in r["triage"] if " pending delegate until " in l]) == 1
          and row and row[0]["routeIndex"] == 0 and row[0]["routeName"] == "A（既定の送り先）" and row[0]["stage"] == "done",
          r["triage"] + [f"dest-a.txt has it: {at('dest-a', said)}, dest-b.txt has it: {at('dest-b', said)}",
                         f"last line: routeIndex {row[0]['routeIndex'] if row else None}, stage {row[0]['stage'] if row else None}"])
    time.sleep(61)

    said = "dest-keep-1 夕食のタスクを 19 時にして"
    r1 = h.run({"answers": sure("delegate"), "verification": 0.9, "action": "switch", "routeIndex": 1, "dictations": [said], "seconds": 20},
               kill_when=lambda text: text.count(" pending delegate until ") >= 2)
    before = rows("dest-keep-")
    r2 = h.run({"answers": sure("delegate"), "verification": 0.9, "dictations": [], "seconds": 9})
    row = rows("dest-keep-")
    check("(i) the app dies after the switch to B, inside B's grace period; on relaunch the request keeps B and goes to B only",
          r1["killed"] and not any(" sending" in l for l in r1["triage"])
          and before and before[0]["stage"] == "pending" and before[0]["routeIndex"] == 1
          and len([l for l in r2["triage"] if " sending delegate" in l]) == 1
          and at("dest-b", said) == 1 and at("dest-a", said) == 0
          and row and row[0]["routeIndex"] == 1 and row[0]["routeName"] == "B（2 番目の送り先）" and row[0]["stage"] == "done",
          ["run 1 (SIGKILL after the switch):"] + r1["triage"]
          + [f"proposals.jsonl after the kill: stage {before[0]['stage'] if before else None}, routeIndex {before[0]['routeIndex'] if before else None}"]
          + ["run 2 (relaunch, no new dictation):"] + r2["triage"]
          + [f"dest-a.txt has it: {at('dest-a', said)}, dest-b.txt has it: {at('dest-b', said)}",
             f"last line: routeIndex {row[0]['routeIndex'] if row else None}, stage {row[0]['stage'] if row else None}"])

    said = "dest-cold-1 郵便の再配達を頼んで"  # A's last send was over 60 s ago, B's was (i)
    r = h.run({"answers": sure("delegate"), "verification": 0.9, "action": "switch", "routeIndex": 2, "dictations": [said], "seconds": 6})
    row = rows("dest-cold-")
    check("(j) switched to C, which has no sends yet, the request waits in 確認が要るもの for C's warm-up and is not sent",
          len([l for l in r["triage"] if " pending delegate until " in l]) == 1
          and any(" exception 送り先の最初の5件" in l for l in r["triage"]) and not any(" sending" in l for l in r["triage"])
          and not h.lines("dest-c.txt") and at("dest-a", said) == 0
          and row and row[0]["stage"] == "review" and row[0]["routeIndex"] == 2 and "送り先の最初の5件" in row[0]["reasons"],
          r["triage"] + [f"dest-c.txt lines: {len(h.lines('dest-c.txt'))}",
                         f"last line: stage {row[0]['stage'] if row else None}, routeIndex {row[0]['routeIndex'] if row else None}, "
                         f"reasons {row[0]['reasons'] if row else None}"])


def disabled():
    h = Home("disabled", enabled=False)
    r = h.run({"answers": sure("self_task"), "dictations": ["off-1 送らない"], "seconds": 4})
    f = h.home / "Library/Application Support/voice-switch/proposals.jsonl"
    check("E7 with triage.enabled false nothing is classified, filed or sent",
          not r["triage"] and not f.exists() and not h.lines("starts.txt"),
          [f"triage log lines: {len(r['triage'])}, proposals.jsonl exists: {f.exists()}, starts.txt lines: {len(h.lines('starts.txt'))}"])


def gates():
    """needs_local 0.7 or more: a sure, light bot request is neither sent nor kept as a memo. keep_note 0.7 or more:
    a thought goes to the memo route at once; below it the thought stays in the journal only."""
    h = Home("gates")
    memo = f't=$(cat); printf "%s\\n" "$t" >> {h.dir}/memo.txt'
    h.config["dictation"]["triage"]["memo"] = {"command": memo, "heavy": False}
    h.config["dictation"]["triage"]["routes"]["look_up"] = {"command": h.config["dictation"]["triage"]["routes"]["self_task"]["command"],
                                                            "heavy": False, "graceSeconds": 2}
    (h.dir / "config.json").write_text(json.dumps(h.config, ensure_ascii=False, indent=2))
    thought = {q: 0.05 for q in QUESTIONS}
    r1 = h.run({"answers": sure("look_up") | {"needs_local": 0.9, "keep_note": 0.7}, "dictations": ["gate-1 原因を特定してください"], "seconds": 6})
    r2 = h.run({"answers": thought | {"needs_local": 0.05, "keep_note": 0.9}, "dictations": ["gate-2 冬休みは年末に取ろう"], "seconds": 4})
    r3 = h.run({"answers": thought | {"needs_local": 0.05, "keep_note": 0.2}, "dictations": ["gate-3 いいですね"], "seconds": 4})
    memo_lines = h.lines("memo.txt")
    check("(k) an instruction to the session on screen (needs_local 0.9) is neither sent to a light bot route nor kept as a memo",
          any(" session" in l for l in r1["triage"]) and not any(" sending" in l for l in r1["triage"])
          and not h.lines("starts.txt") and "gate-1 原因を特定してください" not in memo_lines,
          r1["triage"] + [f"starts.txt lines: {len(h.lines('starts.txt'))}, memo.txt: {memo_lines}"])
    unsure_kind = {q: 0.05 for q in QUESTIONS} | {"wants_action": 0.95, "needs_local": 0.05}
    r4 = h.run({"answers": unsure_kind | {"keep_note": 0.9}, "dictations": ["gate-4 マルチプレクサは後で決める"], "seconds": 4})
    r5 = h.run({"answers": unsure_kind | {"keep_note": 0.2}, "dictations": ["gate-5 あれをお願い"], "seconds": 4})
    rows = {r["text"]: r for r in h.proposals().values()}
    memo_lines = h.lines("memo.txt")
    check("(m) a request of unsure kind that is worth keeping becomes one memo and is not held for review; one that is not stays in review",
          memo_lines.count("gate-4 マルチプレクサは後で決める") == 1 and rows.get("gate-4 マルチプレクサは後で決める", {}).get("stage") != "review"
          and rows.get("gate-5 あれをお願い", {}).get("stage") == "review" and "gate-5 あれをお願い" not in memo_lines,
          r4["triage"] + r5["triage"] + [f"stages: {[(t[:7], r['stage']) for t, r in rows.items()]}", f"memo.txt: {memo_lines}"])
    r6 = h.run({"answers": sure("look_up") | {"needs_local": 0.05}, "verification": 0.3, "dictations": ["gate-6 検索して調査。"], "seconds": 5})
    row6 = {r["text"]: r for r in h.proposals().values()}.get("gate-6 検索して調査。", {})
    check("(n) a light bot request from an automatic recording whose extra check says 0.3 waits in review and is not sent",
          row6.get("stage") == "review" and "自動録音の依頼で確認不足" in (row6.get("reasons") or [])
          and not any(" sending" in l for l in r6["triage"]),
          r6["triage"] + [f"stage {row6.get('stage')}, reasons {row6.get('reasons')}"])
    check("(l) a thought worth keeping (keep_note 0.9) reaches the memo route once; one that is not (0.2) stays in the journal",
          memo_lines.count("gate-2 冬休みは年末に取ろう") == 1 and "gate-3 いいですね" not in memo_lines,
          r2["triage"] + r3["triage"] + [f"memo.txt: {memo_lines}"])


SCENARIOS = {"unsure": unsure, "heavy": heavy, "sure": sure_and_cancel, "restart": restart, "failed": failed,
             "destinations": destinations, "disabled": disabled, "gates": gates}

if __name__ == "__main__":
    if not os.access(BIN, os.X_OK):
        sys.exit(f"no binary at {BIN}; run verify.sh build or set VS_BIN")
    before = {p: stat(p) for p in REAL}
    for name in sys.argv[1:] or SCENARIOS:
        SCENARIOS[name]()
    shown = [f"{run}: {said}" for run, said, found in SAID if found]
    check("no log line carries the dictated text (the triage lines give ids, kinds and fixed reasons only)",
          bool(SAID) and not shown, shown or [f"{len(SAID)} dictated texts, none in the runs' stdout"])
    # The user's own app keeps writing these files during a run, so a changed file is not a leak; a test text in one is.
    changed = leaked()
    check("the user's real proposals.jsonl, triage cache, changelogmemo, memo inbox and journals are untouched",
          not changed, [f"test text found in: {changed}" if changed else "no test text in today's journals, changelogmemo, the memo inbox or proposals.jsonl"])
    print(f"evidence={EVID}")
    sys.exit(0 if all(results) else 1)
