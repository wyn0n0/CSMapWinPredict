"""Read-only independent distribution audit for the frozen step-seven pool.

Keeps lightweight anonymous descriptors, never writes scene/Facts/Narrative payloads.
This is diagnostic only: marginal capacity bounds do not establish joint feasibility.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import itertools
import json
from pathlib import Path
import time

APPROVED = "e60e8bf001ea55b1932fba7bed83468ee104cac8516525fe539409edaae8a3f9"
SPLITS = ("train", "dev", "test")


def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def classify(record, policy):
    """Literal independent reproduction of frozen Describe conditions."""
    f, m = record["input"]["facts"], record["metadata"]
    quality = {q["code"] for q in f["dataQuality"]}
    t, ct = f["alive"]["t"], f["alive"]["ct"]
    formation = f["formation"]
    events = sorted(set(m["selectionTags"]) & set(policy["eventCategories"]))
    low_medium = f["contactRisk"] in ("low", "medium")
    conditions = {
        "post-plant": m["phase"] == "post-plant",
        "clutch": (t == 1 and ct >= 2) or (ct == 1 and t >= 2) or (t == 2 and ct >= 3) or (ct == 2 and t >= 3),
        "formation-split": "split" in (formation["t"], formation["ct"]),
        "isolated": f["isolatedSide"] in ("T", "CT", "both") and not quality.intersection(policy["isolationBlockingQualityCodes"]),
        "high-contact": f["contactRisk"] == "high",
        "c4-transition": any(e.startswith("bomb-") for e in events),
        "low-quality": f["confidence"] == "low" or bool(quality.intersection(policy["relevantQualityCodes"])),
        "ordinary-live": m["phase"] == "live" and low_medium and f["isolatedSide"] == "none",
        "low-medium-contact": low_medium,
        "none-isolation": f["isolatedSide"] == "none",
        "grouped-spread": formation["t"] in ("grouped", "spread") and formation["ct"] in ("grouped", "spread"),
        "high-confidence": f["confidence"] == "high",
    }
    assert set(conditions) == set(policy["categoryPriority"])
    return tuple(sorted(k for k, v in conditions.items() if v)), events


def aggregate(entries, policy, split):
    matches, rounds = defaultdict(list), defaultdict(list)
    counts, overlaps = Counter(), Counter()
    for e in entries:
        matches[e["matchRef"]].append(e)
        rounds[(e["matchRef"], e["roundRef"])].append(e)
        counts.update(e["categories"])
        overlaps.update("|".join(pair) for pair in itertools.combinations(e["categories"], 2))
    sp = policy["splits"][split]
    def capacity(items, category=None):
        by_round = Counter(e["roundRef"] for e in items if category is None or category in e["categories"])
        return min(sp["maxPerMatch"], sum(min(policy["maxPerRound"], n) for n in by_round.values()))
    match_stats = {key: {"samples": len(items), "rounds": len({e["roundRef"] for e in items}),
                            "defaultRoundCapacity": capacity(items),
                            "categories": {c: sum(c in e["categories"] for e in items) for c in policy["categoryPriority"]}}
                   for key, items in sorted(matches.items())}
    round_stats = [{"matchRef": key[0], "roundRef": key[1], "samples": len(items),
                    "categories": {c: sum(c in e["categories"] for e in items) for c in policy["categoryPriority"]},
                    "eventCategories": sorted({c for e in items for c in e["events"]}),
                    "possibleThirdEventCoverage": len({c for e in items for c in e["events"]}) >= 3}
                   for key, items in sorted(rounds.items())]
    bounded = {c: min(sp["samples"], sum(capacity(items, c) for items in matches.values())) for c in policy["categoryPriority"]}
    return {"samples": len(entries), "matches": len(matches), "rounds": len(rounds),
            "categories": {c: counts[c] for c in policy["categoryPriority"]}, "pairwiseOverlap": dict(sorted(overlaps.items())),
            "categoryCapacityUpperBounds": bounded,
            "totalCapacityUpperBound": sum(capacity(items) for items in matches.values()),
            "matchesBelowMinimum": [m for m, s in match_stats.items() if s["defaultRoundCapacity"] < sp["minPerMatch"]],
            "matchStats": match_stats, "roundStats": round_stats}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("dataset", type=Path)
    ap.add_argument("output", type=Path)
    ap.add_argument("--policy", type=Path, default=Path("apps/api/Features/Situation/Training/situation-review-selection-v1.json"))
    args = ap.parse_args()
    if args.output.exists():
        raise ValueError("Diagnostic output must not exist")
    started = time.perf_counter()
    manifest_path = args.dataset / "manifest.json"
    assert sha(manifest_path) == APPROVED, "Unapproved manifest"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assert manifest["status"] == "complete"
    policy_bytes = args.policy.read_bytes()
    policy = json.loads(policy_bytes)
    policy_hash = hashlib.sha256(policy_bytes).hexdigest()
    files = {f["path"]: f for f in manifest["files"]}
    before = {name: sha(args.dataset / name) for name in files}
    assert all(before[name] == f["sha256"] and (args.dataset / name).stat().st_size == f["bytes"] for name, f in files.items())
    fixed_split = json.loads((args.dataset / "split.json").read_text(encoding="utf-8"))
    members = {s: {"match-" + hashlib.sha256(("situation-training-match-ref-v1\ndata=" + manifest["versions"]["data"] + "\nmatch=" + item["matchId"]).encode()).hexdigest() for item in fixed_split[s]} for s in SPLITS}
    descriptors, scan_hashes, scan_seconds = [], {}, {}
    for split in SPLITS:
        tick = time.perf_counter()
        h = hashlib.sha256()
        with (args.dataset / (split + ".jsonl")).open("rb") as stream:
            for line_number, line in enumerate(stream, 1):
                h.update(line)
                record = json.loads(line)
                m = record["metadata"]
                assert m["split"] == split
                assert m["matchRef"] in members[split], "Anonymous match is outside frozen split"
                categories, events = classify(record, policy)
                descriptors.append({"sampleId": record["sampleId"], "split": split, "matchRef": m["matchRef"],
                                    "roundRef": m["roundRef"], "sourceSceneSha256": m["sourceSceneSha256"],
                                    "modelInputSha256": m["modelInputSha256"], "categories": categories, "events": events})
        scan_hashes[split + ".jsonl"] = h.hexdigest()
        assert h.hexdigest() == before[split + ".jsonl"]
        assert line_number == files[split + ".jsonl"]["rows"]
        scan_seconds[split] = time.perf_counter() - tick
    keys = ("sampleId", "sourceSceneSha256", "modelInputSha256")
    conflicts = {}
    for key in keys:
        index = defaultdict(list)
        for e in descriptors:
            index[e[key]].append(e)
        conflicts[key] = {"unique": len(index), "duplicateRows": sum(len(v) - 1 for v in index.values()),
                          "groups": [{"value": k, "sampleIds": [e["sampleId"] for e in v], "splits": sorted({e["split"] for e in v})}
                                     for k, v in sorted(index.items()) if len(v) > 1]}
    seen, retained = {k: set() for k in keys}, []
    for e in sorted(descriptors, key=lambda e: (SPLITS.index(e["split"]), e["sampleId"])):
        if any(e[k] in seen[k] for k in keys):
            continue
        retained.append(e)
        for k in keys:
            seen[k].add(e[k])
    splits, shortfalls = {}, []
    for split in SPLITS:
        raw = aggregate([e for e in descriptors if e["split"] == split], policy, split)
        dedup = aggregate([e for e in retained if e["split"] == split], policy, split)
        assert all(raw[k] == manifest["counts"][split][k] for k in ("samples", "matches", "rounds"))
        assert set(raw["matchStats"]) == members[split]
        splits[split] = {"raw": raw, "deterministicallyDeduplicated": dedup}
        for c, target in policy["splits"][split]["quotas"].items():
            available = raw["categories"][c]
            upper = raw["categoryCapacityUpperBounds"][c]
            if min(available, upper) < target:
                shortfalls.append({"split": split, "category": c, "target": target, "rawAvailable": available,
                                   "capacityUpperBound": upper, "reason": "raw-pool-insufficient" if available < target else "match-round-upper-bound-insufficient"})
    after = {name: sha(args.dataset / name) for name in files}
    assert before == after and sha(manifest_path) == APPROVED
    assert args.policy.read_bytes() == policy_bytes, "Policy changed during audit"
    result = {"kind": "diagnostic-only", "baseManifestSha256": APPROVED, "policyFileSha256": policy_hash,
              "policyCanonicalSha256": hashlib.sha256(json.dumps(policy, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()).hexdigest(),
              "scriptSha256": sha(Path(__file__)), "inputHashesBefore": before, "inputHashesDuringScan": scan_hashes,
              "inputHashesAfter": after, "scanSeconds": scan_seconds, "elapsedSeconds": time.perf_counter() - started,
              "scannedRows": len(descriptors), "uniqueness": conflicts, "splits": splits, "objectiveShortfalls": shortfalls,
              "definitions": {"dedup": "Greedy retain by train/dev/test then ordinal sampleId, reject collision in any of the three keys; this is a feasible subset, not a maximum independent set.",
                              "categoryCapacity": "Per-category upper bound sum over matches min(match cap, sum over rounds min(2, category count)); capped at split target; ignores category interactions and hash conflicts.",
                              "thirdEvent": "At least three distinct event labels in a round is only a necessary possibility signal, not a selection or proof of three distinct representatives.",
                              "jointFeasibility": "Not established by marginal counts; selector and independent validator must establish final constraints.",
                              "memory": "One full JSON record at a time plus O(rows) anonymous descriptors and aggregate indexes; no full payload retained; peak resident memory not measured.",
                              "provenance": "Reuses coordinator verification of historical 85 source and 14 schema bindings; this script independently rehashes all six data files before and after.",
                              "testUsage": "Frozen classification and quotas applied unchanged; no test-driven tuning."}}
    args.output.mkdir(parents=True, exist_ok=False)
    (args.output / "audit.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    lines = ["# Frozen review pool distribution audit", "", "Diagnostic only; joint selection feasibility remains to be verified.",
             f"Scanned {len(descriptors)} rows in {sum(scan_seconds.values()):.3f} seconds.", f"Policy file SHA-256: {policy_hash}", "", "| split | rows | matches | rounds | default capacity |", "|---|---:|---:|---:|---:|"]
    for split in SPLITS:
        r = splits[split]["raw"]
        lines.append(f"| {split} | {r['samples']} | {r['matches']} | {r['rounds']} | {r['totalCapacityUpperBound']} |")
    lines += ["", "Objective shortfalls: " + json.dumps(shortfalls), "Three-key duplicate row counts: " + json.dumps({k: v["duplicateRows"] for k, v in conflicts.items()}), "", "Base manifest and all six input hashes matched before/during/after audit. No payload or identity fields emitted."]
    (args.output / "audit.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps({"rows": len(descriptors), "scanSeconds": scan_seconds, "objectiveShortfalls": shortfalls,
                      "duplicates": {k: v["duplicateRows"] for k, v in conflicts.items()}, "policyFileSha256": policy_hash}))


if __name__ == "__main__":
    main()
