#!/usr/bin/env python3
"""Train, calibrate, and evaluate schema-v4.2 Mirage round-win models."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import tempfile
from importlib.metadata import version
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import joblib
import numpy as np
import pandas as pd
import sklearn
from sklearn.compose import ColumnTransformer
from sklearn.impute import SimpleImputer
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import GroupKFold
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import OneHotEncoder, StandardScaler

if __package__ in (None, ""):
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools.win_model_bundle import CalibratedWinModel, _logit
from tools.win_experiment import load_frozen_experiment
from tools.win_evaluation import (
    SMALL_FEATURES, calibration_bins, ece, evaluation_report, export_coverage, metrics,
    per_match_metrics, plot_reliability, reference_predictions, slices,
)


SCHEMA_VERSION = 4
SEMANTIC_VERSION = "mirage-semantics-v4.2"
CALIBRATION_METHODS = ("identity", "sigmoid", "isotonic")

CATEGORICAL_FEATURES = (
    "mapName", "features.phase", "features.bomb.site", "features.bomb.region",
    "features.baseline.bombState", "features.baseline.previousBombState",
    "features.clock.clockSource", "features.clock.pauseContext", "features.quality.quality",
)

NUMERIC_FEATURES = (
    "roundNumber",
    "features.clock.liveElapsedSeconds", "features.clock.roundRemainingSeconds",
    "features.clock.bombRemainingSeconds", "features.clock.defuseRemainingSeconds",
    "features.scoreT", "features.scoreCT", "features.consecutiveLossesT",
    "features.consecutiveLossesCT", "features.bomb.hasCarrier", "features.bomb.hasDefuser",
    "features.t.alive", "features.t.totalHealth", "features.t.totalKills",
    "features.t.totalDeaths", "features.t.equipmentKnownPlayers", "features.t.totalMoney",
    "features.t.totalArmor", "features.t.helmetCount", "features.t.defuserCount",
    "features.t.equipmentValue", "features.t.grenadeCount", "features.t.rifleCount",
    "features.t.sniperCount", "features.ct.alive", "features.ct.totalHealth",
    "features.ct.totalKills", "features.ct.totalDeaths", "features.ct.equipmentKnownPlayers",
    "features.ct.totalMoney", "features.ct.totalArmor", "features.ct.helmetCount",
    "features.ct.defuserCount", "features.ct.equipmentValue", "features.ct.grenadeCount",
    "features.ct.rifleCount", "features.ct.sniperCount",
    "features.baseline.scoreDifference", "features.baseline.lossStreakDifference",
    "features.baseline.bombStateChangeCount", "features.baseline.secondsSinceBombStateChange",
    "features.baseline.hasExplosionTimer", "features.baseline.hasDefuseTimer",
    "features.baseline.bombWasDropped", "features.baseline.bombWasPlanting",
    "features.baseline.bombWasPlanted", "features.baseline.bombWasDefusing",
    "features.baseline.tPositionDispersion", "features.baseline.ctPositionDispersion",
    "features.baseline.positionDispersionDifference", "features.baseline.tPositionDataMissing",
    "features.baseline.ctPositionDataMissing", "features.baseline.nearestOpponentDistance",
    "features.baseline.nearestOpponentDistanceMissing", "features.baseline.tMeanDistanceToSiteA",
    "features.baseline.tMeanDistanceToSiteB", "features.baseline.ctMeanDistanceToSiteA",
    "features.baseline.ctMeanDistanceToSiteB", "features.baseline.tMinDistanceToSiteA",
    "features.baseline.tMinDistanceToSiteB", "features.baseline.ctMinDistanceToSiteA",
    "features.baseline.ctMinDistanceToSiteB", "features.baseline.tClosestSiteDistance",
    "features.baseline.ctClosestSiteDistance", "features.baseline.siteAProximityDifference",
    "features.baseline.siteBProximityDifference", "features.baseline.equipmentValueDifference",
    "features.baseline.moneyDifference", "features.baseline.armorDifference",
    "features.baseline.helmetCountDifference", "features.baseline.defuserCountDifference",
    "features.baseline.grenadeCountDifference", "features.baseline.rifleCountDifference",
    "features.baseline.sniperCountDifference", "features.baseline.healthDifference",
    "features.baseline.aliveDifference", "features.baseline.totalAlive",
    "features.baseline.tMeanHealth", "features.baseline.ctMeanHealth",
    "features.baseline.isClutch", "features.baseline.tEquipmentCoverage",
    "features.baseline.ctEquipmentCoverage", "features.baseline.equipmentCoverageDifference",
    "features.quality.tMembers", "features.quality.ctMembers", "features.quality.lifeKnown",
    "features.quality.aliveKnown", "features.quality.alivePositionKnown",
    "features.quality.aliveEquipmentKnown",
)


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--comparison", required=True, type=Path)
    parser.add_argument("--validation-split", required=True, type=Path)
    parser.add_argument("--v3-predictions", type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--folds", type=int, default=5)
    parser.add_argument("--evaluate-predictions", type=Path,
                        help="Evaluate saved predictions without retraining the existing models")
    parser.add_argument("--probability-column", action="append",
                        help="Saved prediction column to evaluate; repeat for paired comparisons")
    parser.add_argument("--reference-column", help="Paired comparison reference; defaults to the first saved prediction column")
    parser.add_argument("--bootstrap-resamples", type=int, default=2000)
    parser.add_argument("--experiment", type=Path, help="Frozen new-data experiment.json")
    parser.add_argument("--experiment-sha256", help="Expected hash printed when the experiment was frozen")
    parser.add_argument("--evaluation-role", choices=("development", "test"), default="development")
    parser.add_argument("--candidate-lock", type=Path,
                        help="Required for final test; binds one development-selected model and probability column")
    parser.add_argument("--candidate-artifact", type=Path,
                        help="Final-test model artifact to verify against the candidate lock; never loaded or fitted")
    return parser.parse_args()


def get_path(record: dict[str, Any], path: str):
    value: Any = record
    for part in path.split("."):
        if not isinstance(value, dict):
            return None
        value = value.get(part)
    return value


def sha256(path: Path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def validate_sources(input_path: Path, manifest_path: Path, comparison_path: Path):
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    comparison = json.loads(comparison_path.read_text(encoding="utf-8-sig"))
    if (manifest.get("status"), manifest.get("schemaVersion"), manifest.get("semanticVersion")) != (
            "complete", SCHEMA_VERSION, SEMANTIC_VERSION):
        raise ValueError("Manifest is incomplete or has the wrong semantic version")
    match_ids = [m["matchId"] for m in manifest["matches"]]
    if not match_ids or len(match_ids) != len(set(match_ids)):
        raise ValueError("Manifest contains no matches or duplicate Demo identities")
    if comparison.get("valid") is not True or comparison.get("schemaVersion") != SCHEMA_VERSION:
        raise ValueError("Independent v4 audit did not pass")
    if comparison.get("rows") != sum(m["report"]["rowCount"] for m in manifest["matches"]):
        raise ValueError("Manifest and comparison row counts differ")
    if not input_path.is_file():
        raise FileNotFoundError(input_path)
    return manifest, comparison


def load_records(path: Path, match_ids=None):
    records = []
    feature_names = (*NUMERIC_FEATURES, *CATEGORICAL_FEATURES)
    with path.open(encoding="utf-8") as source:
        for line_number, line in enumerate(source, 1):
            if not line.strip():
                continue
            row = json.loads(line)
            if match_ids is not None and row.get("matchId") not in match_ids:
                continue
            records.append({
                "schemaVersion": row.get("schemaVersion"), "semanticVersion": row.get("semanticVersion"),
                "matchId": row.get("matchId"), "roundId": row.get("roundId"),
                "roundNumber": row.get("roundNumber"), "tick": row.get("tick"),
                "labelTWin": row.get("labelTWin"), "sampleWeight": row.get("sampleWeight"),
                "phase": get_path(row, "features.phase"),
                "totalAlive": get_path(row, "features.baseline.totalAlive"),
                "liveElapsed": get_path(row, "features.clock.liveElapsedSeconds"),
                "__features": {name: get_path(row, name) for name in feature_names},
            })
    if not records:
        raise ValueError("No v4 rows loaded")
    return records


def validate_records(records):
    keys = set()
    rounds = defaultdict(list)
    for row in records:
        if row["schemaVersion"] != SCHEMA_VERSION or row["semanticVersion"] != SEMANTIC_VERSION:
            raise ValueError("Mixed or unsupported schema/semantic version")
        if (not isinstance(row["matchId"], str) or not row["matchId"]
                or not isinstance(row["roundId"], str) or not row["roundId"]
                or row["labelTWin"] not in (0, 1)
                or not np.isfinite(row["sampleWeight"]) or not 0 < row["sampleWeight"] <= 1):
            raise ValueError("Invalid sample identity, label or weight")
        identity = (row["matchId"], row["roundId"])
        key = (*identity, int(row["tick"]))
        if not all(identity) or key in keys:
            raise ValueError(f"Invalid or duplicate sample key: {key}")
        keys.add(key)
        rounds[identity].append(row)
    for identity, rows in rounds.items():
        if len({int(r["labelTWin"]) for r in rows}) != 1:
            raise ValueError(f"Conflicting label in {identity}")
        if abs(sum(float(r["sampleWeight"]) for r in rows) - 1) > 1e-6:
            raise ValueError(f"Round weights do not sum to one in {identity}")
    matches = {r["matchId"] for r in records}
    return {"rows": len(records), "matches": len(matches), "rounds": len(rounds),
            "tWinRounds": sum(rows[0]["labelTWin"] for rows in rounds.values()),
            "ctWinRounds": len(rounds) - sum(rows[0]["labelTWin"] for rows in rounds.values())}


def make_frames(records):
    features = pd.DataFrame({name: [r["__features"].get(name) for r in records]
                             for name in (*NUMERIC_FEATURES, *CATEGORICAL_FEATURES)})
    for name in NUMERIC_FEATURES:
        features[name] = pd.to_numeric(features[name], errors="coerce").astype(float)
    for name in CATEGORICAL_FEATURES:
        features[name] = features[name].fillna("__missing__").astype(str)
    metadata = pd.DataFrame({
        "matchId": [r["matchId"] for r in records], "roundId": [r["roundId"] for r in records],
        "roundNumber": [r["roundNumber"] for r in records], "tick": [r["tick"] for r in records],
        "phase": [r["phase"] for r in records], "totalAlive": [r["totalAlive"] for r in records],
        "liveElapsed": [r["liveElapsed"] for r in records], "label": [r["labelTWin"] for r in records],
        "weight": [r["sampleWeight"] for r in records],
        **{key: features[path].to_numpy() for key, path in {
            "tAlive": "features.t.alive", "ctAlive": "features.ct.alive",
            "equipmentDifference": "features.baseline.equipmentValueDifference",
            "aliveKnown": "features.quality.aliveKnown",
            "equipmentKnown": "features.quality.aliveEquipmentKnown",
            "positionKnown": "features.quality.alivePositionKnown",
            "bombRemaining": "features.clock.bombRemainingSeconds",
        }.items()},
    })
    return features, metadata


def make_preprocessor():
    numeric = Pipeline([("imputer", SimpleImputer(strategy="median", add_indicator=True)),
                        ("scaler", StandardScaler())])
    categorical = OneHotEncoder(handle_unknown="ignore", sparse_output=False)
    return ColumnTransformer([("numeric", numeric, list(NUMERIC_FEATURES)),
                              ("categorical", categorical, list(CATEGORICAL_FEATURES))],
                             sparse_threshold=0, verbose_feature_names_out=False).set_output(transform="pandas")


def make_models(seed, threads):
    from lightgbm import LGBMClassifier

    return {
        "logistic": Pipeline([("preprocessor", make_preprocessor()),
            ("model", LogisticRegression(max_iter=3000, solver="lbfgs", random_state=seed))]),
        "lightgbm": Pipeline([("preprocessor", make_preprocessor()),
            ("model", LGBMClassifier(objective="binary", n_estimators=300, learning_rate=0.03,
                num_leaves=15, max_depth=5, min_child_samples=30, subsample=0.85,
                colsample_bytree=0.85, reg_alpha=0.2, reg_lambda=1.0, random_state=seed,
                n_jobs=threads, verbosity=-1, deterministic=True, force_col_wise=True))]),
    }


def fit_calibrator(method, probability, labels, weights):
    if method == "identity":
        return None
    if method == "sigmoid":
        model = LogisticRegression(solver="lbfgs", random_state=42)
        model.fit(_logit(probability).reshape(-1, 1), labels, sample_weight=weights)
        return model
    model = IsotonicRegression(out_of_bounds="clip", y_min=1e-6, y_max=1 - 1e-6)
    model.fit(probability, labels, sample_weight=weights)
    return model


def apply_calibrator(method, calibrator, probability):
    if method == "identity":
        return np.asarray(probability)
    if method == "sigmoid":
        return calibrator.predict_proba(_logit(probability).reshape(-1, 1))[:, 1]
    return calibrator.predict(probability)


def training_oof(features, metadata, indices, seed, threads, folds):
    subset = metadata.iloc[indices].reset_index(drop=True)
    x = features.iloc[indices].reset_index(drop=True)
    labels = subset.label.to_numpy(int)
    weights = subset.weight.to_numpy(float)
    groups = subset.matchId.to_numpy(str)
    splitter = GroupKFold(n_splits=folds)
    fold_ids = np.full(len(subset), -1, dtype=int)
    predictions = {name: np.full(len(subset), np.nan) for name in make_models(seed, threads)}
    splits = list(splitter.split(x, labels, groups))
    for fold, (train, test) in enumerate(splits):
        fold_ids[test] = fold
        for name, model in make_models(seed, threads).items():
            model.fit(x.iloc[train], labels[train], model__sample_weight=weights[train])
            predictions[name][test] = model.predict_proba(x.iloc[test])[:, 1]
    calibrated = {}
    calibration_metrics = {}
    for name, base_probability in predictions.items():
        calibrated[name] = {}
        calibration_metrics[name] = {}
        for method in CALIBRATION_METHODS:
            cross = np.full(len(subset), np.nan)
            for fold in range(folds):
                test = fold_ids == fold
                train = ~test
                calibrator = fit_calibrator(method, base_probability[train], labels[train], weights[train])
                cross[test] = apply_calibrator(method, calibrator, base_probability[test])
            calibrated[name][method] = cross
            calibration_metrics[name][method] = metrics(labels, cross, weights)
    return subset, predictions, calibrated, calibration_metrics


def json_safe_features(row):
    return {name: (None if pd.isna(value) else value.item() if isinstance(value, np.generic) else value)
            for name, value in row.items()}


def load_validation_ids(path):
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    ids = [item["matchId"] for item in value["validation"]]
    if not ids or len(ids) != len(set(ids)) or any(not isinstance(item, str) or not item for item in ids):
        raise ValueError("Fixed validation IDs must be nonempty and unique")
    return ids, value


def paired_v3(v3_path, validation_meta, v4_probability):
    if v3_path is None:
        return None
    old = {}
    with v3_path.open(encoding="utf-8") as source:
        for line in source:
            row = json.loads(line)
            old[(row["matchId"], row["tick"])] = row["logisticTWin"]
    indices, old_probability = [], []
    for i, row in validation_meta.iterrows():
        value = old.get((row.matchId, int(row.tick)))
        if value is not None:
            indices.append(i)
            old_probability.append(value)
    paired = validation_meta.iloc[indices].copy()
    counts = paired.groupby(["matchId", "roundId"])["tick"].transform("count")
    weights = (1 / counts).to_numpy(float)
    labels = paired.label.to_numpy(int)
    return {"rows": len(indices), "rounds": int(paired[["matchId", "roundId"]].drop_duplicates().shape[0]),
            "weighting": "equal corrected-round weight on common matchId/tick samples",
            "v3Logistic": metrics(labels, np.asarray(old_probability), weights),
            "v4Selected": metrics(labels, np.asarray(v4_probability)[indices], weights)}


def feature_importance(model, name):
    names = model.named_steps["preprocessor"].get_feature_names_out()
    estimator = model.named_steps["model"]
    values = estimator.booster_.feature_importance(importance_type="gain") if name == "lightgbm" else np.abs(estimator.coef_[0])
    order = np.argsort(values)[::-1][:30]
    return [{"feature": str(names[i]), "importance": float(values[i])} for i in order]


def load_saved_predictions(path, metadata, columns, artifact_sha256=None):
    keys = {(r.matchId, r.roundId, int(r.tick)): i for i, r in metadata.iterrows()}
    result = {name: np.full(len(metadata), np.nan) for name in columns}
    seen, found_columns = set(), set()
    with path.open(encoding="utf-8-sig") as source:
        for line in source:
            if not line.strip():
                continue
            row = json.loads(line)
            if artifact_sha256 is not None and row.get("modelArtifactSha256") != artifact_sha256:
                raise ValueError("Test prediction provenance differs from the frozen candidate")
            key = (row["matchId"], row["roundId"], row["tick"])
            if key in seen or key not in keys:
                raise ValueError("Duplicate or out-of-evaluation prediction key")
            seen.add(key)
            index = keys[key]
            expected = metadata.iloc[index]
            if row["labelTWin"] != expected.label or not np.isclose(
                    row["sampleWeight"], expected.weight, rtol=0, atol=1e-12):
                raise ValueError("Saved prediction label/weight differs from evaluation sample")
            for name in columns:
                if name in row:
                    found_columns.add(name)
                    if row[name] is not None:
                        value = float(row[name])
                        if not np.isfinite(value) or not 0 <= value <= 1:
                            raise ValueError("Invalid saved probability")
                        result[name][index] = value
    if found_columns != set(columns):
        raise ValueError("Requested probability column is absent")
    return result


def diagnostics_markdown(report):
    lines = ["# Mirage v4 evaluation diagnostics", "", report["evaluationUse"], "",
             "| Model | Points | Rounds | Matches | Weight | Coverage | Log Loss | Brier | AUC |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---:|"]
    def display(value):
        return "N/A" if value is None else f"{value:.4f}"
    for name, model in report["diagnostics"]["models"].items():
        overall = model["overall"]
        m = overall["metrics"] or {}
        lines.append(f"| {name} | {overall['rows']} | {overall['rounds']} | {overall['matches']} | "
                     f"{overall['weight']:.3f} | {display(overall['coverage']['rowFraction'])} | "
                     f"{display(m.get('logLoss'))} | {display(m.get('brierScore'))} | {display(m.get('rocAuc'))} |")
    lines += ["", "## Paired differences (candidate minus reference)", "",
              "Negative Log Loss/Brier differences favor the candidate. Intervals resample whole matches.", ""]
    for name, difference in report["diagnostics"]["pairedDifferences"].items():
        lines.append(f"- {name} vs {difference['reference']}: {difference['rows']} common points, {difference['matches']} matches.")
        for metric, interval in (difference["intervals"] or {}).items():
            lines.append(f"  - {metric}: {interval['estimate']:.4f}, 95% interval [{interval['low']:.4f}, {interval['high']:.4f}].")
    lines += ["", "## Reliability", "", "![Weighted reliability diagrams](reliability.png)", "",
              "Full slices, per-match metrics, coverage denominators and weighted calibration bins are in report.json.",
              "Empty groups remain visible; single-class groups have no AUC. Slice weights are not renormalized.",
              "Export coverage only covers scheduled candidates in completed eligible rounds, not all replay ticks.",
              "Pistol-round diagnostics await verified format/half metadata. Bootstrap intervals do not certify unseen-match performance.", ""]
    return "\n".join(lines)


def evaluate_saved(args, output, manifest, features, metadata, training_indices, validation_indices):
    validation = metadata.iloc[validation_indices].reset_index(drop=True)
    columns = args.probability_column or ["selectedTWin", "logisticTWin", "lightgbmTWin"]
    candidate = None
    if args.experiment and args.evaluation_role == "test":
        if not args.candidate_lock or not args.candidate_artifact:
            raise ValueError("Final test requires a frozen development-selected candidate lock")
        candidate = json.loads(args.candidate_lock.read_text(encoding="utf-8-sig"))
        artifact = candidate.get("modelArtifactSha256", "")
        if (candidate.get("status") != "frozen" or candidate.get("selectionPartition") != "development"
                or candidate.get("experimentSha256") != args.experiment_sha256
                or not isinstance(artifact, str) or len(artifact) != 64
                or any(c not in "0123456789abcdef" for c in artifact)
                or not isinstance(candidate.get("probabilityColumn"), str) or not candidate["probabilityColumn"]):
            raise ValueError("Invalid final-test candidate lock")
        if args.probability_column and args.probability_column != [candidate["probabilityColumn"]]:
            raise ValueError("Final test may evaluate only the locked candidate column")
        if sha256(args.candidate_artifact) != artifact:
            raise ValueError("Final-test model artifact differs from the frozen candidate")
        columns = [candidate["probabilityColumn"]]
    if len(columns) != len(set(columns)) or {"trainingPrior", "smallLogistic"} & set(columns):
        raise ValueError("Prediction columns must be unique and cannot use reference baseline names")
    predictions = load_saved_predictions(args.evaluate_predictions, validation, columns,
                                         candidate["modelArtifactSha256"] if candidate else None)
    predictions.update(reference_predictions(features.iloc[training_indices], metadata.iloc[training_indices],
                                             features.iloc[validation_indices], args.seed))
    report = {"reportVersion": 1, "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "evaluationUse": (f"Frozen experiment partition: {args.evaluation_role}; no test fitting/calibration."
                          if args.experiment else
                          "Previously observed fixed holdout: diagnostic only, not a new blind test or model selection."),
        "schemaVersion": SCHEMA_VERSION, "semanticVersion": SEMANTIC_VERSION,
        "sources": {name: {"file": path.name, "sha256": sha256(path)} for name, path in {
            "samples": args.input, "manifest": args.manifest, "audit": args.comparison,
            "validationSplit": args.validation_split, "predictions": args.evaluate_predictions,
            "evaluationScript": Path(__file__), "evaluationHelpers": Path(__file__).with_name("win_evaluation.py"),
        }.items()},
        "trainingMatchIds": sorted(metadata.iloc[training_indices].matchId.unique()),
        "validationMatchIds": sorted(validation.matchId.unique()),
        "referenceBaselines": {"fitScope": "training partition only; fixed hyperparameters, no holdout calibration",
            "smallFeatures": list(SMALL_FEATURES)},
        "exportCoverage": export_coverage(manifest, set(validation.matchId)),
        "diagnostics": evaluation_report(validation, predictions, reference=args.reference_column or columns[0],
                                          seed=args.seed, resamples=args.bootstrap_resamples),
        "runtime": {"python": platform.python_version(), "numpy": np.__version__,
            "pandas": pd.__version__, "scikitLearn": sklearn.__version__,
            "scipy": version("scipy"), "matplotlib": version("matplotlib"), "seed": args.seed}}
    for name, path in (("experiment", args.experiment), ("candidateLock", args.candidate_lock),
                       ("candidateArtifact", args.candidate_artifact)):
        if path:
            report["sources"][name] = {"file": path.name, "sha256": sha256(path)}
    # Exclusive directory creation: a failed run remains inspectable; old outputs are never replaced.
    output.mkdir(parents=True, exist_ok=False)
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    (output / "report.md").write_text(diagnostics_markdown(report), encoding="utf-8")
    plot_reliability(report["diagnostics"], output / "reliability.png")
    saved = validation[["matchId", "roundId", "tick", "label", "weight"]].rename(
        columns={"label": "labelTWin", "weight": "sampleWeight"}).assign(**predictions)
    saved.to_json(output / "evaluation_predictions.jsonl", orient="records", lines=True, double_precision=15)
    completion = {"status": "complete", "artifacts": {p.name: sha256(p) for p in output.iterdir() if p.is_file()}}
    (output / "evaluation-manifest.json").write_text(json.dumps(completion, indent=2), encoding="utf-8")
    print(json.dumps({"outputDirectory": str(output), "status": "complete", "evaluationUse": report["evaluationUse"]}))


def markdown(report):
    selected = report["selectedBaseline"]
    lines = ["# Mirage schema-v4.2 round-win baseline", "", f"Generated: {report['generatedAtUtc']}", "",
             "## Data", "", f"- Rows: {report['data']['rows']}", f"- Rounds: {report['data']['rounds']}",
             f"- Split: {report['evaluation']['trainingMatches']} training / {report['evaluation']['validationMatches']} validation matches",
             "", "## Training-only selection", "", "| Base | Calibration | Log loss | Brier | AUC | ECE-10 |",
             "|---|---|---:|---:|---:|---:|"]
    for base, methods in report["trainingOofSelection"].items():
        for method, m in methods.items():
            auc = "N/A" if m['rocAuc'] is None else f"{m['rocAuc']:.4f}"
            lines.append(f"| {base} | {method} | {m['logLoss']:.4f} | {m['brierScore']:.4f} | {auc} | {m['ece10']:.4f} |")
    lines += ["", "## Fixed held-out validation", "", f"Selected before holdout: `{selected['model']}` + `{selected['calibration']}`.", "",
              "| Model | Log loss | Brier | AUC | Accuracy | ECE-10 |", "|---|---:|---:|---:|---:|---:|"]
    for name, m in report["evaluation"]["overall"].items():
        auc = "N/A" if m['rocAuc'] is None else f"{m['rocAuc']:.4f}"
        lines.append(f"| {name} | {m['logLoss']:.4f} | {m['brierScore']:.4f} | {auc} | {m['accuracy']:.4f} | {m['ece10']:.4f} |")
    if report.get("pairedV3V4"):
        lines += ["", "## Paired common-sample comparison", "", "| Model | Log loss | Brier | AUC | ECE-10 |", "|---|---:|---:|---:|---:|"]
        for name in ("v3Logistic", "v4Selected"):
            m = report["pairedV3V4"][name]
            auc = "N/A" if m['rocAuc'] is None else f"{m['rocAuc']:.4f}"
            lines.append(f"| {name} | {m['logLoss']:.4f} | {m['brierScore']:.4f} | {auc} | {m['ece10']:.4f} |")
    lines += ["", "## Limits", "", "The held-out matches form a fixed evaluation set. Results should be confirmed on future unseen matches before making a final generalization claim.", ""]
    return "\n".join(lines)


def main():
    args = parse_args()
    output = args.output_dir.resolve()
    if output.exists():
        raise FileExistsError(output)
    manifest, comparison = validate_sources(args.input, args.manifest, args.comparison)
    validation_ids, split_document = load_validation_ids(args.validation_split)
    allowed_ids = None
    if args.experiment:
        experiment = load_frozen_experiment(args.experiment, args.experiment_sha256, args.validation_split)
        if (args.seed, args.bootstrap_resamples) != (experiment["bootstrap"]["seed"], experiment["bootstrap"]["resamples"]):
            raise ValueError("Evaluation seed/resamples differ from the frozen protocol")
        if args.evaluation_role == "test" and not args.evaluate_predictions:
            raise ValueError("Final test is evaluation-only; fitting/model selection is forbidden")
        if not args.evaluate_predictions:
            raise ValueError("Use frozen experiments for saved-prediction evaluation; new-data training is a separate experiment")
        validation_ids = experiment["partitions"][args.evaluation_role]
        allowed_ids = set(experiment["partitions"]["train"]) | set(validation_ids)
        if set(m["matchId"] for m in manifest["matches"]) - set(m["matchId"] for m in experiment["matches"]):
            raise ValueError("Export contains unregistered matches")
        if allowed_ids - set(m["matchId"] for m in manifest["matches"]):
            raise ValueError("Export is missing a frozen training/evaluation match")
    elif args.evaluation_role == "test" or args.candidate_lock or args.candidate_artifact or args.experiment_sha256:
        raise ValueError("Final-test options require a frozen experiment")
    records = load_records(args.input, allowed_ids)
    summary = validate_records(records)
    selected_manifest = [m for m in manifest["matches"] if allowed_ids is None or m["matchId"] in allowed_ids]
    manifest_rows = sum(match["report"]["rowCount"] for match in selected_manifest)
    if summary["rows"] != manifest_rows or {r["matchId"] for r in records} != {m["matchId"] for m in selected_manifest}:
        raise ValueError("Loaded records do not match the completed export manifest")
    features, metadata = make_frames(records)
    groups = metadata.matchId.to_numpy(str)
    validation_mask = np.isin(groups, validation_ids)
    if set(validation_ids) - set(groups):
        raise ValueError("A fixed validation match is absent from v4")
    training_indices = np.flatnonzero(~validation_mask)
    validation_indices = np.flatnonzero(validation_mask)
    training_match_count = len(set(groups[training_indices]))
    if not training_match_count:
        raise ValueError("No training matches remain outside the fixed validation split")
    if args.evaluate_predictions:
        evaluate_saved(args, output, manifest, features, metadata, training_indices, validation_indices)
        return
    import lightgbm

    if args.folds < 2 or args.folds > training_match_count:
        raise ValueError(f"folds must be between 2 and {training_match_count}")
    training_meta, raw_oof, calibrated_oof, selection = training_oof(
        features, metadata, training_indices, args.seed, args.threads, args.folds)
    winner = min(((m["logLoss"], base, method) for base, methods in selection.items()
                  for method, m in methods.items()), key=lambda item: (item[0], item[1], item[2]))
    _, selected_base, selected_method = winner
    labels = metadata.label.to_numpy(int)
    weights = metadata.weight.to_numpy(float)
    final_models = make_models(args.seed, args.threads)
    validation_predictions = {}
    calibrators = {}
    for name, model in final_models.items():
        model.fit(features.iloc[training_indices], labels[training_indices],
                  model__sample_weight=weights[training_indices])
        base_oof = raw_oof[name]
        method = min(selection[name], key=lambda value: selection[name][value]["logLoss"])
        calibrators[name] = (method, fit_calibrator(method, base_oof,
            training_meta.label.to_numpy(int), training_meta.weight.to_numpy(float)))
        raw = model.predict_proba(features.iloc[validation_indices])[:, 1]
        validation_predictions[name] = raw
        validation_predictions[f"{name}+{method}"] = apply_calibrator(method, calibrators[name][1], raw)
    selected_calibrator = fit_calibrator(selected_method, raw_oof[selected_base],
        training_meta.label.to_numpy(int), training_meta.weight.to_numpy(float))
    selected_raw = final_models[selected_base].predict_proba(features.iloc[validation_indices])[:, 1]
    selected_probability = apply_calibrator(selected_method, selected_calibrator, selected_raw)
    validation_predictions["selected"] = selected_probability
    validation_predictions.update(reference_predictions(features.iloc[training_indices], metadata.iloc[training_indices],
                                                        features.iloc[validation_indices], args.seed))
    validation_meta = metadata.iloc[validation_indices].reset_index(drop=True)
    validation_labels = labels[validation_indices]
    validation_weights = weights[validation_indices]
    overall = {name: metrics(validation_labels, value, validation_weights)
               for name, value in validation_predictions.items()}
    temp = Path(tempfile.mkdtemp(prefix=output.name + ".tmp-", dir=output.parent))
    try:
        for name, model in final_models.items():
            joblib.dump(model, temp / f"{name}.joblib")
        baseline = CalibratedWinModel(final_models[selected_base], selected_method, selected_calibrator)
        joblib.dump(baseline, temp / "baseline.joblib")
        with (temp / "training_oof_predictions.jsonl").open("w", encoding="utf-8") as target:
            chosen_oof = calibrated_oof[selected_base][selected_method]
            for i, row in training_meta.iterrows():
                target.write(json.dumps({"matchId": row.matchId, "roundId": row.roundId, "tick": int(row.tick),
                    "labelTWin": int(row.label), "sampleWeight": float(row.weight),
                    "selectedTWin": float(chosen_oof[i])}, separators=(",", ":")) + "\n")
        with (temp / "validation_predictions.jsonl").open("w", encoding="utf-8") as target:
            for i, row in validation_meta.iterrows():
                target.write(json.dumps({"matchId": row.matchId, "roundId": row.roundId,
                    "roundNumber": int(row.roundNumber), "tick": int(row.tick), "phase": row.phase,
                    "labelTWin": int(row.label), "sampleWeight": float(row.weight),
                    "selectedTWin": float(selected_probability[i]),
                    "logisticTWin": float(validation_predictions["logistic"][i]),
                    "lightgbmTWin": float(validation_predictions["lightgbm"][i])}, separators=(",", ":")) + "\n")
        input_hash = sha256(args.input)
        report = {"generatedAtUtc": datetime.now(timezone.utc).isoformat(), "data": summary,
            "selectedBaseline": {"model": selected_base, "calibration": selected_method,
                "selectionProtocol": f"{args.folds}-fold grouped training-only OOF weighted Log Loss",
                "trainingOofLogLoss": winner[0], "artifact": "baseline.joblib"},
            "trainingOofSelection": selection,
            "evaluation": {"strategy": "fixed-match-holdout", "trainingMatches": len(set(groups[training_indices])),
                "validationMatches": len(validation_ids), "trainingRows": len(training_indices),
                "validationRows": len(validation_indices), "overall": overall,
                "selectedPerMatch": per_match_metrics(validation_meta, validation_labels,
                                                       selected_probability, validation_weights),
                "selectedSlices": slices(validation_meta, validation_labels, selected_probability, validation_weights),
                "selectedCalibrationBins": calibration_bins(validation_labels, selected_probability, validation_weights)},
            "diagnostics": evaluation_report(validation_meta, validation_predictions, reference="selected",
                                               seed=args.seed, resamples=args.bootstrap_resamples),
            "exportCoverage": export_coverage(manifest, set(validation_ids)),
            "pairedV3V4": paired_v3(args.v3_predictions, validation_meta, selected_probability),
            "featureImportance": {name: feature_importance(model, name) for name, model in final_models.items()},
            "features": {"numeric": list(NUMERIC_FEATURES), "categorical": list(CATEGORICAL_FEATURES),
                "excluded": ["matchId", "roundId", "segmentId", "tick", "demoTimeSeconds",
                             "features.players", "features.zones", "labelTWin"]},
            "runtime": {"python": platform.python_version(), "numpy": np.__version__, "pandas": pd.__version__,
                "scikitLearn": sklearn.__version__, "lightgbm": lightgbm.__version__, "seed": args.seed,
                "scipy": version("scipy"), "matplotlib": version("matplotlib"),
                "threads": args.threads, "folds": args.folds}}
        (temp / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
        (temp / "report.md").write_text(markdown(report), encoding="utf-8")
        plot_reliability(report["diagnostics"], temp / "reliability.png")
        (temp / "validation-split.json").write_text(json.dumps(split_document, ensure_ascii=False, indent=2), encoding="utf-8")
        feature_manifest = {"schemaVersion": SCHEMA_VERSION, "semanticVersion": SEMANTIC_VERSION,
            "numeric": list(NUMERIC_FEATURES), "categorical": list(CATEGORICAL_FEATURES)}
        (temp / "feature-manifest.json").write_text(json.dumps(feature_manifest, indent=2), encoding="utf-8")
        fixture_indices = list(range(min(3, len(validation_meta))))
        fixtures = [{"features": json_safe_features(features.iloc[i]),
                     "expectedTWin": float(baseline.predict_proba(features.iloc[[i]])[0, 1])}
                    for i in validation_indices[fixture_indices]]
        (temp / "inference-fixtures.json").write_text(
            json.dumps(fixtures, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
        model_manifest = {"status": "complete", "schemaVersion": SCHEMA_VERSION,
            "semanticVersion": SEMANTIC_VERSION, "dataSha256": input_hash,
            "sourceManifestSha256": sha256(args.manifest), "sourceComparisonSha256": sha256(args.comparison),
            "trainingScriptSha256": sha256(Path(__file__)), "selectedModel": selected_base,
            "evaluationHelpersSha256": sha256(Path(__file__).with_name("win_evaluation.py")),
            "calibration": selected_method, "validationMatchIds": validation_ids,
            "artifacts": {p.name: sha256(p) for p in temp.iterdir() if p.is_file()}}
        (temp / "model-manifest.json").write_text(json.dumps(model_manifest, indent=2), encoding="utf-8")
        os.replace(temp, output)
    except BaseException:
        shutil.rmtree(temp, ignore_errors=True)
        raise
    print(json.dumps({"outputDirectory": str(output), "selectedModel": selected_base,
        "calibration": selected_method, "trainingOofLogLoss": winner[0],
        "validation": overall["selected"], "pairedV3V4": report["pairedV3V4"]}, indent=2))


if __name__ == "__main__":
    main()
