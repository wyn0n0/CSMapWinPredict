#!/usr/bin/env python3
"""Train and evaluate causal CS2 round-win baselines from schema-v3 JSONL."""

from __future__ import annotations

import argparse
import json
import platform
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

import joblib
import lightgbm
import numpy as np
import pandas as pd
import sklearn
from lightgbm import LGBMClassifier
from sklearn.compose import ColumnTransformer
from sklearn.impute import SimpleImputer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import accuracy_score, brier_score_loss, log_loss, roc_auc_score
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import OneHotEncoder, StandardScaler


EXPECTED_SCHEMA_VERSION = 3

CATEGORICAL_FEATURES = (
    "mapName",
    "features.phase",
    "features.bomb.site",
    "features.bomb.region",
    "features.baseline.bombState",
    "features.baseline.previousBombState",
)

NUMERIC_FEATURES = (
    "roundNumber",
    "features.elapsedSeconds",
    "features.remainingSeconds",
    "features.scoreT",
    "features.scoreCT",
    "features.consecutiveLossesT",
    "features.consecutiveLossesCT",
    "features.bomb.hasCarrier",
    "features.bomb.hasDefuser",
    "features.bomb.secondsToExplosion",
    "features.bomb.secondsToDefuse",
    "features.t.alive",
    "features.t.totalHealth",
    "features.t.totalKills",
    "features.t.totalDeaths",
    "features.t.equipmentKnownPlayers",
    "features.t.totalMoney",
    "features.t.totalArmor",
    "features.t.helmetCount",
    "features.t.defuserCount",
    "features.t.equipmentValue",
    "features.t.grenadeCount",
    "features.t.rifleCount",
    "features.t.sniperCount",
    "features.ct.alive",
    "features.ct.totalHealth",
    "features.ct.totalKills",
    "features.ct.totalDeaths",
    "features.ct.equipmentKnownPlayers",
    "features.ct.totalMoney",
    "features.ct.totalArmor",
    "features.ct.helmetCount",
    "features.ct.defuserCount",
    "features.ct.equipmentValue",
    "features.ct.grenadeCount",
    "features.ct.rifleCount",
    "features.ct.sniperCount",
    "features.baseline.scoreDifference",
    "features.baseline.lossStreakDifference",
    "features.baseline.bombStateChangeCount",
    "features.baseline.secondsSinceBombStateChange",
    "features.baseline.hasExplosionTimer",
    "features.baseline.hasDefuseTimer",
    "features.baseline.bombWasDropped",
    "features.baseline.bombWasPlanting",
    "features.baseline.bombWasPlanted",
    "features.baseline.bombWasDefusing",
    "features.baseline.tPositionDispersion",
    "features.baseline.ctPositionDispersion",
    "features.baseline.positionDispersionDifference",
    "features.baseline.tPositionDataMissing",
    "features.baseline.ctPositionDataMissing",
    "features.baseline.nearestOpponentDistance",
    "features.baseline.nearestOpponentDistanceMissing",
    "features.baseline.tMeanDistanceToSiteA",
    "features.baseline.tMeanDistanceToSiteB",
    "features.baseline.ctMeanDistanceToSiteA",
    "features.baseline.ctMeanDistanceToSiteB",
    "features.baseline.tMinDistanceToSiteA",
    "features.baseline.tMinDistanceToSiteB",
    "features.baseline.ctMinDistanceToSiteA",
    "features.baseline.ctMinDistanceToSiteB",
    "features.baseline.tClosestSiteDistance",
    "features.baseline.ctClosestSiteDistance",
    "features.baseline.siteAProximityDifference",
    "features.baseline.siteBProximityDifference",
    "features.baseline.equipmentValueDifference",
    "features.baseline.moneyDifference",
    "features.baseline.armorDifference",
    "features.baseline.helmetCountDifference",
    "features.baseline.defuserCountDifference",
    "features.baseline.grenadeCountDifference",
    "features.baseline.rifleCountDifference",
    "features.baseline.sniperCountDifference",
    "features.baseline.healthDifference",
    "features.baseline.aliveDifference",
    "features.baseline.totalAlive",
    "features.baseline.tMeanHealth",
    "features.baseline.ctMeanHealth",
    "features.baseline.isClutch",
    "features.baseline.tEquipmentCoverage",
    "features.baseline.ctEquipmentCoverage",
    "features.baseline.equipmentCoverageDifference",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--input",
        action="append",
        required=True,
        type=Path,
        help="Schema-v3 JSONL input. Repeat for multiple files.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path("models/win-baseline-v3"),
        help="Directory for fitted models, OOF predictions, and reports.",
    )
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--threads", type=int, default=4)
    return parser.parse_args()


