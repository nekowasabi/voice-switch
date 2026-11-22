"""scripts/changelogmemo-add: one entry on top of changelogmemo in the changelog skill's format, never losing one."""
import datetime
import fcntl
import os
import subprocess
import sys
import tempfile
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

# CHANGELOGMEMO_ADD points the test at another copy of the script (the version before a fix, or a mutant).
SCRIPT = Path(os.environ.get("CHANGELOGMEMO_ADD") or Path(__file__).resolve().parents[1] / "scripts" / "changelogmemo-add")
TODAY = datetime.date.today().isoformat()
AUTHOR = "  takets <nolifeking00@gmail.com>"


def add(kind, text, *, memo=None, home=None, limit=None, timeout=60):
    env = {k: v for k, v in os.environ.items() if k not in ("CHANGELOG_FILE", "CHANGELOGMEMO_TIMEOUT_SECONDS")}
    if limit is not None:
        env["CHANGELOGMEMO_TIMEOUT_SECONDS"] = str(limit)
    if memo is not None:
        env["CHANGELOG_FILE"] = str(memo)
    if home is not None:
        env["HOME"] = str(home)
    return subprocess.run([sys.executable, str(SCRIPT), kind], input=text.encode(), env=env, capture_output=True,
                          timeout=timeout)


# The changelog skill writes changelogmemo with Claude's Edit tool: it reads the file and replaces it whole, without
# the script's lock. This stand-in does that, one "* skill <n>" entry per replacement, until told to stop. argv[3] is
# the seconds between its read and its replacement, argv[4] the seconds it rests after each replacement.
OPPONENT = """
import os, sys, tempfile, time
from pathlib import Path
memo, stop, n = Path(sys.argv[1]), Path(sys.argv[2]), 0
gap, rest_seconds = float(sys.argv[3]), float(sys.argv[4])
while not stop.exists():
    head, _, rest = memo.read_bytes().partition(b"\\n\\n")
    fd, tmp = tempfile.mkstemp(dir=memo.parent, prefix="edit-")
    os.write(fd, head + b"\\n\\n* skill " + str(n).encode() + b"\\n\\n" + rest)
    os.close(fd)
    time.sleep(gap)
    os.replace(tmp, memo)
    n += 1
    time.sleep(rest_seconds)
print(n)
"""
RACE_ADDS = 60  # the edit-like opponent; the old script lost entries in 5 of 5 runs at this count
GAVE_UP = os.EX_TEMPFAIL  # 75: the add ran out of CHANGELOGMEMO_TIMEOUT_SECONDS and left the file without its entry


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

    def race(self, gap, rest_seconds, adds=RACE_ADDS, limit=None):
        """adds adds while the opponent replaces the file; returns (exit codes, opponent's count, final text)."""
        self.memo.write_text(f"{TODAY}{AUTHOR}\n\n")
        stop = Path(self.dir.name) / "stop"
        opponent = subprocess.Popen([sys.executable, "-c", OPPONENT, str(self.memo), str(stop), str(gap), str(rest_seconds)],
                                    stdout=subprocess.PIPE, text=True)
        try:
            limits = limit if isinstance(limit, list) else [limit] * adds
            codes = [add("task", f"競合 {i:02d}\n", memo=self.memo, limit=limits[i], timeout=30).returncode for i in range(adds)]
        finally:
            stop.touch()
            try:
                written = int(opponent.communicate(timeout=30)[0])
            except subprocess.TimeoutExpired:
                opponent.kill()
                raise
        self.assertGreater(written, 0, "the opponent never replaced the file")
        return codes, written, self.memo.read_text()

    def assert_each_add_kept_once_or_gave_up(self, codes, written, text):
        """Every opponent entry stays once; each add left its entry exactly once and exited 0, or none and exited 75."""
        lost = [n for n in range(written) if text.count(f"* skill {n}\n") != 1]
        silent = [(i, code, text.count(f"  - 内容: 競合 {i:02d}\n")) for i, code in enumerate(codes)
                  if (code, text.count(f"  - 内容: 競合 {i:02d}\n")) not in ((0, 1), (GAVE_UP, 0))]
        self.assertEqual((lost, silent), ([], []),
                         f"opponent entries lost, adds as (index, exit, entries left), of {written} replacements")
        self.assertEqual(text.count(f"{TODAY}{AUTHOR}"), 1)

    def test_a_writer_that_replaces_without_rest_never_hangs_or_drops_silently(self):
        # scratchpad race.py's condition: the opponent replaces again the moment it has replaced. Its replacements are
        # based on reads from before the script's last write, so no writer without its lock can always keep its entry.
        # Each add must end within its limit, keep the opponent's entries, and either keep its own entry once (exit 0)
        # or leave none and exit 75, so the app shows the request as failed instead of losing it.
        started = time.monotonic()
        codes, written, text = self.race(0, 0, adds=20, limit=1)
        self.assertLess(time.monotonic() - started, 30, "20 adds with a 1 s limit took 30 s or more")
        self.assert_each_add_kept_once_or_gave_up(codes, written, text)

    def test_a_limit_that_runs_out_mid_race_leaves_no_entry_behind(self):
        # The limit can run out after the script's exchange has published its entry. Then the entry must go again.
        # On this Mac every add gave up at 0.03 s and none did at 0.05 s, so the limits spread over that edge; a run
        # where all adds end alike never reached the exchange-then-limit path this test is for.
        limits = [0.02 + 0.06 * i / 47 for i in range(48)]
        codes, written, text = self.race(0.002, 0.01, adds=48, limit=limits)
        self.assertEqual({0, GAVE_UP} <= set(codes), True, f"exit codes {codes}: the limits missed the edge")
        self.assert_each_add_kept_once_or_gave_up(codes, written, text)

    def test_a_held_lock_past_the_limit_gives_up_with_75_and_writes_nothing(self):
        self.memo.write_text(f"{TODAY}{AUTHOR}\n\n* 前の項目\n\n")
        before = self.memo.read_bytes()
        with open(str(self.memo) + ".lock", "a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            started = time.monotonic()
            r = add("task", "待たされる\n", memo=self.memo, limit=0.3, timeout=10)
            took = time.monotonic() - started
        self.assertEqual((r.returncode, self.memo.read_bytes()), (GAVE_UP, before), r.stderr)
        self.assertLess(took, 3)
        # The lock is the script's only shared state; once released, the next add goes through.
        self.assertEqual(add("task", "次の追記\n", memo=self.memo, limit=1).returncode, 0)
        self.assertEqual(self.memo.read_text().count("  - 内容: 次の追記\n"), 1)

    def test_a_writer_that_edits_now_and_then_loses_nothing(self):
        # Closer to the Edit tool: read, 2 ms later replace, then rest 10 ms. Every add succeeds and both writers'
        # entries stay exactly once.
        codes, written, text = self.race(0.002, 0.01)
        self.assertEqual(codes, [0] * RACE_ADDS)
        lost = [n for n in range(written) if text.count(f"* skill {n}\n") != 1]
        mine = [i for i in range(RACE_ADDS) if text.count(f"  - 内容: 競合 {i:02d}\n") != 1]
        self.assertEqual((lost, mine), ([], []), f"entries not kept exactly once, of {written} replacements")
        self.assertEqual(text.count(f"{TODAY}{AUTHOR}"), 1)


if __name__ == "__main__":
    unittest.main()
