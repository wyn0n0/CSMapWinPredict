"""Weighted diagnostics shared by v4 training and saved-prediction evaluation."""

import numpy as np
import pandas as pd
from scipy.stats import bootstrap
from sklearn.compose import ColumnTransformer
from sklearn.dummy import DummyClassifier
from sklearn.impute import SimpleImputer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import accuracy_score, brier_score_loss, log_loss, roc_auc_score
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler


SMALL_FEATURES = (
    "features.t.alive", "features.ct.alive",
    "features.t.equipmentValue", "features.ct.equipmentValue",
)
BROAD_ENDGAME_DEFINITION = "broad-endgame-v1: live/post-plant; known integer team counts in [1,5]; tAlive + ctAlive <= 7"


def broad_endgame_mask(phase, t, ct):
    phase, t, ct = map(np.asarray, (phase, t, ct))
    return (np.isin(phase, ["live", "post-plant"])
            & np.isin(t, [1, 2, 3, 4, 5]) & np.isin(ct, [1, 2, 3, 4, 5])
            & (t + ct <= 7))


def calibration_bins(labels, probabilities, weights, bins=10):
    # Preserve the existing equal-width, round-weighted bins. sklearn's
    # calibration_curve has no sample_weight argument.
    labels, probabilities, weights = map(np.asarray, (labels, probabilities, weights))
    assignment = np.minimum((np.clip(probabilities, 0, 1) * bins).astype(int), bins - 1)
    return [{"lower": i / bins, "upper": (i + 1) / bins, "rows": int(mask.sum()),
             "weight": float(weights[mask].sum()),
             "meanPrediction": float(np.average(probabilities[mask], weights=weights[mask])),
             "observedTWinRate": float(np.average(labels[mask], weights=weights[mask]))}
            for i in range(bins) if (mask := assignment == i).any()]


def ece(labels, probabilities, weights, bins=10):
    buckets = calibration_bins(labels, probabilities, weights, bins)
    total = np.sum(weights)
    return float(sum(b["weight"] / total * abs(b["meanPrediction"] - b["observedTWinRate"])
                     for b in buckets))


def metrics(labels, probability, weights):
    labels, probability, weights = map(np.asarray, (labels, probability, weights))
    if not (len(labels) == len(probability) == len(weights)) or not len(labels):
        raise ValueError("Metrics require aligned, nonempty arrays")
    if (not np.isin(labels, [0, 1]).all() or not np.isfinite(probability).all()
            or not ((0 <= probability) & (probability <= 1)).all()
            or not np.isfinite(weights).all() or not (weights > 0).all()):
        raise ValueError("Invalid labels, probabilities or weights")
    probability = np.clip(probability.astype(float), 1e-6, 1 - 1e-6)
    return {"logLoss": float(log_loss(labels, probability, sample_weight=weights, labels=[0, 1])),
            "brierScore": float(brier_score_loss(labels, probability, sample_weight=weights)),
            "rocAuc": float(roc_auc_score(labels, probability, sample_weight=weights))
                if np.unique(labels).size == 2 else None,
            "accuracy": float(accuracy_score(labels, probability >= 0.5, sample_weight=weights)),
            "ece10": ece(labels, probability, weights)}


def counts(metadata):
    return {"rows": len(metadata), "rounds": len(metadata[["matchId", "roundId"]].drop_duplicates()),
            "matches": int(metadata.matchId.nunique()), "weight": float(metadata.weight.sum())}


def summarize(metadata, probability):
    probability = np.asarray(probability, dtype=float)
    if probability.shape != (len(metadata),):
        raise ValueError("Predictions must align with metadata")
    if np.isinf(probability).any() or ((probability < 0) | (probability > 1)).any():
        raise ValueError("Invalid probability; only absent predictions may be NaN")
    present = np.isfinite(probability)
    retained = metadata.loc[present]
    available, expected = counts(retained), counts(metadata)
    result = {**available, "coverage": {
        "expected": expected, "missingRows": len(metadata) - len(retained),
        "rowFraction": len(retained) / len(metadata) if len(metadata) else None,
        "weightFraction": available["weight"] / expected["weight"] if expected["weight"] else None,
    }, "metrics": None, "aucUnavailableReason": None, "calibrationBins": []}
    if len(retained):
        labels, weights = retained.label.to_numpy(int), retained.weight.to_numpy(float)
        result["metrics"] = metrics(labels, probability[present], weights)
        if result["metrics"]["rocAuc"] is None:
            result["aucUnavailableReason"] = "single-class"
        result["calibrationBins"] = calibration_bins(labels, probability[present], weights)
    else:
        result["aucUnavailableReason"] = "no-predictions"
    return result