def get_path(record: dict[str, Any], path: str) -> Any:
    value: Any = record
    for part in path.split("."):
        if not isinstance(value, dict):
            return None
        value = value.get(part)
    return value


def load_records(paths: Iterable[Path]) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    for path in paths:
        with path.open("r", encoding="utf-8") as source:
            for line_number, line in enumerate(source, start=1):
                if not line.strip():
                    continue
                try:
                    records.append(json.loads(line))
                except json.JSONDecodeError as error:
                    raise ValueError(
                        f"{path}:{line_number}: invalid JSON: {error}"
                    ) from error
    if not records:
        raise ValueError("No training rows were loaded")
    return records


def validate_records(records: list[dict[str, Any]]) -> dict[str, Any]:
    sample_keys: set[tuple[str, int, int]] = set()
    rounds: dict[tuple[str, int], list[dict[str, Any]]] = defaultdict(list)
    schema_versions: set[int] = set()
    maps: set[str] = set()

    for row in records:
        schema_versions.add(int(row.get("schemaVersion", -1)))
        maps.add(str(row.get("mapName")))
        match_id = str(row.get("matchId", ""))
        round_number = int(row.get("roundNumber", -1))
        tick = int(row.get("tick", -1))
        label = int(row.get("labelTWin", -1))
        weight = float(row.get("sampleWeight", 0))
        if not match_id or round_number < 0 or tick < 0:
            raise ValueError("A row has an invalid matchId, roundNumber, or tick")
        if label not in (0, 1):
            raise ValueError(f"Invalid labelTWin={label}")
        if not np.isfinite(weight) or weight <= 0:
            raise ValueError(f"Invalid sampleWeight={weight}")
        sample_key = (match_id, round_number, tick)
        if sample_key in sample_keys:
            raise ValueError(f"Duplicate sample key: {sample_key}")
        sample_keys.add(sample_key)
        rounds[(match_id, round_number)].append(row)

    if schema_versions != {EXPECTED_SCHEMA_VERSION}:
        raise ValueError(
            f"Expected only schema v{EXPECTED_SCHEMA_VERSION}, got {sorted(schema_versions)}"
        )
    matches = sorted({key[0] for key in rounds})
    if len(matches) < 3:
        raise ValueError("At least three matches are required for grouped evaluation")

    t_wins = 0
    for round_key, rows in rounds.items():
        labels = {int(row["labelTWin"]) for row in rows}
        if len(labels) != 1:
            raise ValueError(f"Inconsistent labels in round {round_key}")
        weight_sum = sum(float(row["sampleWeight"]) for row in rows)
        if abs(weight_sum - 1) > 1e-6:
            raise ValueError(f"Sample weights sum to {weight_sum} in round {round_key}")
        t_wins += next(iter(labels))

    return {
        "rows": len(records),
        "matches": len(matches),
        "rounds": len(rounds),
        "tWinRounds": t_wins,
        "ctWinRounds": len(rounds) - t_wins,
        "maps": sorted(maps),
        "schemaVersion": EXPECTED_SCHEMA_VERSION,
    }


def make_frame(records: list[dict[str, Any]]) -> tuple[pd.DataFrame, pd.DataFrame]:
    feature_data = {
        name: [get_path(row, name) for row in records]
        for name in (*NUMERIC_FEATURES, *CATEGORICAL_FEATURES)
    }
    features = pd.DataFrame(feature_data)
    for name in NUMERIC_FEATURES:
        features[name] = pd.to_numeric(features[name], errors="coerce").astype(float)
    for name in CATEGORICAL_FEATURES:
        features[name] = features[name].fillna("__missing__").astype(str)

    metadata = pd.DataFrame(
        {
            "matchId": [str(row["matchId"]) for row in records],
            "roundNumber": [int(row["roundNumber"]) for row in records],
            "tick": [int(row["tick"]) for row in records],
            "phase": [str(get_path(row, "features.phase")) for row in records],
            "label": [int(row["labelTWin"]) for row in records],
            "weight": [float(row["sampleWeight"]) for row in records],
        }
    )
    return features, metadata


