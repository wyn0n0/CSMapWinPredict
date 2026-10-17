#!/usr/bin/env python3
"""Freeze and run development-only M4 experiments; never promotes a model or uses final test."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
from importlib.metadata import version
import json
from pathlib import Path
import platform
import shutil
import sys
import time

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import joblib
import numpy as np
from sklearn.model_selection import GroupKFold
from threadpoolctl import threadpool_limits

from tools import train_win_baseline_v4 as v4
from tools.win_evaluation import BROAD_ENDGAME_DEFINITION, evaluation_report, export_coverage, metrics, plot_reliability
from tools.win_feature_sets import (
    CONTRACT, FEATURE_SETS, ExperimentalWinRuntime, candidate_specs, config_sha256,
    grouped_explanation, join_spatial, make_frames, make_model, requires_spatial, SPATIAL_GROUPS,
    ENDGAME_CONTRACT, INTERACTIONS, feature_contract, load_experimental_runtime,
)
from tools.win_inference_service import WinModelRuntime
from tools.win_model_bundle import CalibratedWinModel

ROOT = Path(__file__).resolve().parents[1]
SOURCES = ["tools/train_win_experiments.py", "tools/win_feature_sets.py", "tools/train_win_baseline_v4.py",
           "tools/win_evaluation.py", "tools/win_experiment.py", "tools/win_model_bundle.py",
           "tools/win_inference_service.py", "tools/audit_win_spatial.py", "tools/audit_win_v4.py",
           "requirements-train.txt"]


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def save(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")


def runtime_versions():
    return {"python": platform.python_version(), **{name: version(name) for name in (
        "numpy", "pandas", "scikit-learn", "lightgbm", "scipy", "joblib", "matplotlib", "threadpoolctl")}}


def freeze(args):
    if args.output_dir.exists():
        raise FileExistsError(args.output_dir)
    if not args.observed_development:
        raise ValueError("Explicit acknowledgement of observed development data is required")
    specs = candidate_specs(args.suite)
    feature_sets = {s["featureSet"]: {"numeric": list(FEATURE_SETS[s["featureSet"]][0]),
                    "categorical": list(FEATURE_SETS[s["featureSet"]][1])} for s in specs}
    legacy, development = read(args.legacy_dir / "manifest.json"), read(args.development_dir / "manifest.json")
    validation, _ = v4.load_validation_ids(args.validation_split)
    for manifest in (legacy, development):
        if (manifest.get("status"), manifest.get("schemaVersion"), manifest.get("semanticVersion"), manifest.get("sampleStrideTicks")) != (
                "complete", v4.SCHEMA_VERSION, v4.SEMANTIC_VERSION, 64):
            raise ValueError("Requires complete matching v4.2 fixed-stride data")
    legacy_ids = [m["matchId"] for m in legacy["matches"]]
    development_ids = [m["matchId"] for m in development["matches"]]
    if (len(set(legacy_ids)) != len(legacy_ids) or len(set(development_ids)) != len(development_ids)
            or set(legacy_ids) & set(development_ids) or not set(validation) <= set(legacy_ids)):
        raise ValueError("Duplicate, overlapping or missing partition members")
    train = sorted(set(legacy_ids) - set(validation))
    if len(train) < args.folds or args.folds < 2 or len(development_ids) < 2 or args.threads < 1:
        raise ValueError("Insufficient matches or invalid training budget")
    registry = read(args.development_registry)
    if {m["matchId"] for m in registry} != set(development_ids) or len(registry) != len(development_ids):
        raise ValueError("Development source registry does not match exported data")
    keys = [m["matchKey"] for m in registry]
    if not all(keys) or len(set(keys)) != len(keys):
        raise ValueError("Repeated or missing development source match identities")
    spatial_dirs = sorted(p.parent for p in args.spatial_root.glob("spatial-*/manifest.json"))
    spatial_ids = []
    for directory in spatial_dirs:
        spatial_ids.extend(m["matchId"] for m in read(directory / "base/manifest.json")["matches"])
    if set(spatial_ids) != set(train + development_ids) or len(spatial_ids) != len(set(spatial_ids)):
        raise ValueError("Spatial exports must cover exactly train and development")
    baseline = read(args.baseline_dir / "model-manifest.json")
    if (v4.sha256(args.legacy_dir / "samples.jsonl") != baseline["dataSha256"]
            or v4.sha256(args.legacy_dir / "manifest.json") != baseline["sourceManifestSha256"]):
        raise ValueError("Legacy dataset differs from the existing baseline source")
    paths = [ROOT / name for name in SOURCES] + [args.validation_split, args.development_registry]
    paths += [d / name for d in (args.legacy_dir, args.development_dir) for name in ("samples.jsonl", "manifest.json")]
    paths += [d / "manifest.json" for d in spatial_dirs]
    paths += [args.baseline_dir / name for name in ("model-manifest.json", "baseline.joblib", "feature-manifest.json", "report.json", "inference-fixtures.json")]
    plan = dict(status="frozen", purpose="observed-development-only", featureContract=CONTRACT,
        createdAtUtc=datetime.now(timezone.utc).isoformat(), finalTestEnabled=False, promotionEnabled=False,
        limitation="Legacy source match metadata remains incomplete; no chronological unseen-test or cross-event claim",
        adoptionCriteria=None, seed=42, folds=args.folds, threads=args.threads, bootstrapResamples=2000,
        weighting="original equal-round weights; no resampling or slice renormalization",
        calibration="fixed sigmoid for controlled comparisons; alternatives reported using training-only grouped OOF",
        selection="development weighted Log Loss ranks follow-up candidates only; no automatic adoption",
        partitions={"train": train, "development": sorted(development_ids), "legacy-validation": sorted(validation), "test": []},
        suite=args.suite, candidates=specs, featureSets=feature_sets,
        derivedFeatureDefinitions={"derived.isBroadEndgame": BROAD_ENDGAME_DEFINITION} if args.suite == "broad-endgame" else {},
        controlledPairs=controlled_pairs({s["id"] for s in specs}, args.suite),
        spatialGroups=SPATIAL_GROUPS if args.suite == "spatial-ablation" else None,
        runtime=runtime_versions(), legacyDirectory=str(args.legacy_dir.resolve()),
        developmentDirectory=str(args.development_dir.resolve()), baselineDirectory=str(args.baseline_dir.resolve()),
        spatialDirectories=[str(d.resolve()) for d in spatial_dirs],
        inputs={str(p.resolve()): v4.sha256(p) for p in paths},
        observedDevelopmentRegistry=[{**m, "role": "development", "unseen": False} for m in registry])
    args.output_dir.mkdir(parents=True)
    snapshot = args.output_dir / "source"
    for name in SOURCES:
        target = snapshot / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / name, target)
    save(args.output_dir / "plan.json", plan)
    print(json.dumps({"plan": str(args.output_dir / "plan.json"), "sha256": v4.sha256(args.output_dir / "plan.json")}, indent=2))


def load_plan(path, digest):
    if v4.sha256(path) != digest:
        raise ValueError("Frozen plan hash mismatch")
    plan = read(path)
    if (plan["status"], plan["purpose"], plan["featureContract"]) != ("frozen", "observed-development-only", CONTRACT):
        raise ValueError("Unsupported experiment plan")
    if plan["finalTestEnabled"] or plan["promotionEnabled"] or plan["partitions"]["test"]:
        raise ValueError("M4 development runner cannot consume or promote final test")
    roles = [set(plan["partitions"][name]) for name in ("train", "development", "legacy-validation")]
    if any(a & b for i, a in enumerate(roles) for b in roles[i + 1:]):
        raise ValueError("Overlapping experiment partitions")
    suite = plan.get("suite", "m4")
    if suite == "broad-endgame" and plan.get("derivedFeatureDefinitions") != {
            "derived.isBroadEndgame": BROAD_ENDGAME_DEFINITION}:
        raise ValueError("Frozen broad endgame definition drifted")
    specs = candidate_specs(suite)
    if plan["candidates"] != specs or plan["runtime"] != runtime_versions():
        raise ValueError("Candidate definitions or dependency versions drifted")
    expected_features = {s["featureSet"]: {"numeric": list(FEATURE_SETS[s["featureSet"]][0]),
                         "categorical": list(FEATURE_SETS[s["featureSet"]][1])} for s in specs}
    if plan["featureSets"] != expected_features:
        raise ValueError("Frozen feature definitions drifted")
    if "controlledPairs" in plan and plan["controlledPairs"] != [
            list(pair) for pair in controlled_pairs({s["id"] for s in specs}, suite)]:
        raise ValueError("Frozen controlled comparisons drifted")
    for name, expected in plan["inputs"].items():
        if v4.sha256(Path(name)) != expected:
            raise ValueError(f"Frozen input/source hash mismatch: {name}")
    return plan


def load_data(plan):
    records = []
    summaries = {}
    for directory, role in ((plan["legacyDirectory"], "train"), (plan["developmentDirectory"], "development")):
        manifest = read(Path(directory) / "manifest.json")
        ids = set(plan["partitions"][role])
        current = v4.load_records(Path(directory) / "samples.jsonl", ids)
        summary = v4.validate_records(current)
        expected = sum(m["report"]["rowCount"] for m in manifest["matches"] if m["matchId"] in ids)
        if summary["rows"] != expected or {r["matchId"] for r in current} != ids:
            raise ValueError("Loaded samples differ from frozen partitions/manifest")
        summaries[role] = summary
        records.extend(current)
    config, receipts = join_spatial(records, plan["spatialDirectories"])
    frame, meta = make_frames(records)
    train_mask = meta.matchId.isin(plan["partitions"]["train"]).to_numpy()
    return frame, meta, train_mask, config, dict(data=summaries, spatialAudits=receipts)


def fit_candidate(spec, frame, meta, splits, seed, threads):
    labels, weights = meta.label.to_numpy(int), meta.weight.to_numpy(float)
    raw, fold_ids = np.full(len(meta), np.nan), np.full(len(meta), -1, dtype=int)
    for fold, (train, held) in enumerate(splits):
        if set(meta.iloc[train].matchId) & set(meta.iloc[held].matchId):
            raise ValueError("A match crosses training OOF folds")
        model = make_model(spec, seed, threads)
        model.fit(frame.iloc[train], labels[train], model__sample_weight=weights[train])
        raw[held] = model.predict_proba(frame.iloc[held])[:, 1]
        fold_ids[held] = fold
    if not np.isfinite(raw).all():
        raise ValueError("Incomplete training OOF predictions")
    calibration_metrics, cross_predictions = {}, {}
    for method in v4.CALIBRATION_METHODS:
        cross = np.full(len(meta), np.nan)
        for fold in range(len(splits)):
            test = fold_ids == fold
            calibrator = v4.fit_calibrator(method, raw[~test], labels[~test], weights[~test])
            cross[test] = v4.apply_calibrator(method, calibrator, raw[test])
        calibration_metrics[method] = metrics(labels, cross, weights)
        cross_predictions[method] = cross
    model = make_model(spec, seed, threads)
    model.fit(frame, labels, model__sample_weight=weights)
    calibrator = v4.fit_calibrator("sigmoid", raw, labels, weights)
    return CalibratedWinModel(model, "sigmoid", calibrator), calibration_metrics, dict(raw=raw, fold=fold_ids, **cross_predictions)


def samples_for(frame, spec, config):
    numeric, categorical = FEATURE_SETS[spec["featureSet"]]
    context = {"featureContract": feature_contract(spec)}
    if requires_spatial(spec["featureSet"]):
        context["spatialConfigSha256"] = config_sha256(config)
    return [{**context, "features": v4.json_safe_features(row)} for _, row in frame.loc[:, list(numeric + categorical)].iterrows()]


def save_bundle(directory, model, spec, calibration, oof, fixtures_frame, config, plan_hash, fit_seconds):
    directory.mkdir()
    numeric, categorical = FEATURE_SETS[spec["featureSet"]]
    fixtures = samples_for(fixtures_frame, spec, config)
    for sample, probability in zip(fixtures, model.predict_proba(fixtures_frame)[:, 1], strict=True):
        sample["expectedTWin"] = float(probability)
    feature_manifest = dict(schemaVersion=4, semanticVersion=v4.SEMANTIC_VERSION, featureContract=feature_contract(spec),
        numeric=list(numeric), categorical=list(categorical), candidate=spec)
    if feature_contract(spec) == ENDGAME_CONTRACT:
        feature_manifest["derivedFeatures"] = {"derived.isBroadEndgame": BROAD_ENDGAME_DEFINITION}
    if requires_spatial(spec["featureSet"]):
        feature_manifest.update(spatialConfig=config, spatialConfigSha256=config_sha256(config))
    report = dict(selectedBaseline=dict(model=spec["model"], calibration="sigmoid", artifact="baseline.joblib"),
                  candidate=spec, planSha256=plan_hash, trainingOofCalibration=calibration,
                  trainingPreferredCalibration=min(calibration, key=lambda k: calibration[k]["logLoss"]),
                  fixedComparisonCalibration="sigmoid", fitSeconds=fit_seconds, runtime=runtime_versions())
    joblib.dump(model, directory / "baseline.joblib")
    np.savez_compressed(directory / "training-oof.npz", **oof)
    save(directory / "feature-manifest.json", feature_manifest)
    save(directory / "inference-fixtures.json", fixtures)
    save(directory / "report.json", report)
    save(directory / "model-manifest.json", dict(status="complete", schemaVersion=4,
        semanticVersion=v4.SEMANTIC_VERSION, featureContract=feature_contract(spec), planSha256=plan_hash,
        selectedModel=spec["model"], calibration="sigmoid",
        artifacts={p.name: v4.sha256(p) for p in directory.iterdir() if p.is_file()}))
    return report


def runtime_predict(runtime, samples):
    start = time.perf_counter()
    probability = []
    for offset in range(0, len(samples), 256):
        probability.extend(p["tWin"] for p in runtime.predict(samples[offset:offset+256], 256))
    elapsed = time.perf_counter() - start
    return np.array(probability), dict(batchSize=256, rows=len(samples), seconds=elapsed,
                                      millisecondsPerSample=1000 * elapsed / len(samples),
                                      scope="validation, frame construction and inference; excludes parsing, startup and JSON I/O")


def write_predictions(path, meta, predictions, hashes):
    with path.open("x", encoding="utf-8") as target:
        for i, row in enumerate(meta.itertuples(index=False)):
            target.write(json.dumps(dict(matchId=row.matchId, roundId=row.roundId, tick=int(row.tick),
                labelTWin=int(row.label), sampleWeight=float(row.weight),
                predictions={name: float(p[i]) for name, p in predictions.items()}, modelArtifactSha256=hashes),
                separators=(",", ":"), allow_nan=False) + "\n")


def controlled_pairs(predictions, suite="m4"):
    if suite == "broad-endgame":
        pairs = [("B0_logistic", "E0_logistic"), ("B_c4_logistic", "S_only_c4_logistic"),
                 ("B_c4_logistic", "B0_logistic"), ("S_only_c4_logistic", "E0_logistic")]
        return [(a, b) for a, b in pairs if a in predictions and b in predictions]
    if suite == "spatial-ablation":
        pairs = []
        for model in ("logistic", "lightgbm"):
            pairs.extend([(f"S_quality_{model}", f"E0_{model}"),
                          (f"E3_{model}", f"E0_{model}"),
                          (f"E3_{model}", f"S_quality_{model}")])
            for group in SPATIAL_GROUPS:
                if group != "quality":
                    pairs.extend([(f"S_only_{group}_{model}", f"S_quality_{model}"),
                                  (f"S_without_{group}_{model}", f"E3_{model}")])
        return [(a, b) for a, b in pairs if a in predictions and b in predictions]
    pairs = []
    for spec in candidate_specs(suite):
        name, family, base = spec["id"], spec["featureSet"], spec["model"]
        reference = f"E1_{base}" if family in ("E2", "E5_weapon_counts") else f"E0_{base}"
        if name != reference:
            pairs.append((name, reference))
    pairs.append(("E0_lightgbm", "E0_logistic"))
    return [(a, b) for a, b in pairs if a in predictions and b in predictions]


def report_markdown(report):
    lines = ["# M4 observed development experiment", "",
             "Development ranking only. No final test and no default-model switch.", "",
             "| Candidate | Fields | Log Loss | Brier | ECE-10 | Fit seconds | Inference ms/row |",
             "| --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for name in report["developmentRanking"]:
        m = report["evaluation"]["models"][name]["overall"]["metrics"]
        detail = report["candidates"][name]
        lines.append(f"| {name} | {detail['featureCount']} | {m['logLoss']:.6f} | {m['brierScore']:.6f} | {m['ece10']:.6f} | {detail['fitSeconds']:.1f} | {detail['inference']['millisecondsPerSample']:.4f} |")
    lines += ["", "All feature comparisons use the same 5 match-grouped training folds and fixed sigmoid calibration.",
              "Identity/isotonic alternatives are training-only diagnostics, not changes to these comparisons.",
              "Intervals are exploratory paired match bootstrap, without correction for multiple candidate searches.",
              "Full slices, calibration bins, raw predictions, per-match results and grouped explanations are in report.json.", ""]
    return "\n".join(lines)


def run(args):
    if args.output_dir.exists():
        raise FileExistsError(args.output_dir)
    plan = load_plan(args.plan, args.plan_sha256)
    args.output_dir.mkdir(parents=True)
    save(args.output_dir / "started.json", dict(planSha256=args.plan_sha256, runtime=runtime_versions(),
         createdAtUtc=datetime.now(timezone.utc).isoformat(), inputVerification="passed"))
    print("Loading and auditing fixed train/development data", flush=True)
    frame, meta, train_mask, config, audit = load_data(plan)
    train_x, dev_x = frame.loc[train_mask].reset_index(drop=True), frame.loc[~train_mask].reset_index(drop=True)
    train_meta, dev_meta = meta.loc[train_mask].reset_index(drop=True), meta.loc[~train_mask].reset_index(drop=True)
    del frame, meta
    splits = list(GroupKFold(n_splits=plan["folds"]).split(train_x, train_meta.label, train_meta.matchId))
    save(args.output_dir / "data-audit.json", audit)
    save(args.output_dir / "folds.json", [{"fold": i, "training": sorted(set(train_meta.iloc[a].matchId)),
         "heldOut": sorted(set(train_meta.iloc[b].matchId))} for i, (a, b) in enumerate(splits)])
    train_meta[["matchId", "roundId", "tick", "label", "weight"]].to_json(args.output_dir / "training-rows.jsonl", orient="records", lines=True, double_precision=15)
    dev_manifest = read(Path(plan["developmentDirectory"]) / "manifest.json")
    predictions, raw_predictions, details, hashes = {}, {}, {}, {}
    baseline = WinModelRuntime(Path(plan["baselineDirectory"]))
    baseline_samples = [{"features": v4.json_safe_features(row)} for _, row in dev_x[list(v4.NUMERIC_FEATURES + v4.CATEGORICAL_FEATURES)].iterrows()]
    predictions["existing_default"], baseline_cost = runtime_predict(baseline, baseline_samples)
    hashes["existing_default"] = baseline.info["artifactSha256"]
    del baseline_samples
    start = time.perf_counter()
    with threadpool_limits(limits=plan["threads"]):
        for spec in plan["candidates"]:
            name = spec["id"]
            print(f"Training {name}", flush=True)
            fit_start = time.perf_counter()
            model, calibration, oof = fit_candidate(spec, train_x, train_meta, splits, plan["seed"], plan["threads"])
            fit_seconds = time.perf_counter() - fit_start
            directory = args.output_dir / name
            indices = np.linspace(0, len(dev_x)-1, 3, dtype=int)
            bundle_report = save_bundle(directory, model, spec, calibration, oof, dev_x.iloc[indices], config, args.plan_sha256, fit_seconds)
            runtime = load_experimental_runtime(directory)
            samples = samples_for(dev_x, spec, config)
            probability, cost = runtime_predict(runtime, samples)
            expected = model.predict_proba(dev_x)[:, 1]
            parity = float(np.max(np.abs(probability - expected)))
            if parity > 1e-12:
                raise ValueError("Training/inference prediction mismatch")
            predictions[name] = probability
            raw_predictions[name] = model.base_model.predict_proba(dev_x)[:, 1]
            hashes[name] = runtime.info["artifactSha256"]
            numeric, categorical = FEATURE_SETS[spec["featureSet"]]
            derived_count = len(INTERACTIONS[spec["interaction"]]) if spec["interaction"] else 0
            details[name] = dict(**bundle_report, featureCount=len(numeric)+len(categorical),
                derivedFeatureCount=derived_count, modelFeatureCount=len(numeric)+len(categorical)+derived_count,
                inference=cost, inferenceMaxAbsoluteDifference=parity,
                explanation=grouped_explanation(model.base_model, spec, dev_x, dev_meta.weight.to_numpy()),
                rawDevelopmentMetrics=metrics(dev_meta.label, raw_predictions[name], dev_meta.weight))
            save(args.output_dir / f"{name}-verification.json", details[name])
            print(json.dumps(dict(candidate=name, fitSeconds=round(fit_seconds, 2),
                development=metrics(dev_meta.label, probability, dev_meta.weight))), flush=True)
    evaluation = evaluation_report(dev_meta, predictions, reference="existing_default", seed=plan["seed"], resamples=plan["bootstrapResamples"])
    pairs = {}
    for candidate, reference in controlled_pairs(predictions, plan.get("suite", "m4")):
        pair = evaluation_report(dev_meta, {reference: predictions[reference], candidate: predictions[candidate]},
                                 reference=reference, seed=plan["seed"], resamples=plan["bootstrapResamples"])
        pairs[candidate + " minus " + reference] = pair["pairedDifferences"][candidate]
    ranking = sorted(details, key=lambda n: evaluation["models"][n]["overall"]["metrics"]["logLoss"])
    report = dict(status="complete", purpose=plan["purpose"], suite=plan.get("suite", "m4"), planSha256=args.plan_sha256,
        promotionEnabled=False, finalTestObserved=False, candidateAdopted=None,
        data=audit["data"], exportCoverage=export_coverage(dev_manifest, set(plan["partitions"]["development"])),
        developmentRanking=ranking, candidates=details, evaluation=evaluation, controlledPairs=pairs,
        existingDefaultInference=baseline_cost, runtime=runtime_versions(), wallSeconds=time.perf_counter()-start,
        E0DefaultMaxAbsoluteDifference=float(np.max(np.abs(predictions["E0_logistic"] - predictions["existing_default"]))),
        deferred={"E5_fineEquipment": "Smoke/flash/fire inventory is not in v4 or spatial-v1; requires a separately validated as-of exporter",
                  "E6_navigation": "No verified navigation contract or dataset; Euclidean distance is not reachability"})
    save(args.output_dir / "report.json", report)
    (args.output_dir / "report.md").write_text(report_markdown(report), encoding="utf-8")
    write_predictions(args.output_dir / "development-predictions.jsonl", dev_meta, predictions, hashes)
    write_predictions(args.output_dir / "development-raw-predictions.jsonl", dev_meta, raw_predictions, {n: hashes[n] for n in raw_predictions})
    plot_reliability({"models": {n: evaluation["models"][n] for n in ("existing_default", "E0_logistic", ranking[0])}}, args.output_dir / "reliability.png")
    save(args.output_dir / "completion.json", dict(status="complete", planSha256=args.plan_sha256,
        artifacts={p.relative_to(args.output_dir).as_posix(): v4.sha256(p) for p in args.output_dir.rglob("*") if p.is_file()}))
    print(json.dumps(dict(output=str(args.output_dir), developmentLeader=ranking[0], defaultModelChanged=False), indent=2))


def predict(args):
    if args.output.exists():
        raise FileExistsError(args.output)
    runtime = load_experimental_runtime(args.model_dir)
    with args.input.open(encoding="utf-8") as source, args.output.open("x", encoding="utf-8") as target:
        for line in source:
            sample = json.loads(line)
            prediction = runtime.predict([sample], 1)[0]
            target.write(json.dumps(dict(**prediction, modelArtifactSha256=runtime.info["artifactSha256"]), allow_nan=False)+"\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    freeze_parser = sub.add_parser("freeze")
    for flag in ("legacy-dir", "development-dir", "development-registry", "validation-split", "baseline-dir", "spatial-root", "output-dir"):
        freeze_parser.add_argument("--"+flag, required=True, type=Path)
    freeze_parser.add_argument("--observed-development", action="store_true")
    freeze_parser.add_argument("--suite", choices=("m4", "spatial-ablation", "broad-endgame"), default="m4")
    freeze_parser.add_argument("--folds", type=int, default=5)
    freeze_parser.add_argument("--threads", type=int, default=4)
    run_parser = sub.add_parser("run")
    run_parser.add_argument("--plan", required=True, type=Path)
    run_parser.add_argument("--plan-sha256", required=True)
    run_parser.add_argument("--output-dir", required=True, type=Path)
    predict_parser = sub.add_parser("predict")
    for flag in ("model-dir", "input", "output"):
        predict_parser.add_argument("--"+flag, required=True, type=Path)
    args = parser.parse_args()
    {"freeze": freeze, "run": run, "predict": predict}[args.command](args)


if __name__ == "__main__":
    main()
