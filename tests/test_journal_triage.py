"""Exhaustive behavior test for scripts/journal_triage.py.

Every combination of origin x delivery x local marker matches x --jev x API key x --min-confidence x Jev answer
runs through main() with HTTP replaced. The expected result comes from the rules in the task, not from the script:
listed when delivery is history, null or absent; one local match wins without Jev; Jev only with --jev, a key, an
origin of 手動 or ウェイク, and zero or several local matches; only the text is sent; below the floor, none, or an
error becomes none; the log never carries the text.
Failure messages start with the rule id the verify-journal-append skill uses:
T1 no request without --jev and a key, and no real connection ever; T2 no request for an origin other than 手動 / ウェイク;
T3 a request carries only the text; T4 the category (one local hit, the floor, none, errors);
T5 the log carries no text, focusApp or taskchute; T6 which rows are listed, with time and context, journal untouched.

JOURNAL_TRIAGE points the test at another copy of the script (run.sh mutants uses it).
"""
import contextlib
import importlib.util
import io
import itertools
import json
import os
import re
import socket
import sys
import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest import mock

SCRIPT = Path(os.environ.get("JOURNAL_TRIAGE") or Path(__file__).parents[1] / "scripts/journal_triage.py")
spec = importlib.util.spec_from_file_location("journal_triage", SCRIPT)
triage = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = triage  # dataclass resolves its own module while the script loads
spec.loader.exec_module(triage)

DAY = "2026-11-21"
STAMP = "2026-11-21T14:03:05.123+09:00"
FOCUS = "FocusSentinelApp"
TASK = "TaskSentinelName"
# Texts by how many local markers they hit; the 1-hit text is a task. Each carries a token the log must never show.
TEXTS = {0: "今日はいい天気ですね QZX0", 1: "タスク: 牛乳を買う QZX1", 2: "タスク: 新しいマイクを調べて QZX2"}
ORIGINS = ["手動", "ウェイク", "自動", None, "不明"]
SENDABLE = {"手動", "ウェイク"}
DELIVERIES = ["<absent>", None, "paste", "sticky", "pane", "action", "history"]
LISTED = {"<absent>", None, "history"}
FLOORS = [None, 0.5]
# (name, answer body or exception, category Jev means, confidence or None)
ANSWERS = [
    ("high", {"choice": "research", "confidence": 0.95}, "research", 0.95),
    ("at-floor", {"choice": "research", "confidence": 0.9}, "research", 0.9),
    ("below-floor", {"choice": "research", "confidence": 0.85}, "research", 0.85),
    ("low", {"choice": "draft", "confidence": 0.6}, "draft", 0.6),
    ("very-low", {"choice": "agent", "confidence": 0.4}, "agent", 0.4),
    ("none", {"choice": "none", "confidence": 0.99}, "none", 0.99),
    ("unknown-choice", {"choice": "banana", "confidence": 0.99}, None, None),
    ("bad-confidence", {"choice": "task", "confidence": "high"}, None, None),
    ("http-error", urllib.error.URLError("down"), None, None),
    ("not-json", b"<html>busy</html>", None, None),
]


def row(text, origin, delivery, stamp=STAMP):
    out = {"id": "x", "timestamp": stamp, "text": text, "endReason": "Silence", "recordingSeconds": 1,
           "schemaVersion": 1, "focusApp": {"name": FOCUS, "bundleId": "b", "exe": None, "pid": 1},
           "taskchute": {"name": TASK, "id": "t1", "startedAt": None, "source": "file", "readAt": stamp}}
    if origin is not None:
        out["origin"] = origin
    if delivery != "<absent>":
        out["delivery"] = delivery
    return json.dumps(out, ensure_ascii=False)


class Net:
    """Fake urlopen plus a socket guard: every request is kept, any real connection attempt is counted."""

    def __init__(self, answer):
        self.answer, self.requests, self.connects = answer, [], 0

    def urlopen(self, request, timeout=None):
        self.requests.append(request)
        if isinstance(self.answer, Exception):
            raise self.answer
        body = self.answer if isinstance(self.answer, bytes) else json.dumps({"answers": {"classification": self.answer}}).encode()
        return io.BytesIO(body)

    def connect(self, *args, **kwargs):
        self.connects += 1
        raise OSError("network blocked by test")