def make_preprocessor() -> ColumnTransformer:
    numeric = Pipeline(
        [
            ("imputer", SimpleImputer(strategy="median", add_indicator=True)),
            ("scaler", StandardScaler()),
        ]
    )
    categorical = OneHotEncoder(handle_unknown="ignore", sparse_output=False)
    preprocessor = ColumnTransformer(
        [
            ("numeric", numeric, list(NUMERIC_FEATURES)),
            ("categorical", categorical, list(CATEGORICAL_FEATURES)),
        ],
        sparse_threshold=0,
        verbose_feature_names_out=False,
    )
    return preprocessor.set_output(transform="pandas")


def make_models(seed: int, threads: int) -> dict[str, Pipeline]:
    return {
        "logistic": Pipeline(
            [
                ("preprocessor", make_preprocessor()),
                (
                    "model",
                    LogisticRegression(
                        max_iter=3000,
                        solver="lbfgs",
                        random_state=seed,
                    ),
                ),
            ]
        ),
        "lightgbm": Pipeline(
            [
                ("preprocessor", make_preprocessor()),
                (
                    "model",
                    LGBMClassifier(
                        objective="binary",
                        n_estimators=300,
                        learning_rate=0.03,
                        num_leaves=15,
                        max_depth=5,
                        min_child_samples=30,
                        subsample=0.85,
                        colsample_bytree=0.85,
                        reg_alpha=0.2,
                        reg_lambda=1.0,
                        random_state=seed,
                        n_jobs=threads,
                        verbosity=-1,
                        deterministic=True,
                        force_col_wise=True,
                    ),
                ),
            ]
        ),
    }


def expected_calibration_error(
    labels: np.ndarray,
    probabilities: np.ndarray,
    weights: np.ndarray,
    bins: int = 10,
) -> float:
    edges = np.linspace(0, 1, bins + 1)
    assignments = np.clip(np.digitize(probabilities, edges[1:-1]), 0, bins - 1)
    total_weight = float(weights.sum())
    error = 0.0
    for bin_index in range(bins):
        mask = assignments == bin_index
        if not mask.any():
            continue
        bin_weight = float(weights[mask].sum())
        confidence = float(np.average(probabilities[mask], weights=weights[mask]))
        accuracy = float(np.average(labels[mask], weights=weights[mask]))
        error += bin_weight / total_weight * abs(confidence - accuracy)
    return error


def metrics(
    labels: np.ndarray, probabilities: np.ndarray, weights: np.ndarray
) -> dict[str, float]:
    probabilities = np.clip(probabilities, 1e-6, 1 - 1e-6)
    result = {
        "logLoss": float(
            log_loss(labels, probabilities, sample_weight=weights, labels=[0, 1])
        ),
        "brierScore": float(
            brier_score_loss(labels, probabilities, sample_weight=weights)
        ),
        "accuracy": float(
            accuracy_score(labels, probabilities >= 0.5, sample_weight=weights)
        ),
        "ece10": float(expected_calibration_error(labels, probabilities, weights)),
    }
    result["rocAuc"] = (
        float(roc_auc_score(labels, probabilities, sample_weight=weights))
        if np.unique(labels).size == 2
        else float("nan")
    )
    return result


def calibration_bins(
    labels: np.ndarray, probabilities: np.ndarray, weights: np.ndarray, bins: int = 10
) -> list[dict[str, Any]]:
    assignments = np.minimum(
        (np.clip(probabilities, 0, 1) * bins).astype(int), bins - 1
    )
    result: list[dict[str, Any]] = []
    for bin_index in range(bins):
        mask = assignments == bin_index
        if not mask.any():
            continue
        result.append(
            {
                "lower": bin_index / bins,
                "upper": (bin_index + 1) / bins,
                "rows": int(mask.sum()),
                "weight": float(weights[mask].sum()),
                "meanPrediction": float(
                    np.average(probabilities[mask], weights=weights[mask])
                ),
                "observedTWinRate": float(
                    np.average(labels[mask], weights=weights[mask])
                ),
            }
        )
    return result


