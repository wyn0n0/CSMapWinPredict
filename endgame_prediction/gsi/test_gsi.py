"""Synthetic contract/lifecycle/HTTP checks, not GOTV accuracy validation."""

import http.client
import json
import math
import subprocess
import sys
import threading
import time
import unittest
from pathlib import Path

from endgame_prediction.gsi.adapter import GsiAdapter, GsiError
from endgame_prediction.gsi.server import GsiService, decode_payload, make_server, MAX_BODY_BYTES
from endgame_prediction.predict import EndgamePredictor

ROOT = Path(__file__).resolve().parents[2]


def packet(t=3, ct=4, timestamp=1000, raw_round=0, bomb="carried", phase="live"):
    players = {}
    for team, count in (("T", t), ("CT", ct)):
        for i in range(5):
            players[f"{team}{i}"] = {
                "team": team, "position": f"{i * 100}, {0 if team == 'T' else 100}, 0",
                "state": {"health": 80 if i < count else 0, "armor": 50, "helmet": True,
                          "defusekit": team == "CT", "money": 1000, "equip_value": 3000},
                "match_stats": {"kills": i + 1, "deaths": i},
                "weapons": {"weapon_0": {"name": "weapon_ak47"},
                            "weapon_1": {"name": "weapon_flashbang", "ammo_reserve": 2}},
            }
    return {
        "provider": {"appid": 730, "steamid": "synthetic-observer", "timestamp": timestamp},
        "map": {"name": "de_mirage", "phase": "live", "round": raw_round,
                "team_t": {"score": raw_round, "consecutive_round_losses": 0},
                "team_ct": {"score": 0, "consecutive_round_losses": 2}},
        "round": {"phase": "live"}, "phase_countdowns": {"phase": phase, "phase_ends_in": "90"},
        "allplayers": players,
        "bomb": {"state": bomb, "position": "0, 0, 0", "player": "T0"},
    }


class GsiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.model = EndgamePredictor()

    def setUp(self):
        self.adapter = GsiAdapter(self.model)

    def adapt(self, data):
        return self.adapter.adapt(data, received_at=data["provider"]["timestamp"])

    def test_alive_only_aggregation_and_geometry_units(self):
        result = self.adapt(packet())
        self.assertEqual(result["status"], "ready")
        f = result["sample"]["features"]
        self.assertEqual(set(f), set(self.model.runtime.feature_names))
        self.assertEqual(f["roundNumber"], 1)
        self.assertEqual(f["features.t.totalHealth"], 240)
        self.assertEqual(f["features.t.totalMoney"], 3000)
        self.assertEqual(f["features.t.totalKills"], 6)
        self.assertEqual(f["features.ct.totalDeaths"], 6)
        self.assertEqual(f["features.t.grenadeCount"], 6)
        self.assertEqual(f["features.t.rifleCount"], 3)
        self.assertEqual(f["features.baseline.healthDifference"], -80)
        self.assertEqual(f["features.quality.lifeKnown"], 10)
        self.assertEqual(f["features.quality.aliveKnown"], 7)
        self.assertEqual(f["features.baseline.lossStreakDifference"], -2)
        self.assertEqual(f["features.baseline.nearestOpponentDistance"], 100 / 5120)
        self.assertAlmostEqual(f["features.baseline.tPositionDispersion"], math.sqrt(20000 / 3) / 5120)
        self.assertEqual(f["spatial.t.nearestC4Distance"], 0)
        self.assertEqual(f["spatial.t.secondC4Distance"], 100)
        self.assertAlmostEqual(f["spatial.ct.secondC4Distance"], math.sqrt(20000))
        self.assertEqual(f["spatial.nearestC4DistanceDifference"], -100)
        self.assertEqual(f["features.baseline.tEquipmentCoverage"], 1)
        self.assertEqual(f["features.clock.clockSource"], "gsi-phase-countdown")
        for name in ("features.clock.liveElapsedSeconds", "features.bomb.site", "features.bomb.region",
                     "features.baseline.bombStateChangeCount", "features.baseline.previousBombState",
                     "features.baseline.secondsSinceBombStateChange", "features.baseline.bombWasDropped"):
            self.assertIsNone(f[name])
        probability = self.model.predict([result["sample"]])[0]
        self.assertAlmostEqual(probability["tWin"] + probability["ctWin"], 1)

    def test_every_player_count_boundary(self):
        for t in range(6):
            for ct in range(6):
                with self.subTest(t=t, ct=ct):
                    result = self.adapt(packet(t, ct))
                    self.assertEqual(result["status"] == "ready", 0 < t and 0 < ct and t + ct <= 7)

    def test_incomplete_roster_and_unknown_health_never_become_dead(self):
        changes = [lambda p: p["allplayers"].pop("CT4"),
                   lambda p: p["allplayers"]["T0"]["state"].pop("health"),
                   lambda p: p["allplayers"]["T0"]["state"].update(health=True),
                   lambda p: p["allplayers"]["T0"].update(team="CT"),
                   lambda p: p.update(allplayers=None)]
        for change in changes:
            p = packet()
            change(p)
            self.assertEqual(self.adapt(p)["status"], "skipped")
        p = packet()
        p["allplayers"]["T0"]["state"]["health"] = 10 ** 500
        self.assertEqual(self.adapt(p)["reason"], "unknown_life_state")

    def test_equipment_and_positions_mask_like_v4_export(self):
        p = packet()
        del p["allplayers"]["CT0"]["state"]["defusekit"]
        p["allplayers"]["T0"]["position"] = "NaN,0,0"
        f = self.adapt(p)["sample"]["features"]
        self.assertEqual(f["features.quality.quality"], "partial")
        for side in ("t", "ct"):
            self.assertIsNone(f[f"features.{side}.totalMoney"])
            self.assertIsNone(f[f"features.{side}.equipmentKnownPlayers"])
            self.assertTrue(f[f"features.baseline.{side}PositionDataMissing"])
            self.assertIsNone(f[f"features.baseline.{side}MinDistanceToSiteA"])
        self.assertEqual(f["spatial.t.positionStatus"], "missing-position")
        self.assertIsNone(f["spatial.t.nearestC4Distance"])
        self.assertEqual(f["spatial.ct.positionStatus"], "complete")
        self.assertEqual(f["spatial.ct.nearestC4Distance"], 100)
        self.assertEqual(f["spatial.t.c4DistanceCount"], 2)
        self.assertEqual(f["features.quality.aliveEquipmentKnown"], 6)

    def test_only_confirmed_deaths_survive_omitted_state(self):
        self.adapt(packet())
        p = packet(timestamp=1001)
        p["allplayers"]["T4"]["state"] = {}
        self.assertEqual(self.adapt(p)["status"], "ready")
        # New round / seek must not reuse the previous round's confirmed deaths.
        p["phase_countdowns"]["phase_ends_in"] = "100"
        self.assertEqual(self.adapt(p)["reason"], "unknown_life_state")
        self.adapt(packet(timestamp=1002))
        p = packet(timestamp=1003)
        p["allplayers"]["T0"]["state"] = {}
        self.assertEqual(self.adapt(p)["reason"], "unknown_life_state")

    def test_nonfinite_and_missing_grenade_counts_are_unknown(self):
        for value in (None, True, -1, "2", float("inf")):
            p = packet()
            p["allplayers"]["T0"]["weapons"]["weapon_1"]["ammo_reserve"] = value
            self.assertIsNone(self.adapt(p)["sample"]["features"]["features.t.grenadeCount"])

    def test_not_live_pause_ended_map_and_round_guards(self):
        for section, field, value in (("map", "name", "de_inferno"), ("map", "phase", "warmup"),
                ("map", "round", 2), ("round", "phase", "over"), ("round", "phase", "freezetime"),
                ("phase_countdowns", "phase", "paused"), ("phase_countdowns", "phase", "unknown"),
                ("phase_countdowns", "phase_ends_in", "NaN"), ("bomb", "state", "defused"),
                ("bomb", "state", "planted"), ("bomb", "state", {})):
            p = packet()
            p[section][field] = value
            self.assertEqual(self.adapt(p)["status"], "skipped")

    def test_history_keeps_only_positive_observations_and_resets(self):
        # Observe a drop before the endgame, then return to carried.
        self.adapt(packet(5, 5, bomb="dropped"))
        f = self.adapt(packet(timestamp=1001))["sample"]["features"]
        self.assertTrue(f["features.baseline.bombWasDropped"])
        self.assertIsNone(f["features.baseline.bombWasPlanting"])
        self.assertIsNone(f["features.baseline.bombStateChangeCount"])
        f = self.adapt(packet(timestamp=1002, raw_round=1))["sample"]["features"]
        self.assertIsNone(f["features.baseline.bombWasDropped"])
        self.adapt(packet(timestamp=1003, raw_round=1, bomb="dropped"))
        f = self.adapt(packet(timestamp=1010, raw_round=1))["sample"]["features"]
        self.assertIsNone(f["features.baseline.bombWasDropped"])
        self.adapt(packet(timestamp=1011, raw_round=1, bomb="dropped"))
        p = packet(timestamp=1012, raw_round=1)
        p["phase_countdowns"]["phase_ends_in"] = "100"
        self.assertIsNone(self.adapt(p)["sample"]["features"]["features.baseline.bombWasDropped"])

    def test_bomb_and_defuse_timers_and_stationary_position(self):
        p = packet(bomb="planted", phase="bomb")
        p["phase_countdowns"]["phase_ends_in"] = "30"
        p["bomb"]["countdown"] = "30"
        f = self.adapt(p)["sample"]["features"]
        self.assertEqual(f["features.clock.bombRemainingSeconds"], 30)
        self.assertIsNone(f["features.clock.roundRemainingSeconds"])
        p["provider"]["timestamp"] += 1
        p["bomb"] = {"state": "defusing", "player": "CT0", "countdown": "4"}
        p["phase_countdowns"] = {"phase": "defuse", "phase_ends_in": "4"}
        f = self.adapt(p)["sample"]["features"]
        self.assertEqual(f["features.clock.defuseRemainingSeconds"], 4)
        self.assertIsNone(f["features.clock.bombRemainingSeconds"])
        self.assertIsNone(f["features.baseline.hasExplosionTimer"])
        self.assertTrue(f["features.bomb.hasDefuser"])
        self.assertEqual(f["spatial.ct.nearestC4Distance"], 100)
        # Mid-round connect cannot borrow a position from another round.
        self.adapter.reset_round()
        f = self.adapt(p)["sample"]["features"]
        self.assertEqual(f["spatial.bombPositionStatus"], "missing-position")
        self.assertIsNone(f["spatial.ct.nearestC4Distance"])

    def test_single_survivor_second_distance_is_missing_not_zero(self):
        f = self.adapt(packet(1, 5))["sample"]["features"]
        self.assertIsNone(f["spatial.t.secondC4Distance"])
        self.assertIsNone(f["spatial.secondC4DistanceDifference"])
        self.assertEqual(f["features.baseline.tPositionDispersion"], 0)

    def test_source_timestamp_and_order(self):
        self.adapt(packet(timestamp=1000))
        for p, now, code in ((packet(timestamp=999), 1000, "out_of_order"),
                             (packet(), 1010, "stale_source"),
                             (packet(timestamp=1010), 1000, "stale_source")):
            with self.assertRaises(GsiError) as error:
                self.adapter.adapt(p, received_at=now)
            self.assertEqual(error.exception.code, code)
        p = packet()
        p["provider"]["appid"] = 570
        with self.assertRaises(GsiError):
            self.adapt(p)

    def test_snapshot_clears_old_probabilities_and_tracks_source_age(self):
        clock = [0]
        service = GsiService(self.model, clock=lambda: clock[0])
        result = service.ingest(packet(), received_at=1004)
        self.assertEqual(result["status"], "predicted")
        clock[0] = 2
        self.assertEqual(service.snapshot()["status"], "stale")
        self.assertIsNone(service.snapshot()["prediction"])
        service.ingest(packet(timestamp=1005), received_at=1005)
        clock[0] = 8
        self.assertEqual(service.snapshot()["reason"], "no_recent_gsi")
        self.assertIsNone(service.ingest(packet(5, 5, timestamp=1006), received_at=1006)["prediction"])
        self.assertIsNone(service.ingest({}, received_at=1007)["prediction"])

    def test_future_labels_and_previous_deltas_do_not_enter_features(self):
        p = packet()
        expected = self.adapt(p)["sample"]
        p.update(previously={"allplayers": {}}, added={"bomb": True}, labelTWin=1, winner="T")
        p["round"]["win_team"] = "CT"
        p["allplayers"]["T0"]["name"] = "player text is not a feature"
        p["allplayers"]["T0"]["state"]["round_kills"] = 123
        self.assertEqual(self.adapt(p)["sample"], expected)
        p.pop("allplayers")
        self.assertEqual(self.adapt(p)["reason"], "incomplete_roster")

    def test_strict_json(self):
        for raw in (b'[]', b'{"x":NaN}', b'{"x":1,"x":2}', b'{"x":Infinity}', b'{"x":1e999}', b'nope'):
            with self.assertRaises(ValueError):
                decode_payload(raw)

    def test_http_real_model_auth_validation_and_recovery(self):
        service = GsiService(self.model)
        server = make_server(service, port=0, token="test-token")
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            def request(method, path, body=None, headers=None):
                client = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=5)
                try:
                    client.request(method, path, body, headers or {})
                    response = client.getresponse()
                    return response.status, json.loads(response.read())
                finally:
                    client.close()
            self.assertEqual(request("GET", "/health")[1]["gsi"], "waiting")
            headers = {"Content-Type": "application/json"}
            p = packet(timestamp=time.time())
            self.assertEqual(request("POST", "/gsi", json.dumps(p), headers)[0], 403)
            p["auth"] = {"token": "test-token"}
            status, value = request("POST", "/gsi", json.dumps(p), headers)
            self.assertEqual((status, value["status"]), (200, "predicted"))
            self.assertNotIn("test-token", json.dumps(value))
            self.assertNotIn("allplayers", value)
            self.assertEqual(request("GET", "/prediction")[1]["prediction"], value["prediction"])
            self.assertEqual(request("POST", "/gsi", "{", headers)[0], 400)
            self.assertEqual(request("POST", "/gsi", "{}", {**headers, "Origin": "http://example.com"})[0], 403)
            self.assertEqual(request("POST", "/gsi", "{}", {**headers, "Content-Length": str(MAX_BODY_BYTES + 1)})[0], 413)
            self.assertEqual(request("GET", "/no-such-path")[0], 404)
            p["round"]["phase"] = "over"
            self.assertIsNone(request("POST", "/gsi", json.dumps(p), headers)[1]["prediction"])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=5)
            self.assertFalse(thread.is_alive())

    def test_cli_check_from_another_working_directory(self):
        result = subprocess.run([sys.executable, "-B", str(ROOT / "endgame_prediction/gsi/server.py"), "--check"],
                                cwd=ROOT / "endgame_prediction/gsi", capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["status"], "ok")


if __name__ == "__main__":
    unittest.main()
