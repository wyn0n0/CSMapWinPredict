"""Fixed A/B/C C4 controls on the original training matches, with nested grouped CV."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import sys
import time
import warnings

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import joblib
import numpy as np
from sklearn.exceptions import ConvergenceWarning
from sklearn.model_selection import GroupKFold
from sklearn.preprocessing import FunctionTransformer
from threadpoolctl import threadpool_limits

from tools import train_win_baseline_v4 as v4
from tools import train_win_experiments as experiments
from tools.win_evaluation import evaluation_report, export_coverage, plot_reliability
from tools.win_feature_sets import FEATURE_SETS, SPATIAL_GROUPS, candidate_specs, config_sha256, join_spatial, make_frames
from tools.win_inference_service import ModelLoadError, RequestError, WinModelRuntime

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = "win-c4-controls-v1"
PARENT_SHA256 = "5af45ee87777d4edcc7ae4645b16de630353e0a77df6f0ac9ce4b0b64dd4317d"
PARENT = ROOT / ".artifacts/win-broad-endgame-20261015-r1/frozen/plan.json"
CANDIDATES = ("A_base", "B_state", "C_distance")
GEOMETRY = tuple(n for n in SPATIAL_GROUPS["c4"] if "Distance" in n and not n.endswith("Count"))
SOURCES = experiments.SOURCES + ["tools/train_win_c4_controls.py", "tools/test_win_c4_controls.py",
                               "docs/plans/win-c4-controls-20261019.md"]
read, save = experiments.read, experiments.save


def spec_for(candidate):
    if candidate not in CANDIDATES:
        raise ValueError("Unknown C4 control")
    name = "B0" if candidate == "A_base" else "B_c4"
    return next(s for s in candidate_specs("broad-endgame") if s["featureSet"] == name)


def mask_geometry(frame):
    """Preserve all NaN positions, but remove every finite C4 distance magnitude."""
    result = frame.copy()
    result.loc[:, list(GEOMETRY)] = result.loc[:, list(GEOMETRY)].where(
        result.loc[:, list(GEOMETRY)].isna(), 0.0)
    return result


def fit_control(candidate, frame, meta, splits, seed, threads):
    projected = mask_geometry(frame) if candidate == "B_state" else frame
    model, diagnostics, oof = experiments.fit_candidate(
        spec_for(candidate), projected, meta, splits, seed, threads)
    if candidate == "B_state":
        model.base_model.steps.insert(0, ("c4_presence_only", FunctionTransformer(mask_geometry, validate=False)))
    return model, diagnostics, oof


def grouped_splits(meta, folds):
    splits = list(GroupKFold(folds).split(meta, meta.label, meta.matchId))
    visits = np.zeros(len(meta), dtype=int)
    for training, held in splits:
        if set(meta.iloc[training].matchId) & set(meta.iloc[held].matchId):
            raise ValueError("A match crosses a CV fold")
        visits[held] += 1
    if not np.all(visits == 1):
        raise ValueError("Incomplete or duplicate held-out coverage")
    return splits


def fold_members(meta, splits):
    return [{"fold": i, "training": sorted(set(meta.iloc[a].matchId)),
             "heldOut": sorted(set(meta.iloc[b].matchId))} for i, (a, b) in enumerate(splits)]


def samples_for(frame, candidate, config):
    samples = experiments.samples_for(frame, spec_for(candidate), config)
    for sample in samples:
        sample["featureContract"] = CONTRACT
    return samples


class ControlsRuntime(WinModelRuntime):
    FEATURE_CONTRACT = CONTRACT

    def __init__(self, directory):
        manifest = read(Path(directory) / "feature-manifest.json")
        self.candidate = manifest["candidate"]
        numeric, categorical = FEATURE_SETS[spec_for(self.candidate)["featureSet"]]
        if manifest["numeric"] != list(numeric) or manifest["categorical"] != list(categorical):
            raise ModelLoadError("C4 control feature definitions differ")
        self.spatial_hash = None
        if self.candidate != "A_base":
            self.spatial_hash = config_sha256(manifest["spatialConfig"])
            if manifest["spatialConfigSha256"] != self.spatial_hash:
                raise ModelLoadError("C4 control spatial configuration differs")
        super().__init__(Path(directory))

    def _flatten_sample(self, sample, index):
        if not isinstance(sample, dict) or sample.get("featureContract") != CONTRACT:
            raise RequestError("feature_contract_mismatch", "C4 control contract required")
        if self.spatial_hash and sample.get("spatialConfigSha256") != self.spatial_hash:
            raise RequestError("spatial_config_mismatch", "C4 control spatial configuration required")
        return super()._flatten_sample(sample, index)


def save_bundle(directory, candidate, model, diagnostics, oof, frame, config, plan_hash):
    directory.mkdir(parents=True, exist_ok=False)
    numeric, categorical = FEATURE_SETS[spec_for(candidate)["featureSet"]]
    fixtures = samples_for(frame, candidate, config)
    for sample, p in zip(fixtures, model.predict_proba(frame)[:, 1], strict=True):
        sample["expectedTWin"] = float(p)
    manifest = dict(schemaVersion=4, semanticVersion=v4.SEMANTIC_VERSION, featureContract=CONTRACT,
                    candidate=candidate, numeric=list(numeric), categorical=list(categorical),
                    spatialConfig=config, spatialConfigSha256=config_sha256(config),
                    geometryPolicy="finite-to-zero; preserve NaN" if candidate == "B_state" else "unchanged")
    joblib.dump(model, directory / "baseline.joblib")
    np.savez_compressed(directory / "training-oof.npz", **oof)
    save(directory / "feature-manifest.json", manifest)
    save(directory / "inference-fixtures.json", fixtures)
    save(directory / "report.json", dict(candidate=candidate, planSha256=plan_hash,
        selectedBaseline=dict(model="logistic", calibration="sigmoid", artifact="baseline.joblib"),
        trainingOnlyCalibrationDiagnostics=diagnostics, promotionEnabled=False))
    save(directory / "model-manifest.json", dict(status="complete", schemaVersion=4,
        semanticVersion=v4.SEMANTIC_VERSION, featureContract=CONTRACT, selectedModel="logistic",
        calibration="sigmoid", planSha256=plan_hash,
        artifacts={p.name: v4.sha256(p) for p in directory.iterdir()}))


def freeze(output):
    if output.exists():
        raise FileExistsError(output)
    if v4.sha256(PARENT) != PARENT_SHA256:
        raise ValueError("Parent experiment changed")
    parent = read(PARENT)
    train = parent["partitions"]["train"]
    excluded = parent["partitions"]["development"] + parent["partitions"]["legacy-validation"]
    if len(train) != 79 or len(set(train)) != 79 or set(train) & set(excluded):
        raise ValueError("Original training partition changed")
    spatial, members = [], []
    for name in parent["spatialDirectories"]:
        directory = Path(name)
        ids = [m["matchId"] for m in read(directory / "base/manifest.json")["matches"]]
        if set(ids) & set(train):
            if not set(ids) <= set(train):
                raise ValueError("Spatial shard mixes training and excluded matches")
            spatial.append(directory)
            members.extend(ids)
    if sorted(members) != sorted(train):
        raise ValueError("Training spatial membership differs")
    legacy = Path(parent["legacyDirectory"])
    bound = [legacy / "samples.jsonl", legacy / "manifest.json"] + [d / "manifest.json" for d in spatial]
    for path in bound:
        if v4.sha256(path) != parent["inputs"][str(path.resolve())]:
            raise ValueError(f"Parent-bound input changed: {path}")
    paths = bound + [PARENT] + [ROOT / n for n in SOURCES]
    # Bind each raw/base/companion file, as well as the parent-bound shard manifest.
    paths += [d / n for d in spatial for n in read(d / "manifest.json")["files"]]
    baseline_dirs = [Path(parent["baselineDirectory"]),
                     ROOT / ".artifacts/win-broad-endgame-20261015-r1/results/B_c4_logistic",
                     ROOT / "endgame_prediction/runs/20261017-endgame-only-r1/model"]
    preserved = {str(d / n): v4.sha256(d / n) for d in baseline_dirs
                 for n in ("baseline.joblib", "model-manifest.json")}
    plan = dict(status="frozen", featureContract=CONTRACT, purpose="training-only-fixed-controls",
        createdAtUtc=datetime.now(timezone.utc).isoformat(), candidates=list(CANDIDATES),
        geometryFields=list(GEOMETRY), seed=42, threads=4, outerFolds=5, innerFolds=5,
        bootstrapResamples=2000, calibration="fixed sigmoid fitted on inner training OOF only",
        evaluation="nested match-grouped outer OOF; no development or legacy-validation scoring",
        weighting="original equal-round weights; unchanged after slicing",
        interpretation="B-A: added state/count/missingness; C-B: C4 magnitudes conditional on identical missingness",
        followupRule="Prefer B over C unless C-B overall Log Loss 95% upper bound is below zero; inspect key slices; no automatic adoption",
        finalTestEnabled=False, promotionEnabled=False, parentPlanSha256=PARENT_SHA256,
        partitions={"train": train, "excludedObservedDevelopment": parent["partitions"]["development"],
                    "excludedLegacyValidation": parent["partitions"]["legacy-validation"], "test": []},
        runtime=experiments.runtime_versions(), legacyDirectory=str(legacy),
        spatialDirectories=[str(d) for d in spatial], preservedModels=preserved,
        inputs={str(p.resolve()): v4.sha256(p) for p in dict.fromkeys(paths)},
        budget=dict(estimatedMinutes=15, maximumOutputGiB=1, freeBytes=shutil.disk_usage(ROOT).free))
    if plan["budget"]["freeBytes"] < 2 * 1024**3:
        raise ValueError("Less than 2 GiB available for new experiment")
    output.mkdir(parents=True)
    for name in SOURCES:
        target = output / "source" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / name, target)
    save(output / "plan.json", plan)
    print(json.dumps(dict(plan=str(output / "plan.json"), sha256=v4.sha256(output / "plan.json")), indent=2))


def load_plan(path, digest):
    if v4.sha256(path) != digest:
        raise ValueError("Frozen plan hash mismatch")
    plan = read(path)
    if (plan["status"], plan["purpose"], plan["featureContract"]) != (
            "frozen", "training-only-fixed-controls", CONTRACT):
        raise ValueError("Unsupported C4 controls plan")
    if plan["promotionEnabled"] or plan["finalTestEnabled"] or plan["partitions"]["test"]:
        raise ValueError("Final test/promotion is forbidden")
    if plan["candidates"] != list(CANDIDATES) or plan["geometryFields"] != list(GEOMETRY):
        raise ValueError("C4 controls changed")
    if plan["runtime"] != experiments.runtime_versions():
        raise ValueError("Runtime changed")
    for name, expected in {**plan["inputs"], **plan["preservedModels"]}.items():
        if v4.sha256(Path(name)) != expected:
            raise ValueError(f"Frozen file hash mismatch: {name}")
    return plan


def load_training(plan):
    ids = set(plan["partitions"]["train"])
    excluded = set(plan["partitions"]["excludedObservedDevelopment"] + plan["partitions"]["excludedLegacyValidation"])
    if ids & excluded:
        raise ValueError("Training intersects an excluded partition")
    directory = Path(plan["legacyDirectory"])
    records = v4.load_records(directory / "samples.jsonl", ids)
    summary = v4.validate_records(records)
    manifest = read(directory / "manifest.json")
    expected = sum(m["report"]["rowCount"] for m in manifest["matches"] if m["matchId"] in ids)
    if summary["rows"] != expected or {r["matchId"] for r in records} != ids:
        raise ValueError("Training rows differ from frozen membership")
    config, receipts = join_spatial(records, plan["spatialDirectories"])
    frame, meta = make_frames(records)
    return frame, meta, config, dict(training=summary, spatialAudits=receipts,
                                    exportCoverage=export_coverage(manifest, ids))


def run(plan_path, digest, output):
    if output.exists():
        raise FileExistsError(output)
    plan = load_plan(plan_path, digest)
    output.mkdir(parents=True)
    start = time.perf_counter()
    save(output / "started.json", dict(planSha256=digest, createdAtUtc=datetime.now(timezone.utc).isoformat()))
    print("Auditing only the 79 training matches", flush=True)
    frame, meta, config, audit = load_training(plan)
    outer = grouped_splits(meta, plan["outerFolds"])
    save(output / "data-audit.json", audit)
    folds = fold_members(meta, outer)
    inner_splits = []
    for i, (training, held) in enumerate(outer):
        inner_meta = meta.iloc[training].reset_index(drop=True)
        inner = grouped_splits(inner_meta, plan["innerFolds"])
        inner_splits.append(inner)
        folds[i]["innerFolds"] = fold_members(inner_meta, inner)
    save(output / "folds.json", folds)
    predictions, raw_predictions, details, bundle_hashes = {}, {}, {}, {}
    with threadpool_limits(limits=plan["threads"]), warnings.catch_warnings():
        warnings.simplefilter("error", ConvergenceWarning)
        for candidate in CANDIDATES:
            p, raw = np.full(len(meta), np.nan), np.full(len(meta), np.nan)
            candidate_start = time.perf_counter()
            parity, outer_hashes = [], []
            for fold, (training, held) in enumerate(outer):
                print(f"{candidate}: outer fold {fold + 1}/{len(outer)}", flush=True)
                x, m = frame.iloc[training].reset_index(drop=True), meta.iloc[training].reset_index(drop=True)
                model, diagnostics, oof = fit_control(candidate, x, m, inner_splits[fold], plan["seed"], plan["threads"])
                bundle = output / candidate / f"outer-{fold}"
                save_bundle(bundle, candidate, model, diagnostics, oof, x.iloc[[0, len(x)//2, -1]], config, digest)
                runtime = ControlsRuntime(bundle)
                actual, _ = experiments.runtime_predict(runtime, samples_for(frame.iloc[held], candidate, config))
                difference = float(np.max(np.abs(actual - model.predict_proba(frame.iloc[held])[:, 1])))
                if difference > 1e-12:
                    raise ValueError("Saved outer-fold inference differs")
                parity.append(difference)
                p[held] = actual
                raw[held] = model.base_model.predict_proba(frame.iloc[held])[:, 1]
                outer_hashes.append(runtime.info["artifactSha256"])
            if not np.isfinite(p).all():
                raise ValueError("Incomplete outer predictions")
            print(f"{candidate}: fitting final training-only bundle", flush=True)
            model, diagnostics, oof = fit_control(candidate, frame, meta, outer, plan["seed"], plan["threads"])
            bundle = output / candidate / "full-training"
            save_bundle(bundle, candidate, model, diagnostics, oof, frame.iloc[[0, len(frame)//2, -1]], config, digest)
            runtime = ControlsRuntime(bundle)
            predictions[candidate], raw_predictions[candidate] = p, raw
            bundle_hashes[candidate] = dict(outerFoldArtifacts=outer_hashes, fullTrainingArtifact=runtime.info["artifactSha256"])
            details[candidate] = dict(seconds=time.perf_counter()-candidate_start,
                inferenceMaxAbsoluteDifference=max(parity), rawInputFields=runtime.info["featureCount"],
                encodedFields=len(model.base_model.named_steps["model"].coef_[0]),
                encodedFeatureNames=list(model.base_model.named_steps["preprocessor"].get_feature_names_out()))
            print(json.dumps(dict(candidate=candidate, outerOOF=experiments.metrics(meta.label, p, meta.weight))), flush=True)
    print("Evaluating paired outer-fold predictions", flush=True)
    evaluation = evaluation_report(meta, predictions, reference="A_base", seed=plan["seed"], resamples=plan["bootstrapResamples"])
    cb = evaluation_report(meta, {k: predictions[k] for k in ("B_state", "C_distance")},
                           reference="B_state", seed=plan["seed"], resamples=plan["bootstrapResamples"])
    report = dict(status="complete", purpose=plan["purpose"], planSha256=digest,
        evaluationScope="nested grouped OOF on original training matches; not an unseen final test",
        intervalLimitation="Conditional match bootstrap of fixed OOF predictions; folds share training data; no retraining within bootstrap",
        defaultModelChanged=False, finalTestObserved=False, developmentScored=False,
        data=audit["training"], candidates=details, evaluation=evaluation,
        distanceIncrement=cb["pairedDifferences"]["C_distance"], seconds=time.perf_counter()-start)
    save(output / "report.json", report)
    fold_ids = np.empty(len(meta), dtype=int)
    for i, (_, held) in enumerate(outer):
        fold_ids[held] = i
    with (output / "outer-predictions.jsonl").open("x", encoding="utf-8") as stream:
        for i, row in enumerate(meta.to_dict("records")):
            stream.write(json.dumps(dict(**v4.json_safe_features(row), outerFold=int(fold_ids[i]),
                predictions={k: float(p[i]) for k, p in predictions.items()},
                rawPredictions={k: float(p[i]) for k, p in raw_predictions.items()}), allow_nan=False)+"\n")
    save(output / "model-hashes.json", bundle_hashes)
    plot_reliability(evaluation, output / "reliability.png")
    # Recheck bound input, source, and old model bytes after the run.
    load_plan(plan_path, digest)
    save(output / "completion.json", dict(status="complete", planSha256=digest,
        artifacts={p.relative_to(output).as_posix(): v4.sha256(p) for p in output.rglob("*") if p.is_file()}))
    print(json.dumps(dict(output=str(output), seconds=time.perf_counter()-start, defaultModelChanged=False)), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    freezing = commands.add_parser("freeze")
    freezing.add_argument("--output-dir", required=True, type=Path)
    running = commands.add_parser("run")
    running.add_argument("--plan", required=True, type=Path)
    running.add_argument("--plan-sha256", required=True)
    running.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    if args.command == "freeze":
        freeze(args.output_dir)
    else:
        run(args.plan, args.plan_sha256, args.output_dir)


if __name__ == "__main__":
    # Keep serialized FunctionTransformer callables importable in another process.
    from tools.train_win_c4_controls import main as module_main
    module_main()
