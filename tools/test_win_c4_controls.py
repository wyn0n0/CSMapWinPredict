import contextlib
import copy
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import warnings

import numpy as np
from threadpoolctl import threadpool_limits

from tools import train_win_c4_controls as controls
from tools import train_win_experiments as experiments
from tools.test_train_win_experiments import training_fixture
from tools.win_inference_service import ModelLoadError, RequestError, WinModelRuntime


class C4ControlTests(unittest.TestCase):
    def setUp(self):
        context = warnings.catch_warnings()
        context.__enter__()
        self.addCleanup(context.__exit__, None, None, None)
        warnings.filterwarnings("ignore", message="Skipping features without any observed values")

    def test_mask_removes_only_six_magnitudes_and_preserves_missingness(self):
        frame, _ = training_fixture()
        for i, name in enumerate(controls.GEOMETRY):
            frame.loc[i, name] = np.nan
        original = frame.copy(deep=True)
        masked = controls.mask_geometry(frame)
        self.assertEqual(len(controls.GEOMETRY), 6)
        np.testing.assert_array_equal(masked.isna(), original.isna())
        self.assertTrue(masked[list(controls.GEOMETRY)].fillna(0).eq(0).all().all())
        self.assertTrue(frame.equals(original))
        self.assertTrue(masked.drop(columns=list(controls.GEOMETRY)).equals(
            frame.drop(columns=list(controls.GEOMETRY))))
        changed = frame.copy()
        changed.loc[:, list(controls.GEOMETRY)] *= 1234
        self.assertTrue(controls.mask_geometry(changed).equals(masked))

    def test_b_and_c_keep_identical_indicators_and_bundle_contracts(self):
        frame, meta = training_fixture()
        for i, name in enumerate(controls.GEOMETRY):
            frame.loc[i::9, name] = np.nan
        splits = controls.grouped_splits(meta, 3)
        config = {"contractVersion": "win-spatial-v1", "radii": {"supportRadius": 1., "siteRadius": 2.}}
        indicators = {}
        with tempfile.TemporaryDirectory() as temp, threadpool_limits(limits=1):
            for candidate in controls.CANDIDATES:
                model, diagnostics, oof = controls.fit_control(candidate, frame, meta, splits, 42, 1)
                pre = model.base_model.named_steps["preprocessor"]
                indicators[candidate] = [n for n in pre.get_feature_names_out() if n.startswith("missingindicator_")]
                changed = frame.copy()
                changed.loc[:, list(controls.GEOMETRY)] *= 100
                changed["derived.isBroadEndgame"] = -999
                changed["features.baseline.isClutch"] = -999
                if candidate != "C_distance":
                    np.testing.assert_array_equal(model.predict_proba(frame), model.predict_proba(changed))
                directory = Path(temp) / candidate
                controls.save_bundle(directory, candidate, model, diagnostics, oof, frame.iloc[:3], config, "a"*64)
                runtime = controls.ControlsRuntime(directory)
                samples = controls.samples_for(frame, candidate, config)
                actual, _ = experiments.runtime_predict(runtime, samples)
                np.testing.assert_allclose(actual, model.predict_proba(frame)[:, 1], atol=1e-12, rtol=0)
                with self.assertRaisesRegex(ModelLoadError, "experimental feature contract"):
                    WinModelRuntime(directory)
                with self.assertRaisesRegex(RequestError, "contract"):
                    runtime.predict([{**samples[0], "featureContract": "win-broad-endgame-v1"}], 1)
                with self.assertRaisesRegex(RequestError, "forbidden"):
                    runtime.predict([{**samples[0], "labelTWin": 1}], 1)
                if candidate != "A_base":
                    with self.assertRaisesRegex(RequestError, "spatial"):
                        runtime.predict([{**samples[0], "spatialConfigSha256": "wrong"}], 1)
                (directory / "baseline.joblib").write_bytes(b"corrupted")
                with self.assertRaisesRegex(ModelLoadError, "SHA-256"):
                    controls.ControlsRuntime(directory)
        self.assertEqual(indicators["B_state"], indicators["C_distance"])
        self.assertTrue(all("missingindicator_"+n in indicators["B_state"] for n in controls.GEOMETRY))

    def test_nested_run_never_passes_outer_holdout_to_fit_or_calibration(self):
        frame, meta = training_fixture()
        meta["phase"] = frame["features.phase"]
        meta["tAlive"] = frame["features.t.alive"]
        meta["ctAlive"] = frame["features.ct.alive"]
        meta["liveElapsed"] = np.nan  # Also exercise null-safe saved metadata.
        plan = dict(outerFolds=3, innerFolds=2, seed=42, threads=1, bootstrapResamples=10,
                    purpose="training-only-fixed-controls")
        expected = [set(meta.iloc[a].matchId) for a, _ in controls.grouped_splits(meta, 3)]
        calls = []
        real_fit = controls.fit_control

        def checked(candidate, x, m, splits, seed, threads):
            position = sum(c == candidate for c, _ in calls)
            allowed = expected[position] if position < 3 else set(meta.matchId)
            self.assertEqual(set(m.matchId), allowed)
            for training, held in splits:
                self.assertFalse(set(m.iloc[training].matchId) & set(m.iloc[held].matchId))
                self.assertTrue(set(m.iloc[training].matchId) | set(m.iloc[held].matchId) <= allowed)
            calls.append((candidate, allowed))
            return real_fit(candidate, x, m, splits, seed, threads)

        with tempfile.TemporaryDirectory() as temp, patch.object(controls, "load_plan", return_value=plan), \
                patch.object(controls, "load_training", return_value=(frame, meta, {}, {"training": {"rows": len(meta)}})), \
                patch.object(controls, "fit_control", side_effect=checked), contextlib.redirect_stdout(io.StringIO()):
            output = Path(temp) / "results"
            controls.run(Path(temp) / "plan.json", "a"*64, output)
            report = controls.read(output / "report.json")
            self.assertFalse(report["developmentScored"])
            self.assertFalse(report["defaultModelChanged"])
            self.assertEqual(len(calls), 12)
            rows = [__import__("json").loads(line) for line in (output / "outer-predictions.jsonl").read_text().splitlines()]
            self.assertEqual(len(rows), len(meta))
            self.assertTrue(all(r["liveElapsed"] is None for r in rows))
            self.assertEqual({r["outerFold"] for r in rows}, {0, 1, 2})
            for name, digest in controls.read(output / "completion.json")["artifacts"].items():
                self.assertEqual(controls.v4.sha256(output / name), digest)

    def test_plan_rejects_hash_drift_and_final_test(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "source.txt"
            source.write_text("original")
            plan = dict(status="frozen", featureContract=controls.CONTRACT, purpose="training-only-fixed-controls",
                candidates=list(controls.CANDIDATES), geometryFields=list(controls.GEOMETRY),
                promotionEnabled=False, finalTestEnabled=False, partitions={"test": []},
                runtime=experiments.runtime_versions(), inputs={str(source): controls.v4.sha256(source)}, preservedModels={})
            path = root / "plan.json"
            controls.save(path, plan)
            digest = controls.v4.sha256(path)
            self.assertEqual(controls.load_plan(path, digest)["status"], "frozen")
            with self.assertRaisesRegex(ValueError, "plan hash"):
                controls.load_plan(path, "wrong")
            changed = copy.deepcopy(plan)
            changed["partitions"]["test"] = ["forbidden"]
            bad = root / "bad.json"
            controls.save(bad, changed)
            with self.assertRaisesRegex(ValueError, "forbidden"):
                controls.load_plan(bad, controls.v4.sha256(bad))
            source.write_text("changed")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                controls.load_plan(path, digest)
            with patch.object(controls, "load_plan") as load:
                with self.assertRaises(FileExistsError):
                    controls.run(path, digest, root)
                load.assert_not_called()

    def test_training_loader_rejects_partition_overlap_before_reading(self):
        plan = {"partitions": {"train": ["a"], "excludedObservedDevelopment": ["a"], "excludedLegacyValidation": []}}
        with patch.object(controls.v4, "load_records") as load:
            with self.assertRaisesRegex(ValueError, "intersects"):
                controls.load_training(plan)
            load.assert_not_called()


if __name__ == "__main__":
    unittest.main()
