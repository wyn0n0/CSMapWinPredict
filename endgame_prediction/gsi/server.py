"""Local-only GSI HTTP receiver; the existing endgame model is loaded once."""

from __future__ import annotations

import argparse
import hmac
import json
import math
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from endgame_prediction.gsi.adapter import GsiAdapter, GsiError, SOURCE_CONTRACT, obj
from endgame_prediction.predict import EndgamePredictor, MODEL_DIR
from tools.win_inference_service import ModelLoadError, RequestError

MAX_BODY_BYTES = 1024 * 1024


def decode_payload(raw):
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate JSON keys are not accepted")
            result[key] = value
        return result

    def invalid_constant(value):
        raise ValueError("Non-finite JSON numbers are not accepted")

    def finite_float(value):
        number = float(value)
        if not math.isfinite(number):
            invalid_constant(value)
        return number

    value = json.loads(raw, object_pairs_hook=unique_object, parse_constant=invalid_constant,
                       parse_float=finite_float)
    if not isinstance(value, dict):
        raise ValueError("GSI body must be a JSON object")
    return value


class GsiService:
    def __init__(self, model, max_age=5.0, clock=time.monotonic):
        self.model = model
        self.adapter = GsiAdapter(model, max_age)
        self.clock = clock
        # ponytail: one observer per process; serialize state/model access, split processes for multiple matches.
        self.lock = threading.Lock()
        self.latest = {"sourceContract": SOURCE_CONTRACT, "status": "waiting", "prediction": None}
        self.received = None

    def invalidate(self, reason, message=None):
        with self.lock:
            self.adapter.reset_round()
            self.latest = {"sourceContract": SOURCE_CONTRACT, "status": "invalid", "reason": reason,
                           "message": message, "prediction": None}
            self.received = self.clock()
            return self.latest.copy()

    def ingest(self, payload, *, received_at=None):
        with self.lock:
            started = self.clock()
            try:
                result = self.adapter.adapt(payload, received_at=received_at)
                sample = result.pop("sample", None)
                if sample is not None:
                    result["prediction"] = self.model.predict([sample])[0]
                    result["status"] = "predicted"
            except (GsiError, RequestError, ValueError, OverflowError) as error:
                self.adapter.reset_round()
                result = {"sourceContract": SOURCE_CONTRACT, "status": "invalid", "prediction": None,
                          "reason": getattr(error, "code", "invalid_features"), "message": str(error)}
            result["processingMs"] = round((self.clock() - started) * 1000, 3)
            self.latest, self.received = result, started
            return self.snapshot_unlocked()

    def snapshot_unlocked(self):
        result = self.latest.copy()
        age = None if self.received is None else max(0, self.clock() - self.received)
        result["ageSeconds"] = age
        if age is not None and "sourceAgeSeconds" in result:
            result["sourceAgeSeconds"] += age
        if age is not None and age > self.adapter.max_age:
            result.update(status="stale", reason="no_recent_gsi", prediction=None)
        elif result.get("sourceAgeSeconds", 0) > self.adapter.max_age:
            result.update(status="stale", reason="stale_source", prediction=None)
        return result

    def snapshot(self):
        with self.lock:
            return self.snapshot_unlocked()


def make_server(service, port=3001, token=None, *, recorder=None):
    class Handler(BaseHTTPRequestHandler):
        def setup(self):
            super().setup()
            self.connection.settimeout(3)

        def log_message(self, *_):
            pass  # Payloads contain identities; do not log them or the auth token.

        def reply(self, status, value):
            body = json.dumps(value, ensure_ascii=False, allow_nan=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def do_GET(self):
            if self.path == "/health":
                health = {"status": "ok", "sourceContract": SOURCE_CONTRACT,
                          "model": service.model.info, "gsi": service.snapshot()["status"]}
                if recorder is not None:
                    health["recording"] = recorder.snapshot()
                    if health["recording"]["status"] == "failed":
                        health["status"] = "error"
                self.reply(200 if health["status"] == "ok" else 503, health)
            elif self.path == "/prediction":
                self.reply(200, service.snapshot())
            elif self.path == "/recording" and recorder is not None:
                self.reply(200, recorder.snapshot())
            else:
                self.reply(404, {"error": "not_found"})

        def do_POST(self):
            if self.path != "/gsi":
                self.reply(404, {"error": "not_found"})
                return
            # Browser forms must not inject a fake local game state. No CORS support.
            if self.headers.get("Origin") is not None:
                self.reply(403, {"error": "browser_origin_rejected"})
                return
            if self.headers.get("Transfer-Encoding") or len(self.headers.get_all("Content-Length", [])) != 1:
                self.reply(400, {"error": "content_length_required"})
                return
            if self.headers.get_content_type() != "application/json":
                self.reply(415, {"error": "application_json_required"})
                return
            try:
                length = int(self.headers["Content-Length"])
            except ValueError:
                length = 0
            if not 0 < length <= MAX_BODY_BYTES:
                self.reply(413, {"error": "invalid_body_size"})
                return
            try:
                raw = self.rfile.read(length)
                received_at, received_monotonic = time.time(), time.monotonic()
                if len(raw) != length:
                    raise ValueError("Incomplete body")
                payload = decode_payload(raw)
            except (ValueError, UnicodeError, RecursionError, TimeoutError):
                # An unauthenticated malformed packet must not reset another client's state.
                if not token:
                    service.invalidate("invalid_json")
                self.reply(400, {"error": "invalid_json"})
                return
            supplied = obj(payload.get("auth")).get("token")
            if token and (not isinstance(supplied, str) or not hmac.compare_digest(supplied.encode(), token.encode())):
                self.reply(403, {"error": "invalid_token"})
                return
            # One synchronous prediction is small; return its result and elapsed time.
            # GOTV consumes the 200 ACK; other local clients can poll /prediction.
            if recorder is None:
                self.reply(200, service.ingest(payload))
            else:
                try:
                    result = recorder.ingest(payload, received_at=received_at,
                                             received_monotonic=received_monotonic)
                except OSError:
                    self.reply(503, {"error": "recording_failed", "prediction": None})
                    return
                self.reply(200, result)

    return ThreadingHTTPServer(("127.0.0.1", port), Handler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=3001)
    parser.add_argument("--max-age", type=float, default=5.0, help="maximum source/receipt age in seconds")
    parser.add_argument("--model-dir", type=Path, default=MODEL_DIR)
    parser.add_argument("--check", action="store_true", help="verify model and adapter; do not bind a port")
    args = parser.parse_args()
    if not 0 <= args.port <= 65535:
        parser.error("port must be between 0 and 65535")
    try:
        model = EndgamePredictor(args.model_dir)
        service = GsiService(model, args.max_age)
        if args.check:
            print(json.dumps({"status": "ok", "sourceContract": SOURCE_CONTRACT, "model": model.info}, ensure_ascii=False))
            return 0
        with make_server(service, args.port, os.environ.get("ENDGAME_GSI_TOKEN")) as server:
            print(f"GSI: http://127.0.0.1:{server.server_port}/gsi | results: /prediction | Ctrl+C to stop", flush=True)
            server.serve_forever()
    except KeyboardInterrupt:
        return 0
    except (ModelLoadError, OSError, ValueError, KeyError) as error:
        print(f"GSI startup failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