def slice_masks(metadata):
    def column(name):
        return pd.to_numeric(metadata.get(name, pd.Series(np.nan, index=metadata.index)),
                             errors="coerce").to_numpy(float)

    t, ct = column("tAlive"), column("ctAlive")
    elapsed, economy = column("liveElapsed"), column("equipmentDifference")
    alive, equipment, position = column("aliveKnown"), column("equipmentKnown"), column("positionKnown")
    bomb = column("bombRemaining")
    phase = metadata.phase.to_numpy()
    post_plant = phase == "post-plant"
    one_vs_n = (t > 0) & (ct > 0) & ((t == 1) | (ct == 1))
    # In 5v5, either side <= 2 already implies total <= 7. Keep the old
    # oneVsN / totalAliveAtMost6 slices and trained isClutch feature distinct.
    broad_endgame = broad_endgame_mask(phase, t, ct)
    return {
        "all": np.ones(len(metadata), bool),
        "live": phase == "live", "postPlant": post_plant,
        "fiveVsFive": (t == 5) & (ct == 5),
        "totalAliveAtMost6": t + ct <= 6,
        "broadEndgame": broad_endgame,
        "oneVsN": one_vs_n, "oneVsOne": (t == 1) & (ct == 1),
        "oneVsMany": one_vs_n & (t + ct > 2),
        "openingFiveSeconds": (elapsed >= 0) & (elapsed <= 5),
        "tEquipmentAdvantage": economy > 0, "equalEquipmentValue": economy == 0,
        "ctEquipmentAdvantage": economy < 0, "equipmentValueUnknown": np.isnan(economy),
        "equipmentMissing": equipment < alive, "positionMissing": position < alive,
        "postPlantBombRemaining0To10": post_plant & (bomb >= 0) & (bomb <= 10),
        "postPlantBombRemaining10To20": post_plant & (bomb > 10) & (bomb <= 20),
        "postPlantBombRemainingOver20": post_plant & (bomb > 20),
    }


def slices(metadata, labels, probabilities, weights):
    # labels/weights remain in the signature for existing training callers.
    meta = metadata.assign(label=np.asarray(labels), weight=np.asarray(weights))
    probability = np.asarray(probabilities)
    return {name: summarize(meta.loc[mask], probability[mask])
            for name, mask in slice_masks(meta).items()}


def per_match_metrics(metadata, labels, probabilities, weights):
    meta = metadata.assign(label=np.asarray(labels), weight=np.asarray(weights))
    probability = np.asarray(probabilities)
    return {match: summarize(meta.loc[mask], probability[mask])
            for match in sorted(meta.matchId.unique())
            if (mask := meta.matchId.to_numpy() == match).any()}


def match_bootstrap(metadata, probability, reference=None, *, seed=42, resamples=2000):
    """Resample whole matches; paired differences use identical rows and draws."""
    if resamples < 2:
        raise ValueError("bootstrap resamples must be at least two")
    probability = np.asarray(probability, dtype=float)
    summarize(metadata, probability)  # Validate before treating NaN as non-output.
    mask = np.isfinite(probability)
    if reference is not None:
        reference = np.asarray(reference, dtype=float)
        summarize(metadata, reference)
        mask &= np.isfinite(reference)
    meta = metadata.loc[mask].reset_index(drop=True)
    p = probability[mask]
    r = reference[mask] if reference is not None else None
    result = {"unit": "match", "method": "percentile", "confidenceLevel": .95,
              "seed": seed, "resamples": resamples, **counts(meta), "intervals": None,
              "difference": "candidate-minus-reference" if r is not None else None}
    if meta.matchId.nunique() < 2:
        result["unavailableReason"] = "fewer-than-two-matches"
        return result
    # Aggregate with sklearn once per match. Resampling these weighted sums is
    # equivalent to copying all rows, without allocating millions of row copies.
    totals, mass = [], []
    for match in sorted(meta.matchId.unique()):
        selected = meta.matchId.to_numpy() == match
        y, w = meta.label.to_numpy(int)[selected], meta.weight.to_numpy(float)[selected]
        value = metrics(y, p[selected], w)
        other = metrics(y, r[selected], w) if r is not None else {"logLoss": 0, "brierScore": 0}
        mass.append(w.sum())
        totals.append([(value[k] - other[k]) * w.sum() for k in ("logLoss", "brierScore")])
    totals, mass = np.asarray(totals).T, np.asarray(mass)

    def statistic(indices, axis=-1):
        return totals[:, indices].sum(axis=axis) / mass[indices].sum(axis=axis)

    sampled = bootstrap((np.arange(len(mass)),), statistic, vectorized=True,
                        n_resamples=resamples, batch=128, method="percentile",
                        confidence_level=.95, rng=np.random.default_rng(seed))
    point = statistic(np.arange(len(mass)))
    result["intervals"] = {name: {"estimate": float(point[i]),
        "low": float(sampled.confidence_interval.low[i]), "high": float(sampled.confidence_interval.high[i])}
        for i, name in enumerate(("logLoss", "brierScore"))}
    return result


