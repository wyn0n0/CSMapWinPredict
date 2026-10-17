import unittest

import numpy as np
import pandas as pd

from endgame_prediction.train import split_endgames
from tools.test_train_win_experiments import training_fixture


class EndgameTrainingTests(unittest.TestCase):
    def setUp(self):
        self.frame, self.meta = training_fixture()
        self.frame["mapName"] = "de_mirage"
        self.frame["features.t.alive"] = np.tile([3, 4, 2, 1, 0, 5, 3, 4, 2, 1, 0, 5], 6)
        self.frame["features.ct.alive"] = np.tile([4, 4, 5, 1, 3, 3, 4, 4, 5, 1, 3, 3], 6)
        self.meta = self.meta.assign(phase=self.frame["features.phase"],
            tAlive=self.frame["features.t.alive"], ctAlive=self.frame["features.ct.alive"])
        self.train = [f"m{i}" for i in range(5)]
        self.test = ["m5"]

    def test_only_endgames_in_both_partitions_and_weights_unchanged(self):
        parts, coverage = split_endgames(self.frame, self.meta, self.train, self.test)
        for role, ids, expected_count in (("train", self.train, 30), ("test", self.test, 6)):
            x, m = parts[role]
            self.assertEqual(len(x), expected_count)
            self.assertEqual(set(m.matchId), set(ids))
            self.assertTrue((m.tAlive + m.ctAlive <= 7).all())
            self.assertTrue(((m.tAlive > 0) & (m.ctAlive > 0)).all())
            self.assertTrue(m.phase.isin(["live", "post-plant"]).all())
            self.assertTrue(m.weight.eq(1 / 3).all())
            self.assertEqual(coverage[role]["excludedRows"], expected_count)
            self.assertNotIn("features.baseline.isClutch", x)
            self.assertFalse(set(x) & {"label", "labelTWin", "matchId", "winner"})
        # Selection must be independent of future outcome labels.
        changed, _ = split_endgames(self.frame, self.meta.assign(label=1-self.meta.label), self.train, self.test)
        for role in parts:
            pd.testing.assert_frame_equal(parts[role][0], changed[role][0])

    def test_phase_and_unknown_counts_excluded_and_overlap_rejected(self):
        for index, value in enumerate((np.nan, 2.5, 6, -1)):
            self.frame.loc[index, "features.t.alive"] = value
            self.meta.loc[index, "tAlive"] = value
        self.frame.loc[6, "features.phase"] = "ended"
        self.meta.loc[6, "phase"] = "ended"
        parts, _ = split_endgames(self.frame, self.meta, self.train, self.test)
        x, m = parts["train"]
        self.assertFalse(x["features.t.alive"].isna().any())
        self.assertTrue(x["features.t.alive"].isin([1, 2, 3, 4, 5]).all())
        self.assertFalse(m.phase.eq("ended").any())
        with self.assertRaisesRegex(ValueError, "Overlapping"):
            split_endgames(self.frame, self.meta, self.train + self.test, self.test)

    def test_misaligned_counts_and_empty_match_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "counts differ"):
            split_endgames(self.frame, self.meta.assign(tAlive=5), self.train, self.test)
        self.frame.loc[self.meta.matchId.eq("m5"), "features.t.alive"] = 0
        self.meta.loc[self.meta.matchId.eq("m5"), "tAlive"] = 0
        with self.assertRaisesRegex(ValueError, "no endgame"):
            split_endgames(self.frame, self.meta, self.train, self.test)


if __name__ == "__main__":
    unittest.main()
