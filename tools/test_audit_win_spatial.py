import copy
import json
from pathlib import Path
import tempfile
import unittest

from audit_win_spatial import audit, expected_features, sha256


class SpatialAuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "base").mkdir()
        quality = dict(tMembers=5, ctMembers=5, lifeKnown=10, aliveKnown=2,
                       alivePositionKnown=2, aliveEquipmentKnown=2, rosterKnown=True, quality="usable", reasons=[])
        self.base = dict(schemaVersion=4, semanticVersion="mirage-semantics-v4.2", mapName="de_mirage",
                         matchId="match", roundId="r1", tick=64, segmentId=0, roundNumber=1, labelTWin=1,
                         sampleWeight=1, features=dict(quality=quality, phase="live", t=dict(alive=1), ct=dict(alive=1),
                         clock=dict(clockKnown=True, liveElapsedSeconds=0)))
        self.source = dict(matchId="match", roundId="r1", tick=64, quality=copy.deepcopy(quality),
                           players=[dict(id="t", team="T", x=0, y=0, z=0), dict(id="ct", team="CT", x=3, y=4, z=0)],
                           bomb=dict(state="dropped", carrierId=None, x=0, y=0, z=0))
        def team(distance):
            return dict(observedAliveCount=1, validPositionCount=1, positionStatus="complete", c4DistanceCount=1,
                        nearestC4Distance=distance, secondC4Distance=None, siteANearbyCount=0, siteBNearbyCount=0,
                        meanNearestTeammateDistance=None, maxNearestTeammateDistance=None)
        features = dict(bombState="dropped", bombPositionStatus="known", rosterStatus="complete", t=team(0), ct=team(5),
                        carrier=dict(status="not-applicable", teammateCount=None, validTeammatePositionCount=None,
                                     nearestTeammateDistance=None, nearbyTeammateCount=None),
                        nearestC4DistanceDifference=-5, secondC4DistanceDifference=None)
        self.spatial = {k: self.base[k] for k in ("matchId", "roundId", "tick", "segmentId", "roundNumber", "mapName")}
        self.spatial.update(contractVersion="win-spatial-v1", features=features)
        self.manifest = dict(status="complete", contractVersion="win-spatial-v1", baseSchemaVersion=4,
                             baseSemanticVersion="mirage-semantics-v4.2", distanceMetric="euclidean-xy-world-units",
                             radii=dict(supportRadius=5, siteRadius=10), siteA=dict(x=-378.01, y=-2102.56),
                             siteB=dict(x=-1942.49, y=356.14), rows=1, matches=1)
        self.write("base/manifest.json", dict(status="complete", schemaVersion=4, semanticVersion="mirage-semantics-v4.2",
                                              matches=[dict(matchId="match", report=dict(rowCount=1, exportedRounds=1))]))
        self.flush()

    def write(self, name, value):
        (self.root/name).write_text(json.dumps(value)+"\n", encoding="utf-8")

    def flush(self):
        self.write("base/samples.jsonl", self.base)
        self.write("spatial.jsonl", self.spatial)
        self.write("source.jsonl", self.source)
        self.manifest["files"] = {p.relative_to(self.root).as_posix(): sha256(p)
                                  for p in self.root.rglob("*") if p.is_file() and p != self.root/"manifest.json"}
        self.write("manifest.json", self.manifest)

    def test_valid_manual_345_geometry(self):
        self.assertEqual(audit(self.root)["rows"], 1)

    def test_hash_tampering(self):
        self.spatial["features"]["ct"]["nearestC4Distance"] = 6
        self.write("spatial.jsonl", self.spatial)
        with self.assertRaisesRegex(ValueError, "Hash mismatch"):
            audit(self.root)

    def test_rehashed_wrong_distance(self):
        self.spatial["features"]["ct"]["nearestC4Distance"] = 6
        self.flush()
        with self.assertRaisesRegex(ValueError, "Numeric mismatch"):
            audit(self.root)

    def test_key_misalignment(self):
        self.source["tick"] = 128
        self.flush()
        with self.assertRaisesRegex(ValueError, "misaligned"):
            audit(self.root)

    def test_row_count_mismatch(self):
        self.flush()
        (self.root/"spatial.jsonl").write_text("", encoding="utf-8")
        self.manifest["files"]["spatial.jsonl"] = sha256(self.root/"spatial.jsonl")
        self.write("manifest.json", self.manifest)
        with self.assertRaisesRegex(ValueError, "Row counts differ"):
            audit(self.root)

    def test_wrong_contract_or_label_leak(self):
        for key, value in (("contractVersion", "win-spatial-v2"), ("labelTWin", 1)):
            with self.subTest(key=key):
                original = copy.deepcopy(self.spatial)
                self.spatial[key] = value
                self.flush()
                with self.assertRaisesRegex(ValueError, "fields/contract"):
                    audit(self.root)
                self.spatial = original

    def test_wrong_weights(self):
        self.base["sampleWeight"] = .5
        self.flush()
        with self.assertRaisesRegex(ValueError, "round weights"):
            audit(self.root)

    def test_wrong_geometry(self):
        self.manifest["siteA"]["x"] = 0
        self.flush()
        with self.assertRaisesRegex(ValueError, "siteA"):
            audit(self.root)

    def test_missing_position_cannot_be_zero(self):
        self.source["players"][1]["x"] = None
        self.source["quality"]["alivePositionKnown"] = 1
        self.base["features"]["quality"]["alivePositionKnown"] = 1
        self.flush()
        with self.assertRaises(ValueError):
            audit(self.root)

    def test_required_binding(self):
        del self.manifest["files"]["source.jsonl"]
        self.write("manifest.json", self.manifest)
        with self.assertRaisesRegex(ValueError, "Missing bound file"):
            audit(self.root)

    def test_empty_team_and_carrier_absence(self):
        self.source["players"] = [self.source["players"][0]]
        self.source["quality"].update(aliveKnown=1, alivePositionKnown=1)
        self.source["bomb"].update(state="carried", carrierId="t")
        f = expected_features(self.source, self.manifest)
        self.assertEqual((f["carrier"]["status"], f["carrier"]["nearbyTeammateCount"]), ("no-teammates", 0))
        self.assertIsNone(f["carrier"]["nearestTeammateDistance"])
        self.assertEqual(f["ct"]["siteANearbyCount"], 0)
        self.assertIsNone(f["ct"]["nearestC4Distance"])


if __name__ == "__main__":
    unittest.main()
