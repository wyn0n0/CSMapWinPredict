import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from tools.win_inference_service import (
    SCHEMA_VERSION,
    SEMANTIC_VERSION,
    ModelLoadError,
    WinModelRuntime,
    process_request,
)


ROOT = Path(__file__).resolve().parents[1]
MODEL_DIR = ROOT / "models" / "win-baseline-v4-holdout-68-5-20260906"


def nested_sample(flattened):
    result = {}
    for path, value in flattened.items():
        target = result
        parts = path.split(".")
        for part in parts[:-1]:
            target = target.setdefault(part, {})
        target[parts[-1]] = value
    return result


class WinModelRuntimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.runtime = WinModelRuntime(MODEL_DIR)
        cls.fixtures = json.loads(
            (MODEL_DIR / "inference-fixtures.json").read_text(encoding="utf-8")
        )

    def test_health_reports_verified_model_contract(self):
        info = self.runtime.info
        self.assertEqual(info["status"], "ready")
        self.assertEqual(info["schemaVersion"], SCHEMA_VERSION)
        self.assertEqual(info["semanticVersion"], SEMANTIC_VERSION)
        self.assertEqual(info["selfTestFixtures"], len(self.fixtures))
        self.assertEqual(info["featureCount"], 98)

    def test_fixture_batch_matches_training_predictions(self):
        actual = self.runtime.predict(self.fixtures, 10)
        for prediction, fixture in zip(actual, self.fixtures):
            self.assertAlmostEqual(prediction["tWin"], fixture["expectedTWin"], places=12)
            self.assertAlmostEqual(prediction["tWin"] + prediction["ctWin"], 1, places=12)

    def test_nested_builder_shape_matches_flat_fixture(self):
        sample = nested_sample(self.fixtures[0]["features"])
        prediction = self.runtime.predict([sample], 1)[0]
        self.assertAlmostEqual(prediction["tWin"], self.fixtures[0]["expectedTWin"], places=12)

    def test_rejects_missing_features_and_future_label(self):
        missing = nested_sample(self.fixtures[0]["features"])
        del missing["features"]["clock"]["liveElapsedSeconds"]
        response, _ = process_request(self.runtime, {
            "id": "missing",
            "op": "predict",
            "schemaVersion": SCHEMA_VERSION,
            "semanticVersion": SEMANTIC_VERSION,
            "samples": [missing],
        }, 10)
        self.assertFalse(response["ok"])
        self.assertEqual(response["error"]["code"], "missing_features")

        future = {"features": dict(self.fixtures[0]["features"]), "labelTWin": 1}
        response, _ = process_request(self.runtime, {
            "id": "future",
            "op": "predict",
            "schemaVersion": SCHEMA_VERSION,
            "semanticVersion": SEMANTIC_VERSION,
            "samples": [future],
        }, 10)
        self.assertFalse(response["ok"])
        self.assertEqual(response["error"]["code"], "future_field_rejected")

    def test_rejects_schema_mismatch_and_large_batch(self):
        response, _ = process_request(self.runtime, {
            "id": 1,
            "op": "predict",
            "schemaVersion": 3,
            "semanticVersion": SEMANTIC_VERSION,
            "samples": self.fixtures,
        }, 10)
        self.assertEqual(response["error"]["code"], "schema_mismatch")

        response, _ = process_request(self.runtime, {
            "id": 2,
            "op": "predict",
            "schemaVersion": SCHEMA_VERSION,
            "semanticVersion": SEMANTIC_VERSION,
            "samples": self.fixtures,
        }, 2)
        self.assertEqual(response["error"]["code"], "batch_too_large")

    def test_rejects_tampered_required_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory)
            for name in (
                "baseline.joblib",
                "feature-manifest.json",
                "inference-fixtures.json",
                "model-manifest.json",
                "report.json",
            ):
                shutil.copy2(MODEL_DIR / name, target / name)
            with (target / "feature-manifest.json").open("a", encoding="utf-8") as stream:
                stream.write(" ")
            with self.assertRaisesRegex(ModelLoadError, "SHA-256 mismatch"):
                WinModelRuntime(target)


class JsonLineProtocolTests(unittest.TestCase):
    def test_process_health_predict_error_and_shutdown(self):
        process = subprocess.Popen(
            [sys.executable, str(ROOT / "tools" / "win_inference_service.py"),
             "--model-dir", str(MODEL_DIR)],
            cwd=ROOT,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )
        def cleanup_process():
            if process.poll() is None:
                process.kill()
            process.wait()
            for stream in (process.stdin, process.stdout, process.stderr):
                if stream is not None and not stream.closed:
                    stream.close()

        self.addCleanup(cleanup_process)

        def exchange(request):
            process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
            process.stdin.flush()
            line = process.stdout.readline()
            if not line:
                self.fail(f"inference service exited early: {process.stderr.read()}")
            return json.loads(line)

        health = exchange({"id": "health-1", "op": "health"})
        self.assertTrue(health["ok"])
        self.assertEqual(health["result"]["status"], "ready")

        invalid = exchange({"id": "bad-1", "op": "unknown"})
        self.assertFalse(invalid["ok"])
        self.assertEqual(invalid["error"]["code"], "unknown_operation")

        fixture = json.loads(
            (MODEL_DIR / "inference-fixtures.json").read_text(encoding="utf-8")
        )[0]
        predicted = exchange({
            "id": "predict-1",
            "op": "predict",
            "schemaVersion": SCHEMA_VERSION,
            "semanticVersion": SEMANTIC_VERSION,
            "samples": [fixture],
        })
        self.assertTrue(predicted["ok"])
        self.assertAlmostEqual(
            predicted["result"]["predictions"][0]["tWin"], fixture["expectedTWin"], places=12
        )

        stopped = exchange({"id": "stop-1", "op": "shutdown"})
        self.assertTrue(stopped["ok"])
        process.stdin.close()
        self.assertEqual(process.wait(timeout=10), 0)


if __name__ == "__main__":
    unittest.main()
