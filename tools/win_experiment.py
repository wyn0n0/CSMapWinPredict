#!/usr/bin/env python3
"""Prepare a registry and freeze a new experiment without changing legacy artifacts."""

import argparse
import hashlib
import json
import math
from datetime import datetime
from pathlib import Path


ROLES = ("train", "development", "test", "legacy-validation")
EVALUATION_SOURCES = {"evaluator": "train_win_baseline_v4.py", "evaluationHelpers": "win_evaluation.py",
                      "freezer": "win_experiment.py"}


def file_sha256(path):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def legacy_roles(manifest, split):
    if (manifest.get("status"), manifest.get("schemaVersion"), manifest.get("semanticVersion")) != (
            "complete", 4, "mirage-semantics-v4.2"):
        raise ValueError("Expected a complete Mirage v4.2 legacy manifest")
    ids = [m["matchId"] for m in manifest["matches"]]
    validation = [m["matchId"] for m in split["validation"]]
    if (not ids or not validation or len(ids) != len(set(ids)) or len(validation) != len(set(validation))
            or not set(validation) < set(ids)):
        raise ValueError("Legacy matches/split are invalid or duplicated")
    return {match: "legacy-validation" if match in validation else "train" for match in ids}


def registry_template(manifest, split):
    roles = legacy_roles(manifest, split)
    return {"registryVersion": 1, "status": "awaiting-metadata-and-new-matches", "matches": [
        {"matchId": m["matchId"], "demoSha256": m["matchId"], "fileName": m["file"],
         "demoPath": None, "matchKey": None, "playedAtUtc": None, "eventId": None,
         "gameVersion": None, "mapName": "de_mirage", "parserVersion": manifest.get("parserVersion"),
         "source": None, "role": roles[m["matchId"]], "unseen": False}
        for m in manifest["matches"]]}


def plan_template():
    return {"protocolVersion": 1, "testWindow": {"startUtc": None, "endUtc": None, "matchCount": None},
            "candidates": ["trainingPrior", "smallLogistic", "logistic", "lightgbm"],
            "adoption": {"maxLogLossRegression": None, "maxBrierRegression": None,
                         "maxKeySliceLogLossRegression": None, "maxInferenceMillisecondsPerSample": None}}


def utc_time(value):
    if not isinstance(value, str):
        raise ValueError("Explicit UTC match dates and test window are required")
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None or parsed.utcoffset().total_seconds() != 0:
        raise ValueError("Dates must include a UTC timezone")
    return parsed


def freeze_experiment(registry, plan, manifest, split, registry_directory):
    roles = legacy_roles(manifest, split)
    if registry.get("registryVersion") != 1 or plan.get("protocolVersion") != 1:
        raise ValueError("Unsupported registry or protocol version")
    window = plan["testWindow"]
    start, end = utc_time(window["startUtc"]), utc_time(window["endUtc"])
    if start >= end or type(window["matchCount"]) is not int or window["matchCount"] < 2:
        raise ValueError("Freeze a positive test time window and at least two complete matches")
    candidates = plan["candidates"]
    if (not candidates or any(not isinstance(c, str) or not c.strip() for c in candidates)
            or len(candidates) != len(set(candidates))):
        raise ValueError("Candidate names must be nonempty and unique")
    for key in plan_template()["adoption"]:
        value = plan["adoption"].get(key)
        if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
            raise ValueError(f"Freeze the adoption budget before testing: {key}")
    seen_ids, seen_hashes, seen_matches = set(), set(), set()
    matches, dates = [], {}
    for item in registry["matches"]:
        match_id, digest, match_key = item["matchId"], item["demoSha256"], item["matchKey"]
        if (not isinstance(match_id, str) or not match_id or not isinstance(digest, str)
                or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest)
                or not isinstance(match_key, str) or not match_key.strip()):
            raise ValueError("Every match needs a matchId, SHA-256 and source matchKey")
        if match_id in seen_ids or digest in seen_hashes or match_key in seen_matches:
            raise ValueError("Duplicate match identity or Demo hash (including cross-role duplicates)")
        seen_ids.add(match_id)
        seen_hashes.add(digest)
        seen_matches.add(match_key)
        if match_id != digest:
            raise ValueError("v4 matchId must equal the Demo SHA-256")
        role = item["role"]
        if role not in ROLES or (match_id in roles and role != roles[match_id]):
            raise ValueError("Legacy split membership cannot change")
        if role == "legacy-validation" and match_id not in roles:
            raise ValueError("New matches cannot be relabeled as legacy validation")
        if item["mapName"] != "de_mirage":
            raise ValueError("Only Mirage belongs in this experiment")
        for key in ("eventId", "gameVersion", "parserVersion", "source", "demoPath"):
            if not isinstance(item.get(key), str) or not item[key].strip():
                raise ValueError(f"Missing verifiable match metadata: {key}")
        played = utc_time(item["playedAtUtc"])
        dates[match_id] = played
        if role == "test" and (item.get("unseen") is not True or not start <= played < end):
            raise ValueError("Final test matches must be unseen and inside the frozen time window")
        source = Path(item["demoPath"])
        if not source.is_absolute():
            source = Path(registry_directory) / source
        if file_sha256(source) != digest:
            raise ValueError("Demo content differs from its registered SHA-256")
        matches.append({**{key: item.get(key) for key in (
            "matchId", "demoSha256", "matchKey", "playedAtUtc", "eventId", "gameVersion",
            "mapName", "parserVersion", "source", "role", "unseen")}, "fileName": source.name})
    if set(roles) - seen_ids:
        raise ValueError("All legacy matches must remain registered with their original roles")
    partitions = {role: sorted(m["matchId"] for m in matches if m["role"] == role) for role in ROLES}
    if len(partitions["test"]) != window["matchCount"] or not partitions["development"]:
        raise ValueError("Test match count must match the plan and development must be nonempty")
    if min(dates[m] for m in partitions["test"]) <= max(
            dates[m["matchId"]] for m in matches if m["role"] != "test"):
        raise ValueError("Final test must be later than every training/development/legacy match")
    return {"status": "frozen", "protocolVersion": 1, "schemaVersion": 4,
            "semanticVersion": "mirage-semantics-v4.2", "plan": plan,
            "grouping": "whole source match; duplicate matchKey or Demo SHA-256 rejected",
            "primaryMetric": "round-weighted Log Loss", "secondaryMetrics": ["Brier", "AUC", "ECE-10"],
            "weighting": "original per-round weights; no renormalization in slices",
            "bootstrap": {"unit": "match", "paired": True, "method": "percentile", "resamples": 2000, "seed": 42},
            "calibration": "identity/sigmoid/isotonic selected on grouped training OOF or development only",
            "testUse": "one frozen-candidate evaluation; no tuning or recalibration; old validation is diagnostic only",
            "partitions": partitions, "matches": sorted(matches, key=lambda m: m["matchId"])}


