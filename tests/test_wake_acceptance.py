import copy
import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("acceptance", Path(__file__).parents[1] / "scripts/wake_acceptance.py")
acceptance = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acceptance)


def labeled():
    return {"rules": ["auto_b"], "negativeExposureHours": {"after_input": 10, "idle": 10}, "safetyIncidents": 0,
            "attempts": [{"id": f"{context}-{i}", "context": context, "intent": True,
                          "strictWake": i >= 50, "wakes": {"auto_b": i >= 25}}
                         for context in ("after_input", "idle") for i in range(100)]}


class WakeAcceptanceTests(unittest.TestCase):
    def test_paired_reduction(self):
        report = acceptance.evaluate(labeled())
        self.assertEqual(report["verdict"], "pass")
        self.assertEqual(report["results"][0]["relative_miss_reduction"], 0.5)
        self.assertEqual(report["results"][0]["candidate_miss_percent"], 25)

    def test_false_wake_limit_and_baseline_delta(self):
        data = labeled()
        data["attempts"].extend({"id": f"false-{i}", "context": "idle", "intent": False, "strictWake": False,
                                 "wakes": {"auto_b": True}} for i in range(2))
        self.assertEqual(acceptance.evaluate(data)["verdict"], "fail")

    def test_insufficient_or_unknown_evidence(self):
        data = labeled()
        data["negativeExposureHours"]["idle"] = 9
        self.assertEqual(acceptance.evaluate(data)["verdict"], "inconclusive")
        data = labeled()
        data["attempts"][0]["context"] = "unknown"
        self.assertEqual(acceptance.evaluate(data)["verdict"], "inconclusive")

    def test_safety_incident_fails_even_without_exposure(self):
        data = {"rules": ["followup"], "negativeExposureHours": {}, "safetyIncidents": 1, "attempts": []}
        self.assertEqual(acceptance.evaluate(data)["verdict"], "fail")

    def test_rejects_duplicate_and_implicit_labels(self):
        data = labeled()
        data["attempts"].append(copy.deepcopy(data["attempts"][0]))
        with self.assertRaises(ValueError): acceptance.evaluate(data)
        data = labeled()
        data["attempts"][0]["intent"] = "true"
        with self.assertRaises(ValueError): acceptance.evaluate(data)
        data = labeled()
        data["negativeExposureHours"]["idle"] = float("nan")
        with self.assertRaises(ValueError): acceptance.evaluate(data)


if __name__ == "__main__":
    unittest.main()
