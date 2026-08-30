import unittest

import numpy as np

from tools.train_win_baseline import (
    EXPECTED_SCHEMA_VERSION,
    SELECTED_BASELINE,
    expected_calibration_error,
    validate_records,
)


def row(match_id: str, label: int, weight: float = 1.0, tick: int = 64) -> dict:
    return {
        "schemaVersion": EXPECTED_SCHEMA_VERSION,
        "matchId": match_id,
        "mapName": "de_mirage",
        "roundNumber": 1,
        "tick": tick,
        "labelTWin": label,
        "sampleWeight": weight,
    }


class ValidationTests(unittest.TestCase):
    def test_logistic_is_selected_baseline(self) -> None:
        self.assertEqual(SELECTED_BASELINE, "logistic")

    def test_accepts_three_matches_and_equal_round_weights(self) -> None:
        records = [
            row("match-a", 0),
            row("match-b", 1),
            row("match-c", 1),
        ]

        summary = validate_records(records)

        self.assertEqual(summary["matches"], 3)
        self.assertEqual(summary["rounds"], 3)
        self.assertEqual(summary["tWinRounds"], 2)
        self.assertEqual(summary["ctWinRounds"], 1)

    def test_rejects_duplicate_sample_keys(self) -> None:
        duplicate = row("match-a", 0)
        records = [duplicate, duplicate, row("match-b", 1), row("match-c", 0)]

        with self.assertRaisesRegex(ValueError, "Duplicate sample key"):
            validate_records(records)

    def test_rejects_inconsistent_round_labels(self) -> None:
        records = [
            row("match-a", 0, 0.5, 64),
            row("match-a", 1, 0.5, 128),
            row("match-b", 1),
            row("match-c", 0),
        ]

        with self.assertRaisesRegex(ValueError, "Inconsistent labels"):
            validate_records(records)

    def test_perfect_probabilities_have_zero_ece(self) -> None:
        labels = np.array([0, 0, 1, 1])
        probabilities = np.array([0.0, 0.0, 1.0, 1.0])
        weights = np.ones(4)

        self.assertEqual(expected_calibration_error(labels, probabilities, weights), 0)


if __name__ == "__main__":
    unittest.main()
