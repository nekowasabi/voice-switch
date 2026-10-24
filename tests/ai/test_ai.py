import hashlib
import json
import os
import re
import stat
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))

from voice_switch_ai.calibration import recommend
from voice_switch_ai.classify import Config, classify
from voice_switch_ai.jev import JevAnswer, build_body, question
from voice_switch_ai.journal import JournalEntry, JournalReader, JsonlJournalReader
from voice_switch_ai.pipeline import run

FIXTURE = ROOT / "tests" / "fixtures" / "ai" / "journal"
SCRIPT = ROOT / "scripts" / "voice-switch-ai"
PROBE = ROOT / "scripts" / "tray_jev_probe.py"


def entry(text, entry_id="x", end_reason="silence", origin="automatic"):
    return JournalEntry(
        id=entry_id,
        timestamp=datetime.fromisoformat("2026-10-23T09:00:00+09:00"),
        text=text,
        origin=origin,
        end_reason=end_reason,
        recording_seconds=1.0,
        schema_version=1,
    )


class FakeJev:
    def __init__(self, choice="task", confidence=0.87, boom=False):
        self.choice = choice
        self.confidence = confidence
        self.boom = boom
        self.calls = []

    def choose(self, text):
        self.calls.append(text)
        if self.boom:
            raise RuntimeError("secret-key-should-not-leak")
        return JevAnswer(self.choice, self.confidence)


class FakeReader(JournalReader):
    def read(self, path):
        return [entry("明日までに報告書を出さなきゃ", "from-fake")]


