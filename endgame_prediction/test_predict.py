import copy
import json
import subprocess
import sys
import unittest
from pathlib import Path

from endgame_prediction.predict import EndgamePredictor, MODEL_DIR
from tools.win_inference_service import ModelLoadError, RequestError, WinModelRuntime, process_request

ROOT = Path(__file__).resolve().parents[1]


class EndgameTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.model = EndgamePredictor()
        cls.request = json.loads((ROOT / "endgame_prediction/example-request.json").read_text(encoding="utf-8"))
        cls.sample = cls.request["samples"][0]

    def test_all_team_counts_and_phase_boundaries(self):
        accepted = []
        for phase in ("live", "post-plant", "freeze", "ended", None):
            for t in range(6):
                for ct in range(6):
                    sample = copy.deepcopy(self.sample)
                    sample["features"].update({"features.phase": phase, "features.t.alive": t, "features.ct.alive": ct})
                    if phase in ("live", "post-plant") and t > 0 and ct > 0 and t + ct <= 7:
                        accepted.append(sample)
                    else:
                        with self.subTest(phase=phase, t=t, ct=ct), self.assertRaises(RequestError) as error:
                            self.model.predict([sample])
                        self.assertEqual(error.exception.code, "not_endgame")
        # Gating must leave every accepted model probability untouched.
        self.assertEqual(self.model.predict(accepted), self.model.runtime.predict(accepted, 512))

    def test_invalid_inputs_and_mixed_batch_fail(self):
        for side in ("t", "ct"):
            for value in (None, True, False, -1, 6, 2.5, "3", float("nan")):
                sample = copy.deepcopy(self.sample)
                sample["features"][f"features.{side}.alive"] = value
                with self.subTest(side=side, value=value), self.assertRaises(RequestError):
                    self.model.predict([self.sample, sample])
        for samples in ([], None, {}, [self.sample] * 513):
            with self.subTest(batch=type(samples).__name__), self.assertRaises(RequestError):
                self.model.predict(samples)

    def test_map_contract_features_and_future_labels_are_checked(self):
        variants = []
        for key, value in (("mapName", "de_inferno"), ("features.t.alive", None)):
            sample = copy.deepcopy(self.sample)
            sample["features"][key] = value
            variants.append(sample)
        for key, value in (("featureContract", "win-m4-v1"), ("spatialConfigSha256", "wrong"), ("labelTWin", 1)):
            variants.append({**self.sample, key: value})
        missing = copy.deepcopy(self.sample)
        del missing["features"]["features.t.alive"]
        variants.append(missing)
        for sample in variants:
            with self.subTest(sample=sample.keys()), self.assertRaises(RequestError):
                self.model.predict([sample])
        for key, value in (("schemaVersion", 3), ("semanticVersion", "old")):
            response, _ = process_request(self.model, {**self.request, key: value}, 512)
            self.assertFalse(response["ok"])

    def test_reference_fixture_nested_input_and_production_isolation(self):
        fixture = json.loads((MODEL_DIR / "inference-fixtures.json").read_text())[2]
        self.assertAlmostEqual(self.model.predict([fixture])[0]["tWin"], fixture["expectedTWin"], places=12)
        actual = self.model.predict([self.sample])[0]
        self.assertAlmostEqual(actual["tWin"] + actual["ctWin"], 1.0, places=12)
        nested = {k: v for k, v in self.sample.items() if k != "features"}
        for path, value in self.sample["features"].items():
            target = nested
            parts = path.split(".")
            for part in parts[:-1]:
                target = target.setdefault(part, {})
            target[parts[-1]] = value
        self.assertEqual(self.model.predict([nested]), [actual])
        with self.assertRaises(ModelLoadError):
            WinModelRuntime(MODEL_DIR)

    def test_command_line_jsonl_recovers_after_rejected_request(self):
        invalid = copy.deepcopy(self.request)
        invalid["samples"][0]["features"].update({"features.t.alive": 4, "features.ct.alive": 4})
        commands = [{"id": "health", "op": "health"}, invalid, self.request, {"id": "stop", "op": "shutdown"}]
        result = subprocess.run(
            [sys.executable, "-B", str(ROOT / "endgame_prediction/predict.py")],
            input="\n".join(json.dumps(x) for x in commands) + "\n", text=True,
            capture_output=True, cwd=ROOT / "endgame_prediction", timeout=60,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        responses = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(len(responses), 4)
        self.assertEqual(responses[0]["result"]["scope"], "endgame-only")
        self.assertEqual(responses[1]["error"]["code"], "not_endgame")
        self.assertEqual(responses[2]["result"]["predictions"], self.model.predict(self.request["samples"]))
        self.assertTrue(responses[3]["ok"])


if __name__ == "__main__":
    unittest.main()
