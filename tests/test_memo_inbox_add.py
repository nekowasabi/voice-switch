"""scripts/memo-inbox-add: one whole JSON line per add, nothing else in the inbox changes, a failure is a non-zero exit.

MEMO_INBOX_ADD points the test at another copy of the script (code-mutants.sh uses it).
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

SCRIPT = Path(os.environ.get("MEMO_INBOX_ADD") or Path(__file__).parents[1] / "scripts/memo-inbox-add")


def add(kind, text, inbox=None, home=None):
    env = {k: v for k, v in os.environ.items() if k != "VOICE_SWITCH_MEMO_INBOX"}
    if inbox is not None:
        env["VOICE_SWITCH_MEMO_INBOX"] = str(inbox)
    if home is not None:
        env["HOME"] = str(home)
    return subprocess.run([sys.executable, str(SCRIPT), kind], input=text.encode(), env=env, capture_output=True, timeout=30)


def rows(inbox):
    return [json.loads(line) for line in inbox.read_text(encoding="utf-8").splitlines() if line.startswith("{")]


class MemoInboxAdd(unittest.TestCase):
    def test_each_add_is_one_line_with_its_kind_and_text(self):
        with tempfile.TemporaryDirectory() as tmp:
            inbox = Path(tmp) / "inbox.jsonl"
            self.assertEqual(add("task", "牛乳を買う\n帰りに", inbox).returncode, 0)
            self.assertEqual(add("draft", "お礼のメール", inbox).returncode, 0)
            self.assertEqual(add("thought", "この坂、思ったより長いな", inbox).returncode, 0)
            got = rows(inbox)
            self.assertEqual([(r["kind"], r["title"], r["text"]) for r in got],
                             [("task", "自分の作業", "牛乳を買う\n帰りに"), ("draft", "下書き", "お礼のメール"),
                              ("thought", "考え", "この坂、思ったより長いな")])
            self.assertEqual(len({r["id"] for r in got}), 3)
            self.assertEqual(inbox.stat().st_mode & 0o777, 0o600)

    def test_earlier_bytes_never_change(self):
        with tempfile.TemporaryDirectory() as tmp:
            inbox = Path(tmp) / "inbox.jsonl"
            add("task", "一件目", inbox)
            before = inbox.read_bytes()
            add("task", "二件目", inbox)
            self.assertTrue(inbox.read_bytes().startswith(before))

    def test_parallel_adds_each_land_once_and_whole(self):
        with tempfile.TemporaryDirectory() as tmp:
            inbox = Path(tmp) / "inbox.jsonl"
            with ThreadPoolExecutor(24) as pool:
                codes = list(pool.map(lambda n: add("task", f"並列 {n} " + "あ" * 300, inbox).returncode, range(48)))
            self.assertEqual(codes, [0] * 48)
            lines = inbox.read_text(encoding="utf-8").splitlines()
            self.assertEqual(sorted(json.loads(l)["text"].split()[1] for l in lines), sorted(str(n) for n in range(48)))

    def test_a_cut_last_line_is_kept_and_the_new_line_starts_fresh(self):
        with tempfile.TemporaryDirectory() as tmp:
            inbox = Path(tmp) / "inbox.jsonl"
            add("task", "前", inbox)
            with inbox.open("ab") as stream:
                stream.write('{"id": "cut", "text": "途'.encode())
            self.assertEqual(add("task", "後", inbox).returncode, 0)
            lines = inbox.read_text(encoding="utf-8").splitlines()
            self.assertEqual(lines[1], '{"id": "cut", "text": "途')
            self.assertEqual(json.loads(lines[2])["text"], "後")

    def test_an_unwritable_inbox_fails_and_writes_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            blocker = Path(tmp) / "file"
            blocker.write_text("x")
            result = add("task", "書けない", blocker / "inbox.jsonl")
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(blocker.read_text(), "x")

    def test_bad_input_fails_and_writes_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            inbox = Path(tmp) / "inbox.jsonl"
            self.assertNotEqual(add("task", "  \n", inbox).returncode, 0)
            self.assertNotEqual(add("memo", "種類が違う", inbox).returncode, 0)
            self.assertFalse(inbox.exists())

    def test_the_default_inbox_is_beside_changelogmemo(self):
        with tempfile.TemporaryDirectory() as home:
            self.assertEqual(add("task", "既定の場所", home=home).returncode, 0)
            self.assertEqual(rows(Path(home) / "repos/changelog/voice-switch-inbox.jsonl")[0]["text"], "既定の場所")


if __name__ == "__main__":
    unittest.main()
