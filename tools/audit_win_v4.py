#!/usr/bin/env python3
"""Validate a completed v4 export and compare source-hash/tick identities with v3.

Uses only the standard library. Never fits or replaces a model.
"""
import argparse
import json
import math
from collections import Counter, defaultdict
from pathlib import Path


def records(path):
    with path.open(encoding="utf-8-sig") as source:
        for line in source:
            if line.strip():
                yield json.loads(line)


def validate_row(row):
    if row["schemaVersion"] != 4 or row["mapName"] != "de_mirage":
        raise ValueError("Expected Mirage schema v4")
    if not row["roundId"] or row["roundNumber"] < 1 or row["labelTWin"] not in (0, 1):
        raise ValueError("Invalid identity or label")
    if not 0 < row["sampleWeight"] <= 1:
        raise ValueError("Invalid weight")
    f = row["features"]
    q, clock = f["quality"], f["clock"]
    if not q["rosterKnown"] or q["lifeKnown"] != 10 or q["quality"] == "unusable":
        raise ValueError("Unusable roster exported")
    if not clock["clockKnown"] or clock["liveElapsedSeconds"] is None or clock["liveElapsedSeconds"] < 0:
        raise ValueError("Unknown live clock exported")
    if f["phase"] not in ("live", "post-plant"):
        raise ValueError("Non-live sample")
    if f["phase"] == "post-plant" and clock["roundRemainingSeconds"] is not None:
        raise ValueError("Round clock must be inapplicable post-plant")
    if f["t"]["alive"] + f["ct"]["alive"] != q["aliveKnown"]:
        raise ValueError("Alive totals disagree with roster")
    if q["aliveEquipmentKnown"] < q["aliveKnown"]:
        for side in ("t", "ct"):
            if f[side]["totalMoney"] is not None:
                raise ValueError("Partial equipment masquerades as a total")
    def walk(value):
        if isinstance(value, float) and not math.isfinite(value):
            raise ValueError("Non-finite number")
        if isinstance(value, dict):
            for k, v in value.items():
                if k in ("playerId", "carrierId", "steamId"):
                    raise ValueError("Player identity leaked")
                walk(v)
        elif isinstance(value, list):
            for v in value:
                walk(v)
    walk(row)


def audit(v3, directory):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    if manifest["status"] != "complete":
        raise ValueError("Export incomplete")
    old = {}
    old_rounds = defaultdict(set)
    old_number_anomalies = defaultdict(set)
    for r in records(v3):
        key = (r["matchId"], r["tick"])
        if key in old:
            raise ValueError("Duplicate v3 match/tick")
        old[key] = (r["roundNumber"], r["labelTWin"], r["features"]["elapsedSeconds"], len(r["features"].get("players", [])))
        old_rounds[r["matchId"]].add(r["roundNumber"])
        if r["roundNumber"] != r["features"]["scoreT"] + r["features"]["scoreCT"] + 1:
            old_number_anomalies[r["matchId"]].add(r["roundNumber"])
    seen = set()
    weights = defaultdict(float)
    labels = {}
    official = {}
    stats = defaultdict(Counter)
    changed_rounds = defaultdict(set)
    for r in records(directory / "samples.jsonl"):
        validate_row(r)
        identity = (r["matchId"], r["roundId"])
        key = (*identity, r["tick"])
        if key in seen:
            raise ValueError("Duplicate v4 key")
        seen.add(key)
        if identity in labels and labels[identity] != r["labelTWin"]:
            raise ValueError("Conflicting label")
        labels[identity] = r["labelTWin"]
        official_key = (r["matchId"], r["segmentId"], r["roundNumber"])
        if official_key in official and official[official_key] != r["roundId"]:
            raise ValueError("Multiple completed attempts for official round")
        official[official_key] = r["roundId"]
        weights[identity] += r["sampleWeight"]
        s = stats[r["matchId"]]
        s["v4Rows"] += 1
        old_row = old.pop((r["matchId"], r["tick"]), None)
        if old_row is None:
            s["newTicks"] += 1
        else:
            s["commonTicks"] += 1
            if old_row[3] < 10:
                s["retainedOldMissingEntityRows"] += 1
            if old_row[0] != r["roundNumber"]:
                s["renumberedRows"] += 1
                changed_rounds[r["matchId"]].add(old_row[0])
            if old_row[1] != r["labelTWin"]:
                s["changedLabels"] += 1
            if old_row[2] != r["features"]["clock"]["liveElapsedSeconds"]:
                s["changedElapsedRows"] += 1
    if any(abs(w - 1) > 1e-6 for w in weights.values()):
        raise ValueError("Round weight sum is not one")
    for match, _ in weights:
        stats[match]["v4Rounds"] += 1
    for match, _ in old:
        stats[match]["v3OnlyTicks"] += 1
    files = {m["matchId"]: m["file"] for m in manifest["matches"]}
    per_match = []
    for match, counts in stats.items():
        per_match.append(dict(matchId=match, file=files.get(match), **counts,
            oldRoundCount=len(old_rounds[match]),
            oldNumberAnomalyRounds=sorted(old_number_anomalies[match]),
            renumberedOldRounds=sorted(changed_rounds[match])))
    return dict(schemaVersion=4, valid=True, rows=len(seen), rounds=len(weights),
                matches=len({m for m, _ in weights}), perMatch=per_match)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--v3", type=Path, required=True)
    p.add_argument("--v4-dir", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    args = p.parse_args()
    if args.output.exists():
        raise FileExistsError(args.output)
    result = audit(args.v3, args.v4_dir)
    with args.output.open("x", encoding="utf-8") as target:
        json.dump(result, target, ensure_ascii=False, indent=2)
    print(json.dumps({k: v for k, v in result.items() if k != "perMatch"}))


if __name__ == "__main__":
    main()
