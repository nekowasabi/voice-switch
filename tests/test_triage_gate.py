"""journal_triage.gate: the same lines as TriageClassifier.sessionTalk / worthKeeping (tests/triage/main.swift "gates")."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
import journal_triage as jt  # noqa: E402


class Gate(unittest.TestCase):
    def test_lines_match_the_app(self):
        g = jt.gate
        self.assertEqual(g("修正してください", ["thought"], {"needs_local": 0.7, "keep_note": 0.79}), "session")
        self.assertEqual(g("いったん使ってみて、メモ", ["thought"], {"needs_local": 0.79, "keep_note": 0.81}), "memo")
        self.assertEqual(g("冬休みは年末に", ["thought"], {"needs_local": 0.69, "keep_note": 0.7}), "memo")
        self.assertIsNone(g("いいですね", ["thought"], {"needs_local": 0.1, "keep_note": 0.69}))
        self.assertEqual(g("アレクサ、今何時?", ["action:look_up"], {"needs_local": 0.0, "keep_note": 0.0}), "session")
        self.assertIsNone(g("うん", ["filler"], {"needs_local": 0.9, "keep_note": 0.9}))
        self.assertIsNone(g("古いキャッシュ", ["thought"], None))
        self.assertIsNone(g("古いキャッシュ", ["thought"], {"wants_action": 0.1}))  # answers cached before the gates
        self.assertIsNone(g("会議の話", ["thought"], {"needs_local": 0.1, "keep_note": 0.79}, "自動"))
        self.assertEqual(g("会議の話", ["thought"], {"needs_local": 0.1, "keep_note": 0.8}, "自動"), "memo")
        self.assertEqual(g("口述", ["thought"], {"needs_local": 0.1, "keep_note": 0.7}, "ウェイク"), "memo")


class Outcome(unittest.TestCase):
    def test_where_rows_go(self):
        o, a = jt.outcome, lambda local=0.0, keep=0.0: {"needs_local": local, "keep_note": keep}
        self.assertEqual(o("うん", ["filler"], a(keep=0.9)), "drop")
        self.assertEqual(o("修正して", ["action:look_up"], a(local=0.9)), "drop")
        self.assertEqual(o("使ってみて、メモ", ["action:look_up"], a(local=0.9, keep=0.85)), "memo")
        self.assertEqual(o("天気を調べて", ["action:look_up"], a(local=0.1)), "route:look_up")
        self.assertEqual(o("天気を調べて", ["action:look_up"], a(local=0.3)), "review")
        self.assertEqual(o("牛乳", ["action:self_task"], a()), "memo")
        self.assertEqual(o("あれを", ["unjudged:kind"], a(keep=0.9)), "memo")
        self.assertEqual(o("あれを", ["unjudged:kind"], a()), "review")
        self.assertEqual(o("あれを", ["action:look_up", "action:delegate"], a()), "review")
        self.assertEqual(o("冬休み", ["thought"], a(keep=0.7)), "memo")
        self.assertEqual(o("いいね", ["thought"], a(keep=0.6)), "drop")


if __name__ == "__main__":
    unittest.main()