def evaluate_grouped(
    features: pd.DataFrame,
    metadata: pd.DataFrame,
    seed: int,
    threads: int,
) -> tuple[dict[str, Any], dict[str, np.ndarray]]:
    labels = metadata["label"].to_numpy(dtype=int)
    weights = metadata["weight"].to_numpy(dtype=float)
    groups = metadata["matchId"].to_numpy(dtype=str)
    match_ids = sorted(np.unique(groups))
    predictions = {
        "constant": np.full(len(metadata), np.nan),
        "logistic": np.full(len(metadata), np.nan),
        "lightgbm": np.full(len(metadata), np.nan),
    }
    folds: list[dict[str, Any]] = []

    for match_id in match_ids:
        test_mask = groups == match_id
        train_mask = ~test_mask
        train_indices = np.flatnonzero(train_mask)
        test_indices = np.flatnonzero(test_mask)
        prior = float(np.average(labels[train_indices], weights=weights[train_indices]))
        predictions["constant"][test_indices] = prior
        fold_models = make_models(seed, threads)
        fold_metrics = {
            "constant": metrics(
                labels[test_indices],
                predictions["constant"][test_indices],
                weights[test_indices],
            )
        }
        for name, model in fold_models.items():
            model.fit(
                features.iloc[train_indices],
                labels[train_indices],
                model__sample_weight=weights[train_indices],
            )
            probabilities = model.predict_proba(features.iloc[test_indices])[:, 1]
            predictions[name][test_indices] = probabilities
            fold_metrics[name] = metrics(
                labels[test_indices], probabilities, weights[test_indices]
            )
        held_out = metadata.iloc[test_indices]
        folds.append(
            {
                "heldOutMatchId": match_id,
                "rows": len(test_indices),
                "rounds": int(
                    held_out[["matchId", "roundNumber"]].drop_duplicates().shape[0]
                ),
                "tWinRate": float(
                    np.average(labels[test_indices], weights=weights[test_indices])
                ),
                "trainingPriorTWin": prior,
                "metrics": fold_metrics,
            }
        )

    if any(np.isnan(values).any() for values in predictions.values()):
        raise RuntimeError("Grouped evaluation left samples without predictions")

    overall = {
        name: metrics(labels, values, weights) for name, values in predictions.items()
    }
    phase_metrics: dict[str, dict[str, dict[str, float]]] = {}
    for phase in sorted(metadata["phase"].unique()):
        mask = metadata["phase"].to_numpy() == phase
        phase_metrics[phase] = {
            name: metrics(labels[mask], values[mask], weights[mask])
            for name, values in predictions.items()
        }
    return {"overall": overall, "byPhase": phase_metrics, "folds": folds}, predictions


def fitted_feature_importance(model: Pipeline, model_name: str) -> list[dict[str, Any]]:
    feature_names = model.named_steps["preprocessor"].get_feature_names_out()
    estimator = model.named_steps["model"]
    if model_name == "lightgbm":
        values = estimator.booster_.feature_importance(importance_type="gain")
    else:
        values = np.abs(estimator.coef_[0])
    order = np.argsort(values)[::-1][:25]
    return [
        {"feature": str(feature_names[index]), "importance": float(values[index])}
        for index in order
    ]


