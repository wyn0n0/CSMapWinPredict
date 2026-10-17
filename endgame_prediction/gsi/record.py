"""Record one GSI observer session to a new directory, with paired predictions."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import threading
import time
import uuid
from collections import Counter
from contextlib import ExitStack
from datetime import datetime, timezone
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from endgame_prediction.gsi.adapter import SOURCE_CONTRACT, obj
from endgame_prediction.gsi.server import GsiService, make_server
from endgame_prediction.predict import EndgamePredictor, MODEL_DIR
from tools.win_inference_service import ModelLoadError

RECORDING_CONTRACT = "gotv-gsi-recording-v1"
RECORDINGS_DIR = Path(__file__).resolve().parent / "recordings"


def utc_time(timestamp):
    return datetime.fromtimestamp(timestamp, timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def without_auth(payload):
    # Keep unknown game fields and deltas, but never persist GSI credentials.
    result = {key: value for key, value in payload.items() if key != "auth"}
    for key in ("previously", "added"):
        if isinstance(result.get(key), dict):
            result[key] = {k: v for k, v in result[key].items() if k != "auth"}
    return result


def write_json(stream, value):
    # ASCII escapes also preserve JSON strings containing unpaired surrogates.
    stream.write(json.dumps(value, allow_nan=False, separators=(",", ":")) + "\n")
    stream.flush()
    os.fsync(stream.fileno())


class GsiRecorder:
    def __init__(self, service, output_dir):
        self.service = service
        self.output_dir = Path(output_dir).resolve()
        self.lock = threading.Lock()
        self.started = time.monotonic()
        self.last_received = None
        self.first_received_at = self.last_received_at = None
        self.snapshots = self.results = 0
        self.status_counts = Counter()
        self.max_gap = 0.0
        self.gameover_observed = False
        self.failed = self.closed = False
        self.files = ExitStack()
        # No resume/append to old sessions: each run has one observer and a new directory.
        self.output_dir.mkdir(parents=True, exist_ok=False)
        try:
            self.raw_file = self.files.enter_context((self.output_dir / "snapshots.jsonl").open("x", encoding="utf-8"))
            self.result_file = self.files.enter_context((self.output_dir / "predictions.jsonl").open("x", encoding="utf-8"))
            source_files = (Path(__file__), Path(__file__).with_name("server.py"),
                            Path(__file__).with_name("adapter.py"), Path(__file__).parents[1] / "predict.py")
            manifest = {
                "recordingContract": RECORDING_CONTRACT, "sourceContract": SOURCE_CONTRACT,
                "sessionId": str(uuid.uuid4()), "startedAt": utc_time(time.time()),
                "model": service.model.info, "maxAgeSeconds": service.adapter.max_age,
                "sourceSha256": {str(p.relative_to(Path(__file__).parents[1])).replace("\\", "/"):
                                 hashlib.sha256(p.read_bytes()).hexdigest() for p in source_files},
                "authRemoved": True, "rawFormat": "parsed-json-without-auth",
                "files": {"snapshots": "snapshots.jsonl", "predictions": "predictions.jsonl", "summary": "summary.json"},
            }
            with (self.output_dir / "manifest.json").open("x", encoding="utf-8") as stream:
                write_json(stream, manifest)
        except BaseException:
            self.files.close()
            raise

    def __enter__(self):
        return self

    def __exit__(self, exc_type, *_):
        self.close(clean_stop=exc_type is None)

    def ingest(self, payload, *, received_at=None, received_monotonic=None):
        received_at = time.time() if received_at is None else received_at
        received_monotonic = time.monotonic() if received_monotonic is None else received_monotonic
        # ponytail: synchronous disk + prediction per packet; split processes for multiple observers.
        with self.lock:
            if self.closed or self.failed:
                raise OSError("Recording is closed or failed; use a new output directory")
            receipt = {"sequence": self.snapshots + 1, "receivedAt": utc_time(received_at),
                       "receivedUnixSeconds": received_at,
                       "receivedElapsedSeconds": received_monotonic - self.started}
            try:
                # Commit the snapshot BEFORE any adapter/model code; a prediction failure cannot erase it.
                write_json(self.raw_file, {**receipt, "payload": without_auth(payload)})
                self.snapshots += 1
                self.first_received_at = self.first_received_at or receipt["receivedAt"]
                self.last_received_at = receipt["receivedAt"]
                if self.last_received is not None:
                    self.max_gap = max(self.max_gap, received_monotonic - self.last_received)
                self.last_received = received_monotonic
                self.gameover_observed |= obj(payload.get("map")).get("phase") == "gameover"
                try:
                    # Include time spent waiting/writing in the source-age gate.
                    processing_at = received_at + max(0, time.monotonic() - received_monotonic)
                    result = self.service.ingest(payload, received_at=processing_at)
                except Exception as error:
                    # Unexpected model errors still get a result row. Do not log payloads/error text.
                    result = self.service.invalidate("prediction_failed", type(error).__name__)
                write_json(self.result_file, {**receipt, "result": result})
                self.results += 1
                self.status_counts[result["status"]] += 1
            except OSError:
                self.failed = True
                self.service.invalidate("recording_failed")
                print("GSI recording failed; output preserved. Restart with a new directory.", file=sys.stderr, flush=True)
                raise
            return {**result, "recordingSequence": receipt["sequence"]}

    def snapshot(self):
        with self.lock:
            return self._snapshot()

    def _snapshot(self):
        return {"status": "failed" if self.failed else "stopped" if self.closed else "recording",
                "snapshots": self.snapshots, "results": self.results,
                "statusCounts": dict(self.status_counts), "firstReceivedAt": self.first_received_at,
                "lastReceivedAt": self.last_received_at,
                "lastSnapshotAgeSeconds": None if self.last_received is None else max(0, time.monotonic() - self.last_received),
                "maxGapSeconds": self.max_gap, "gameoverObserved": self.gameover_observed}

    def close(self, *, clean_stop=True):
        with self.lock:
            if self.closed:
                return
            self.closed = True
            try:
                with (self.output_dir / "summary.json").open("x", encoding="utf-8") as stream:
                    write_json(stream, {"recordingContract": RECORDING_CONTRACT, **self._snapshot(),
                                        "stoppedAt": utc_time(time.time()),
                                        "cleanStop": clean_stop and not self.failed and self.snapshots == self.results})
            finally:
                self.files.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, help="new session directory; existing paths are refused")
    parser.add_argument("--port", type=int, default=3002)
    parser.add_argument("--max-age", type=float, default=5.0)
    parser.add_argument("--model-dir", type=Path, default=MODEL_DIR)
    parser.add_argument("--check", action="store_true", help="check model/adapter without opening a port or recording")
    args = parser.parse_args()
    if not 0 <= args.port <= 65535:
        parser.error("port must be between 0 and 65535")
    try:
        service = GsiService(EndgamePredictor(args.model_dir), args.max_age)
        if args.check:
            print(json.dumps({"status": "ok", "recordingContract": RECORDING_CONTRACT, "model": service.model.info}))
            return 0
        output = args.output_dir or RECORDINGS_DIR / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ-") + uuid.uuid4().hex[:8])
        with GsiRecorder(service, output) as recorder:
            with make_server(service, args.port, os.environ.get("ENDGAME_GSI_TOKEN"), recorder=recorder) as server:
                # Finish in-flight requests before writing the closing summary.
                server.daemon_threads = False
                print(f"Recording: {recorder.output_dir}\nGSI: http://127.0.0.1:{server.server_port}/gsi | status: /recording | Ctrl+C to stop", flush=True)
                try:
                    server.serve_forever()
                except KeyboardInterrupt:
                    pass
        print(f"Stopped: {recorder.snapshots} snapshots, {recorder.results} results | {recorder.output_dir / 'summary.json'}", flush=True)
        return 1 if recorder.failed else 0
    except (ModelLoadError, OSError, ValueError, KeyError) as error:
        print(f"GSI recorder failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
