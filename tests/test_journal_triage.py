"""Behavior test for scripts/journal_triage.py, the triage CLI (docs/journal-triage-design.md).

Every case drives main() through its arguments with HTTP replaced and HOME in a temp folder, and reads back what a
caller can see: the JSON on stdout, the request sent to Jev, the files under the journal root and the cache, stderr.
Expected values come from the design (the truth table lives in tests/parity/make_triage_decide.py), not from the script.
Failure messages start with the rule id the verify-journal-append skill uses:
T1 no Jev request with --no-jev or without a key, and no real connection ever; T6 which rows are listed, with time
and context, the journal untouched, broken rows counted, dot folders not read as devices;
C1 the truth table has no hole (tests/parity/fixtures/triage_decide.json); C2 filler only when wants_action is no and
is_filler is yes, never on an error or without Jev; C3 the request carries only the table's fields (only the utterance
with --no-context); C4 a cached answer with the same question wording is not asked again, a new wording is; C5 the
journal bytes stay, cache lines of ids gone from the journal go; C6 stderr carries no text, app or task; C7 previous is
the same device, listed rows, within 60 seconds before, at most 2; C8 the label split depends on the id only and tune
proposes only when the holdout gets lighter without more filler misses (and only from 30 labels); C9 eval and tune use
no network and score the answers kept with the labels; C10 label writes one line to its own device's file only.

JOURNAL_TRIAGE points the test at another copy of the script (run.sh mutants uses it).
"""
import contextlib
import importlib.util
import io
import itertools
import json
import os
import shutil
import socket
import sys
import tempfile
import unittest
import urllib.error
from datetime import datetime, timedelta
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
SCRIPT = Path(os.environ.get("JOURNAL_TRIAGE") or HERE.parent / "scripts/journal_triage.py")


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module  # dataclass resolves its own module while the script loads
    spec.loader.exec_module(module)
    return module


triage = load("journal_triage", SCRIPT)
ref = load("make_triage_decide", HERE / "parity/make_triage_decide.py")
QUESTIONS = ref.QUESTIONS
DEFAULT = {q: dict(ref.DEFAULT) for q in QUESTIONS}

DAY = "2026-11-21"
T0 = datetime.fromisoformat("2026-11-21T14:03:05.123+09:00")
FOCUS, TASK = "FocusSentinelApp", "TaskSentinelName"
# Never sent: what the journal row holds beyond the table's fields.
HIDDEN = ("BundleSentinel", "987654", "ExeSentinel", "TaskIdSentinel", "Silence", "IDS-")
STARTS = ("手動", "ウェイク", "自動")
ORIGINS = [*STARTS, None, "謎の値"]
DELIVERIES = ["<absent>", None, "paste", "sticky", "pane", "action", "history"]
LISTED = {"<absent>", None, "history"}
# Texts by how many local markers they hit. Every text carries QZX, which stderr must never show.
TEXTS = {0: "今日はいい天気ですね QZX0", 1: "タスク: 牛乳を買う QZX1", 2: "タスク: 新しいマイクを調べて QZX2"}
NO = {q: 0.05 for q in QUESTIONS}


def answers(**values):
    return NO | values


def row(entry_id, when, text, origin="ウェイク", delivery="history", app=FOCUS, task=TASK):
    out = {"id": entry_id, "timestamp": when.isoformat(timespec="milliseconds"), "text": text, "endReason": "Silence",
           "recordingSeconds": 1, "schemaVersion": 1,
           "focusApp": {"name": app, "bundleId": "BundleSentinel", "exe": "ExeSentinel", "pid": 987654},
           "taskchute": {"name": task, "id": "TaskIdSentinel", "startedAt": None, "source": "file", "readAt": None}}
    if app is None:
        out["focusApp"] = None
    if task is None:
        out["taskchute"] = None
    if origin is not None:
        out["origin"] = origin
    if delivery != "<absent>":
        out["delivery"] = delivery
    return json.dumps(out, ensure_ascii=False)