def reference_predictions(training_features, training_meta, evaluation_features, seed=42):
    """Fixed diagnostics: neither fitting nor calibration consumes evaluation labels."""
    if training_meta.label.nunique() != 2:
        raise ValueError("Reference baseline training requires both classes")
    prior = DummyClassifier(strategy="prior")
    prior.fit(training_features, training_meta.label, sample_weight=training_meta.weight)
    small = Pipeline([
        ("columns", ColumnTransformer([("small", Pipeline([
            ("imputer", SimpleImputer(strategy="median", keep_empty_features=True)),
            ("scaler", StandardScaler()),
        ]), list(SMALL_FEATURES))])),
        ("model", LogisticRegression(max_iter=3000, solver="lbfgs", random_state=seed)),
    ])
    small.fit(training_features, training_meta.label, model__sample_weight=training_meta.weight)
    return {"trainingPrior": prior.predict_proba(evaluation_features)[:, 1],
            "smallLogistic": small.predict_proba(evaluation_features)[:, 1]}


def evaluation_report(metadata, predictions, *, reference, seed=42, resamples=2000):
    if reference not in predictions:
        raise ValueError("Reference prediction is absent")
    report = {"weighting": "original per-round weights; no renormalization after slicing or missing predictions",
              "bootstrapScope": "Log Loss and Brier only; conditional on observed matches, not new-match certification",
              "sliceDefinitions": {"broadEndgame": BROAD_ENDGAME_DEFINITION},
              "unavailableSlices": {"pistolRound": "round format/half metadata is not verified in schema v4.2"},
              "models": {}, "pairedDifferences": {}}
    masks = slice_masks(metadata)
    key_slices = ("openingFiveSeconds", "fiveVsFive", "postPlant", "broadEndgame", "oneVsN")
    for name, probability in predictions.items():
        probability = np.asarray(probability, dtype=float)
        report["models"][name] = {
            "overall": summarize(metadata, probability),
            "slices": slices(metadata, metadata.label, probability, metadata.weight),
            "perMatch": per_match_metrics(metadata, metadata.label, probability, metadata.weight),
            "uncertainty": match_bootstrap(metadata, probability, seed=seed, resamples=resamples),
            "sliceUncertainty": {key: match_bootstrap(metadata.loc[masks[key]], probability[masks[key]],
                                  seed=seed, resamples=resamples) for key in key_slices},
        }
        if name != reference:
            report["pairedDifferences"][name] = {"reference": reference,
                **match_bootstrap(metadata, probability, predictions[reference], seed=seed, resamples=resamples),
                "slices": {key: match_bootstrap(metadata.loc[masks[key]], probability[masks[key]],
                           np.asarray(predictions[reference])[masks[key]], seed=seed, resamples=resamples)
                           for key in key_slices}}
    return report


def export_coverage(manifest, match_ids):
    """Coverage of eligible scheduled candidates, not all ticks or void attempts."""
    by_match = {}
    for item in manifest["matches"]:
        if item["matchId"] not in match_ids:
            continue
        retained = item["report"]["rowCount"]
        rejected = item["report"].get("rejected", {})
        missing = sum(rejected.values())
        by_match[item["matchId"]] = {"retainedRows": retained, "rejectedRows": missing,
            "candidateRows": retained + missing, "reasons": rejected}
    retained = sum(m["retainedRows"] for m in by_match.values())
    candidates = sum(m["candidateRows"] for m in by_match.values())
    return {"scope": "scheduled candidates in completed eligible rounds; excludes void/ineligible attempts and unsampled ticks",
            "retainedRows": retained, "candidateRows": candidates,
            "retainedFraction": retained / candidates if candidates else None, "perMatch": by_match}


def plot_reliability(report, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    fig, axes = plt.subplots(1, 3, figsize=(13, 4), layout="constrained")
    for axis, section in zip(axes, ("all", "openingFiveSeconds", "postPlant")):
        axis.plot([0, 1], [0, 1], "--", color="gray", label="Ideal")
        for name, model in report["models"].items():
            bins = model["slices"][section]["calibrationBins"]
            if bins:
                axis.plot([b["meanPrediction"] for b in bins], [b["observedTWinRate"] for b in bins],
                          marker="o", markersize=3, label=name)
        axis.set(xlim=(0, 1), ylim=(0, 1), title=section,
                 xlabel="Round-weighted mean prediction", ylabel="Round-weighted T win rate")
        axis.legend(fontsize=7)
    fig.savefig(path, dpi=160)
    plt.close(fig)
