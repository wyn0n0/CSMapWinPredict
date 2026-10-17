"""Copy the verified, already-trained C4 bundle here without changing its source."""

import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / ".artifacts/win-broad-endgame-20261015-r1/results/B_c4_logistic"
TARGET = Path(__file__).resolve().parent / "model"
MODEL_SHA256 = "adeb826dc1ad9ed241cd30cfefef0652eaf9f2cca65c6d0b14b9bed6473ea455"
FILES = (
    "baseline.joblib", "feature-manifest.json", "inference-fixtures.json",
    "report.json", "training-oof.npz",
)


def prepare():
    manifest = json.loads((SOURCE / "model-manifest.json").read_text(encoding="utf-8"))
    if manifest.get("status") != "complete" or manifest.get("featureContract") != "win-broad-endgame-v1":
        raise ValueError("Source must be a complete broad-endgame model bundle")
    if manifest["artifacts"].get("baseline.joblib") != MODEL_SHA256:
        raise ValueError("Source is not the documented C4 model")
    for name in FILES:
        if hashlib.sha256((SOURCE / name).read_bytes()).hexdigest() != manifest["artifacts"].get(name):
            raise ValueError(f"Source SHA-256 mismatch: {name}")
    TARGET.mkdir()  # Fail if it exists; never replace an installed bundle.
    for name in (*FILES, "model-manifest.json"):
        shutil.copyfile(SOURCE / name, TARGET / name)
        if (SOURCE / name).read_bytes() != (TARGET / name).read_bytes():
            raise ValueError(f"Copied file differs: {name}")
    print("Verified C4 model copied to endgame_prediction/model/ (no training).")


if __name__ == "__main__":
    prepare()
