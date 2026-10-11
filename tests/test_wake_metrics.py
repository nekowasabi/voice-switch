import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("metrics", Path(__file__).parents[1] / "scripts/overnight_metrics.py")
metrics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(metrics)

class WakeMetricsTests(unittest.TestCase):
    def test_contexts_and_labels_without_text(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.log"
            path.write_text("\n".join([
                "dictation recognition: complete id=1 leadingWake=False rule=auto_b inputIdleSeconds=3.000",
                "dictation recognition: complete id=2 leadingWake=True rule=strict inputIdleSeconds=0.200",
                "dictation recognition: complete id=3 leadingWake=False rule=followup inputIdleSeconds=-1",
                "dictation recognition: complete id=4 leadingWake=False",
                "wake label: MISS id=1 rule=auto_b inputIdleSeconds=3.000",
            ]))
            self.assertEqual(metrics.parse_metrics(path)["wake_telemetry"], {
                "rules": {"auto_b": 1, "strict": 1, "followup": 1},
                "contexts": {"auto_b:idle": 1, "strict:after_input": 1, "followup:unknown": 1},
                "missing_fields": 1, "miss_labels": 1, "miss_contexts": {"auto_b:idle": 1},
            })

if __name__ == "__main__":
    unittest.main()
