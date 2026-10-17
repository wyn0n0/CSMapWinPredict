"""Train one fixed C4 model on endgames only, with an observed match-held-out test."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import sys
import time

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
from sklearn.model_selection import GroupKFold
from threadpoolctl import threadpool_limits

from endgame_prediction.predict import EndgamePredictor
from tools import train_win_experiments as experiments
from tools import train_win_baseline_v4 as v4
from tools.win_evaluation import BROAD_ENDGAME_DEFINITION, broad_endgame_mask, counts, evaluation_report
from tools.win_feature_sets import FEATURE_SETS, candidate_specs

ROOT = Path(__file__).resolve().parents[1]
MODULE = Path(__file__).resolve().parent
PARENT_PLAN = ROOT / ".artifacts/win-broad-endgame-20261015-r1/frozen/plan.json"
PARENT_SHA256 = "5af45ee87777d4edcc7ae4645b16de630353e0a77df6f0ac9ce4b0b64dd4317d"
REFERENCE = ROOT / ".artifacts/win-broad-endgame-20261015-r1/results/B_c4_logistic"
SPEC = next(s for s in candidate_specs("broad-endgame") if s["id"] == "B_c4_logistic")
SOURCES = experiments.SOURCES + ["endgame_prediction/train.py", "endgame_prediction/predict.py",
                                "endgame_prediction/test_train.py"]


def split_endgames(frame, meta, train_ids, test_ids):
    """Filter before fitting anything; preserve whole-match partitions and original weights."""
    train_ids, test_ids = set(train_ids), set(test_ids)
    if train_ids & test_ids or set(meta.matchId) != train_ids | test_ids:
        raise ValueError("Overlapping, missing or unexpected partition matches")
    if len(frame) != len(meta) or not frame.index.equals(meta.index):
        raise ValueError("Feature/metadata row alignment differs")
    for column, feature in (("tAlive", "features.t.alive"), ("ctAlive", "features.ct.alive")):
        if not np.array_equal(meta[column].to_numpy(float), frame[feature].to_numpy(float), equal_nan=True):
            raise ValueError("Feature/metadata player counts differ")
    if not np.array_equal(meta.phase, frame["features.phase"]):
        raise ValueError("Feature/metadata phases differ")
    if not frame["mapName"].eq("de_mirage").all():
        raise ValueError("Only Mirage data is supported")
    eligible = broad_endgame_mask(frame["features.phase"], frame["features.t.alive"], frame["features.ct.alive"])
    columns = list(sum(FEATURE_SETS[SPEC["featureSet"]], ()))
    partitions, coverage = {}, {}
    for role, ids in (("train", train_ids), ("test", test_ids)):
        before = meta.matchId.isin(ids).to_numpy()
        retained = before & eligible
        x, m = frame.loc[retained, columns].reset_index(drop=True), meta.loc[retained].reset_index(drop=True)
        if m.empty or set(m.matchId) != ids:
            raise ValueError(f"{role} has a match with no endgame samples")
        if m.duplicated(["matchId", "roundId", "tick"]).any():
            raise ValueError(f"{role} contains repeated sample keys")
        partitions[role] = (x, m)
        coverage[role] = dict(before=counts(meta.loc[before]), retained=counts(m),
                              excludedRows=int((before & ~eligible).sum()))
    return partitions, coverage


def write_dataset(path, frame, meta, config):
    samples = experiments.samples_for(frame, SPEC, config)
    with path.open("x", encoding="utf-8") as output:
        for sample, (_, row) in zip(samples, meta.iterrows(), strict=True):
            output.write(json.dumps(dict(sample=sample, metadata=v4.json_safe_features(row)),
                                    separators=(",", ":"), allow_nan=False) + "\n")


def run(output):
    output = Path(output).resolve()
    output.relative_to(MODULE / "runs")
    if output.exists():
        raise FileExistsError(output)
    start = time.perf_counter()
    parent = experiments.load_plan(PARENT_PLAN, PARENT_SHA256)
    reference = EndgamePredictor(REFERENCE)
    output.mkdir(parents=True)
    source_hashes = {name: v4.sha256(ROOT / name) for name in SOURCES}
    plan = dict(
        status="frozen", contract="endgame-only-training-v1", createdAtUtc=datetime.now(timezone.utc).isoformat(),
        parentPlan=str(PARENT_PLAN), parentPlanSha256=PARENT_SHA256,
        definition=BROAD_ENDGAME_DEFINITION, candidate=SPEC,
        partitions=dict(train=parent["partitions"]["train"], test=parent["partitions"]["development"],
                        excludedLegacyValidation=parent["partitions"]["legacy-validation"]),
        testPurpose="previously-observed held-out diagnostic; not unseen or used for fitting/tuning",
        unseenTest=False, productionPromotion=False,
        seed=42, folds=5, threads=4, bootstrapResamples=2000,
        weighting="original per-round weights retained after filtering; no renormalization",
        calibration="fixed sigmoid from match-grouped training OOF only",
        parameterSearch=False, outputDirectory=str(output), runtime=experiments.runtime_versions(),
        sources=source_hashes, inputs=parent["inputs"],
        referenceModelSha256=reference.info["artifactSha256"],
        referenceFiles={p.name: v4.sha256(p) for p in REFERENCE.iterdir() if p.is_file()},
        budget=dict(estimatedMinutes="3-10", estimatedOutputGiB=1, freeDiskBytes=shutil.disk_usage(output).free),
    )
    experiments.save(output / "plan.json", plan)
    plan_hash = v4.sha256(output / "plan.json")
    for name in SOURCES:
        target = output / "source" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / name, target)

    print("Auditing existing v4.2 data and spatial companions", flush=True)
    frame, meta, _, config, audit = experiments.load_data(parent)
    partitions, coverage = split_endgames(frame, meta, plan["partitions"]["train"], plan["partitions"]["test"])
    del frame, meta
    train_x, train_meta = partitions["train"]
    test_x, test_meta = partitions["test"]
    if train_meta.matchId.nunique() < plan["folds"] or set(train_meta.label) != {0, 1}:
        raise ValueError("Insufficient endgame matches/classes for grouped training")
    experiments.save(output / "data-audit.json", dict(sourceAudit=audit, selection=coverage,
                                                       definition=BROAD_ENDGAME_DEFINITION))
    for role, (x, m) in partitions.items():
        write_dataset(output / f"{role}.jsonl", x, m, config)
    print(json.dumps(coverage), flush=True)
    splits = list(GroupKFold(n_splits=plan["folds"]).split(train_x, train_meta.label, train_meta.matchId))
    experiments.save(output / "folds.json", [dict(fold=i, training=sorted(set(train_meta.iloc[a].matchId)),
        heldOut=sorted(set(train_meta.iloc[b].matchId))) for i, (a, b) in enumerate(splits)])
    # Bind the exact filtered rows before fitting the model or probability calibrator.
    experiments.save(output / "training-inputs.json", dict(planSha256=plan_hash,
        files={name: v4.sha256(output / name) for name in ("train.jsonl", "test.jsonl", "folds.json", "data-audit.json")}))
    print("Training one C4 logistic model on endgame rows only (5 grouped folds)", flush=True)
    fit_start = time.perf_counter()
    with threadpool_limits(limits=plan["threads"]):
        model, calibration, oof = experiments.fit_candidate(
            SPEC, train_x, train_meta, splits, plan["seed"], plan["threads"])
    fit_seconds = time.perf_counter() - fit_start
    # Self-test fixtures also come from training, never from the held-out test.
    indices = np.linspace(0, len(train_x) - 1, 3, dtype=int)
    experiments.save_bundle(output / "model", model, SPEC, calibration, oof,
                            train_x.iloc[indices], config, plan_hash, fit_seconds)
    runtime = EndgamePredictor(output / "model")
    samples = experiments.samples_for(test_x, SPEC, config)
    probability, cost = experiments.runtime_predict(runtime, samples)
    expected = model.predict_proba(test_x)[:, 1]
    parity = float(np.max(np.abs(probability - expected)))
    if parity > 1e-12:
        raise ValueError("Saved model inference differs from trained model")
    reference_probability, _ = experiments.runtime_predict(reference, samples)
    predictions = dict(endgame_only=probability, previous_full_round=reference_probability)
    hashes = dict(endgame_only=runtime.info["artifactSha256"], previous_full_round=reference.info["artifactSha256"])
    evaluation = evaluation_report(test_meta, predictions, reference="previous_full_round",
                                   seed=plan["seed"], resamples=plan["bootstrapResamples"])
    experiments.write_predictions(output / "test-predictions.jsonl", test_meta, predictions, hashes)
    report = dict(status="complete", purpose=plan["testPurpose"], planSha256=plan_hash, data=coverage,
                  evaluation=evaluation, trainingOofCalibration=calibration,
                  fitSeconds=fit_seconds, inference=cost, inferenceMaxAbsoluteDifference=parity,
                  runtime=experiments.runtime_versions(), wallSeconds=time.perf_counter() - start,
                  modelArtifactSha256=hashes["endgame_only"], productionModelChanged=False)
    experiments.save(output / "report.json", report)
    if any(v4.sha256(ROOT / name) != digest for name, digest in source_hashes.items()):
        raise ValueError("Training source changed during the run")
    experiments.load_plan(PARENT_PLAN, PARENT_SHA256)
    if any(v4.sha256(REFERENCE / name) != digest for name, digest in plan["referenceFiles"].items()):
        raise ValueError("Reference model changed during the run")
    experiments.save(output / "completion.json", dict(status="complete", planSha256=plan_hash,
        artifacts={p.relative_to(output).as_posix(): v4.sha256(p) for p in output.rglob("*") if p.is_file()}))
    print(json.dumps(dict(output=str(output), data=coverage, fitSeconds=fit_seconds,
        test=evaluation["models"]["endgame_only"]["overall"]["metrics"],
        previous=evaluation["models"]["previous_full_round"]["overall"]["metrics"]), indent=2), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", required=True, type=Path, help="new directory under endgame_prediction/runs/")
    run(parser.parse_args().output_dir)