def markdown_report(report: dict[str, Any]) -> str:
    summary = report["data"]
    lines = [
        "# Preliminary Mirage round-win baseline",
        "",
        f"Generated: {report['generatedAtUtc']}",
        "",
        "## Data",
        "",
        f"- Matches: {summary['matches']}",
        f"- Rounds: {summary['rounds']} ({summary['tWinRounds']} T wins / {summary['ctWinRounds']} CT wins)",
        f"- Rows: {summary['rows']}",
        f"- Validation: leave-one-match-out ({summary['matches']} folds), weighted equally per round",
        "",
        "## Out-of-fold metrics",
        "",
        "| Model | Log loss | Brier | ROC-AUC | Accuracy | ECE-10 |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for name in ("constant", "logistic", "lightgbm"):
        value = report["evaluation"]["overall"][name]
        lines.append(
            f"| {name} | {value['logLoss']:.4f} | {value['brierScore']:.4f} | "
            f"{value['rocAuc']:.4f} | {value['accuracy']:.4f} | {value['ece10']:.4f} |"
        )
    lines.extend(
        [
            "",
            "## Held-out matches (LightGBM)",
            "",
            "| Match | Rounds | T-win rate | Log loss | Brier | ROC-AUC |",
            "|---|---:|---:|---:|---:|---:|",
        ]
    )
    for fold in report["evaluation"]["folds"]:
        value = fold["metrics"]["lightgbm"]
        lines.append(
            f"| {fold['heldOutMatchId'][:12]} | {fold['rounds']} | {fold['tWinRate']:.3f} | "
            f"{value['logLoss']:.4f} | {value['brierScore']:.4f} | {value['rocAuc']:.4f} |"
        )
    lines.extend(
        ["", "## Top LightGBM features", "", "| Feature | Gain |", "|---|---:|"]
    )
    for item in report["featureImportance"]["lightgbm"][:15]:
        lines.append(f"| `{item['feature']}` | {item['importance']:.2f} |")
    lines.extend(
        [
            "",
            "## Limitation",
            "",
            "Only six matches are available. Scores are useful for pipeline validation, not for claiming generalization. "
            "The final saved models are fitted on all six matches and are not probability-calibrated.",
            "",
        ]
    )
    return "\n".join(lines)


def main() -> int:
    args = parse_args()
    input_paths = [path.resolve() for path in args.input]
    for path in input_paths:
        if not path.is_file():
            raise FileNotFoundError(path)
    records = load_records(input_paths)
    data_summary = validate_records(records)
    features, metadata = make_frame(records)
    evaluation, predictions = evaluate_grouped(
        features, metadata, args.seed, args.threads
    )

    args.output_dir.mkdir(parents=True, exist_ok=True)
    final_models = make_models(args.seed, args.threads)
    labels = metadata["label"].to_numpy(dtype=int)
    weights = metadata["weight"].to_numpy(dtype=float)
    importance: dict[str, list[dict[str, Any]]] = {}
    for name, model in final_models.items():
        model.fit(features, labels, model__sample_weight=weights)
        joblib.dump(model, args.output_dir / f"{name}.joblib")
        importance[name] = fitted_feature_importance(model, name)

    report = {
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "inputs": [str(path) for path in input_paths],
        "data": data_summary,
        "features": {
            "numeric": list(NUMERIC_FEATURES),
            "categorical": list(CATEGORICAL_FEATURES),
            "excluded": [
                "matchId",
                "tick",
                "timeSeconds",
                "features.players",
                "features.zones",
                "labelTWin",
            ],
        },
        "evaluation": evaluation,
        "calibration": {
            name: calibration_bins(labels, values, weights)
            for name, values in predictions.items()
        },
        "featureImportance": importance,
        "runtime": {
            "python": platform.python_version(),
            "numpy": np.__version__,
            "pandas": pd.__version__,
            "scikitLearn": sklearn.__version__,
            "lightgbm": lightgbm.__version__,
            "seed": args.seed,
            "threads": args.threads,
        },
    }
    with (args.output_dir / "report.json").open("w", encoding="utf-8") as target:
        json.dump(report, target, ensure_ascii=False, indent=2, allow_nan=False)
    (args.output_dir / "report.md").write_text(
        markdown_report(report), encoding="utf-8"
    )

    with (args.output_dir / "oof_predictions.jsonl").open(
        "w", encoding="utf-8"
    ) as target:
        for index, row in metadata.iterrows():
            value = {
                "matchId": row["matchId"],
                "roundNumber": int(row["roundNumber"]),
                "tick": int(row["tick"]),
                "labelTWin": int(row["label"]),
                "sampleWeight": float(row["weight"]),
                "constantTWin": float(predictions["constant"][index]),
                "logisticTWin": float(predictions["logistic"][index]),
                "lightgbmTWin": float(predictions["lightgbm"][index]),
            }
            target.write(json.dumps(value, separators=(",", ":")) + "\n")

    print(
        json.dumps(
            {
                "outputDirectory": str(args.output_dir.resolve()),
                **data_summary,
                "metrics": evaluation["overall"],
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
