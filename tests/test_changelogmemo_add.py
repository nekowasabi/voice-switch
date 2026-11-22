"""scripts/changelogmemo-add: one entry on top of changelogmemo in the changelog skill's format, never losing one."""
import datetime
import os
import subprocess
import sys
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "changelogmemo-add"
TODAY = datetime.date.today().isoformat()
AUTHOR = "  takets <nolifeking00@gmail.com>"


def add(kind, text, *, memo=None, home=None):
    env = {k: v for k, v in os.environ.items() if k != "CHANGELOG_FILE"}
    if memo is not None:
        env["CHANGELOG_FILE"] = str(memo)
    if home is not None:
        env["HOME"] = str(home)
    return subprocess.run([sys.executable, str(SCRIPT), kind], input=text.encode(), env=env, capture_output=True)


class ChangelogmemoAdd(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.memo = Path(self.dir.name) / "changelogmemo"

    def tearDown(self):
        self.dir.cleanup()

    def test_new_file_gets_the_header_and_one_entry(self):
        self.assertEqual(add("task", "牛乳を買う\n", memo=self.memo).returncode, 0)
        self.assertEqual(self.memo.read_text(), f"{TODAY}{AUTHOR}\n\n"
                         f"* 自分の作業 {TODAY} [note]: [voice-switch]:\n  - 内容: 牛乳を買う\n\n")

    def test_entry_goes_on_top_and_keeps_the_rest(self):
        old = ("2026-01-02  someone <a@example.com>\n\n"
               "* 前の項目 2026-01-02 [worklog]: [git]:\n  - 内容: 前\n\n"
               "* もっと前 2026-01-01 [note]: [vim]:\n  - 内容: 前の前\n\n")
        self.memo.write_text(old)
        os.chmod(self.memo, 0o640)
        self.assertEqual(add("draft", "お礼のメール\n二行目\n", memo=self.memo).returncode, 0)
        self.assertEqual(self.memo.read_text(), f"{TODAY}  someone <a@example.com>\n\n"
                         f"* 下書き {TODAY} [note]: [voice-switch]:\n  - 内容: お礼のメール\n    二行目\n\n"
                         + old.split("\n", 2)[2])
        self.assertEqual(self.memo.stat().st_mode & 0o777, 0o640)

    def test_without_changelog_file_it_writes_the_default_path_under_home(self):
        home = Path(self.dir.name) / "home"
        self.assertEqual(add("task", "既定の場所\n", home=home).returncode, 0)
        self.assertIn("  - 内容: 既定の場所\n", (home / "repos/changelog/changelogmemo").read_text())

    def test_bad_input_changes_nothing(self):
        self.memo.write_text("keep\n")
        for kind, text in (("task", " \n"), ("memo", "x"), ("", "x")):
            self.assertNotEqual(add(kind, text, memo=self.memo).returncode, 0, (kind, text))
        self.assertEqual(self.memo.read_text(), "keep\n")

    def test_parallel_writers_lose_no_entry(self):
        with ThreadPoolExecutor(max_workers=16) as pool:
            codes = list(pool.map(lambda i: add("task", f"並行 {i:02d}\n", memo=self.memo).returncode, range(24)))
        self.assertEqual(codes, [0] * 24)
        text = self.memo.read_text()
        for i in range(24):
            self.assertEqual(text.count(f"  - 内容: 並行 {i:02d}\n"), 1, i)
        self.assertEqual(text.count(f"{TODAY}{AUTHOR}"), 1)


if __name__ == "__main__":
    unittest.main()
