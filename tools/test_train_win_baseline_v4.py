import unittest
import copy
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
import pandas as pd

from tools.train_win_baseline_v4 import (
    SCHEMA_VERSION,
    SEMANTIC_VERSION,
    ece,
    json_safe_features,
    load_records,
    load_saved_predictions,
    make_frames,
    sha256,
    validate_records,
    main as training_main,
    evaluate_saved,
)
from tools.win_evaluation import (
    SMALL_FEATURES, calibration_bins, evaluation_report, export_coverage, match_bootstrap,
    metrics, reference_predictions, slice_masks, summarize,
)
from tools.win_experiment import (
    file_sha256, freeze_experiment, load_frozen_experiment, plan_template, registry_template,
    EVALUATION_SOURCES,
)


def row(match_id="match-a", round_id="s0-a1", tick=64, label=1, weight=1.0):
    return {
        "schemaVersion": SCHEMA_VERSION,
        "semanticVersion": SEMANTIC_VERSION,
        "matchId": match_id,
        "roundId": round_id,
        "roundNumber": 1,
        "tick": tick,
        "labelTWin": label,
        "sampleWeight": weight,
    }


class V4TrainingValidationTests(unittest.TestCase):
    def test_accepts_equal_round_weight(self):
        records = [row(tick=64, weight=0.5), row(tick=128, weight=0.5)]
        summary = validate_records(records)
        self.assertEqual(summary["rows"], 2)
        self.assertEqual(summary["rounds"], 1)

    def test_rejects_duplicate_match_round_tick(self):
        records = [row(), row()]
        with self.assertRaisesRegex(ValueError, "duplicate sample key"):
            validate_records(records)

    def test_rejects_round_weight_drift(self):
        records = [row(tick=64, weight=0.6), row(tick=128, weight=0.6)]
        with self.assertRaisesRegex(ValueError, "weights do not sum to one"):
            validate_records(records)

    def test_rejects_mixed_semantics(self):
        value = row()
        value["semanticVersion"] = "older"
        with self.assertRaisesRegex(ValueError, "Mixed or unsupported"):
            validate_records([value])

    def test_perfect_probabilities_have_zero_ece(self):
        labels = np.array([0, 0, 1, 1])
        probabilities = np.array([0.0, 0.0, 1.0, 1.0])
        self.assertEqual(ece(labels, probabilities, np.ones(4)), 0)

    def test_fixture_features_are_strict_json_values(self):
        result = json_safe_features(pd.Series({"missing": np.nan, "integer": np.int64(3), "text": "live"}))
        self.assertIsNone(result["missing"])
        self.assertEqual(result["integer"], 3)
        self.assertIs(type(result["integer"]), int)

    def test_rejects_missing_identity_invalid_labels_and_weights(self):
        for changes in ({"matchId": None}, {"roundId": ""}, {"labelTWin": 2},
                        {"sampleWeight": float("nan")}, {"sampleWeight": -1}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                validate_records([{**row(), **changes}])


def evaluation_meta():
    return pd.DataFrame({
        "matchId": ["a", "a", "b", "c"], "roundId": ["r1", "r2", "r1", "r1"],
        "tick": [64, 128, 64, 64], "roundNumber": [1, 2, 1, 1],
        "label": [1, 1, 0, 0], "weight": [1., 1., 1., 1.],
        "phase": ["live", "post-plant", "live", "live"],
        "tAlive": [1, 1, 3, 0], "ctAlive": [1, 3, 3, 3],
        "liveElapsed": [0, 30, 5, 60], "bombRemaining": [np.nan, 10, np.nan, np.nan],
        "equipmentDifference": [100, -100, 0, np.nan],
        "aliveKnown": [2, 4, 6, 3], "equipmentKnown": [1, 4, 6, 3],
        "positionKnown": [2, 3, 6, 3],
    })


class EvaluationTests(unittest.TestCase):
    def test_clutch_groups_separate_1v1_1v3_3v3_and_0v3(self):
        masks = slice_masks(evaluation_meta())
        self.assertNotIn("clutch", masks)
        np.testing.assert_array_equal(masks["oneVsN"], [True, True, False, False])
        np.testing.assert_array_equal(masks["oneVsOne"], [True, False, False, False])
        np.testing.assert_array_equal(masks["oneVsMany"], [False, True, False, False])
        self.assertTrue(masks["totalAliveAtMost6"].all())
        self.assertEqual(masks["equipmentMissing"].sum(), 1)
        self.assertEqual(masks["positionMissing"].sum(), 1)
        self.assertEqual(masks["postPlantBombRemaining0To10"].sum(), 1)

    def test_single_class_and_empty_slices_are_visible(self):
        report = evaluation_report(evaluation_meta(), {"model": [.8, .7, .2, .1]}, reference="model", resamples=20)
        clutch = report["models"]["model"]["slices"]["oneVsN"]
        self.assertEqual(clutch["rows"], 2)
        self.assertEqual(clutch["rounds"], 2)
        self.assertIsNone(clutch["metrics"]["rocAuc"])
        self.assertEqual(clutch["aucUnavailableReason"], "single-class")
        self.assertIsNotNone(clutch["metrics"]["logLoss"])
        empty = report["models"]["model"]["slices"]["fiveVsFive"]
        self.assertEqual(empty["rows"], 0)
        self.assertIsNone(empty["metrics"])
        json.dumps(report, allow_nan=False)

    def test_coverage_counts_round_identity_across_matches_and_missing_predictions(self):
        meta = evaluation_meta()
        meta.loc[0, "weight"] = .5
        result = summarize(meta, [.8, np.nan, .2, np.nan])
        self.assertEqual((result["rows"], result["rounds"], result["matches"]), (2, 2, 2))
        self.assertEqual(result["weight"], 1.5)
        self.assertEqual(result["coverage"]["rowFraction"], .5)
        self.assertAlmostEqual(result["coverage"]["weightFraction"], 1.5 / 3.5)
        self.assertEqual(result["coverage"]["expected"]["rounds"], 4)

    def test_weighted_calibration_uses_round_weights(self):
        bucket = calibration_bins(np.array([0, 1]), np.array([.2, .2]), np.array([9., 1.]))[0]
        self.assertAlmostEqual(bucket["observedTWinRate"], .1)
        self.assertAlmostEqual(bucket["meanPrediction"], .2)
        self.assertEqual(bucket["weight"], 10)

    def test_bootstrap_matches_naive_whole_match_resampling(self):
        from scipy.stats import bootstrap
        meta, p = evaluation_meta(), np.array([.8, .7, .2, .1])
        actual = match_bootstrap(meta, p, seed=5, resamples=40)
        groups = [np.flatnonzero(meta.matchId.to_numpy() == m) for m in sorted(meta.matchId.unique())]
        def naive(indices):
            rows = np.concatenate([groups[i] for i in indices])
            value = metrics(meta.label.to_numpy()[rows], p[rows], meta.weight.to_numpy()[rows])
            return [value["logLoss"], value["brierScore"]]
        expected = bootstrap((np.arange(3),), naive, vectorized=False, method="percentile",
                             n_resamples=40, rng=np.random.default_rng(5))
        for i, key in enumerate(("logLoss", "brierScore")):
            self.assertAlmostEqual(actual["intervals"][key]["low"], expected.confidence_interval.low[i])
            self.assertAlmostEqual(actual["intervals"][key]["high"], expected.confidence_interval.high[i])

    def test_bootstrap_is_paired_and_does_not_treat_duplicated_points_as_new_matches(self):
        meta, p = evaluation_meta(), np.array([.8, .7, .2, .1])
        paired = match_bootstrap(meta, p, p, resamples=30)
        self.assertEqual(paired["intervals"]["logLoss"], {"estimate": 0., "low": 0., "high": 0.})
        baseline = match_bootstrap(meta, p, resamples=30)
        doubled = pd.concat([meta, meta], ignore_index=True).assign(weight=.5)
        copied = match_bootstrap(doubled, np.tile(p, 2), resamples=30)
        self.assertEqual(baseline["intervals"], copied["intervals"])
        self.assertIsNone(match_bootstrap(meta.iloc[:2], p[:2], resamples=30)["intervals"])
        missing = match_bootstrap(meta, [np.nan, .7, .2, .1], [.8, np.nan, .2, .1], resamples=30)
        self.assertEqual((missing["rows"], missing["matches"]), (2, 2))

    def test_reference_baselines_use_only_training_labels_and_weights(self):
        x = pd.DataFrame({name: [1., 2., 3., 4.] for name in SMALL_FEATURES})
        meta = evaluation_meta().assign(label=[0, 0, 1, 1], weight=[1., 1., 3., 3.])
        predictions = reference_predictions(x, meta, x.iloc[:2])
        np.testing.assert_allclose(predictions["trainingPrior"], [.75, .75])
        repeated = reference_predictions(x, meta, x.iloc[:2])
        np.testing.assert_allclose(predictions["smallLogistic"], repeated["smallLogistic"])

    def test_invalid_probabilities_are_not_silently_clipped_or_counted_as_missing(self):
        for invalid in (float("inf"), -.1, 1.1):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                summarize(evaluation_meta(), [invalid, .5, .5, .5])

    def test_export_coverage_uses_rejections_not_all_ticks(self):
        manifest = {"matches": [{"matchId": "a", "report": {"rowCount": 8, "rejected": {"clock": 2}}},
                                {"matchId": "train", "report": {"rowCount": 99, "rejected": {}}}]}
        result = export_coverage(manifest, {"a"})
        self.assertEqual(result["candidateRows"], 10)
        self.assertEqual(result["retainedFraction"], .8)


class SavedEvaluationTests(unittest.TestCase):
    def test_saved_prediction_alignment_rejects_duplicates_foreign_keys_and_wrong_labels(self):
        meta = evaluation_meta().iloc[:1].reset_index(drop=True)
        good = {"matchId": "a", "roundId": "r1", "tick": 64, "labelTWin": 1,
                "sampleWeight": 1., "candidate": .8}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "predictions.jsonl"
            for rows in ([good, good], [{**good, "matchId": "training"}], [{**good, "labelTWin": 0}],
                         [{**good, "sampleWeight": .5}], [{**good, "candidate": float("nan")} ]):
                path.write_text("\n".join(json.dumps(r) for r in rows), encoding="utf-8")
                with self.assertRaises(ValueError):
                    load_saved_predictions(path, meta, ["candidate"])
            path.write_text(json.dumps(good), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "provenance"):
                load_saved_predictions(path, meta, ["candidate"], "a" * 64)

    def test_excluded_test_rows_are_not_loaded_into_training_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "samples.jsonl"
            path.write_text(json.dumps({**row(), "features": {}}) + "\n" +
                            json.dumps({"matchId": "final-test", "labelTWin": "must not be consumed"}), encoding="utf-8")
            records = load_records(path, {"match-a"})
            self.assertEqual(len(records), 1)
            self.assertEqual(records[0]["matchId"], "match-a")

    def test_evaluation_cli_writes_new_diagnostics_without_changing_sources(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            records, manifest_matches, predictions = [], [], []
            for i, name in enumerate(["train-a", "train-b", "validation-a", "validation-b"]):
                record = {**row(name, label=i % 2), "features": {
                    "phase": "live", "t": {"alive": 1, "equipmentValue": i * 100},
                    "ct": {"alive": 3, "equipmentValue": 200},
                    "baseline": {"totalAlive": 4}, "clock": {"liveElapsedSeconds": 1}}}
                records.append(record)
                manifest_matches.append({"matchId": name, "report": {"rowCount": 1, "rejected": {}}})
                if i == 2:  # One missing prediction must remain in the coverage denominator.
                    predictions.append({**row(name, label=i % 2), "selectedTWin": .2})
            files = {
                "samples.jsonl": "\n".join(map(json.dumps, records)),
                "manifest.json": json.dumps({"status": "complete", "schemaVersion": 4, "semanticVersion": SEMANTIC_VERSION,
                                              "matches": manifest_matches}),
                "comparison.json": json.dumps({"valid": True, "schemaVersion": 4, "rows": 4}),
                "split.json": json.dumps({"validation": [{"matchId": "validation-a"}, {"matchId": "validation-b"}]}),
                "predictions.jsonl": "\n".join(map(json.dumps, predictions)),
            }
            for name, content in files.items():
                (root / name).write_text(content, encoding="utf-8")
            before = {name: sha256(root / name) for name in files}
            command = [sys.executable, "-m", "tools.train_win_baseline_v4", "--input", str(root / "samples.jsonl"),
                       "--manifest", str(root / "manifest.json"), "--comparison", str(root / "comparison.json"),
                       "--validation-split", str(root / "split.json"), "--evaluate-predictions", str(root / "predictions.jsonl"),
                       "--probability-column", "selectedTWin", "--reference-column", "trainingPrior",
                       "--bootstrap-resamples", "20", "--output-dir", str(root / "output")]
            result = subprocess.run(command, capture_output=True, text=True, timeout=60)
            self.assertEqual(result.returncode, 0, result.stderr)
            report = json.loads((root / "output/report.json").read_text(encoding="utf-8"))
            self.assertEqual(report["diagnostics"]["models"]["selectedTWin"]["overall"]["coverage"]["rowFraction"], .5)
            self.assertEqual(report["trainingMatchIds"], ["train-a", "train-b"])
            self.assertEqual(report["diagnostics"]["pairedDifferences"]["selectedTWin"]["reference"], "trainingPrior")
            self.assertTrue((root / "output/reliability.png").is_file())
            output_hash = sha256(root / "output/report.json")
            rerun = subprocess.run(command, capture_output=True, text=True, timeout=60)
            self.assertNotEqual(rerun.returncode, 0)
            self.assertEqual(sha256(root / "output/report.json"), output_hash)
            self.assertEqual(before, {name: sha256(root / name) for name in files})

    def test_final_test_fitting_is_rejected_before_reading_samples(self):
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(output_dir=Path(directory) / "new", input=None, manifest=None, comparison=None,
                validation_split=None, experiment=Path("frozen.json"), experiment_sha256="bound",
                evaluation_role="test", evaluate_predictions=None, seed=42, bootstrap_resamples=2000)
            with patch("tools.train_win_baseline_v4.parse_args", return_value=args), \
                    patch("tools.train_win_baseline_v4.validate_sources", return_value=({}, {})), \
                    patch("tools.train_win_baseline_v4.load_validation_ids", return_value=(["old"], {})), \
                    patch("tools.train_win_baseline_v4.load_frozen_experiment", return_value={"bootstrap": {"seed": 42, "resamples": 2000}}), \
                    patch("tools.train_win_baseline_v4.load_records") as loader:
                with self.assertRaisesRegex(ValueError, "forbidden"):
                    training_main()
                loader.assert_not_called()

    def test_final_test_requires_candidate_lock_before_reference_fitting(self):
        args = SimpleNamespace(probability_column=None, experiment=Path("frozen.json"), evaluation_role="test",
                               candidate_lock=None, candidate_artifact=None)
        with patch("tools.train_win_baseline_v4.reference_predictions") as fit:
            with self.assertRaisesRegex(ValueError, "candidate lock"):
                evaluate_saved(args, None, {}, pd.DataFrame(), evaluation_meta(), [], [0, 1])
            fit.assert_not_called()


class ExperimentFreezeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.entries = []
        for i, role in enumerate(["train", "legacy-validation", "development", "test", "test"]):
            path = self.root / f"match-{i}.dem"
            path.write_bytes(f"synthetic Demo {i}".encode())
            digest = file_sha256(path)
            self.entries.append({"matchId": digest, "demoSha256": digest, "demoPath": path.name,
                "matchKey": f"source-match-{i}", "playedAtUtc": f"2026-10-{i + 1:02d}T00:00:00Z",
                "eventId": "event", "gameVersion": "verified-version", "mapName": "de_mirage",
                "parserVersion": "parser/version", "source": "https://example.com/matches", "role": role,
                "unseen": role == "test", "fileName": path.name})
        self.manifest = {"status": "complete", "schemaVersion": 4, "semanticVersion": SEMANTIC_VERSION,
                         "parserVersion": "parser/version", "matches": [
                             {"matchId": m["matchId"], "file": m["fileName"]} for m in self.entries[:2]]}
        self.split = {"validation": [{"matchId": self.entries[1]["matchId"]}]}
        self.registry = {"registryVersion": 1, "matches": self.entries}
        self.plan = plan_template()
        self.plan["testWindow"] = {"startUtc": "2026-10-04T00:00:00Z", "endUtc": "2026-10-06T00:00:00Z", "matchCount": 2}
        self.plan["adoption"] = {key: 0 for key in self.plan["adoption"]}

    def freeze(self):
        return freeze_experiment(self.registry, self.plan, self.manifest, self.split, self.root)

    def test_template_does_not_fabricate_metadata_or_promote_old_validation_to_test(self):
        template = registry_template(self.manifest, self.split)
        self.assertEqual(template["matches"][1]["role"], "legacy-validation")
        self.assertIsNone(template["matches"][0]["playedAtUtc"])
        self.assertFalse(template["matches"][1]["unseen"])

    def test_freeze_retains_legacy_membership_and_checks_content(self):
        frozen = self.freeze()
        self.assertEqual(frozen["partitions"]["legacy-validation"], [self.entries[1]["matchId"]])
        self.assertNotIn("demoPath", frozen["matches"][0])
        (self.root / "match-0.dem").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            self.freeze()

    def test_rejects_duplicate_demo_and_alternate_encoding_of_same_match(self):
        original = copy.deepcopy(self.registry)
        self.registry["matches"].append(copy.deepcopy(self.entries[0]))
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.freeze()
        self.registry = original
        self.registry["matches"][3]["matchKey"] = self.registry["matches"][0]["matchKey"]
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.freeze()

    def test_rejects_legacy_split_changes_and_previously_seen_test(self):
        self.entries[1]["role"] = "test"
        with self.assertRaisesRegex(ValueError, "Legacy split"):
            self.freeze()
        self.entries[1]["role"] = "legacy-validation"
        self.entries[3]["unseen"] = False
        with self.assertRaisesRegex(ValueError, "unseen"):
            self.freeze()

    def test_rejects_test_count_time_and_adoption_budget_drift(self):
        self.plan["testWindow"]["matchCount"] = 3
        with self.assertRaisesRegex(ValueError, "count"):
            self.freeze()
        self.plan["testWindow"]["matchCount"] = 2
        self.entries[2]["playedAtUtc"] = "2026-10-05T00:00:00Z"
        with self.assertRaisesRegex(ValueError, "later"):
            self.freeze()
        self.entries[2]["playedAtUtc"] = "2026-10-03T00:00:00Z"
        self.plan["adoption"]["maxLogLossRegression"] = None
        with self.assertRaisesRegex(ValueError, "budget"):
            self.freeze()

    def test_frozen_reader_binds_experiment_and_legacy_split_hashes(self):
        path, split_path = self.root / "experiment.json", self.root / "split.json"
        split_path.write_text(json.dumps(self.split), encoding="utf-8")
        frozen = self.freeze()
        frozen["sources"] = {"legacyValidationSplit": {"sha256": file_sha256(split_path)}}
        frozen["sources"].update({name: {"sha256": file_sha256(Path(__file__).with_name(filename))}
                                   for name, filename in EVALUATION_SOURCES.items()})
        path.write_text(json.dumps(frozen), encoding="utf-8")
        expected = file_sha256(path)
        self.assertEqual(load_frozen_experiment(path, expected, split_path)["status"], "frozen")
        split_path.write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "split SHA-256"):
            load_frozen_experiment(path, expected, split_path)
        with self.assertRaisesRegex(ValueError, "experiment SHA-256"):
            load_frozen_experiment(path, "0" * 64, split_path)

    def test_frozen_cli_isolates_development_and_accepts_only_locked_test_predictions(self):
        files = {"legacy-manifest.json": self.manifest, "split.json": self.split,
                 "registry.json": self.registry, "plan.json": self.plan}
        for name, value in files.items():
            (self.root / name).write_text(json.dumps(value), encoding="utf-8")
        freeze_command = [sys.executable, "-m", "tools.win_experiment", "freeze",
            "--legacy-manifest", str(self.root / "legacy-manifest.json"),
            "--legacy-validation-split", str(self.root / "split.json"),
            "--registry", str(self.root / "registry.json"), "--plan", str(self.root / "plan.json"),
            "--output-dir", str(self.root / "frozen")]
        result = subprocess.run(freeze_command, capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stderr)
        frozen_path = self.root / "frozen/experiment.json"
        frozen_hash = file_sha256(frozen_path)
        records, matches = [], []
        artifact = self.root / "candidate.model"
        artifact.write_bytes(b"synthetic artifact; evaluator must not execute this file")
        digest = file_sha256(artifact)
        for entry in self.entries:
            # Two opposite-label rounds per match make train-only baselines identifiable.
            for label in (0, 1):
                records.append({**row(entry["matchId"], label=label), "roundId": f"round-{label}",
                    "features": {"phase": "live", "t": {"alive": 1, "equipmentValue": 100},
                                 "ct": {"alive": 3, "equipmentValue": 200},
                                 "clock": {"liveElapsedSeconds": 1}}})
            matches.append({"matchId": entry["matchId"], "report": {"rowCount": 2, "rejected": {}}})
        (self.root / "samples.jsonl").write_text("\n".join(map(json.dumps, records)), encoding="utf-8")
        (self.root / "manifest.json").write_text(json.dumps({**self.manifest, "matches": matches}), encoding="utf-8")
        (self.root / "comparison.json").write_text(json.dumps({"valid": True, "schemaVersion": 4, "rows": 10}), encoding="utf-8")
        lock = {"status": "frozen", "selectionPartition": "development", "experimentSha256": frozen_hash,
                "modelArtifactSha256": digest, "probabilityColumn": "candidate"}
        (self.root / "candidate-lock.json").write_text(json.dumps(lock), encoding="utf-8")
        for role in ("development", "test"):
            members = {m["matchId"] for m in self.entries if m["role"] == role}
            predictions = [{**r, "candidate": .4, "modelArtifactSha256": digest}
                           for r in records if r["matchId"] in members]
            prediction_path = self.root / f"{role}-predictions.jsonl"
            prediction_path.write_text("\n".join(map(json.dumps, predictions)), encoding="utf-8")
            command = [sys.executable, "-m", "tools.train_win_baseline_v4",
                "--input", str(self.root / "samples.jsonl"), "--manifest", str(self.root / "manifest.json"),
                "--comparison", str(self.root / "comparison.json"), "--validation-split", str(self.root / "split.json"),
                "--experiment", str(frozen_path), "--experiment-sha256", frozen_hash, "--evaluation-role", role,
                "--evaluate-predictions", str(prediction_path), "--probability-column", "candidate",
                "--output-dir", str(self.root / f"{role}-output")]
            if role == "test":
                command += ["--candidate-lock", str(self.root / "candidate-lock.json"), "--candidate-artifact", str(artifact)]
            result = subprocess.run(command, capture_output=True, text=True, timeout=60)
            self.assertEqual(result.returncode, 0, result.stderr)
            report = json.loads((self.root / f"{role}-output/report.json").read_text(encoding="utf-8"))
            self.assertEqual(report["trainingMatchIds"], [self.entries[0]["matchId"]])
            self.assertEqual(set(report["validationMatchIds"]), members)
            self.assertEqual(report["diagnostics"]["models"]["trainingPrior"]["overall"]["metrics"]["brierScore"], .25)
        artifact.write_bytes(b"candidate changed after selection")
        command[command.index("--output-dir") + 1] = str(self.root / "changed-candidate-output")
        result = subprocess.run(command, capture_output=True, text=True, timeout=60)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("differs from the frozen candidate", result.stderr)
        self.assertFalse((self.root / "changed-candidate-output").exists())
        frozen = json.loads(frozen_path.read_text(encoding="utf-8"))
        frozen["sources"]["evaluationHelpers"]["sha256"] = "0" * 64
        frozen_path.write_text(json.dumps(frozen), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "source SHA-256"):
            load_frozen_experiment(frozen_path, file_sha256(frozen_path), self.root / "split.json")


if __name__ == "__main__":
    unittest.main()
