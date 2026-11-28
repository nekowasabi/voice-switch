"""parity-test uses a freshly built binary on Darwin (finding 14). Dry-run only."""

import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def dry_run(uname):
    return subprocess.run(
        ["make", "-n", "parity-test", f"UNAME_S={uname}"],
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=30,
    )


class ParityBuildOrder(unittest.TestCase):
    def test_darwin_builds_before_parity(self):
        completed = dry_run("Darwin")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        plan = completed.stdout
        self.assertIn("run_parity.py", plan)
        self.assertIn(
            "swift build -c release",
            plan,
            "Darwin parity-test must build first so run_parity.py uses current sources",
        )
        self.assertLess(
            plan.index("swift build -c release"),
            plan.index("run_parity.py"),
            "swift build must come before run_parity.py in the Darwin plan",
        )

    def test_linux_does_not_build(self):
        completed = dry_run("Linux")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        plan = completed.stdout
        self.assertIn("run_parity.py", plan)
        self.assertNotIn(
            "swift build -c release",
            plan,
            "Linux has no macOS build; parity-test must not depend on it",
        )


if __name__ == "__main__":
    unittest.main()
