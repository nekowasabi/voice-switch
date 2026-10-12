import copy
import hashlib
import importlib.util
import json
import struct
import subprocess
import sys
import tempfile
import unittest
import wave
from pathlib import Path

SCRIPT = Path(__file__).parents[1] / "scripts/cough_acceptance.py"
spec = importlib.util.spec_from_file_location("cough_acceptance", SCRIPT)
acceptance = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acceptance)


class CoughAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.cases = []
        for index in range(40):
            path = self.root / f"private-audio-{index}.wav"
            with wave.open(str(path), "wb") as audio:
                audio.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
                audio.writeframes(struct.pack("<h", index + 1) * 160)
            self.cases.append({
                "id": f"case-{index:02}",
                "label": "cough" if index < 10 else "throat_clear" if index < 20 else "short_speech",
                "audio": self.file_ref(path),
                "expected_transcripts": [] if index < 20 else ["private spoken words"],
            })
        self.manifest = {"schema_version": 1, "corpus_source": "human_recording", "label_source": "human",
                         "labeler": "reviewer-1", "labeled_at": "2026-10-12T00:00:00Z", "cases": self.cases}
        self.observations = {"schema_version": 1, "runs": []}
        for platform in ("windows", "macos"):
            for variant in ("baseline", "candidate"):
                evidence = self.root / f"private-log-{platform}-{variant}.txt"
                evidence.write_text("Synthetic unit-test evidence. No native run occurred.", encoding="utf-8")
                self.observations["runs"].append({
                    "id": f"{platform}-{variant}", "platform": platform, "variant": variant,
                    "source": "native_run", "revision": ("a" if variant == "baseline" else "b") * 40,
                    "recorded_at": "2026-10-12T01:00:00+00:00", "setup_id": f"{platform}-setup-1",
                    "recognizer_id": f"{platform}-recognizer-1", "fixed_settings_sha256": "c" * 64,
                    "automatic_min_speech_ms": 300 if variant == "baseline" else 450,
                    "interjection_filter_enabled": variant == "candidate", "evidence": self.file_ref(evidence),
                    "observations": [
                        {"case_id": case["id"], "automatic_starts": int(index >= 20),
                         "transcript": "private spoken words" if index >= 20 else "", "retained": index >= 20}
                        for index, case in enumerate(self.cases)],
                })

    @staticmethod
    def file_ref(path):
        return {"path": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}

    def evaluate(self):
        return acceptance.evaluate(self.manifest, self.observations, self.root, self.root)

    def run_case(self, platform="windows", variant="candidate", index=20):
        return next(run for run in self.observations["runs"]
                    if run["platform"] == platform and run["variant"] == variant)["observations"][index]

    def cli(self, manifest_text=None):
        manifest = self.root / "manifest.json"
        manifest.write_text(manifest_text if manifest_text is not None else json.dumps(self.manifest), encoding="utf-8")
        observations = self.root / "observations.json"
        observations.write_text(json.dumps(self.observations), encoding="utf-8")
        return subprocess.run([sys.executable, str(SCRIPT), str(manifest), str(observations)],
                              capture_output=True, text=True, check=False)

    def test_complete_declared_evidence_passes_both_platform_gates(self):
        result = self.evaluate()
        self.assertEqual(result["verdict"], "pass")
        self.assertEqual(result["case_counts"], {"cough": 10, "throat_clear": 10, "short_speech": 20})
        self.assertEqual(result["evidence_blockers"], [])
        for row in result["results"]:
            self.assertEqual(row["gates"], {"zero_cough_automatic_starts": True,
                "short_speech_start_misses_nonworse": True, "short_speech_transcript_misses_nonworse": True,
                "short_speech_retention_misses_nonworse": True})
            self.assertEqual(row["metrics"]["candidate"]["short_speech_transcript_hits"], 20)

    def test_clean_history_cannot_hide_automatic_cough_starts(self):
        case = self.run_case(index=0)
        case.update(automatic_starts=2, transcript="あっ", retained=False)
        result = self.evaluate()
        self.assertEqual(result["verdict"], "not_accepted")
        self.assertEqual(result["results"][0]["metrics"]["candidate"]["cough_automatic_starts"], 2)
        self.assertEqual(result["results"][0]["metrics"]["candidate"]["cough_retained_cases"], 0)
        self.assertEqual(result["results"][0]["metrics"]["candidate"]["cough_started_case_ids"], ["case-00"])
        self.assertFalse(result["results"][0]["gates"]["zero_cough_automatic_starts"])
        self.assertTrue(result["results"][0]["gates"]["short_speech_retention_misses_nonworse"])

    def test_short_speech_start_regression_fails_with_zero_cough_starts(self):
        self.run_case().update(automatic_starts=0, transcript="", retained=False)
        result = self.evaluate()
        self.assertEqual(result["verdict"], "not_accepted")
        self.assertTrue(result["results"][0]["gates"]["zero_cough_automatic_starts"])
        self.assertFalse(result["results"][0]["gates"]["short_speech_start_misses_nonworse"])
        self.assertEqual(result["results"][0]["metrics"]["candidate"]["short_speech_transcript_misses"], 1)

    def test_wrong_transcript_fails_even_when_session_started(self):
        self.run_case()["transcript"] = "wrong words"
        result = self.evaluate()
        self.assertEqual(result["verdict"], "not_accepted")
        self.assertTrue(result["results"][0]["gates"]["short_speech_start_misses_nonworse"])
        self.assertFalse(result["results"][0]["gates"]["short_speech_transcript_misses_nonworse"])

    def test_lost_interjection_fails_even_when_recognition_is_correct(self):
        self.cases[20]["expected_transcripts"] = ["あっ"]
        for platform in ("windows", "macos"):
            for variant in ("baseline", "candidate"):
                self.run_case(platform, variant)["transcript"] = "あっ。"
        self.run_case()["retained"] = False
        result = self.evaluate()
        self.assertEqual(result["verdict"], "not_accepted")
        self.assertTrue(result["results"][0]["gates"]["short_speech_transcript_misses_nonworse"])
        self.assertFalse(result["results"][0]["gates"]["short_speech_retention_misses_nonworse"])

    def test_equal_baseline_misses_pass_without_claiming_perfect_recall(self):
        for platform in ("windows", "macos"):
            for variant in ("baseline", "candidate"):
                self.run_case(platform, variant).update(automatic_starts=0, transcript="", retained=False)
        result = self.evaluate()
        self.assertEqual(result["verdict"], "pass")
        self.assertEqual(result["results"][0]["metrics"]["candidate"]["short_speech_transcript_hits"], 19)
        self.assertEqual(result["results"][0]["metrics"]["candidate"]["short_speech_start_misses"], 1)

    def test_one_platform_cannot_offset_the_other(self):
        self.run_case("windows", "baseline").update(automatic_starts=0, transcript="", retained=False)
        self.run_case("macos").update(automatic_starts=0, transcript="", retained=False)
        result = self.evaluate()
        self.assertEqual(result["verdict"], "not_accepted")
        self.assertTrue(result["results"][0]["gates"]["short_speech_start_misses_nonworse"])
        self.assertFalse(result["results"][1]["gates"]["short_speech_start_misses_nonworse"])

    def test_transcript_normalization_is_nfkc_casefold_space_and_punctuation(self):
        self.run_case()["transcript"] = "ＰＲＩＶＡＴＥ spoken, WORDS！"
        self.assertEqual(self.evaluate()["verdict"], "pass")

    def test_synthetic_or_unlabeled_evidence_never_passes(self):
        for field, value in (("corpus_source", "synthetic"), ("label_source", "synthetic"),
                             ("label_source", "unlabeled")):
            old = self.manifest[field]
            self.manifest[field] = value
            with self.subTest(field=field, value=value):
                self.assertEqual(self.evaluate()["verdict"], "not_accepted")
            self.manifest[field] = old
        self.observations["runs"][0]["source"] = "synthetic"
        self.assertEqual(self.evaluate()["evidence_blockers"], ["native_runs_required"])

    def test_too_few_cases_never_passes(self):
        for removed in (0, 20):
            original_manifest = copy.deepcopy(self.manifest)
            original_observations = copy.deepcopy(self.observations)
            del self.manifest["cases"][removed]
            for run in self.observations["runs"]:
                del run["observations"][removed]
            with self.subTest(removed=removed):
                self.assertEqual(self.evaluate()["verdict"], "not_accepted")
            self.manifest = original_manifest
            self.observations = original_observations

    def test_both_non_speech_labels_required(self):
        for case in self.cases[:20]:
            case["label"] = "cough"
        self.assertEqual(self.evaluate()["verdict"], "not_accepted")

    def test_duplicate_missing_unknown_case_ids_are_invalid(self):
        mutations = [lambda cases: cases.append(copy.deepcopy(cases[0])), lambda cases: cases.pop(),
                     lambda cases: cases[0].update(case_id="unknown")]
        for mutate in mutations:
            original = copy.deepcopy(self.observations)
            mutate(self.observations["runs"][0]["observations"])
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                self.evaluate()
            self.observations = original

    def test_duplicate_missing_unknown_runs_are_invalid(self):
        mutations = [lambda runs: runs.append(copy.deepcopy(runs[0])), lambda runs: runs.pop(),
                     lambda runs: runs[0].update(platform="linux"), lambda runs: runs[0].update(variant="other"),
                     lambda runs: runs[0].update(source="unknown")]
        for mutate in mutations:
            original = copy.deepcopy(self.observations)
            mutate(self.observations["runs"])
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                self.evaluate()
            self.observations = original

    def test_implicit_boolean_negative_fractional_and_unknown_fields_are_invalid(self):
        changes = [{"automatic_starts": True}, {"automatic_starts": -1}, {"automatic_starts": 0.5},
                   {"automatic_starts": "0"}, {"retained": "false"}, {"retained": 0},
                   {"transcript": None}, {"case_id": []}, {"extra": 1}]
        for change in changes:
            original = copy.deepcopy(self.observations)
            self.run_case().update(change)
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.evaluate()
            self.observations = original

    def test_missing_fields_and_wrong_containers_are_invalid(self):
        for value in (None, [], "manifest", 1, True, {}):
            with self.subTest(value=value), self.assertRaises(ValueError):
                acceptance.evaluate(value, self.observations, self.root, self.root)
        del self.cases[0]["label"]
        with self.assertRaises(ValueError):
            self.evaluate()

    def test_impossible_retention_and_transcript_are_invalid(self):
        for change in ({"automatic_starts": 0}, {"transcript": "", "retained": True}):
            original = copy.deepcopy(self.observations)
            self.run_case().update(change)
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.evaluate()
            self.observations = original

    def test_unpaired_setup_recognizer_settings_or_revision_is_invalid(self):
        for name, value in (("setup_id", "another-setup"), ("recognizer_id", "another-model"),
                            ("fixed_settings_sha256", "d" * 64), ("revision", "f" * 40)):
            original = copy.deepcopy(self.observations)
            self.observations["runs"][1][name] = value
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.evaluate()
            self.observations = original

    def test_mismatched_candidate_controls_and_enabled_baseline_filter_are_invalid(self):
        for name, value in (("automatic_min_speech_ms", 600), ("interjection_filter_enabled", False)):
            original = copy.deepcopy(self.observations)
            self.observations["runs"][1][name] = value
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.evaluate()
            self.observations = original
        for run in self.observations["runs"]:
            if run["variant"] == "baseline":
                run["interjection_filter_enabled"] = True
        with self.assertRaisesRegex(ValueError, "baseline interjection filter must be off"):
            self.evaluate()

    def test_labels_after_either_candidate_run_are_invalid(self):
        self.manifest["labeled_at"] = "2099-01-01T00:00:00Z"
        with self.assertRaisesRegex(ValueError, "labels must be frozen"):
            self.evaluate()
        self.manifest["labeled_at"] = "2026-10-12T00:00:00Z"
        self.observations["runs"][3]["recorded_at"] = "2026-10-11T23:59:59Z"
        with self.assertRaisesRegex(ValueError, "labels must be frozen"):
            self.evaluate()

    def test_label_chronology_compares_timezone_offsets_and_allows_equality(self):
        self.manifest["labeled_at"] = "2026-10-12T10:00:00+09:00"
        self.observations["runs"][3]["recorded_at"] = "2026-10-11T20:00:00-05:00"
        self.assertEqual(self.evaluate()["verdict"], "pass")
        self.manifest["labeled_at"] = "2026-10-12T10:00:01+09:00"
        with self.assertRaisesRegex(ValueError, "labels must be frozen"):
            self.evaluate()

    def test_bad_metadata_types_and_values_are_invalid(self):
        for name, value in (("automatic_min_speech_ms", True), ("automatic_min_speech_ms", 0),
                            ("interjection_filter_enabled", 1), ("revision", "unknown"),
                            ("recorded_at", "2026-10-12T01:00:00"), ("id", "private words")):
            original = copy.deepcopy(self.observations)
            self.observations["runs"][0][name] = value
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.evaluate()
            self.observations = original

    def test_duplicate_clips_cannot_fill_the_sample_quota(self):
        self.cases[1]["audio"] = copy.deepcopy(self.cases[0]["audio"])
        with self.assertRaisesRegex(ValueError, "duplicate audio hash"):
            self.evaluate()

    def test_missing_tampered_and_outside_audio_are_invalid(self):
        for name, value in (("path", "missing.wav"), ("sha256", "0" * 64),
                            ("path", "../outside.wav"), ("path", "/tmp/private.wav"),
                            ("path", "C:\\private.wav")):
            original = copy.deepcopy(self.cases[0]["audio"])
            self.cases[0]["audio"][name] = value
            with self.subTest(name=name, value=value), self.assertRaises(ValueError):
                self.evaluate()
            self.cases[0]["audio"] = original

    def test_invalid_wav_is_rejected_even_with_matching_hash(self):
        path = self.root / self.cases[0]["audio"]["path"]
        path.write_bytes(b"not WAV")
        self.cases[0]["audio"] = self.file_ref(path)
        with self.assertRaisesRegex(ValueError, "PCM WAV"):
            self.evaluate()

    def test_malformed_riff_chunk_returns_sanitized_cli_error(self):
        path = self.root / self.cases[0]["audio"]["path"]
        path.write_bytes(b"RIFF" + struct.pack("<I", 12) + b"WAVEJUNK" + struct.pack("<I", 1024))
        self.cases[0]["audio"] = self.file_ref(path)
        result = self.cli()
        self.assertEqual(result.returncode, 2)
        self.assertEqual(json.loads(result.stdout), {"verdict": "invalid", "error": "case[0]: cannot read PCM WAV"})
        self.assertEqual(result.stderr, "")
        self.assertNotIn(str(self.root), result.stdout)

    def test_oversized_wav_data_declaration_is_rejected(self):
        path = self.root / self.cases[0]["audio"]["path"]
        data = bytearray(path.read_bytes())
        data[40:44] = struct.pack("<I", 0xFFFFFFFE)
        path.write_bytes(data)
        self.cases[0]["audio"] = self.file_ref(path)
        with self.assertRaisesRegex(ValueError, "WAV declares more data"):
            self.evaluate()

    def test_symlink_loop_returns_sanitized_cli_error(self):
        path = self.root / "private-loop.wav"
        try:
            path.symlink_to(path.name)
        except (OSError, NotImplementedError):
            self.skipTest("Creating symlinks is unavailable on this test host")
        self.cases[0]["audio"]["path"] = path.name
        result = self.cli()
        self.assertEqual(result.returncode, 2)
        self.assertEqual(json.loads(result.stdout),
                         {"verdict": "invalid", "error": "case[0]: cannot read local evidence file"})
        self.assertEqual(result.stderr, "")
        self.assertNotIn("private-loop", result.stdout)
        self.assertNotIn(str(self.root), result.stdout)

    def test_path_encoding_error_is_sanitized(self):
        self.cases[0]["audio"]["path"] = "private-\ud800.wav"
        result = self.cli()
        self.assertEqual(result.returncode, 2)
        self.assertEqual(json.loads(result.stdout)["verdict"], "invalid")
        self.assertNotIn("private-", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_missing_or_changed_run_evidence_is_invalid(self):
        self.observations["runs"][0]["evidence"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            self.evaluate()

    def test_cli_pass_and_privacy(self):
        result = self.cli()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["verdict"], "pass")
        self.assertNotIn("private spoken", result.stdout)
        self.assertNotIn("private-audio", result.stdout)
        self.assertNotIn("private-log", result.stdout)
        self.assertNotIn(str(self.root), result.stdout)

    def test_cli_nonacceptance_exit(self):
        self.manifest["corpus_source"] = "synthetic"
        result = self.cli()
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["verdict"], "not_accepted")

    def test_cli_rejects_duplicate_keys_nonfinite_numbers_and_invalid_json(self):
        for payload in ('{"schema_version":1,"schema_version":1}', '{"number":NaN}',
                        '{"number":Infinity}', '{"private transcript":'):
            with self.subTest(payload=payload):
                result = self.cli(payload)
                self.assertEqual(result.returncode, 2)
                self.assertEqual(json.loads(result.stdout)["verdict"], "invalid")
                self.assertNotIn("private transcript", result.stdout)

    def test_cli_error_does_not_print_paths(self):
        self.cases[0]["audio"]["path"] = "secret-name.wav"
        result = self.cli()
        self.assertEqual(result.returncode, 2)
        self.assertNotIn("secret-name", result.stdout)
        self.assertNotIn(str(self.root), result.stdout)


if __name__ == "__main__":
    unittest.main()
