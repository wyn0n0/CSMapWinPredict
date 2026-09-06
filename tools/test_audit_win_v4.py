import copy
import unittest

from tools.audit_win_v4 import validate_row


def row():
    return dict(schemaVersion=4, mapName="de_mirage", roundId="s0-a1",
                roundNumber=1, labelTWin=1, sampleWeight=1,
                features=dict(phase="live", quality=dict(rosterKnown=True, lifeKnown=10,
                    quality="usable", aliveKnown=10, aliveEquipmentKnown=10),
                    clock=dict(clockKnown=True, liveElapsedSeconds=0, roundRemainingSeconds=115),
                    t=dict(alive=5, totalMoney=4000), ct=dict(alive=5, totalMoney=4000)))


class AuditTests(unittest.TestCase):
    def test_accepts_known_state(self):
        validate_row(row())

    def test_rejects_unknown_life(self):
        r = row()
        r["features"]["quality"]["lifeKnown"] = 9
        with self.assertRaises(ValueError):
            validate_row(r)

    def test_rejects_false_resource_total(self):
        r = row()
        r["features"]["quality"]["aliveEquipmentKnown"] = 9
        with self.assertRaisesRegex(ValueError, "Partial equipment"):
            validate_row(r)

    def test_accepts_partial_resource_nulls(self):
        r = row()
        r["features"]["quality"]["aliveEquipmentKnown"] = 9
        r["features"]["t"]["totalMoney"] = None
        r["features"]["ct"]["totalMoney"] = None
        validate_row(r)

    def test_rejects_invalid_clock_or_nonfinite(self):
        for change in ({"liveElapsedSeconds": -1}, {"clockKnown": False},
                       {"liveElapsedSeconds": float("nan")}):
            r = row()
            r["features"]["clock"].update(change)
            with self.assertRaises(ValueError):
                validate_row(r)

    def test_postplant_round_clock_is_null(self):
        r = row()
        r["features"]["phase"] = "post-plant"
        with self.assertRaises(ValueError):
            validate_row(r)
        r["features"]["clock"]["roundRemainingSeconds"] = None
        validate_row(r)

    def test_identity_not_a_feature(self):
        r = copy.deepcopy(row())
        r["features"]["carrierId"] = "123"
        with self.assertRaises(ValueError):
            validate_row(r)


if __name__ == "__main__":
    unittest.main()