def run(argv, answer=None, key=None):
    net = Net(answer)
    env = {k: v for k, v in os.environ.items() if k not in ("TYPESAFE_API_KEY", "JEV_API_KEY")}
    if key:
        env["TYPESAFE_API_KEY"] = key
    out, err = io.StringIO(), io.StringIO()
    with mock.patch.dict(os.environ, env, clear=True), \
            mock.patch.object(triage.urllib.request, "urlopen", net.urlopen), \
            mock.patch.object(socket.socket, "connect", net.connect), \
            contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            triage.main(argv)
        except SystemExit as stop:
            if stop.code not in (0, None):
                raise AssertionError(f"exit {stop.code}: {err.getvalue()}")
    return out.getvalue(), err.getvalue(), net


def items(stdout):
    return [line for line in stdout.splitlines() if line.startswith("- ")]


def category(line):
    found = re.findall(r"`(task|research|draft|agent|none)`", line)
    assert len(found) == 1, line
    return found[0]


def skipped(stdout):
    found = [int(n) for line in stdout.splitlines() if re.search(r"(?i)skip|malformed|broken", line)
             for n in re.findall(r"\b\d+\b", line)]
    assert len(found) == 1, stdout
    return found[0]


class ExhaustiveTriage(unittest.TestCase):
    def test_every_combination(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "j.jsonl"
            cases = 0
            for origin, delivery, hits, jev, key, floor, (name, answer, meant, conf) in itertools.product(
                    ORIGINS, DELIVERIES, TEXTS, [False, True], [None, "k-sentinel"], FLOORS, ANSWERS):
                cases += 1
                text = TEXTS[hits]
                path.write_text(row(text, origin, delivery) + "\n", encoding="utf-8")
                before = path.read_bytes()
                argv = [DAY, str(path)] + (["--jev"] if jev else []) + ([] if floor is None else ["--min-confidence", str(floor)])
                stdout, stderr, net = run(argv, answer, key)
                where = f"origin={origin} delivery={delivery} hits={hits} jev={jev} key={bool(key)} floor={floor} answer={name}"

                self.assertEqual(path.read_bytes(), before, "T6 journal changed " + where)
                self.assertEqual(net.connects, 0, "T1 real connection " + where)
                listed = items(stdout)
                if delivery not in LISTED:
                    self.assertEqual(listed, [], "T6 listed a delivered row " + where)
                    self.assertEqual(net.requests, [], "T1 sent a delivered row " + where)
                    continue
                self.assertEqual(len(listed), 1, "T6 not listed " + where)
                line = listed[0]
                self.assertIn("14:03:05", line, "T6 time " + where)
                self.assertIn(FOCUS, line, "T6 focusApp " + where)
                self.assertIn(TASK, line, "T6 taskchute " + where)

                send = jev and key is not None and origin in SENDABLE and hits != 1
                rule = "T1" if not jev or key is None else "T2" if origin not in SENDABLE else "T4"
                self.assertEqual(len(net.requests), 1 if send else 0, f"{rule} requests " + where)
                if hits == 1:
                    want = "task"
                elif not send:
                    want = "none"
                else:
                    floor_value = 0.9 if floor is None else floor
                    want = meant if meant is not None and conf >= floor_value else "none"
                self.assertEqual(category(line), want, "T4 category " + where)

                if send:
                    request = net.requests[0]
                    self.assertEqual(request.get_method(), "POST", "T3 method " + where)
                    self.assertEqual(request.host, "api.typesafe.ai", "T3 host " + where)
                    body = json.loads(request.data)
                    self.assertEqual(body["state"], {"dictation": text}, "T3 sent more than the text " + where)
                    wire = request.data.decode("utf-8") + json.dumps(dict(request.header_items()))
                    for extra in (FOCUS, TASK, "t1", STAMP, "Silence"):
                        self.assertNotIn(extra, wire, f"T3 sent {extra} " + where)
                for secret in (text, text.split()[-1], FOCUS, TASK):
                    self.assertNotIn(secret, stderr, "T5 log carries content " + where)
            self.assertEqual(cases, len(ORIGINS) * len(DELIVERIES) * len(TEXTS) * 2 * 2 * len(FLOORS) * len(ANSWERS))


class Reading(unittest.TestCase):
    def test_each_local_category_wins_alone(self):
        # The exhaustive test's one-hit text is a task; one text per other category keeps their markers honest.
        texts = {"task": "やること: 部屋の掃除", "research": "新しいマイクを調べて", "draft": "下書きを書いて お礼のメール",
                 "agent": "クロードに依頼 テストの追加", "none": "今日はいい天気ですね"}
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "j.jsonl"
            for want, text in texts.items():
                path.write_text(row(text, "ウェイク", "history") + "\n", encoding="utf-8")
                stdout, _, net = run([DAY, str(path), "--jev"], {"choice": "none", "confidence": 0.99}, "k")
                self.assertEqual(category(items(stdout)[0]), want, "T4 local " + text)
                self.assertEqual(len(net.requests), 1 if want == "none" else 0, "T4 requests " + text)

    def test_broken_rows_are_skipped_and_counted(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "j.jsonl"
            good = row(TEXTS[0], "ウェイク", "history")
            other_day = row(TEXTS[0], "ウェイク", "history", stamp="2026-11-20T09:00:00+09:00")
            broken = ["not json", "[1]", json.dumps({"timestamp": "x", "text": "y"}), "{\"id\":\"frag\",\"text\":\"cut"]
            path.write_bytes(("\n".join([good, broken[0], other_day, broken[1], broken[2], good]) + "\n" + broken[3]).encode())
            stdout, _, net = run([DAY, str(path)])
            self.assertEqual(len(items(stdout)), 2)
            self.assertEqual(skipped(stdout), len(broken))
            self.assertEqual(net.requests, [])

    def test_directory_reads_only_that_day(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / f"{DAY}.jsonl").write_text(row(TEXTS[0], "ウェイク", None) + "\n", encoding="utf-8")
            (Path(tmp) / "2026-11-20.jsonl").write_text(row(TEXTS[0], "ウェイク", None, "2026-11-20T09:00:00+09:00") + "\n", encoding="utf-8")
            stdout, _, _ = run([DAY, tmp])
            self.assertEqual(len(items(stdout)), 1)
            self.assertEqual(skipped(stdout), 0)

    def test_windows_timestamp_with_seven_digits(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / f"{DAY}.jsonl").write_text(row(TEXTS[0], "ウェイク", "history", "2026-11-21T14:03:05.1234567+09:00") + "\n", encoding="utf-8")
            stdout, _, _ = run([DAY, tmp])
            self.assertEqual(len(items(stdout)), 1)
            self.assertIn("14:03:05", items(stdout)[0])

    def test_default_location_is_read(self):
        with tempfile.TemporaryDirectory() as home:
            journal = Path(home) / "Library/Application Support/voice-switch/journal"
            if os.name == "nt":
                journal = Path(home) / "voice-switch/journal"
            journal.mkdir(parents=True)
            (journal / f"{DAY}.jsonl").write_text(row(TEXTS[0], "ウェイク", "history") + "\n", encoding="utf-8")
            with mock.patch.dict(os.environ, {"HOME": home, "LOCALAPPDATA": home}):
                stdout, _, _ = run([DAY])
            self.assertEqual(len(items(stdout)), 1)

    def test_subfolders_are_merged_in_time_order_with_labels(self):
        with tempfile.TemporaryDirectory() as tmp:
            for name, stamp in (("wsl", "2026-11-21T11:00:00+09:00"), ("private", "2026-11-21T10:00:00+09:00")):
                (Path(tmp) / name).mkdir()
                (Path(tmp) / name / f"{DAY}.jsonl").write_text(row(TEXTS[0], "ウェイク", "history", stamp) + "\n", encoding="utf-8")
            stdout, _, _ = run([DAY, tmp])
            listed = items(stdout)
            self.assertEqual(len(listed), 2)
            self.assertIn("10:00:00** · private · ", listed[0])
            self.assertIn("11:00:00** · wsl · ", listed[1])

    def test_default_is_the_parent_of_the_configured_journal_dir(self):
        with tempfile.TemporaryDirectory() as home:
            root = Path(home) / "storage/voice-switch"
            for name in ("private", "wsl"):
                (root / name).mkdir(parents=True)
                (root / name / f"{DAY}.jsonl").write_text(row(TEXTS[0], "ウェイク", "history") + "\n", encoding="utf-8")
            config = Path(home) / ".config/voice-switch/config.json"
            config.parent.mkdir(parents=True)
            config.write_text(json.dumps({"dictation": {"journalDir": "~/storage/voice-switch/private"}}), encoding="utf-8")
            with mock.patch.dict(os.environ, {"HOME": home, "USERPROFILE": home}):
                stdout, _, _ = run([DAY])
            self.assertEqual(len(items(stdout)), 2)


if __name__ == "__main__":
    unittest.main()