class AiTests(unittest.TestCase):
    def test_journal_reader_keeps_bytes_and_skips_bad_lines(self):
        with tempfile.TemporaryDirectory() as tmp:
            journal = Path(tmp) / "day.jsonl"
            original = (
                '{"id":"a","timestamp":"2026-10-23T09:00:00+09:00","text":"うん",'
                '"origin":"自動","endReason":"silence","recordingSeconds":1,"schemaVersion":1}\n'
                "not-json\n"
            )
            journal.write_text(original, encoding="utf-8")
            before = journal.read_bytes()
            reader = JsonlJournalReader()
            entries = reader.read(journal)
            self.assertEqual(journal.read_bytes(), before)
            self.assertEqual(len(entries), 1)
            self.assertEqual(entries[0].origin, "automatic")
            self.assertEqual(entries[0].end_reason, "silence")
            self.assertTrue(any(line.startswith("journal: skip") for line in reader.warnings))

    def test_end_reason_does_not_change_route(self):
        config = Config()
        silence = classify(entry("明日までに報告書を出さなきゃ", "a", "silence"), config, None)
        button = classify(entry("明日までに報告書を出さなきゃ", "b", "button"), config, None)
        self.assertEqual(silence.route, button.route)
        self.assertEqual(silence.log, "triage: local=task@1.00 -> tray")

    def test_local_task_does_not_call_jev_even_with_client(self):
        fake = FakeJev()
        config = Config(cloud=True, jev_min_confidence=0.91)
        decision = classify(entry("明日までに報告書を出さなきゃ"), config, fake)
        self.assertEqual(decision.log, "triage: local=task@1.00 -> tray")
        self.assertEqual(fake.calls, [])

    def test_ambiguous_offline_ignores_key(self):
        fake = FakeJev()
        decision = classify(entry("仕様を調べなきゃいけないし、田中さんに聞く文面もいる"), Config(), fake)
        self.assertEqual(decision.log, "triage: local=ambiguous jev=off -> memo")
        self.assertEqual(fake.calls, [])

    def test_uncalibrated_cloud_does_not_call(self):
        fake = FakeJev()
        config = Config(cloud=True, jev_min_confidence=None)
        decision = classify(entry("仕様を調べなきゃいけないし、田中さんに聞く文面もいる"), config, fake)
        self.assertEqual(decision.log, "triage: jev=uncalibrated -> memo")
        self.assertEqual(fake.calls, [])

    def test_jev_high_low_none_and_error(self):
        text = "仕様を調べなきゃいけないし、田中さんに聞く文面もいる"
        high = FakeJev("task", 0.87)
        decision = classify(entry(text), Config(cloud=True, jev_min_confidence=0.87), high)
        self.assertEqual(decision.log, "triage: jev=task@0.87 -> tray")
        self.assertEqual(high.calls, [text])
        low = classify(entry(text), Config(cloud=True, jev_min_confidence=0.99), FakeJev("task", 0.87))
        self.assertEqual(low.log, "triage: jev=task@0.87 -> memo")
        none = classify(entry(text), Config(cloud=True, jev_min_confidence=0.5), FakeJev("none", 0.99))
        self.assertEqual(none.log, "triage: jev=none@0.99 -> memo")
        failed = classify(entry(text), Config(cloud=True, jev_min_confidence=0.5), FakeJev(boom=True))
        self.assertEqual(failed.log, "triage: jev=error -> memo")
        self.assertNotIn("secret", failed.log)

    def test_jev_body_classifies_only(self):
        body = build_body("明日までに報告書を出さなきゃ")
        encoded = json.dumps(body, ensure_ascii=False)
        self.assertNotIn("title", encoded)
        self.assertNotIn("tags", encoded)
        self.assertEqual(set(question()["criteria"]), {"task", "research", "draft", "agent", "none"})
        self.assertIn("Priority: agent, then task, then research, then draft, else none.", question()["instructions"])

    def test_default_floor_is_unset_and_source_has_no_pane_floor(self):
        self.assertIsNone(Config().jev_min_confidence)
        self.assertFalse(Config().cloud)
        package = ROOT / "python" / "voice_switch_ai"
        for path in package.glob("*.py"):
            text = path.read_text(encoding="utf-8").replace("pane floor 0.8 is not used", "")
            self.assertIsNone(re.search(r"(?<![\d.])0\.80?(?![\d.])", text), path.name)
            if path.name != "jev.py":
                self.assertNotIn("urlopen", text, path.name)
            self.assertNotIn("api.github.com", text, path.name)

    def test_muse_read_only_and_write_draft(self):
        read = classify(entry("ミューズ、昨日の決定を要約して"), Config(), FakeJev())
        self.assertEqual(read.log, "triage: muse=research@1.00 -> execute")
        self.assertTrue(read.read_only)
        write = classify(entry("ミューズ、この内容でGitHubのIssueを作って"), Config(), None)
        self.assertEqual(write.log, "triage: muse=agent@1.00 -> tray")
        self.assertFalse(write.read_only)
        blocked = Config(allowed_executions=())
        held = classify(entry("ミューズ、昨日の決定を要約して"), blocked, None)
        self.assertEqual(held.route, "tray")

    def test_fixture_pipeline_outputs(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "ai"
            before = hashlib.sha256(b"".join(path.read_bytes() for path in sorted(FIXTURE.glob("*.jsonl")))).hexdigest()
            result = run(FIXTURE, out, Config(), jev=FakeJev())
            after = hashlib.sha256(b"".join(path.read_bytes() for path in sorted(FIXTURE.glob("*.jsonl")))).hexdigest()
            self.assertEqual(before, after)
            self.assertEqual(stat.S_IMODE(out.stat().st_mode), 0o700)
            self.assertEqual(stat.S_IMODE((out / "tray.jsonl").stat().st_mode), 0o600)
            logs = "\n".join(result.logs)
            self.assertIn("triage: local=task@1.00 -> tray", logs)
            self.assertIn("triage: local=ambiguous jev=off -> memo", logs)
            self.assertIn("triage: muse=research@1.00 -> execute", logs)
            self.assertIn("triage: muse=agent@1.00 -> tray", logs)
            self.assertIn("triage: local=none@1.00 -> journal", logs)
            self.assertIn("recall: e16 related=e6", logs)
            self.assertIn("recall: conclusion-change e6 e16", logs)
            self.assertNotIn("secret", logs)
            notes = [json.loads(line) for line in (out / "notes.jsonl").read_text(encoding="utf-8").splitlines()]
            report = next(note for note in notes if "e2" in note["sourceIds"])
            self.assertNotIn("えーと", report["cleaned"])
            self.assertTrue(report["title"])
            self.assertTrue(report["summary"])
            self.assertIn("タスク", report["tags"])
            self.assertEqual(report["deadline"], "2026-10-24")
            invoice = next(note for note in notes if "e10" in note["sourceIds"])
            self.assertEqual(invoice["sourceIds"], ["e9", "e10", "e11"])
            self.assertEqual(invoice["cleaned"].count("やっぱり請求書は金曜までに出さないと"), 1)
            decision = next(note for note in notes if "e6" in note["sourceIds"])
            self.assertIn("決定", decision["tags"])
            self.assertEqual(decision["route"], "journal")
            tray = [json.loads(line) for line in (out / "tray.jsonl").read_text(encoding="utf-8").splitlines()]
            issue = next(row for row in tray if row["kind"] == "issue")
            self.assertEqual(issue["status"], "draft")
            self.assertTrue(issue["needsApproval"])
            self.assertTrue(issue["explicitIntent"])
            self.assertFalse(issue["externalWrite"])
            self.assertNotIn("e7", [source for row in tray for source in row["sourceIds"]])
            executions = (out / "executions.jsonl").read_text(encoding="utf-8")
            self.assertIn("read-only", executions)
            self.assertIn("ジャーナルディレクトリ", executions)
            self.assertIn("e5", (out / "agent-staging.jsonl").read_text(encoding="utf-8"))
            self.assertIn("何だっけ", (out / "research.jsonl").read_text(encoding="utf-8"))
            daily = json.loads((out / "digests" / "2026-10-23.json").read_text(encoding="utf-8"))
            self.assertTrue(daily["themes"])
            self.assertTrue(daily["決めたこと"])
            self.assertTrue(daily["引っかかっていること"])
            self.assertTrue(any(loop["theme"] == "請求書" for loop in daily["openLoops"]))
            self.assertIn("明日へ", daily["handoff"])
            weekly = json.loads((out / "digests" / "2026-W43.json").read_text(encoding="utf-8"))
            self.assertEqual(weekly["kind"], "weekly")
            self.assertTrue(weekly["結論の変化"])
            blob = "\n".join(path.read_text(encoding="utf-8") for path in out.rglob("*") if path.is_file())
            self.assertNotIn("api.github.com", blob)
            first = (out / "tray.jsonl").read_text(encoding="utf-8")
            again = run(FIXTURE, out, Config(), jev=None)
            self.assertEqual(first, (out / "tray.jsonl").read_text(encoding="utf-8"))
            self.assertEqual([line for line in result.logs if line.startswith("triage:")], [line for line in again.logs if line.startswith("triage:")])

    def test_fake_reader_and_approval_stick(self):
        with tempfile.TemporaryDirectory() as tmp:
            journal = Path(tmp) / "journal"
            journal.mkdir()
            (journal / "day.jsonl").write_text('{"id":"file-only","timestamp":"2026-10-23T09:00:00+09:00","text":"ファイル側","schemaVersion":1}\n', encoding="utf-8")
            out = Path(tmp) / "ai"
            run(journal, out, Config(), reader=FakeReader(), jev=None)
            notes = (out / "notes.jsonl").read_text(encoding="utf-8")
            self.assertIn("from-fake", notes)
            self.assertNotIn("ファイル側", notes)
            tray_id = "tray-from-fake"
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "tray", "approve", "--out", str(out), "--id", tray_id],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(proc.returncode, 0, proc.stderr)
            self.assertIn("approved locally; no write API called", proc.stdout)
            run(journal, out, Config(), reader=FakeReader(), jev=None)
            rows = [json.loads(line) for line in (out / "tray.jsonl").read_text(encoding="utf-8").splitlines()]
            self.assertEqual(rows[0]["status"], "approved")
            self.assertFalse(rows[0]["externalWrite"])

    def test_refuses_derived_inside_journal_and_write_capabilities(self):
        with tempfile.TemporaryDirectory() as tmp:
            journal = Path(tmp) / "journal"
            journal.mkdir()
            (journal / "day.jsonl").write_text('{"id":"a","timestamp":"2026-10-23T09:00:00+09:00","text":"うん","schemaVersion":1}\n', encoding="utf-8")
            with self.assertRaises(OSError):
                run(journal, journal / "ai", Config(), jev=None)
        from voice_switch_ai.cli import load_config

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "config.json"
            path.write_text(
                json.dumps({"dictation": {"ai": {"cloud": "false", "allowedExecutions": ["summarize", "paste", "github"]}}}),
                encoding="utf-8",
            )
            config, warnings = load_config(path)
            self.assertFalse(config.cloud)
            self.assertEqual(config.allowed_executions, ("summarize",))
            self.assertTrue(any("paste" in warning for warning in warnings))
            self.assertTrue(any("github" in warning for warning in warnings))

    def test_calibration_is_computed(self):
        rows = [("none", 0.92, "none"), ("task", 0.95, "task"), ("task", 0.41, "none"), ("research", 0.93, "research")]
        result = recommend(rows)
        self.assertEqual(result["recommended_min_confidence"], 0.92)
        self.assertNotEqual(result["recommended_min_confidence"], 0.8)
        blocked = [("none", 0.92, "none"), ("task", 0.95, "task"), ("task", 0.97, "none"), ("research", 0.93, "research")]
        self.assertIsNone(recommend(blocked)["recommended_min_confidence"])

    def test_probe_list_and_score(self):
        listed = subprocess.run([sys.executable, str(PROBE), "--list"], check=False, capture_output=True, text=True)
        self.assertEqual(listed.returncode, 0, listed.stderr)
        count = int(re.search(r"cases=(\d+)", listed.stdout).group(1))
        self.assertGreaterEqual(count, 30)
        self.assertLessEqual(count, 40)
        for edge in ("backchannel", "rhetorical", "decision", "mixed", "fragment"):
            self.assertIn(f"edge={edge}", listed.stdout)
        scored = subprocess.run(
            [sys.executable, str(PROBE), "--score", str(ROOT / "tests" / "fixtures" / "ai" / "jev_score_synthetic.json")],
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertEqual(scored.returncode, 0, scored.stderr)
        self.assertIn("source: synthetic", scored.stdout)
        self.assertIn("recommended_min_confidence: 0.92", scored.stdout)
        self.assertIn("pane floor 0.8 is not used", scored.stdout)

    def test_cli_try_is_offline(self):
        env = os.environ.copy()
        env["TYPESAFE_API_KEY"] = "fake-not-a-real-key"
        proc = subprocess.run([sys.executable, str(SCRIPT), "try"], cwd=ROOT, env=env, check=False, capture_output=True, text=True, timeout=30)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertIn("cloud=off", proc.stdout)
        self.assertIn("journal_unchanged=yes", proc.stdout)
        self.assertIn("triage: local=ambiguous jev=off -> memo", proc.stdout)
        self.assertIn("search: conclusion-change", proc.stdout)
        self.assertNotIn("jev=error", proc.stdout)
        doctor = subprocess.run([sys.executable, str(SCRIPT), "doctor"], cwd=ROOT, check=False, capture_output=True, text=True)
        self.assertEqual(doctor.returncode, 0, doctor.stderr)
        self.assertIn("cloud=off", doctor.stdout)
        self.assertIn("jevMinConfidence=unset", doctor.stdout)

    def test_config_selects_journal_without_a_flag(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "out"
            config_path = Path(tmp) / "config.json"
            config_path.write_text(
                json.dumps({"dictation": {"journalDir": str(FIXTURE), "ai": {"cloud": False, "derivedDir": str(out)}}}),
                encoding="utf-8",
            )
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "triage", "--config", str(config_path)],
                cwd=ROOT,
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(proc.returncode, 0, proc.stderr)
            self.assertIn("triage: local=task@1.00 -> tray", proc.stdout)
            self.assertTrue((out / "tray.jsonl").is_file())
            from voice_switch_ai.cli import expand_path

            os.environ["LOCALAPPDATA"] = r"C:\Users\me\AppData\Local"
            expanded = expand_path(r"%LOCALAPPDATA%\voice-switch\journal")
            self.assertIn("voice-switch", str(expanded))
            self.assertIn("AppData", str(expanded))


if __name__ == "__main__":
    unittest.main()
