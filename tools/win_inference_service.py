#!/usr/bin/env python3
"""Serve schema-v4 Mirage round-win predictions over JSON Lines stdin/stdout."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from pathlib import Path
from typing import Any

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import joblib
import numpy as np
import pandas as pd
import sklearn


SCHEMA_VERSION = 4
SEMANTIC_VERSION = "mirage-semantics-v4.2"
DEFAULT_MODEL_DIR = (
    Path(__file__).resolve().parents[1]
    / "models"
    / "win-baseline-v4-holdout-87-8-20260908"
)
DEFAULT_MAX_BATCH_SIZE = 512
DEFAULT_MAX_REQUEST_BYTES = 4 * 1024 * 1024
FORBIDDEN_FIELDS = frozenset(
    name.casefold() for name in ("labelTWin", "winner", "roundWinner", "winningTeam")
)


class ModelLoadError(RuntimeError):
    """The configured model bundle is incomplete, incompatible, or corrupt."""


class RequestError(ValueError):
    """A client request does not satisfy the inference protocol."""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def _reject_json_constant(value: str):
    raise ValueError(f"Non-finite JSON constant is not allowed: {value}")


def _read_json(path: Path):
    try:
        return json.loads(path.read_text(encoding="utf-8"), parse_constant=_reject_json_constant)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as error:
        raise ModelLoadError(f"Cannot read {path.name}: {error}") from error


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
    except OSError as error:
        raise ModelLoadError(f"Cannot read {path.name}: {error}") from error
    return digest.hexdigest()


def _require_mapping(value: Any, name: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ModelLoadError(f"{name} must be a JSON object")
    return value


def _require_string_list(value: Any, name: str) -> tuple[str, ...]:
    if not isinstance(value, list) or not value or any(not isinstance(item, str) for item in value):
        raise ModelLoadError(f"{name} must be a non-empty string array")
    if len(set(value)) != len(value):
        raise ModelLoadError(f"{name} contains duplicate feature names")
    return tuple(value)


def _find_forbidden_field(value: Any) -> str | None:
    if not isinstance(value, dict):
        return None
    for key, nested in value.items():
        if isinstance(key, str) and key.casefold() in FORBIDDEN_FIELDS:
            return key
        found = _find_forbidden_field(nested)
        if found is not None:
            return found
    return None


def _get_path(value: dict[str, Any], path: str) -> tuple[bool, Any]:
    current: Any = value
    for part in path.split("."):
        if not isinstance(current, dict) or part not in current:
            return False, None
        current = current[part]
    return True, current


class WinModelRuntime:
    def __init__(self, model_dir: Path):
        self.model_dir = model_dir.resolve()
        model_manifest = _require_mapping(
            _read_json(self.model_dir / "model-manifest.json"), "model-manifest.json"
        )
        feature_manifest = _require_mapping(
            _read_json(self.model_dir / "feature-manifest.json"), "feature-manifest.json"
        )
        report = _require_mapping(_read_json(self.model_dir / "report.json"), "report.json")

        self._validate_version(model_manifest, "model-manifest.json")
        self._validate_version(feature_manifest, "feature-manifest.json")
        if model_manifest.get("status") != "complete":
            raise ModelLoadError("model-manifest.json status must be complete")

        artifacts = _require_mapping(model_manifest.get("artifacts"), "model manifest artifacts")
        required_artifacts = (
            "baseline.joblib",
            "feature-manifest.json",
            "inference-fixtures.json",
            "report.json",
        )
        for name in required_artifacts:
            expected = artifacts.get(name)
            if not isinstance(expected, str) or len(expected) != 64:
                raise ModelLoadError(f"Missing SHA-256 for required artifact {name}")
            actual = _sha256(self.model_dir / name)
            if actual != expected:
                raise ModelLoadError(f"SHA-256 mismatch for {name}")

        self.numeric_features = _require_string_list(feature_manifest.get("numeric"), "numeric features")
        self.categorical_features = _require_string_list(
            feature_manifest.get("categorical"), "categorical features"
        )
        overlap = set(self.numeric_features) & set(self.categorical_features)
        if overlap:
            raise ModelLoadError(f"Features cannot be both numeric and categorical: {sorted(overlap)}")
        self.feature_names = self.numeric_features + self.categorical_features

        selected = _require_mapping(report.get("selectedBaseline"), "report selectedBaseline")
        if selected.get("artifact") != "baseline.joblib":
            raise ModelLoadError("report selected baseline must use baseline.joblib")
        if selected.get("model") != model_manifest.get("selectedModel"):
            raise ModelLoadError("selected model differs between report and model manifest")
        if selected.get("calibration") != model_manifest.get("calibration"):
            raise ModelLoadError("calibration differs between report and model manifest")

        try:
            self.model = joblib.load(self.model_dir / "baseline.joblib")
        except Exception as error:
            raise ModelLoadError(f"Cannot load baseline.joblib: {error}") from error
        if not callable(getattr(self.model, "predict_proba", None)):
            raise ModelLoadError("baseline.joblib does not expose predict_proba")

        fixtures = _read_json(self.model_dir / "inference-fixtures.json")
        if not isinstance(fixtures, list) or not fixtures:
            raise ModelLoadError("inference-fixtures.json must contain at least one fixture")
        self.fixture_count = self._verify_fixtures(fixtures)
        self.info = {
            "status": "ready",
            "schemaVersion": SCHEMA_VERSION,
            "semanticVersion": SEMANTIC_VERSION,
            "selectedModel": model_manifest["selectedModel"],
            "calibration": model_manifest["calibration"],
            "featureCount": len(self.feature_names),
            "selfTestFixtures": self.fixture_count,
            "artifactSha256": artifacts["baseline.joblib"],
            "runtime": {
                "python": sys.version.split()[0],
                "joblib": joblib.__version__,
                "numpy": np.__version__,
                "pandas": pd.__version__,
                "scikitLearn": sklearn.__version__,
            },
        }

    @staticmethod
    def _validate_version(document: dict[str, Any], name: str):
        if document.get("schemaVersion") != SCHEMA_VERSION:
            raise ModelLoadError(f"{name} schemaVersion must be {SCHEMA_VERSION}")
        if document.get("semanticVersion") != SEMANTIC_VERSION:
            raise ModelLoadError(f"{name} semanticVersion must be {SEMANTIC_VERSION}")

    def _flatten_sample(self, sample: Any, index: int) -> dict[str, Any]:
        if not isinstance(sample, dict):
            raise RequestError("invalid_sample", f"samples[{index}] must be an object")
        forbidden = _find_forbidden_field(sample)
        if forbidden is not None:
            raise RequestError(
                "future_field_rejected", f"samples[{index}] contains forbidden field {forbidden}"
            )

        nested_features = sample.get("features")
        if isinstance(nested_features, dict) and all(
            name in nested_features for name in self.feature_names
        ):
            flattened = {name: nested_features[name] for name in self.feature_names}
        elif all(name in sample for name in self.feature_names):
            flattened = {name: sample[name] for name in self.feature_names}
        else:
            flattened = {}
            missing = []
            for name in self.feature_names:
                present, value = _get_path(sample, name)
                if not present:
                    missing.append(name)
                else:
                    flattened[name] = value
            if missing:
                preview = ", ".join(missing[:3])
                suffix = "" if len(missing) <= 3 else f" (+{len(missing) - 3} more)"
                raise RequestError(
                    "missing_features", f"samples[{index}] is missing {preview}{suffix}"
                )

        for name in self.numeric_features:
            value = flattened[name]
            if value is None:
                continue
            if not isinstance(value, (bool, int, float)):
                raise RequestError(
                    "invalid_feature_type", f"samples[{index}].{name} must be numeric or null"
                )
            if not math.isfinite(float(value)):
                raise RequestError(
                    "non_finite_feature", f"samples[{index}].{name} must be finite or null"
                )
        for name in self.categorical_features:
            if flattened[name] is not None and not isinstance(flattened[name], str):
                raise RequestError(
                    "invalid_feature_type", f"samples[{index}].{name} must be a string or null"
                )
        return flattened

    def predict(self, samples: Any, max_batch_size: int) -> list[dict[str, float]]:
        if not isinstance(samples, list) or not samples:
            raise RequestError("invalid_samples", "samples must be a non-empty array")
        if len(samples) > max_batch_size:
            raise RequestError(
                "batch_too_large", f"samples contains {len(samples)} items; maximum is {max_batch_size}"
            )
        rows = [self._flatten_sample(sample, index) for index, sample in enumerate(samples)]
        frame = pd.DataFrame(rows, columns=self.feature_names)
        for name in self.numeric_features:
            frame[name] = pd.to_numeric(frame[name], errors="coerce").astype(float)
        for name in self.categorical_features:
            frame[name] = frame[name].fillna("__missing__").astype(str)
        try:
            probabilities = np.asarray(self.model.predict_proba(frame), dtype=float)
        except Exception as error:
            raise RequestError("prediction_failed", f"Model prediction failed: {error}") from error
        if probabilities.shape != (len(rows), 2):
            raise RequestError("prediction_failed", "Model returned an unexpected probability shape")
        if not np.isfinite(probabilities).all() or np.any(probabilities < 0) or np.any(probabilities > 1):
            raise RequestError("prediction_failed", "Model returned invalid probabilities")
        return [
            {"tWin": float(probabilities[index, 1]), "ctWin": float(probabilities[index, 0])}
            for index in range(len(rows))
        ]

    def _verify_fixtures(self, fixtures: list[Any]) -> int:
        samples = []
        expected = []
        for index, fixture in enumerate(fixtures):
            if not isinstance(fixture, dict) or not isinstance(fixture.get("features"), dict):
                raise ModelLoadError(f"Inference fixture {index} is malformed")
            value = fixture.get("expectedTWin")
            if not isinstance(value, (int, float)) or not math.isfinite(float(value)):
                raise ModelLoadError(f"Inference fixture {index} has an invalid expectedTWin")
            samples.append(fixture)
            expected.append(float(value))
        try:
            actual = self.predict(samples, len(samples))
        except RequestError as error:
            raise ModelLoadError(f"Inference fixture validation failed: {error}") from error
        for index, (prediction, expected_t_win) in enumerate(zip(actual, expected)):
            if not math.isclose(
                prediction["tWin"], expected_t_win, rel_tol=1e-12, abs_tol=1e-12
            ):
                raise ModelLoadError(
                    f"Inference fixture {index} drifted: expected {expected_t_win}, "
                    f"got {prediction['tWin']}"
                )
        return len(fixtures)


def _request_id(request: Any):
    if not isinstance(request, dict):
        return None
    value = request.get("id")
    return value if isinstance(value, (str, int)) and not isinstance(value, bool) else None


def process_request(
    runtime: WinModelRuntime, request: Any, max_batch_size: int
) -> tuple[dict[str, Any], bool]:
    request_id = _request_id(request)
    try:
        if not isinstance(request, dict):
            raise RequestError("invalid_request", "request must be an object")
        if "id" not in request or request_id is None:
            raise RequestError("invalid_id", "id must be a string or integer")
        operation = request.get("op")
        if operation == "health":
            result: dict[str, Any] = runtime.info
            stop = False
        elif operation == "predict":
            if request.get("schemaVersion") != SCHEMA_VERSION:
                raise RequestError("schema_mismatch", f"schemaVersion must be {SCHEMA_VERSION}")
            if request.get("semanticVersion") != SEMANTIC_VERSION:
                raise RequestError(
                    "semantic_mismatch", f"semanticVersion must be {SEMANTIC_VERSION}"
                )
            result = {"predictions": runtime.predict(request.get("samples"), max_batch_size)}
            stop = False
        elif operation == "shutdown":
            result = {"status": "stopping"}
            stop = True
        else:
            raise RequestError("unknown_operation", "op must be health, predict, or shutdown")
        return {"id": request_id, "ok": True, "result": result}, stop
    except RequestError as error:
        return {
            "id": request_id,
            "ok": False,
            "error": {"code": error.code, "message": str(error)},
        }, False


def _write_response(response: dict[str, Any]):
    print(json.dumps(response, ensure_ascii=False, allow_nan=False, separators=(",", ":")), flush=True)


def _drain_line(stream, max_request_bytes: int):
    while True:
        block = stream.readline(max_request_bytes + 1)
        if not block or block.endswith(b"\n"):
            return


def serve(runtime: WinModelRuntime, max_batch_size: int, max_request_bytes: int) -> int:
    stream = sys.stdin.buffer
    while True:
        raw = stream.readline(max_request_bytes + 1)
        if not raw:
            return 0
        if len(raw) > max_request_bytes:
            if not raw.endswith(b"\n"):
                _drain_line(stream, max_request_bytes)
            _write_response({
                "id": None,
                "ok": False,
                "error": {"code": "request_too_large", "message": "request line exceeds byte limit"},
            })
            continue
        if not raw.strip():
            continue
        try:
            request = json.loads(raw.decode("utf-8"), parse_constant=_reject_json_constant)
        except (UnicodeError, json.JSONDecodeError, ValueError) as error:
            _write_response({
                "id": None,
                "ok": False,
                "error": {"code": "invalid_json", "message": str(error)},
            })
            continue
        response, stop = process_request(runtime, request, max_batch_size)
        _write_response(response)
        if stop:
            return 0


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", type=Path, default=DEFAULT_MODEL_DIR)
    parser.add_argument("--max-batch-size", type=int, default=DEFAULT_MAX_BATCH_SIZE)
    parser.add_argument("--max-request-bytes", type=int, default=DEFAULT_MAX_REQUEST_BYTES)
    parser.add_argument(
        "--check", action="store_true", help="load and verify the model, print status, then exit"
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.max_batch_size < 1:
        print("max-batch-size must be positive", file=sys.stderr)
        return 2
    if args.max_request_bytes < 1024:
        print("max-request-bytes must be at least 1024", file=sys.stderr)
        return 2
    try:
        runtime = WinModelRuntime(args.model_dir)
    except ModelLoadError as error:
        print(json.dumps({"status": "failed", "error": str(error)}), file=sys.stderr, flush=True)
        return 1
    if args.check:
        print(json.dumps(runtime.info, ensure_ascii=False, allow_nan=False, indent=2))
        return 0
    return serve(runtime, args.max_batch_size, args.max_request_bytes)


if __name__ == "__main__":
    raise SystemExit(main())
