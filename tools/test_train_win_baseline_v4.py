import unittest

import numpy as np
import pandas as pd

from tools.train_win_baseline_v4 import (
    SCHEMA_VERSION,
    SEMANTIC_VERSION,
    ece,
    json_safe_features,
    validate_records,
)


def row(match_id="match-a", round_id="s0-a1", tick=64, label=1, weight=1.0):
    return {
        "schemaVersion": SCHEMA_VERSION,
        "semanticVersion": SEMANTIC_VERSION,
        "matchId": match_id,
        "roundId": round_id,
        "roundNumber": 1,
        "tick": tick,
        "labelTWin": label,
        "sampleWeight": weight,
    }


class V4TrainingValidationTests(unittest.TestCase):
    def test_accepts_equal_round_weight(self):
        records = [row(tick=64, weight=0.5), row(tick=128, weight=0.5)]
        summary = validate_records(records)
        self.assertEqual(summary["rows"], 2)
        self.assertEqual(summary["rounds"], 1)

    def test_rejects_duplicate_match_round_tick(self):
        records = [row(), row()]
        with self.assertRaisesRegex(ValueError, "duplicate sample key"):
            validate_records(records)

    def test_rejects_round_weight_drift(self):
        records = [row(tick=64, weight=0.6), row(tick=128, weight=0.6)]
        with self.assertRaisesRegex(ValueError, "weights do not sum to one"):
            validate_records(records)

    def test_rejects_mixed_semantics(self):
        value = row()
        value["semanticVersion"] = "older"
        with self.assertRaisesRegex(ValueError, "Mixed or unsupported"):
            validate_records([value])

    def test_perfect_probabilities_have_zero_ece(self):
        labels = np.array([0, 0, 1, 1])
        probabilities = np.array([0.0, 0.0, 1.0, 1.0])
        self.assertEqual(ece(labels, probabilities, np.ones(4)), 0)

    def test_fixture_features_are_strict_json_values(self):
        result = json_safe_features(pd.Series({"missing": np.nan, "integer": np.int64(3), "text": "live"}))
        self.assertIsNone(result["missing"])
        self.assertEqual(result["integer"], 3)
        self.assertIs(type(result["integer"]), int)


if __name__ == "__main__":
    unittest.main()
