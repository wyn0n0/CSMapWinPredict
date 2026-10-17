"""Recorder durability, full-session coverage and isolation checks using synthetic GSI."""

import copy
import http.client
import json
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch

from endgame_prediction.gsi.record import GsiRecorder
from endgame_prediction.gsi.server import GsiService, make_server
from endgame_prediction.gsi.test_gsi import packet, ROOT
from endgame_prediction.predict import EndgamePredictor


def read_lines(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


class RecorderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.model = EndgamePredictor()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.output = Path(self.temp.name) / "session"
        self.service = GsiService(self.model)

    def test_full_session_preserves_all_snapshots_and_pairs_results(self):
        now = time.time()
        warmup = packet(5, 5, timestamp=now)
        warmup["map"]["phase"] = "warmup"
        full = packet(5, 5, timestamp=now)
        endgame = packet(timestamp=now)
        endgame.update(auth={"token": "secret"}, previously={"auth": {"token": "secret"}, "round": {}},
                       added={"auth": {"token": True}}, unknown_future_field={"value": "中文"})
        ended = packet(timestamp=now)
        ended["round"].update(phase="over", win_team="CT")
        ended["map"]["phase"] = "gameover"
        packets = [warmup, full, endgame, ended, {}]
        original = copy.deepcopy(packets)
        with GsiRecorder(self.service, self.output) as recorder:
            for p in packets:
                recorder.ingest(p)
            rows = read_lines(self.output / "snapshots.jsonl")
            predictions = read_lines(self.output / "predictions.jsonl")
            self.assertEqual(len(rows), 5)  # Readable before close, even for invalid/skipped states.
            self.assertEqual([p["result"]["status"] for p in predictions],
                             ["skipped", "skipped", "predicted", "skipped", "invalid"])
            for index, (raw, result) in enumerate(zip(rows, predictions), 1):
                self.assertEqual(raw["sequence"], index)
                self.assertEqual(raw["sequence"], result["sequence"])
                self.assertEqual(raw["receivedAt"], result["receivedAt"])
                self.assertTrue(raw["receivedAt"].endswith("Z"))
                self.assertGreaterEqual(raw["receivedUnixSeconds"], now)
                self.assertGreaterEqual(raw["receivedElapsedSeconds"], 0)
            self.assertEqual(rows[3]["payload"]["round"]["win_team"], "CT")
            self.assertEqual(rows[2]["payload"]["unknown_future_field"], {"value": "中文"})
            self.assertAlmostEqual(sum(predictions[2]["result"]["prediction"].values()), 1)
        self.assertEqual(packets, original)  # Neither redaction nor inference changes the source object.
        self.assertNotIn("secret", "".join(p.read_text(encoding="utf-8") for p in self.output.iterdir()))
        summary = json.loads((self.output / "summary.json").read_text())
        self.assertTrue(summary["cleanStop"])
        self.assertTrue(summary["gameoverObserved"])
        self.assertEqual((summary["snapshots"], summary["results"]), (5, 5))
        manifest = json.loads((self.output / "manifest.json").read_text())
        self.assertEqual(manifest["model"]["artifactSha256"], self.model.info["artifactSha256"])

    def test_inference_failure_keeps_raw_and_next_packet_recovers(self):
        with GsiRecorder(self.service, self.output) as recorder:
            def broken_predict(*_):
                self.assertEqual(len(read_lines(self.output / "snapshots.jsonl")), 1)
                raise RuntimeError("sensitive exception text")
            with patch.object(self.model, "predict", side_effect=broken_predict):
                result = recorder.ingest(packet(timestamp=time.time()))
            self.assertEqual(result["reason"], "prediction_failed")
            self.assertIsNone(result["prediction"])
            self.assertEqual(recorder.ingest(packet(timestamp=time.time()))["status"], "predicted")
        self.assertNotIn("sensitive exception text", (self.output / "predictions.jsonl").read_text())

    def test_new_directory_required_and_check_does_not_write(self):
        self.output.mkdir()
        marker = self.output / "do-not-overwrite.txt"
        marker.write_text("original")
        with self.assertRaises(FileExistsError):
            GsiRecorder(self.service, self.output)
        self.assertEqual(marker.read_text(), "original")
        check_output = self.output / "check-only"
        result = subprocess.run([sys.executable, "-B", str(ROOT / "endgame_prediction/gsi/record.py"),
                                 "--check", "--output-dir", str(check_output)],
                                cwd=ROOT / "endgame_prediction/gsi", capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["status"], "ok")
        self.assertFalse(check_output.exists())

    def test_concurrent_requests_keep_unique_pairs_and_duplicate_packets(self):
        with GsiRecorder(self.service, self.output) as recorder:
            p = packet(5, 5, timestamp=time.time())
            with ThreadPoolExecutor(max_workers=4) as pool:
                list(pool.map(recorder.ingest, [p] * 20))
            raws = read_lines(self.output / "snapshots.jsonl")
            results = read_lines(self.output / "predictions.jsonl")
            self.assertEqual([r["sequence"] for r in raws], list(range(1, 21)))
            self.assertEqual([r["sequence"] for r in results], list(range(1, 21)))
            self.assertTrue(all(r["payload"] == p for r in raws))

    def test_storage_failure_is_sticky_and_does_not_ack_success(self):
        with GsiRecorder(self.service, self.output) as recorder:
            server = make_server(self.service, port=0, token="test-token", recorder=recorder)
            server.daemon_threads = False
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            try:
                def request(method, path, payload=None):
                    client = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=5)
                    try:
                        client.request(method, path, json.dumps(payload) if payload is not None else None,
                                       {"Content-Type": "application/json"})
                        response = client.getresponse()
                        return response.status, json.loads(response.read())
                    finally:
                        client.close()
                self.assertEqual(request("GET", "/recording")[1]["snapshots"], 0)
                p = packet(timestamp=time.time())
                self.assertEqual(request("POST", "/gsi", p)[0], 403)
                self.assertEqual(recorder.snapshots, 0)
                p["auth"] = {"token": "test-token"}
                self.assertEqual(request("POST", "/gsi", p)[1]["recordingSequence"], 1)
                self.assertEqual(request("GET", "/health")[1]["recording"]["snapshots"], 1)
                with patch.object(recorder.result_file, "write", side_effect=OSError("disk full")):
                    self.assertEqual(request("POST", "/gsi", p)[0], 503)
                self.assertEqual(request("POST", "/gsi", p)[0], 503)
                self.assertEqual(request("GET", "/health")[0], 503)
                self.assertEqual(request("GET", "/recording")[1]["status"], "failed")
                self.assertIsNone(request("GET", "/prediction")[1]["prediction"])
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=5)
                self.assertFalse(thread.is_alive())
        self.assertEqual(len(read_lines(self.output / "snapshots.jsonl")), 2)
        self.assertEqual(len(read_lines(self.output / "predictions.jsonl")), 1)
        self.assertFalse(json.loads((self.output / "summary.json").read_text())["cleanStop"])

    def test_interrupted_prediction_leaves_recoverable_unpaired_snapshot(self):
        with self.assertRaises(KeyboardInterrupt):
            with GsiRecorder(self.service, self.output) as recorder:
                with patch.object(self.model, "predict", side_effect=KeyboardInterrupt):
                    recorder.ingest(packet(timestamp=time.time()))
        self.assertEqual(len(read_lines(self.output / "snapshots.jsonl")), 1)
        self.assertEqual(read_lines(self.output / "predictions.jsonl"), [])
        self.assertFalse(json.loads((self.output / "summary.json").read_text())["cleanStop"])

    def test_queue_delay_is_included_in_source_freshness(self):
        with GsiRecorder(self.service, self.output) as recorder:
            result = recorder.ingest(packet(timestamp=time.time() - 10), received_at=time.time() - 10,
                                     received_monotonic=time.monotonic() - 10)
            self.assertEqual(result["reason"], "stale_source")
            self.assertIsNone(result["prediction"])

    def test_server_waits_for_inflight_write_before_session_close(self):
        entered, release = threading.Event(), threading.Event()
        with GsiRecorder(self.service, self.output) as recorder:
            server = make_server(self.service, port=0, recorder=recorder)
            server.daemon_threads = False
            listener = threading.Thread(target=server.serve_forever)
            listener.start()
            original_predict = self.model.predict
            def slow_predict(*args):
                entered.set()
                if not release.wait(5):
                    raise RuntimeError("test timed out")
                return original_predict(*args)
            def post():
                client = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=10)
                try:
                    client.request("POST", "/gsi", json.dumps(packet(timestamp=time.time())),
                                   {"Content-Type": "application/json"})
                    response = client.getresponse()
                    return response.status, json.loads(response.read())
                finally:
                    client.close()
            try:
                with patch.object(self.model, "predict", side_effect=slow_predict), ThreadPoolExecutor() as pool:
                    response = pool.submit(post)
                    self.assertTrue(entered.wait(5))
                    server.shutdown()
                    closing = pool.submit(server.server_close)
                    self.assertFalse(closing.done())
                    release.set()
                    closing.result(timeout=5)
                    self.assertEqual(response.result(timeout=5)[0], 200)
            finally:
                release.set()
                server.shutdown()
                server.server_close()
                listener.join(timeout=5)
        self.assertTrue(json.loads((self.output / "summary.json").read_text())["cleanStop"])
        self.assertEqual(len(read_lines(self.output / "predictions.jsonl")), 1)


if __name__ == "__main__":
    unittest.main()
