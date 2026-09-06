"""Stable joblib-loadable wrapper for calibrated round-win models."""

from __future__ import annotations

import numpy as np


class CalibratedWinModel:
    def __init__(self, base_model, calibration: str = "identity", calibrator=None):
        self.base_model = base_model
        self.calibration = calibration
        self.calibrator = calibrator

    def predict_proba(self, features):
        probability = self.base_model.predict_proba(features)[:, 1]
        if self.calibration == "sigmoid":
            transformed = _logit(probability).reshape(-1, 1)
            probability = self.calibrator.predict_proba(transformed)[:, 1]
        elif self.calibration == "isotonic":
            probability = self.calibrator.predict(probability)
        elif self.calibration != "identity":
            raise ValueError(f"Unknown calibration method: {self.calibration}")
        probability = np.clip(np.asarray(probability, dtype=float), 1e-6, 1 - 1e-6)
        return np.column_stack((1 - probability, probability))


def _logit(probability):
    value = np.clip(np.asarray(probability, dtype=float), 1e-6, 1 - 1e-6)
    return np.log(value / (1 - value))
