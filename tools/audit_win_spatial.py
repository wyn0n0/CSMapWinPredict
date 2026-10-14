"""Audit win-spatial-v1 companions against keyed v4 rows and raw as-of evidence.

Standard library only; does not select radii, train or load models.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
from itertools import zip_longest
import json
import math
from pathlib import Path
import struct

from audit_win_v4 import records, validate_row


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def expected_features(source, manifest):
    # .NET serializes float32 origins as shortest round-trip decimals. Recover the
    # float32 values before computing in double precision, as the producer does.
    def point(item):
        xyz = [item[k] for k in ("x", "y", "z")]
        if any(v is None or not math.isfinite(v) for v in xyz):
            return None
        return tuple(struct.unpack("f", struct.pack("f", v))[0] for v in xyz)

    players, q, bomb = source["players"], source["quality"], source["bomb"]
    require(all(p["team"] in ("T", "CT") for p in players), "Non-playing team in evidence")
    complete = (q["rosterKnown"] and q["tMembers"] == q["ctMembers"] == 5 and
                q["lifeKnown"] == 10 and q["aliveKnown"] == len(players) and
                len({p["id"] for p in players}) == len(players) and
                all(sum(p["team"] == t for p in players) <= 5 for t in ("T", "CT")))
    positions_agree = sum(point(p) is not None for p in players) == q["alivePositionKnown"]
    carrier = next((p for p in players if p["id"] == bomb["carrierId"] and p["team"] == "T"), None)
    carried = bomb["state"] in ("carried", "planting")
    bomb_status = ("not-applicable" if bomb["state"] in ("defused", "exploded")
                   else "unknown-state" if bomb["state"] not in ("carried", "planting", "dropped", "planted", "defusing")
                   else "missing-carrier" if carried and carrier is None
                   else "known" if point(bomb) is not None else "missing-position")
    def distance(a, b):
        return math.hypot(a[0] - b[0], a[1] - b[1])

    def team(side):
        members = [p for p in players if p["team"] == side]
        positioned = [point(p) for p in members if point(p) is not None]
        status = ("incomplete-roster" if not complete else "missing-position"
                  if not positions_agree or len(positioned) != len(members) else "complete")
        valid = status == "complete"
        distances = sorted(distance(p, point(bomb)) for p in positioned) if bomb_status == "known" else []
        support = [min(distance(point(p), point(other)) for other in members if other["id"] != p["id"])
                   for p in members] if valid and len(members) >= 2 else []
        def near(site):
            anchor = manifest[site]
            return sum(distance(p, (anchor["x"], anchor["y"])) <= manifest["radii"]["siteRadius"]
                       for p in positioned) if valid else None
        return dict(observedAliveCount=len(members), validPositionCount=len(positioned), positionStatus=status,
                    c4DistanceCount=len(distances), nearestC4Distance=distances[0] if valid and distances else None,
                    secondC4Distance=distances[1] if valid and len(distances) >= 2 else None,
                    siteANearbyCount=near("siteA"), siteBNearbyCount=near("siteB"),
                    meanNearestTeammateDistance=sum(support)/len(support) if support else None,
                    maxNearestTeammateDistance=max(support) if support else None)

    support = dict(status="not-applicable", teammateCount=None, validTeammatePositionCount=None,
                   nearestTeammateDistance=None, nearbyTeammateCount=None)
    if carried:
        support["status"] = "incomplete-roster" if not complete else "missing-carrier"
        if complete and carrier is not None:
            mates = [p for p in players if p["team"] == "T" and p["id"] != carrier["id"]]
            support.update(teammateCount=len(mates), validTeammatePositionCount=sum(point(p) is not None for p in mates),
                           status="missing-position")
            if positions_agree and point(carrier) is not None and all(point(p) is not None for p in mates):
                distances = [distance(point(p), point(carrier)) for p in mates]
                support.update(status="available" if mates else "no-teammates",
                               nearestTeammateDistance=min(distances) if distances else None,
                               nearbyTeammateCount=sum(d <= manifest["radii"]["supportRadius"] for d in distances))
    t, ct = team("T"), team("CT")
    def diff(key):
        return t[key]-ct[key] if t[key] is not None and ct[key] is not None else None
    return dict(bombState=bomb["state"], bombPositionStatus=bomb_status, rosterStatus="complete" if complete else "incomplete",
                t=t, ct=ct, carrier=support, nearestC4DistanceDifference=diff("nearestC4Distance"),
                secondC4DistanceDifference=diff("secondC4Distance"))


def compare(actual, expected, path="features"):
    if isinstance(expected, dict):
        require(isinstance(actual, dict) and actual.keys() == expected.keys(), f"Field mismatch: {path}")
        for key in expected:
            compare(actual[key], expected[key], f"{path}.{key}")
    elif isinstance(expected, (float, int)) and not isinstance(expected, bool):
        require(isinstance(actual, (float, int)) and not isinstance(actual, bool) and
                math.isclose(actual, expected, abs_tol=1e-8, rel_tol=1e-10), f"Numeric mismatch: {path}")
    else:
        require(actual == expected, f"Value mismatch: {path}")


def audit(directory):
    directory = Path(directory).resolve()
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    require(manifest["status"] == "complete" and manifest["contractVersion"] == "win-spatial-v1", "Wrong/incomplete spatial contract")
    require(manifest["baseSchemaVersion"] == 4 and manifest["baseSemanticVersion"] == "mirage-semantics-v4.2", "Wrong base contract")
    require(manifest["distanceMetric"] == "euclidean-xy-world-units", "Wrong distance metric")
    compare(manifest["siteA"], dict(x=-378.01, y=-2102.56), "siteA")
    compare(manifest["siteB"], dict(x=-1942.49, y=356.14), "siteB")
    require(set(manifest["radii"]) == {"supportRadius", "siteRadius"} and
            all(math.isfinite(v) and v > 0 for v in manifest["radii"].values()), "Invalid radii")
    required = {"base/samples.jsonl", "base/manifest.json", "spatial.jsonl", "source.jsonl"}
    require(required <= manifest["files"].keys(), "Missing bound file")
    for name, digest in manifest["files"].items():
        file = (directory / name).resolve()
        require(file.is_relative_to(directory), "Invalid bound path")
        require(sha256(file) == digest, f"Hash mismatch: {name}")
    base_manifest = json.loads((directory / "base/manifest.json").read_text(encoding="utf-8"))
    require((base_manifest["status"], base_manifest["schemaVersion"], base_manifest["semanticVersion"]) ==
            ("complete", 4, "mirage-semantics-v4.2"), "Invalid v4 manifest")
    seen, weights, labels, official = set(), defaultdict(float), {}, {}
    matches, states, statuses, missing = Counter(), Counter(), Counter(), Counter()
    def key(row):
        return row["matchId"], row["roundId"], row["tick"]
    for base, spatial, source in zip_longest(records(directory/"base/samples.jsonl"),
                                            records(directory/"spatial.jsonl"), records(directory/"source.jsonl")):
        require(base is not None and spatial is not None and source is not None, "Row counts differ")
        validate_row(base)
        require(base["semanticVersion"] == manifest["baseSemanticVersion"], "Wrong row semantic version")
        require(key(base) == key(spatial) == key(source) and key(base) not in seen, "Duplicate or misaligned key")
        seen.add(key(base))
        for field in ("segmentId", "roundNumber", "mapName"):
            require(base[field] == spatial[field], f"Mismatched {field}")
        require(set(spatial) == {"contractVersion", "matchId", "roundId", "segmentId", "roundNumber", "tick", "mapName", "features"}
                and spatial["contractVersion"] == manifest["contractVersion"], "Unexpected row fields/contract")
        require(source["quality"] == base["features"]["quality"], "Evidence roster disagrees with base")
        compare(spatial["features"], expected_features(source, manifest))
        identity = key(base)[:2]
        require(labels.setdefault(identity, base["labelTWin"]) == base["labelTWin"], "Conflicting round outcome")
        round_key = base["matchId"], base["segmentId"], base["roundNumber"]
        require(official.setdefault(round_key, base["roundId"]) == base["roundId"], "Conflicting official round")
        weights[identity] += base["sampleWeight"]
        matches[base["matchId"]] += 1
        f = spatial["features"]
        states[f["bombState"]] += 1
        statuses[f["carrier"]["status"]] += 1
        for side in ("t", "ct"):
            for field, value in f[side].items():
                if value is None:
                    missing[f"{side}.{field}"] += 1
    require(seen and all(abs(w-1) <= 1e-6 for w in weights.values()), "Empty data or invalid round weights")
    require(len(base_manifest["matches"]) == len(matches) == manifest["matches"], "Match count mismatch")
    for item in base_manifest["matches"]:
        require(matches[item["matchId"]] == item["report"]["rowCount"], "Manifest row count mismatch")
        require(sum(k[0] == item["matchId"] for k in weights) == item["report"]["exportedRounds"], "Manifest round count mismatch")
    require(len(seen) == manifest["rows"], "Spatial row count mismatch")
    return dict(status="passed", contractVersion=manifest["contractVersion"], rows=len(seen), rounds=len(weights),
                matches=len(matches), radii=manifest["radii"], bombStates=dict(states), carrierStatuses=dict(statuses),
                nullCounts=dict(missing), manifestSha256=sha256(directory/"manifest.json"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, help="New JSON report file; never overwritten")
    args = parser.parse_args()
    result = audit(args.directory)
    text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        with args.output.open("x", encoding="utf-8") as output:
            output.write(text)
    print(text)
