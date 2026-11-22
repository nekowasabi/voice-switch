"""scripts/memo-import: inbox lines reach changelogmemo once, newest first under the date line; nothing else changes."""
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).parents[1]
HEAD = "2026-01-01  takets <a@example.com>\n\n* 既存の項目 2026-01-01 [note]: [x]:\n  - 内容: 前からある\n\n"


def add(inbox, kind, text):
    return subprocess.run([sys.executable, str(ROOT / "scripts/memo-inbox-add"), kind], input=text.encode(),
                          env={"VOICE_SWITCH_MEMO_INBOX": str(inbox), "PATH": "/usr/bin:/bin"}, capture_output=True)


def imp(inbox):
    return subprocess.run([sys.executable, str(ROOT / "scripts/memo-import"), str(inbox)], capture_output=True)


class MemoImport(unittest.TestCase):
    def test_an_add_lands_in_changelogmemo_once_above_older_entries(self):
        with tempfile.TemporaryDirectory() as tmp:
            inbox, memo = Path(tmp) / "inbox.jsonl", Path(tmp) / "changelogmemo"
            memo.write_text(HEAD, encoding="utf-8")
            self.assertEqual(add(inbox, "task", "牛乳を買う\n帰りに").returncode, 0)
            self.assertEqual(add(inbox, "thought", "坂が長い").returncode, 0)
            self.assertEqual(imp(inbox).returncode, 0)
            text = memo.read_text(encoding="utf-8")
            ids = [json.loads(l)["id"] for l in inbox.read_text(encoding="utf-8").splitlines()]
            self.assertEqual([text.count(f"  - id: {i}") for i in ids], [1, 1])
            self.assertIn("* 自分の作業 ", text)
            self.assertIn("  - 内容: 牛乳を買う\n    帰りに\n", text)
            self.assertTrue(text.endswith("* 既存の項目 2026-01-01 [note]: [x]:\n  - 内容: 前からある\n\n"))
            self.assertRegex(text.splitlines()[0], r"^\d{4}-\d{2}-\d{2}  takets <a@example.com>$")
            self.assertNotEqual(text.splitlines()[0][:10], "2026-01-01")

    def test_an_entry_dropped_by_another_writer_comes_back_on_the_next_import(self):
        with tempfile.TemporaryDirectory() as tmp:
            inbox, memo = Path(tmp) / "inbox.jsonl", Path(tmp) / "changelogmemo"
            memo.write_text(HEAD, encoding="utf-8")
            add(inbox, "task", "一件目")
            memo.write_text(HEAD, encoding="utf-8")  # an editor saves its old buffer over the import
            add(inbox, "task", "二件目")
            text = memo.read_text(encoding="utf-8")
            self.assertIn("一件目", text)
            self.assertIn("二件目", text)

    def test_no_changelogmemo_means_nothing_is_created(self):
        with tempfile.TemporaryDirectory() as tmp:
            inbox = Path(tmp) / "inbox.jsonl"
            self.assertEqual(add(inbox, "task", "置き場なし").returncode, 0)
            self.assertFalse((Path(tmp) / "changelogmemo").exists())


if __name__ == "__main__":
    unittest.main()
