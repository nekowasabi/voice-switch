import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).parents[1]
spec = importlib.util.spec_from_file_location("metrics", ROOT / "scripts/overnight_metrics.py")
metrics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(metrics)


class WakeMetricsTests(unittest.TestCase):
    def test_shared_fixture_matches_csharp(self):
        lines = [f"dictation recognition: complete id=1 rule=strict inputIdleSeconds={idle}"
                 for idle in ("0.200", "3.000", "-1", "null", "NaN", "Infinity")]
        lines += [
            "dictation recognition: complete id=2",
            'dictation recognition: complete text="rule=strict inputIdleSeconds=3.000"',
            "dictation recognition: complete rule=auto_b inputIdleSeconds=3.000",
            "wake label: MISS id=1 rule=strict inputIdleSeconds=-1",
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.log"
            path.write_text("\n".join(lines), encoding="utf-8")
            expected = {"rules": {"strict": 6},
                        "contexts": {"strict:after_input": 1, "strict:idle": 1, "strict:unknown": 4},
                        "missing_fields": 3, "miss_labels": 1}
            self.assertEqual(metrics.parse_metrics(path)["wake_telemetry"], expected)
            replay = subprocess.run(
                ["dotnet", "run", "--project", str(ROOT / "dotnet/VoiceSwitch.WakeReplay"),
                 "-c", "Release", "--", "--log", str(path)],
                capture_output=True, text=True, timeout=60,
                env={**os.environ, "DOTNET_ROLL_FORWARD": "Major"}, cwd=ROOT)
            self.assertEqual(replay.returncode, 0, replay.stderr + replay.stdout)
            summary = json.loads(next(line.removeprefix("wake telemetry ")
                                     for line in replay.stdout.splitlines()
                                     if line.startswith("wake telemetry ")))
            self.assertEqual(summary, {"Rules": expected["rules"], "Contexts": expected["contexts"],
                                       "MissingFields": 3, "MissLabels": 1})


if __name__ == "__main__":
    unittest.main()
