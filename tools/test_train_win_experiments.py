import copy
import json
from pathlib import Path
import tempfile
import unittest
import warnings
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
import pandas as pd
from sklearn.model_selection import GroupKFold
from threadpoolctl import threadpool_limits

from tools import train_win_baseline_v4 as v4
from tools import train_win_experiments as experiments
from tools.win_feature_sets import (
    CONTRACT, FEATURE_SETS, KD, PROGRESS, ExperimentalWinRuntime, add_interactions,
    candidate_specs, config_sha256, grouped_explanation, join_spatial, make_model,
    requires_spatial, SPATIAL_GROUPS, SPATIAL_NUMERIC, SPATIAL_CATEGORICAL,
    ENDGAME_CONTRACT, ENDGAME_BASES, load_experimental_runtime,
)
from tools.win_evaluation import slice_masks
from tools.win_inference_service import ModelLoadError, RequestError, WinModelRuntime


def training_fixture():
    size = 72
    rng = np.random.default_rng(42)
    frame = pd.DataFrame({n: rng.uniform(0, 5, size) for n in v4.NUMERIC_FEATURES})
    for name in v4.CATEGORICAL_FEATURES:
        frame[name] = "known"
    frame["features.phase"] = np.where(np.arange(size) % 2, "post-plant", "live")
    frame["features.ct.defuserCount"] = np.arange(size) % 2
    frame["features.t.alive"] = rng.integers(1, 6, size).astype(float)
    frame["features.ct.alive"] = rng.integers(1, 6, size).astype(float)
    # An entirely missing column exercises feature dropping and explanation alignment.
    frame["features.clock.defuseRemainingSeconds"] = np.nan
    for name in set(FEATURE_SETS["E3"][0]) - set(v4.NUMERIC_FEATURES):
        frame[name] = rng.uniform(0, 100, size)
    for name in set(FEATURE_SETS["E3"][1]) - set(v4.CATEGORICAL_FEATURES):
        frame[name] = "known"
    meta = pd.DataFrame(dict(matchId=np.repeat([f"m{i}" for i in range(6)], 12),
        roundId=np.tile(np.repeat(["r0", "r1", "r2", "r3"], 3), 6), tick=np.tile([64, 128, 192], 24),
        label=np.tile(np.repeat([0, 1, 0, 1], 3), 6), weight=np.full(size, 1/3)))
    return frame, meta