class World:
    """A temp HOME whose config.json points at <root>/private, so this device is "private"."""

    def __init__(self, tmp):
        self.home, self.root = Path(tmp) / "home", Path(tmp) / "root"
        for device in ("private", "wsl"):
            (self.root / device).mkdir(parents=True)
        config = self.home / ".config/voice-switch/config.json"
        config.parent.mkdir(parents=True)
        config.write_text(json.dumps({"dictation": {"journalDir": str(self.root / "private")}}), encoding="utf-8")
        self.cache = self.home / "Library/Caches/voice-switch/triage"

    def journal(self, device="private", day=DAY):
        return self.root / device / f"{day}.jsonl"

    def write(self, lines, device="private", day=DAY):
        self.journal(device, day).write_text("".join(line + "\n" for line in lines), encoding="utf-8")

    def cached(self, device="private", day=DAY):
        path = self.cache / device / f"{day}.jsonl"
        return path.read_text(encoding="utf-8").splitlines() if path.exists() else []

    def snapshot(self):
        return {p: p.read_bytes() for base in (self.root, self.home) for p in base.rglob("*") if p.is_file()}


class Net:
    """Fake urlopen plus a socket guard. reply(state) gives the Noul answers, an exception, or raw bytes."""

    def __init__(self, reply):
        self.reply, self.requests, self.connects = reply, [], 0

    def urlopen(self, request, timeout=None):
        self.requests.append(request)
        reply = self.reply(json.loads(request.data)["state"]) if callable(self.reply) else self.reply
        if isinstance(reply, Exception):
            raise reply
        if isinstance(reply, bytes):
            return io.BytesIO(reply)
        return io.BytesIO(json.dumps({"answers": {q: {"type": "noul", "noul": v} for q, v in reply.items()}}).encode())

    def connect(self, *args, **kwargs):
        self.connects += 1
        raise OSError("network blocked by test")

    def states(self):
        return [json.loads(request.data)["state"] for request in self.requests]


@contextlib.contextmanager
def environ(**values):
    """Set or (with None) remove a few variables, then restore them. patch.dict(clear=True) rewrites the whole
    environment twice per run, which was most of the test's time."""
    saved = {name: os.environ.get(name) for name in values}
    try:
        for name, value in values.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value
        yield
    finally:
        for name, value in saved.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value


def run(world, argv, reply=None, key="k-sentinel"):
    net = Net(reply)
    home = str(world.home)
    out, err = io.StringIO(), io.StringIO()
    code = 0
    with environ(HOME=home, USERPROFILE=home, LOCALAPPDATA=home, TYPESAFE_API_KEY=key, JEV_API_KEY=None), \
            mock.patch.object(triage.urllib.request, "urlopen", net.urlopen), \
            mock.patch.object(socket.socket, "connect", net.connect), \
            contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            triage.main(argv)
        except SystemExit as stop:
            code = stop.code or 0
    stderr = err.getvalue()
    for secret in ("QZX", FOCUS, TASK):
        assert secret not in stderr, f"C6 stderr carries {secret}: {argv}"
    assert net.connects == 0, f"T1 real connection: {argv}"
    return code, out.getvalue(), net


def listed(world, *args, reply=None, key="k-sentinel"):
    code, stdout, net = run(world, ["list", DAY, "--root", str(world.root), *args], reply, key)
    assert code == 0, f"T6 list exited {code}"
    return json.loads(stdout), net


def rows_of(result):
    return [(group, item) for group in ("actions", "thoughts", "unjudged") for item in result[group]]


def wire(request):
    return request.data.decode("utf-8") + json.dumps(dict(request.header_items()), ensure_ascii=False)


class Decide(unittest.TestCase):
    def test_fixture(self):
        fixture = json.loads((HERE / "parity/fixtures/triage_decide.json").read_text(encoding="utf-8"))
        self.assertEqual(fixture["default"], ref.DEFAULT)
        for case in fixture["cases"]:
            got = triage.decide(case["answers"], case.get("thresholds", DEFAULT))
            if got == ["filler"] != case["want"]:
                self.fail(f"C2 filler for {case['name']}")
            self.assertEqual(got, case["want"], f"C1 {case['name']}")