def load_frozen_experiment(path, expected_sha256, legacy_split_path):
    if not expected_sha256 or file_sha256(path) != expected_sha256:
        raise ValueError("Frozen experiment SHA-256 mismatch")
    value = read_json(path)
    if (value.get("status"), value.get("protocolVersion"), value.get("schemaVersion"),
            value.get("semanticVersion")) != ("frozen", 1, 4, "mirage-semantics-v4.2"):
        raise ValueError("Experiment must be a frozen v4.2 protocol")
    if value["sources"]["legacyValidationSplit"]["sha256"] != file_sha256(legacy_split_path):
        raise ValueError("Legacy validation split SHA-256 mismatch")
    for name, filename in EVALUATION_SOURCES.items():
        if value["sources"][name]["sha256"] != file_sha256(Path(__file__).with_name(filename)):
            raise ValueError("Frozen evaluation source SHA-256 mismatch; review and freeze a new experiment")
    partitions = value["partitions"]
    ids = [m for role in ROLES for m in partitions[role]]
    if len(ids) != len(set(ids)) or set(ids) != {m["matchId"] for m in value["matches"]}:
        raise ValueError("Frozen partition membership overlaps or is incomplete")
    legacy = {m["matchId"] for m in read_json(legacy_split_path)["validation"]}
    if set(partitions["legacy-validation"]) != legacy:
        raise ValueError("Legacy validation membership drift")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("init", "freeze"))
    parser.add_argument("--legacy-manifest", required=True, type=Path)
    parser.add_argument("--legacy-validation-split", required=True, type=Path)
    parser.add_argument("--registry", type=Path)
    parser.add_argument("--plan", type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    if args.output_dir.exists():
        raise FileExistsError(args.output_dir)
    manifest, split = read_json(args.legacy_manifest), read_json(args.legacy_validation_split)
    if args.command == "init":
        outputs = {"registry.json": registry_template(manifest, split), "experiment-plan.json": plan_template()}
    else:
        if args.registry is None or args.plan is None:
            parser.error("freeze requires --registry and --plan")
        frozen = freeze_experiment(read_json(args.registry), read_json(args.plan), manifest, split, args.registry.parent)
        frozen["sources"] = {name: {"file": path.name, "sha256": file_sha256(path)} for name, path in {
            "legacyManifest": args.legacy_manifest, "legacyValidationSplit": args.legacy_validation_split,
            "registry": args.registry, "plan": args.plan,
            **{name: Path(__file__).with_name(filename) for name, filename in EVALUATION_SOURCES.items()},
        }.items()}
        outputs = {"experiment.json": frozen}
    args.output_dir.mkdir(parents=True, exist_ok=False)
    for name, value in outputs.items():
        with (args.output_dir / name).open("x", encoding="utf-8") as target:
            json.dump(value, target, ensure_ascii=False, indent=2, allow_nan=False)
    print(json.dumps({"status": "pending" if args.command == "init" else "frozen",
                      "artifacts": {name: file_sha256(args.output_dir / name) for name in outputs}}))


if __name__ == "__main__":
    main()