class M4ExperimentTests(unittest.TestCase):
    def setUp(self):
        warning_context = warnings.catch_warnings()
        warning_context.__enter__()
        self.addCleanup(warning_context.__exit__, None, None, None)
        warnings.filterwarnings("ignore", message="Skipping features without any observed values")

    def test_controlled_feature_changes_and_no_future_columns(self):
        self.assertEqual(sum(map(len, FEATURE_SETS["E0"])), 98)
        self.assertEqual(set(FEATURE_SETS["E0"][0]) - set(FEATURE_SETS["E4_no_kd"][0]), set(KD))
        self.assertEqual(set(FEATURE_SETS["E0"][0]) - set(FEATURE_SETS["E4_no_progress"][0]), set(PROGRESS))
        for numeric, categorical in FEATURE_SETS.values():
            names = numeric + categorical
            self.assertEqual(len(names), len(set(names)))
            self.assertFalse(set(names) & {"labelTWin", "matchId", "roundId", "tick", "winner"})
        self.assertEqual(len(candidate_specs()), 20)

    def test_spatial_groups_partition_all_fields_and_control_add_remove_pairs(self):
        groups = [n for names in SPATIAL_GROUPS.values() for n in names]
        self.assertEqual(len(groups), len(set(groups)))
        self.assertEqual(set(groups), set(SPATIAL_NUMERIC + SPATIAL_CATEGORICAL))
        self.assertEqual({g: len(n) for g, n in SPATIAL_GROUPS.items()},
                         dict(quality=7, c4=10, carrier=5, site=4, formation=4))
        specs = candidate_specs("spatial-ablation")
        self.assertEqual(len(specs), 22)
        self.assertEqual(len({s["id"] for s in specs}), 22)
        full = set(sum(FEATURE_SETS["E3"], ()))
        quality = set(sum(FEATURE_SETS["S_quality"], ()))
        for group, names in SPATIAL_GROUPS.items():
            if group == "quality":
                continue
            added = set(sum(FEATURE_SETS[f"S_only_{group}"], ()))
            removed = set(sum(FEATURE_SETS[f"S_without_{group}"], ()))
            self.assertEqual(added - quality, set(names))
            self.assertEqual(full - removed, set(names))
            self.assertTrue(quality <= added and quality <= removed)
        pairs = experiments.controlled_pairs({s["id"] for s in specs}, "spatial-ablation")
        self.assertEqual(len(pairs), 22)
        for model in ("logistic", "lightgbm"):
            self.assertIn((f"S_only_c4_{model}", f"S_quality_{model}"), pairs)
            self.assertIn((f"S_without_c4_{model}", f"E3_{model}"), pairs)
        with self.assertRaisesRegex(ValueError, "Unknown"):
            candidate_specs("unknown")

    def test_interactions_preserve_unknowns_and_use_only_current_inputs(self):
        frame = pd.DataFrame({"features.phase": ["live", "post-plant", "__missing__"],
            "features.t.alive": [5., 3., 2.], "features.ct.alive": [5., 1., 1.],
            "features.clock.bombRemainingSeconds": [np.nan, 12., 10.],
            "features.ct.defuserCount": [1., 0., np.nan]})
        phase = add_interactions(frame, "phase_alive")["interaction.postPlantAliveDifference"]
        kit = add_interactions(frame, "bomb_kit")["interaction.bombRemainingHasKit"]
        self.assertEqual(list(phase[:2]), [0., 2.])
        self.assertTrue(np.isnan(phase[2]))
        self.assertTrue(np.isnan(kit[0]) and np.isnan(kit[2]))
        self.assertEqual(kit[1], 0.)
        self.assertNotIn("interaction.bombRemainingHasKit", frame)

    def test_broad_endgame_replaces_old_flag_and_matches_evaluation_boundaries(self):
        cases = [(3, 4, "live", 1), (4, 3, "post-plant", 1), (2, 5, "live", 1),
                 (3, 3, "live", 1), (1, 5, "live", 1), (1, 1, "live", 1),
                 (4, 4, "live", 0), (5, 3, "live", 0), (0, 3, "live", 0),
                 (np.nan, 2, "live", 0), (2, np.nan, "live", 0), (2.5, 2, "live", 0),
                 (6, 1, "live", 0), (-1, 2, "live", 0), (1, 3, "ended", 0),
                 (1, 3, "__missing__", 0), (np.inf, 1, "live", 0)]
        meta = pd.DataFrame(cases, columns=["tAlive", "ctAlive", "phase", "expected"])
        frame = meta.rename(columns={"tAlive": "features.t.alive", "ctAlive": "features.ct.alive",
                                     "phase": "features.phase"})
        frame["features.baseline.isClutch"] = 1 - meta.expected
        derived = add_interactions(frame, "broad_endgame")["derived.isBroadEndgame"]
        np.testing.assert_array_equal(derived, meta.expected)
        np.testing.assert_array_equal(derived, slice_masks(meta)["broadEndgame"])
        for name, base in ENDGAME_BASES.items():
            self.assertEqual(set(FEATURE_SETS[base][0]) - set(FEATURE_SETS[name][0]),
                             {"features.baseline.isClutch"})
            self.assertEqual(FEATURE_SETS[base][1], FEATURE_SETS[name][1])
        specs = candidate_specs("broad-endgame")
        self.assertEqual(len(specs), 4)
        self.assertEqual(len(experiments.controlled_pairs({s["id"] for s in specs}, "broad-endgame")), 4)

    def test_grouped_fit_bundle_roundtrip_explanation_and_contract_rejection(self):
        frame, meta = training_fixture()
        splits = list(GroupKFold(3).split(frame, meta.label, meta.matchId))
        for train, held in splits:
            self.assertFalse(set(meta.iloc[train].matchId) & set(meta.iloc[held].matchId))
        specs = [s for s in candidate_specs() if s["id"] in (
            "E0_logistic", "E1_lightgbm", "E3_logistic", "E0_logistic_phase_alive", "E0_logistic_bomb_kit")]
        specs += [s for s in candidate_specs("spatial-ablation") if s["id"] in (
            "S_quality_logistic", "S_only_c4_logistic", "S_only_carrier_lightgbm", "S_without_formation_logistic")]
        specs += [s for s in candidate_specs("broad-endgame") if s["featureSet"] in ENDGAME_BASES]
        config = dict(contractVersion="win-spatial-v1", radii={"supportRadius": 5., "siteRadius": 10.})
        with tempfile.TemporaryDirectory() as temp, threadpool_limits(limits=1):
            for spec in specs:
                with self.subTest(candidate=spec["id"]):
                    model, selection, oof = experiments.fit_candidate(spec, frame, meta, splits, 42, 1)
                    self.assertEqual(set(selection), {"identity", "sigmoid", "isotonic"})
                    self.assertEqual(set(oof["fold"]), {0, 1, 2})
                    target = Path(temp) / spec["id"]
                    experiments.save_bundle(target, model, spec, selection, oof, frame.iloc[:3], config, "a"*64, .1)
                    runtime = load_experimental_runtime(target)
                    samples = experiments.samples_for(frame, spec, config)
                    actual, _ = experiments.runtime_predict(runtime, samples)
                    np.testing.assert_allclose(actual, model.predict_proba(frame)[:, 1], atol=1e-12, rtol=0)
                    explanation = grouped_explanation(model.base_model, spec, frame, meta.weight)
                    if spec["model"] == "logistic":
                        self.assertLess(explanation["maxReconstructionError"], 1e-8)
                    with self.assertRaisesRegex(ModelLoadError, "experimental feature contract"):
                        WinModelRuntime(target)
                    with self.assertRaisesRegex(RequestError, "contract"):
                        runtime.predict([{"features": samples[0]["features"]}], 1)
                    with self.assertRaisesRegex(RequestError, "forbidden"):
                        runtime.predict([{**samples[0], "labelTWin": 1}], 1)
                    if spec["featureSet"] in ENDGAME_BASES:
                        self.assertEqual(samples[0]["featureContract"], ENDGAME_CONTRACT)
                        with self.assertRaisesRegex(ModelLoadError, "contract"):
                            ExperimentalWinRuntime(target)
                        with self.assertRaisesRegex(RequestError, "contract"):
                            runtime.predict([{**samples[0], "featureContract": CONTRACT}], 1)
                        changed = frame.copy()
                        changed["features.baseline.isClutch"] = 999
                        changed["derived.isBroadEndgame"] = 999
                        np.testing.assert_array_equal(model.predict_proba(changed), model.predict_proba(frame))
                    if requires_spatial(spec["featureSet"]):
                        with self.assertRaisesRegex(RequestError, "spatial"):
                            runtime.predict([{**samples[0], "spatialConfigSha256": "bad"}], 1)
                        without_config = {k: v for k, v in samples[0].items() if k != "spatialConfigSha256"}
                        with self.assertRaisesRegex(RequestError, "spatial"):
                            runtime.predict([without_config], 1)
                        # Excluded geometry must not influence fit/prediction through extra frame columns.
                        changed = frame.copy()
                        included = set(sum(FEATURE_SETS[spec["featureSet"]], ()))
                        for name in set(SPATIAL_NUMERIC) - included:
                            changed[name] = 1e9
                        for name in set(SPATIAL_CATEGORICAL) - included:
                            changed[name] = "unseen-excluded-state"
                        np.testing.assert_array_equal(model.predict_proba(changed), model.predict_proba(frame))
                    with self.assertRaises(FileExistsError):
                        experiments.save_bundle(target, model, spec, selection, oof, frame.iloc[:3], config, "a"*64, .1)
                    (target / "baseline.joblib").write_bytes(b"corrupt")
                    with self.assertRaisesRegex(ModelLoadError, "SHA-256"):
                        load_experimental_runtime(target)

    def test_real_spatial_audit_join_refuses_missing_duplicate_or_changed_base(self):
        import sys
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from tools import test_audit_win_spatial
        fixture = test_audit_win_spatial.SpatialAuditTests()
        fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        records = v4.load_records(fixture.root / "base/samples.jsonl")
        config, receipts = join_spatial(copy.deepcopy(records), [fixture.root])
        self.assertEqual(receipts[0]["rows"], 1)
        self.assertEqual(config_sha256(config), config_sha256(copy.deepcopy(config)))
        with self.assertRaisesRegex(ValueError, "Missing spatial"):
            join_spatial(copy.deepcopy(records), [])
        with self.assertRaisesRegex(ValueError, "duplicate"):
            join_spatial(copy.deepcopy(records), [fixture.root, fixture.root])
        changed = copy.deepcopy(records)
        changed[0]["labelTWin"] = 0
        with self.assertRaisesRegex(ValueError, "base differs"):
            join_spatial(changed, [fixture.root])

    def test_plan_blocks_final_test_hash_drift_and_overwrite_before_training(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "plan.json"
            source = Path(temp) / "input.txt"
            source.write_text("source", encoding="utf-8")
            plan = dict(status="frozen", purpose="observed-development-only", featureContract=CONTRACT,
                finalTestEnabled=False, promotionEnabled=False, partitions={"train": ["a"], "development": ["b"],
                "legacy-validation": ["c"], "test": []}, candidates=candidate_specs(),
                featureSets={s["featureSet"]: {"numeric": list(FEATURE_SETS[s["featureSet"]][0]),
                    "categorical": list(FEATURE_SETS[s["featureSet"]][1])} for s in candidate_specs()},
                runtime=experiments.runtime_versions(), inputs={str(source): v4.sha256(source)})
            experiments.save(path, plan)
            digest = v4.sha256(path)
            self.assertEqual(experiments.load_plan(path, digest)["purpose"], "observed-development-only")
            changed_plan = copy.deepcopy(plan)
            changed_plan["featureSets"]["E0"]["numeric"].pop()
            changed_path = Path(temp) / "changed-plan.json"
            experiments.save(changed_path, changed_plan)
            with self.assertRaisesRegex(ValueError, "feature definitions"):
                experiments.load_plan(changed_path, v4.sha256(changed_path))
            with self.assertRaisesRegex(ValueError, "plan hash"):
                experiments.load_plan(path, "wrong")
            source.write_text("changed", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                experiments.load_plan(path, digest)
            plan["partitions"]["test"] = ["secret"]
            path.write_text(json.dumps(plan), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "final test"):
                experiments.load_plan(path, v4.sha256(path))
            with patch.object(experiments, "load_data") as load:
                with self.assertRaises(FileExistsError):
                    experiments.run(SimpleNamespace(output_dir=Path(temp)))
                load.assert_not_called()

    def test_data_loading_excludes_legacy_labels_and_preserves_development_roles(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name, ids in (("legacy", ["train", "legacy-validation"]), ("development", ["dev"])):
                directory = root / name
                directory.mkdir()
                experiments.save(directory / "manifest.json", {"matches": [
                    {"matchId": identity, "report": {"rowCount": 1}} for identity in ids]})
                with (directory / "samples.jsonl").open("w", encoding="utf-8") as stream:
                    for identity in ids:
                        # The invalid legacy label must never reach validation or fitting.
                        stream.write(json.dumps(dict(schemaVersion=4, semanticVersion=v4.SEMANTIC_VERSION,
                            matchId=identity, roundId="r1", tick=64, roundNumber=1, sampleWeight=1.,
                            labelTWin="forbidden" if identity == "legacy-validation" else int(identity == "train")))+"\n")
            plan = dict(legacyDirectory=str(root / "legacy"), developmentDirectory=str(root / "development"),
                        partitions={"train": ["train"], "development": ["dev"]}, spatialDirectories=[])
            with patch.object(experiments, "join_spatial", return_value=({}, [])) as join:
                _, meta, train_mask, _, audit = experiments.load_data(plan)
            self.assertEqual(list(meta.matchId), ["train", "dev"])
            self.assertEqual(list(train_mask), [True, False])
            self.assertEqual(list(meta.label), [1, 0])
            self.assertEqual(audit["data"]["train"]["matches"], 1)
            self.assertEqual(len(join.call_args.args[0]), 2)


if __name__ == "__main__":
    unittest.main()
