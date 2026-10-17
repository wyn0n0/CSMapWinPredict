"""Mirage round-win inference using the C4 model trained only on endgame samples."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools.win_evaluation import BROAD_ENDGAME_DEFINITION, broad_endgame_mask
from tools.win_feature_sets import BroadEndgameWinRuntime, ENDGAME_CONTRACT
from tools.win_inference_service import (
    DEFAULT_MAX_BATCH_SIZE, DEFAULT_MAX_REQUEST_BYTES, ModelLoadError, RequestError, serve,
)

MODEL_DIR = Path(__file__).resolve().parent / "runs" / "20261017-endgame-only-r1" / "model"


class EndgamePredictor:
    def __init__(self, model_dir: Path = MODEL_DIR):
        # Run the bundle's own self-tests before applying the endgame-only gate.
        self.runtime = BroadEndgameWinRuntime(model_dir)
        self.info = {
            **self.runtime.info,
            "scope": "endgame-only",
            "featureContract": ENDGAME_CONTRACT,
            "endgameDefinition": BROAD_ENDGAME_DEFINITION,
            "spatialConfigSha256": self.runtime.spatial_config_sha256,
            "modelStatus": "development-candidate",
        }

    def predict(self, samples, max_batch_size=DEFAULT_MAX_BATCH_SIZE):
        if not isinstance(samples, list) or not samples:
            raise RequestError("invalid_samples", "samples must be a non-empty array")
        if len(samples) > max_batch_size:
            raise RequestError("batch_too_large", f"maximum batch size is {max_batch_size}")
        for index, sample in enumerate(samples):
            row = self.runtime._flatten_sample(sample, index)
            if row["mapName"] != "de_mirage":
                raise RequestError("unsupported_map", f"samples[{index}] requires de_mirage")
            t, ct = row["features.t.alive"], row["features.ct.alive"]
            if any(isinstance(count, bool) or count not in range(6) for count in (t, ct)):
                raise RequestError(
                    "invalid_player_count", f"samples[{index}] requires known integer counts from 0 to 5"
                )
            if not broad_endgame_mask([row["features.phase"]], [t], [ct])[0]:
                raise RequestError(
                    "not_endgame",
                    f"samples[{index}] requires live/post-plant, both teams alive, and total alive <= 7",
                )
        # Validate the whole batch before producing any probabilities; preserve input order.
        return self.runtime.predict(samples, max_batch_size)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="verify the local model and exit")
    parser.add_argument("--model-dir", type=Path, default=MODEL_DIR, help="explicit compatible model bundle")
    args = parser.parse_args()
    try:
        runtime = EndgamePredictor(args.model_dir)
    except (ModelLoadError, OSError, ValueError, KeyError) as error:
        print(json.dumps({"status": "failed", "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 1
    if args.check:
        print(json.dumps(runtime.info, ensure_ascii=False, indent=2))
        return 0
    return serve(runtime, DEFAULT_MAX_BATCH_SIZE, DEFAULT_MAX_REQUEST_BYTES)


if __name__ == "__main__":
    raise SystemExit(main())