class Listing(unittest.TestCase):
    """Through main(): the destination of each answer, origin and local-marker count, with and without Jev."""

    REPLIES = [(f"wa={wa} filler={fi} kinds={kinds}", answers(wants_action=wa, is_filler=fi, **kinds))
               for wa in (0.9, 0.5, 0.1) for fi in (0.9, 0.5, 0.1)
               for kinds in ({}, {"kind_look_up": 0.8}, {"kind_self_task": 0.95, "kind_computer_change": 0.7},
                             {"kind_write_text": 0.6})]
    ERRORS = [("http-error", urllib.error.URLError("down")), ("not-json", b"<html>busy</html>"),
              ("missing-question", {q: 0.9 for q in QUESTIONS if q != "kind_look_up"}),
              ("noul-string", answers(wants_action="0.9")), ("noul-above-1", answers(is_filler=1.5))]

    def test_every_combination(self):
        # The file also holds verify_route, the app's extra check for a heavy route; the classifier asks only its 6.
        stored = json.loads((SCRIPT.parents[1] / "Resources/triage_questions.json").read_text(encoding="utf-8"))
        questions = {q: stored[q] for q in QUESTIONS}
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            n = 0
            # Without Jev the reply cannot matter, so one reply stands for all of them there.
            for origin, hits, mode, (name, reply) in itertools.product(
                    ORIGINS, TEXTS, ("jev", "no-jev", "no-key"), self.REPLIES + self.ERRORS):
                if mode != "jev" and name != self.REPLIES[0][0]:
                    continue
                n += 1
                text = TEXTS[hits]
                world.write([row(f"IDS-{n}", T0, text, origin)])
                before = world.journal().read_bytes()
                result, net = listed(world, *(["--no-jev"] if mode == "no-jev" else []),
                                     reply=reply, key=None if mode == "no-key" else "k-sentinel")
                where = f"origin={origin} hits={hits} mode={mode} reply={name}"
                self.assertEqual(world.journal().read_bytes(), before, "C5 journal changed " + where)
                send = mode == "jev"
                self.assertEqual(len(net.requests), int(send), f"{'C4' if send else 'T1'} requests " + where)
                if send:
                    want = ref.decide(reply if isinstance(reply, dict) else None, {})
                else:
                    want = ["action:self_task"] if hits == 1 else ["unjudged:no-jev"]
                found = rows_of(result)
                if want == ["filler"]:
                    self.assertEqual((found, result["filler_count"]), ([], 1), "C2 filler not counted " + where)
                    continue
                self.assertEqual(result["filler_count"], 0, "C2 filler " + where)
                self.assertEqual(len(found), 1, "T6 not listed " + where)
                group, item = found[0]
                self.assertEqual(item["destinations"], want, "C1 destination " + where)
                self.assertEqual(group, {"action": "actions", "thought": "thoughts"}.get(want[0].split(":")[0], "unjudged"),
                                 "T6 group " + where)
                self.assertEqual(datetime.fromisoformat(item["timestamp"]), T0, "T6 time " + where)
                self.assertEqual((item["app"], item["task"]), (FOCUS, TASK), "T6 context " + where)
                if send:
                    request = net.requests[0]
                    self.assertEqual((request.get_method(), request.host), ("POST", "api.typesafe.ai"), "C3 target " + where)
                    self.assertEqual(json.loads(request.data)["questions"], questions, "C3 questions " + where)
                    self.assertEqual(net.states()[0]["utterance"]["text"], text, "C3 text " + where)
            self.assertEqual(n, len(ORIGINS) * len(TEXTS) * (len(self.REPLIES) + len(self.ERRORS) + 2))

    def test_only_history_null_or_absent_is_listed(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            for n, (origin, delivery, key) in enumerate(itertools.product(ORIGINS, DELIVERIES, (None, "k"))):
                world.write([row(f"IDS-{n}", T0, TEXTS[0], origin, delivery)])
                result, net = listed(world, reply=answers(), key=key)
                where = f"origin={origin} delivery={delivery} key={bool(key)}"
                want = 1 if delivery in LISTED else 0
                self.assertEqual(len(rows_of(result)), want, "T6 listed " + where)
                self.assertEqual(len(net.requests), want if key else 0, "T1 requests " + where)

    def test_each_local_marker_alone(self):
        texts = {"action:self_task": "やること: 部屋の掃除 QZX", "action:look_up": "新しいマイクを調べて QZX",
                 "action:write_text": "下書きを書いて お礼のメール QZX", "action:computer_change": "クロードに依頼 テストの追加 QZX",
                 "unjudged:no-jev": "えーと QZX"}
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            for want, text in texts.items():
                world.write([row("IDS-m", T0, text)])
                result, net = listed(world, "--no-jev", reply=answers(is_filler=0.99))
                self.assertEqual([item["destinations"] for _, item in rows_of(result)], [[want]], "C2 marker " + text)
                self.assertEqual(net.requests, [], "T1 requests " + text)

    def test_broken_rows_are_skipped_and_counted(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            good = [row(f"IDS-{n}", T0, TEXTS[0]) for n in range(2)]
            other_day = row("IDS-9", T0 - timedelta(days=1), TEXTS[0])
            broken = ["not json", "[1]", json.dumps({"timestamp": "x", "text": "y"}), '{"id":"frag","text":"cut']
            world.journal().write_bytes(("\n".join([good[0], broken[0], other_day, broken[1], broken[2], good[1]])
                                         + "\n" + broken[3]).encode())
            result, _ = listed(world, "--no-jev")
            self.assertEqual((len(rows_of(result)), result["skipped"]), (2, len(broken)), "T6 skipped")

    def test_windows_timestamp_with_seven_digits(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            world.write([row("IDS-w", T0, TEXTS[0]).replace("14:03:05.123", "14:03:05.1234567")])
            result, _ = listed(world, "--no-jev")
            self.assertEqual(datetime.fromisoformat(rows_of(result)[0][1]["timestamp"]).time().second, 5, "T6 time")

    def test_devices_are_merged_in_time_order_and_dot_folders_skipped(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            world.write([row("IDS-w", T0 + timedelta(hours=1), TEXTS[0])], device="wsl")
            world.write([row("IDS-p", T0, TEXTS[0])])
            (world.root / ".triage").mkdir()
            (world.root / ".triage" / f"{DAY}.jsonl").write_text(row("IDS-dot", T0, TEXTS[0]) + "\n", encoding="utf-8")
            result, _ = listed(world, "--no-jev")
            self.assertEqual([(item["id"], item["device"]) for _, item in rows_of(result)],
                             [("IDS-p", "private"), ("IDS-w", "wsl")], "T6 devices")

    def test_default_root_is_the_parent_of_the_configured_journal_dir(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            world.write([row("IDS-p", T0, TEXTS[0])])
            world.write([row("IDS-w", T0, TEXTS[0])], device="wsl")
            code, stdout, _ = run(world, ["list", DAY, "--no-jev"])
            self.assertEqual(len(rows_of(json.loads(stdout))), 2, "T6 default root")

    def test_text_format(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            world.write([row("IDS-a", T0, "あ QZXa"), row("IDS-b", T0, "い QZXb"), row("IDS-c", T0, "う QZXc")])
            replies = {"あ QZXa": answers(wants_action=0.9, kind_look_up=0.9), "い QZXb": answers(),
                       "う QZXc": answers(is_filler=0.9)}
            code, stdout, _ = run(world, ["list", DAY, "--root", str(world.root), "--format", "text"],
                                  lambda state: replies[state["utterance"]["text"]])
            self.assertEqual(code, 0)
            for part in ("### 依頼", "### 考え", "### 未判定", "あ QZXa", "い QZXb", "フィラー 1 件"):
                self.assertIn(part, stdout, "T6 text format")
            self.assertNotIn("う QZXc", stdout, "C2 filler text shown")


class Request(unittest.TestCase):
    def test_state_holds_only_the_table(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            for n, (origin, app, task, no_context, prior) in enumerate(itertools.product(
                    ORIGINS, (FOCUS, None), (TASK, None), (False, True), (False, True))):
                lines = [row(f"IDS-p{n}", T0 - timedelta(seconds=30), "前の発話 QZXp")] if prior else []
                world.write(lines + [row(f"IDS-{n}", T0, TEXTS[0], origin, app=app, task=task)])
                _, net = listed(world, *(["--no-context"] if no_context else []), reply=answers())
                where = f"origin={origin} app={app} task={task} no_context={no_context} prior={prior}"
                states = [s for s in net.states() if s["utterance"]["text"] == TEXTS[0]]
                self.assertEqual(len(states), 1, "C4 requests " + where)
                state = states[0]
                started = state["utterance"].get("started")
                self.assertEqual(set(state["utterance"]), {"text", "started"}, "C3 utterance " + where)
                if origin in STARTS:
                    self.assertEqual(started, origin, "C3 started " + where)
                else:
                    self.assertNotIn(started, (*STARTS, origin), "C3 started " + where)
                for request in net.requests:
                    for hidden in HIDDEN:
                        self.assertNotIn(hidden, wire(request), f"C3 sent {hidden} " + where)
                if no_context:
                    self.assertEqual(set(state), {"utterance"}, "C3 context with --no-context " + where)
                    continue
                self.assertLessEqual(set(state), {"utterance", "context", "previous"}, "C3 keys " + where)
                context = {"app": app, "task": task, "time": "14:03"}
                self.assertEqual(state.get("context"), {k: v for k, v in context.items() if v}, "C3 context " + where)
                previous = state.get("previous", [])
                self.assertEqual([p["text"] for p in previous], ["前の発話 QZXp"] if prior else [], "C7 previous " + where)
                for item in previous:
                    self.assertTrue(all(type(v) in (int, float) for k, v in item.items() if k != "text"),
                                    "C3 previous " + where)

    def test_previous_is_same_device_listed_within_60_seconds_at_most_two(self):
        # Same device and listed at every edge; a pasted row and another device's row at 1 and 60 seconds.
        candidates = ([(offset, "history", "private") for offset in (0, 1, 59, 60, 61)]
                      + [(offset, *other) for offset in (1, 60) for other in (("paste", "private"), ("history", "wsl"))])
        target = "いまの発話 QZXt"
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            for size in range(5):
                for chosen in itertools.combinations(candidates, size):
                    lines = {"private": [], "wsl": []}
                    for offset, delivery, device in chosen:
                        lines[device].append(row(f"IDS-{offset}{delivery}{device}", T0 - timedelta(seconds=offset),
                                                 f"前 {offset} {delivery} {device} QZX", delivery=delivery))
                    lines["private"].append(row("IDS-t", T0, target))
                    for device, items in lines.items():
                        world.write(items, device)
                    shutil.rmtree(world.cache, ignore_errors=True)
                    _, net = listed(world, reply=answers())
                    state = next(s for s in net.states() if s["utterance"]["text"] == target)
                    near = sorted((o, f"前 {o} {d} {v} QZX") for o, d, v in chosen
                                  if v == "private" and d == "history" and 0 < o <= 60)
                    self.assertEqual(sorted(p["text"] for p in state.get("previous", [])),
                                     sorted(text for _, text in near[:2]), f"C7 previous for {chosen}")

    def test_previous_reaches_across_midnight(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            midnight = datetime.fromisoformat("2026-11-21T00:00:30+09:00")
            world.write([row("IDS-y", midnight - timedelta(seconds=40), "昨日の終わり QZX")], day="2026-11-20")
            world.write([row("IDS-t", midnight, "今日の始まり QZX")])
            _, net = listed(world, reply=answers())
            self.assertEqual([p["text"] for p in net.states()[0].get("previous", [])], ["昨日の終わり QZX"], "C7 midnight")


class Cache(unittest.TestCase):
    """Every sequence of 4 steps from: A append a row, D delete the oldest row, R run, E run with the network down,
    W change the question wording, N run --no-jev. The model says which ids must be asked and which cache lines stay."""

    def test_every_sequence(self):
        base = json.loads((SCRIPT.parents[1] / "Resources/triage_questions.json").read_text(encoding="utf-8"))
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            wordings = [Path(tmp) / f"questions-{w}.json" for w in (0, 1)]
            for w, path in enumerate(wordings):
                changed = json.loads(json.dumps(base))
                changed["is_filler"]["instructions"] += " (wording 1)" * w
                path.write_text(json.dumps(changed, ensure_ascii=False), encoding="utf-8")
            for seq in itertools.product("ADREWN", repeat=4):
                shutil.rmtree(world.cache, ignore_errors=True)
                journal, cached, wording, next_id = ["IDS-0", "IDS-1"], {}, 0, 2
                world.write([row(i, T0, f"発話 {i} QZX") for i in journal])
                for step, op in enumerate(seq):
                    where = f"seq={''.join(seq)} step={step}({op})"
                    if op == "A":
                        journal.append(f"IDS-{next_id}")
                        next_id += 1
                    elif op == "D" and journal:
                        journal.pop(0)
                    elif op == "W":
                        wording = 1 - wording
                    if op in "AD":
                        world.write([row(i, T0, f"発話 {i} QZX") for i in journal])
                        continue
                    if op == "W":
                        continue
                    before = world.journal().read_bytes()
                    reply = urllib.error.URLError("down") if op == "E" else answers()
                    with mock.patch.object(triage, "QUESTIONS_PATH", wordings[wording]):
                        _, net = listed(world, *(["--no-jev"] if op == "N" else []), reply=reply)
                    self.assertEqual(world.journal().read_bytes(), before, "C5 journal changed " + where)
                    asked = sorted(s["utterance"]["text"].split()[1] for s in net.states())
                    want = [] if op == "N" else sorted(i for i in journal if cached.get(i) != wording)
                    self.assertEqual(asked, want, "C4 asked " + where)
                    cached = {i: w for i, w in cached.items() if i in journal}
                    if op == "R":
                        cached |= {i: wording for i in journal}
                    lines = [json.loads(line) for line in world.cached()]
                    self.assertEqual(sorted(line["id"] for line in lines), sorted(cached), "C5 cache ids " + where)
                    rest = {}
                    for line in lines:
                        rest.setdefault(cached[line.pop("id")], set()).add(json.dumps(line, sort_keys=True))
                    self.assertTrue(all(len(v) == 1 for v in rest.values()) and len(set().union(*rest.values()) if rest else ()) == len(rest),
                                    "C4 cache line does not tell the wording apart " + where)


def label_world(tmp, specs):
    """Journal rows whose Jev answers are given, listed once so the cache holds them. specs: id -> answers."""
    world = World(tmp)
    world.write([row(i, T0 + timedelta(minutes=n), f"発話 {i} QZX") for n, i in enumerate(specs)])
    listed(world, reply=lambda state: specs[state["utterance"]["text"].split()[1]])
    return world


def label(world, entry_id, *destinations):
    code, stdout, net = run(world, ["label", entry_id, *destinations, "--root", str(world.root)])
    assert code == 0 and net.requests == [], f"C10 label {entry_id} {destinations} exited {code}"
    return stdout


def cli(world, *argv):
    code, stdout, net = run(world, [*argv, "--root", str(world.root)], reply=urllib.error.URLError("down"))
    assert code == 0, f"C9 {argv} exited {code}"
    assert net.requests == [], f"C9 {argv} asked Jev"
    return json.loads(stdout)


def weight(labels, thresholds):
    """The design's error weights: content to filler 10, request to thought 3, unjudged 0.5, any other miss 1."""
    total, misses = 0, 0
    for target, given in labels:
        actual = ref.decide(given, thresholds)
        if set(actual) == set(target):
            continue
        if actual == ["filler"]:
            total, misses = total + 10, misses + 1
        elif actual == ["thought"] and target[0].startswith("action:"):
            total += 3
        elif actual[0].startswith("unjudged:"):
            total += 0.5
        else:
            total += 1
    return total, misses


POOL = [answers(), answers(is_filler=0.9), answers(is_filler=0.5), answers(wants_action=0.5),
        answers(wants_action=0.9, kind_look_up=0.9), answers(wants_action=0.9, kind_self_task=0.8, kind_write_text=0.9),
        answers(wants_action=0.9), answers(wants_action=0.65, kind_computer_change=0.9), answers(is_filler=0.65),
        answers(wants_action=0.25, is_filler=0.75)]
TARGETS = [["thought"], ["filler"], ["action:look_up"], ["action:self_task", "action:write_text"], ["action:computer_change"]]


class Labels(unittest.TestCase):
    def test_label_writes_one_line_to_its_own_device_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            world.write([row("IDS-p", T0, "自分の端末 QZX")])
            world.write([row("IDS-w", T0, "別の端末 QZX")], device="wsl")
            listed(world, reply=answers())
            labels = world.root / ".triage/labels"
            labels.mkdir(parents=True)
            (labels / "wsl-cli.jsonl").write_text('{"id": "x"}\n', encoding="utf-8")
            (labels / "private-app.jsonl").write_text('{"id": "y"}\n', encoding="utf-8")
            mine = labels / "private-cli.jsonl"
            for n, (entry_id, target) in enumerate(itertools.product(
                    ("IDS-p", "IDS-w"), [*([d] for d in ("thought", "filler", "unjudged:intent")), *TARGETS])):
                before = world.snapshot()
                label(world, entry_id, *target)
                after = world.snapshot()
                changed = {p for p in before.keys() | after.keys() if before.get(p) != after.get(p)}
                self.assertEqual(changed, {mine}, f"C10 label {entry_id} {target} changed {changed}")
                added = after[mine][len(before.get(mine, b"")):].decode("utf-8")
                self.assertEqual(before.get(mine, b""), after[mine][:len(before.get(mine, b""))], "C10 label rewrote")
                self.assertEqual(added.count("\n"), 1, "C10 label lines")
                self.assertEqual(json.loads(added)["id"], entry_id, "C10 label id")

    def test_bad_destination_or_id_writes_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            world = World(tmp)
            world.write([row("IDS-p", T0, "発話 QZX")])
            label(world, "IDS-p", "thought")  # the same world takes a good label
            for argv in (["IDS-p", "none"], ["IDS-p", "action:banana"], ["IDS-p", "thought", "filler"],
                         ["IDS-p", "action:look_up", "thought"], ["IDS-missing", "thought"]):
                before = world.snapshot()
                code, _, net = run(world, ["label", *argv, "--root", str(world.root)])
                self.assertNotEqual(code, 0, f"C10 accepted {argv}")
                self.assertEqual(world.snapshot(), before, f"C10 wrote for {argv}")

    def test_review_puts_unjudged_then_near_threshold_first_and_writes_no_label(self):
        specs = {"IDS-far": answers(wants_action=1, kind_look_up=1, is_filler=0),
                 "IDS-near": answers(wants_action=0.72, kind_look_up=1, is_filler=0),
                 "IDS-unjudged": answers(wants_action=0.5)}
        with tempfile.TemporaryDirectory() as tmp:
            world = label_world(tmp, specs)
            before = world.snapshot()
            code, stdout, net = run(world, ["review", DAY, "--root", str(world.root)], reply=urllib.error.URLError("down"))
            self.assertEqual((code, net.requests), (0, []), "C4 review asked again")
            self.assertEqual([c["id"] for c in json.loads(stdout)["candidates"]], ["IDS-unjudged", "IDS-near", "IDS-far"],
                             "C10 review order")
            self.assertEqual(world.snapshot(), before, "C10 review wrote")


class Scoring(unittest.TestCase):
    def test_eval_scores_the_kept_answers_without_network(self):
        specs = {f"IDS-{n:03d}": POOL[n % len(POOL)] for n in range(40)}
        targets = {i: TARGETS[(n * 3) % len(TARGETS)] for n, i in enumerate(specs)}
        custom = {**DEFAULT, "wants_action": {"no": 0.2, "yes": 0.6}, "is_filler": {"no": 0.4, "yes": 0.6}}
        with tempfile.TemporaryDirectory() as tmp:
            world = label_world(tmp, specs)
            for i, target in targets.items():
                label(world, i, *target)
            shutil.rmtree(world.cache)  # labels keep their answers: no cache needed
            labelled = [(targets[i], specs[i]) for i in specs]
            for name, thresholds in (("default", None), ("custom", custom)):
                if thresholds:
                    (world.root / ".triage/thresholds.json").write_text(json.dumps(thresholds), encoding="utf-8")
                result = cli(world, "eval")
                self.assertEqual((result["weight"], result["filler_misses"]), weight(labelled, thresholds or DEFAULT),
                                 f"C9 eval {name}")
            self.assertEqual(len(list((world.root / ".triage/reports").glob("*.md"))), 2, "C9 eval report")

    def partition(self, tmp, ids, order):
        """Which ids tune puts in the holdout, read from its counts as labels are added one at a time."""
        world = label_world(tmp, {i: answers() for i in ids})
        holdout, last = set(), 0
        for i in order:
            label(world, i, "thought")
            count = cli(world, "tune")["holdout_count"]
            if count > last:
                holdout.add(i)
            last = count
        return holdout

    def test_split_depends_on_the_id_only(self):
        ids = [f"IDS-{n:03d}" for n in range(60)]
        with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
            first, second = self.partition(a, ids, ids), self.partition(b, ids, ids[::-1])
        self.assertEqual(first, second, "C8 split depends on the order")
        self.assertTrue(8 <= len(first) <= 28, f"C8 split is not 7 to 3: {len(first)} of 60 held out")

    def tune_case(self, specs, targets, *, want_proposal, then=None):
        with tempfile.TemporaryDirectory() as tmp:
            world = label_world(tmp, specs)
            for i, target in targets.items():
                label(world, i, *target)
            thresholds = world.root / ".triage/thresholds.json"
            result = cli(world, "tune")
            self.assertFalse(thresholds.exists(), "C8 tune without --apply wrote")
            proposal = result["proposal"]
            self.assertEqual(proposal is not None, want_proposal, f"C8 proposal {proposal}")
            again = cli(world, "tune", "--apply")
            self.assertEqual(again["proposal"], proposal, "C8 tune is not repeatable")
            self.assertEqual(thresholds.exists(), want_proposal, "C8 --apply")
            if proposal:
                self.assertEqual(json.loads(thresholds.read_text(encoding="utf-8")), proposal, "C8 applied")
            if then:
                then(world, proposal)

    def test_tune_gates(self):
        with tempfile.TemporaryDirectory() as tmp:
            ids = [f"IDS-{n:03d}" for n in range(90)]
            holdout = self.partition(tmp, ids, ids)
        training = [i for i in ids if i not in holdout]
        unsure_request = answers(wants_action=0.65, kind_look_up=0.9)
        unsure_filler = answers(is_filler=0.6)
        # Training and holdout both gain from a lower wants_action edge: a proposal, and it takes effect.
        specs = {i: unsure_request for i in ids[:40]}

        def takes_effect(world, proposal):
            kept = [(["action:look_up"], specs[i]) for i in specs if i in holdout]
            self.assertLess(weight(kept, proposal), weight(kept, DEFAULT), "C8 holdout not lighter")
            result, _ = listed(world, reply=urllib.error.URLError("down"))
            self.assertEqual({tuple(item["destinations"]) for _, item in rows_of(result)}, {("action:look_up",)},
                             "C8 applied thresholds not used")

        self.tune_case(specs, {i: ["action:look_up"] for i in specs}, want_proposal=True, then=takes_effect)
        # Fewer than 30 labels: no proposal.
        specs = {i: unsure_request for i in ids[:29]}
        self.tune_case(specs, {i: ["action:look_up"] for i in specs}, want_proposal=False)
        # The holdout disagrees with the training labels: no proposal.
        specs = {i: unsure_request for i in ids[:40]}
        self.tune_case(specs, {i: ["action:look_up"] if i in training else ["thought"] for i in specs},
                       want_proposal=False)
        # The holdout gets lighter but one more content utterance becomes filler: no proposal. The plain thoughts keep
        # the wants_action edge above 0.05, so the training labels can only gain by lowering the is_filler edge.
        held = [i for i in ids if i in holdout]
        self.assertGreaterEqual(len(held), 21, "C8 too few holdout ids for this case")
        specs = ({i: unsure_request for i in training[:20] + held[:20]} | {i: unsure_filler for i in training[20:30] + held[20:21]}
                 | {i: answers() for i in training[30:35]})
        targets = {i: ["action:look_up"] if specs[i] is unsure_request else ["thought"] if specs[i] == answers() or i in holdout
                   else ["filler"] for i in specs}
        self.tune_case(specs, targets, want_proposal=False)

    def test_tune_ties_keep_the_closest_threshold(self):
        # Every wants_action yes edge in (0, 0.65] fixes these labels equally. The proposal must move the edge only as
        # far as the labels need (0.7 -> 0.65) and leave every other edge alone, not jump to the smallest tied value.
        specs = {f"IDS-{n:03d}": answers(wants_action=0.65, kind_look_up=0.9) for n in range(40)}

        def closest(world, proposal):
            self.assertEqual(proposal["wants_action"], {"no": DEFAULT["wants_action"]["no"], "yes": 0.65},
                             "C8 tie took a far threshold")
            self.assertEqual({k: v for k, v in proposal.items() if k != "wants_action"},
                             {k: v for k, v in DEFAULT.items() if k != "wants_action"}, "C8 untouched edges moved")

        self.tune_case(specs, {i: ["action:look_up"] for i in specs}, want_proposal=True, then=closest)


if __name__ == "__main__":
    unittest.main()
