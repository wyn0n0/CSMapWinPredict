"""Conservative GOTV GSI snapshot projection onto the existing endgame model.

Only positive, observed history is a model input. GSI snapshots cannot prove an
exact transition count, an unobserved event's absence, or the demo clock origin.
"""

from __future__ import annotations

import json
import math
import time

from tools.win_feature_sets import ENDGAME_CONTRACT, FEATURE_SETS

SOURCE_CONTRACT = "gotv-gsi-endgame-v1"
ACTIVE_BOMB = {"carried", "dropped", "planting", "planted", "defusing"}
POST_PLANT = {"planted", "defusing"}
RIFLES = {"ak47", "galilar", "famas", "m4a1", "m4a1_silencer", "aug", "sg556"}
SNIPERS = {"awp", "ssg08", "scar20", "g3sg1"}
GRENADES = {"smokegrenade", "flashbang", "hegrenade", "molotov", "incgrenade", "decoy"}
EQUIPMENT_FIELDS = (
    "totalMoney", "totalArmor", "helmetCount", "defuserCount", "equipmentValue",
    "grenadeCount", "rifleCount", "sniperCount",
)


class GsiError(ValueError):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def obj(value):
    return value if isinstance(value, dict) else {}


def number(value, *, integer=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    try:
        finite = math.isfinite(value)
    except OverflowError:
        return None
    if not finite or value < 0 or (integer and value != int(value)):
        return None
    return int(value) if integer else value


def countdown(value):
    # Valve emits countdowns and positions as strings, other numbers as JSON numbers.
    if isinstance(value, str):
        try:
            value = float(value)
        except ValueError:
            return None
    return number(value)


def position(value):
    if not isinstance(value, str):
        return None
    try:
        xyz = tuple(float(part.strip()) for part in value.split(","))
    except ValueError:
        return None
    return xyz if len(xyz) == 3 and all(math.isfinite(v) for v in xyz) else None


def difference(a, b):
    return None if a is None or b is None else a - b


def total(values):
    return None if any(v is None for v in values) else sum(values)


def equipment(player):
    state = obj(player.get("state"))
    values = {key: number(state.get(source), integer=True) for key, source in (
        ("totalMoney", "money"), ("totalArmor", "armor"), ("equipmentValue", "equip_value"))}
    for key, source in (("helmetCount", "helmet"), ("defuserCount", "defusekit")):
        value = state.get(source)
        # Full GSI player states omit defusekit when absent (verified against demo).
        # Only an omitted key means false; explicit null/malformed values stay unknown.
        if source == "defusekit" and source not in state:
            value = False
        values[key] = int(value) if isinstance(value, bool) else None
    weapons = player.get("weapons")
    counts = dict(grenadeCount=0, rifleCount=0, sniperCount=0)
    if not isinstance(weapons, dict):
        counts = dict.fromkeys(counts)
    else:
        for weapon in weapons.values():
            name = obj(weapon).get("name")
            if not isinstance(name, str) or not name.startswith("weapon_"):
                counts = dict.fromkeys(counts)
                break
            name = name.removeprefix("weapon_")
            counts["rifleCount"] += int(name in RIFLES)
            counts["sniperCount"] += int(name in SNIPERS)
            if name in GRENADES:
                # A flashbang slot may hold two grenades. Do not count weapon slots.
                reserve = number(weapon.get("ammo_reserve"), integer=True)
                counts["grenadeCount"] = total([
                    counts["grenadeCount"], None if reserve is None else max(1, reserve)])
    return {**values, **counts}


class GsiAdapter:
    def __init__(self, predictor, max_age=5.0):
        if not math.isfinite(max_age) or max_age <= 0:
            raise ValueError("max_age must be finite and positive")
        expected = FEATURE_SETS["B_c4"][0] + FEATURE_SETS["B_c4"][1]
        if tuple(predictor.runtime.feature_names) != expected:
            raise ValueError("GSI v1 requires the B_c4 feature set")
        self.feature_names = expected
        self.spatial_hash = predictor.runtime.spatial_config_sha256
        manifest = json.loads((predictor.runtime.model_dir / "feature-manifest.json").read_text())
        self.sites = {site: tuple(manifest["spatialConfig"][f"site{site}"][axis] for axis in ("x", "y"))
                      for site in ("A", "B")}
        self.max_age = max_age
        self.last_source = None
        self.last_timestamp = None
        self.last_received = None
        self.reset_round()

    def reset_round(self):
        self.round_key = None
        self.seen_states = set()
        self.last_state = None
        self.last_phase = None
        self.last_countdown = None
        self.known_dead = set()
        self.planted_position = None

    def adapt(self, payload, *, received_at=None):
        now = time.time() if received_at is None else received_at
        if not isinstance(payload, dict):
            raise GsiError("invalid_payload", "GSI body must be an object")
        provider, map_data = obj(payload.get("provider")), obj(payload.get("map"))
        source, timestamp = provider.get("steamid"), number(provider.get("timestamp"))
        if provider.get("appid") != 730 or not isinstance(source, str) or not source or timestamp is None:
            raise GsiError("invalid_provider", "CS2 provider appid, steamid and timestamp are required")
        if timestamp > now + 1 or now - timestamp > self.max_age:
            self.reset_round()
            raise GsiError("stale_source", "Provider timestamp is stale or in the future")
        if source == self.last_source and self.last_timestamp is not None and timestamp < self.last_timestamp:
            self.reset_round()
            raise GsiError("out_of_order", "Provider timestamps must not go backwards")
        if source != self.last_source or (self.last_received is not None and now - self.last_received > self.max_age):
            self.reset_round()
        self.last_source, self.last_timestamp, self.last_received = source, timestamp, now
        result = {
            "sourceContract": SOURCE_CONTRACT, "sourceTimestamp": timestamp,
            "sourceAgeSeconds": max(0, now - timestamp),
            "compatibility": "unvalidated-gsi-projection", "status": "skipped",
            "reason": None, "prediction": None,
        }

        def skip(reason):
            self.reset_round()
            return {**result, "reason": reason}

        if map_data.get("name") != "de_mirage":
            return skip("unsupported_map")
        round_data, phases = obj(payload.get("round")), obj(payload.get("phase_countdowns"))
        phase = phases.get("phase")
        if map_data.get("phase") != "live" or round_data.get("phase") != "live":
            return skip("not_live")
        if phase not in ("live", "bomb", "defuse"):
            return skip("paused_or_unknown_phase")
        bomb = obj(payload.get("bomb"))
        bomb_state = bomb.get("state")
        if bomb_state in ("exploded", "defused"):
            return skip("round_ended")
        if not isinstance(bomb_state, str) or bomb_state not in ACTIVE_BOMB:
            return skip("unknown_bomb_state")
        post_plant = phase in ("bomb", "defuse")
        if post_plant != (bomb_state in POST_PLANT) or (phase == "defuse" and bomb_state != "defusing"):
            return skip("inconsistent_phase")
        remaining = countdown(phases.get("phase_ends_in"))
        if remaining is None:
            return skip("missing_countdown")
        scores = [number(obj(map_data.get(f"team_{side}")).get("score"), integer=True) for side in ("t", "ct")]
        raw_round = number(map_data.get("round"), integer=True)
        if any(score is None for score in scores) or raw_round is None or sum(scores) != raw_round:
            return skip("round_unconfirmed")

        allplayers = payload.get("allplayers")
        if (not isinstance(allplayers, dict) or len(allplayers) != 10 or "0" in allplayers
                or any(not isinstance(pid, str) or not pid for pid in allplayers)):
            return skip("incomplete_roster")
        players = list(allplayers.values())
        if any(not isinstance(p, dict) or p.get("team") not in ("T", "CT") for p in players):
            return skip("incomplete_roster")
        if any(sum(p["team"] == team for p in players) != 5 for team in ("T", "CT")):
            return skip("incomplete_roster")
        key = (source, raw_round, *scores, tuple(sorted((pid, p["team"]) for pid, p in allplayers.items())))
        health = {pid: number(obj(p.get("state")).get("health"), integer=True) for pid, p in allplayers.items()}
        # Reset before carrying any evidence across a detected seek or resurrection.
        revived = any(health.get(pid) is not None and health[pid] > 0 for pid in self.known_dead)
        rewind = self.last_phase == phase and self.last_countdown is not None and remaining > self.last_countdown + 0.25
        if key != self.round_key or revived or rewind or (self.last_phase in ("bomb", "defuse") and phase == "live"):
            self.reset_round()
            self.round_key = key
        # Some snapshots omit dead state. Only a prior explicit zero in this same
        # uninterrupted round is evidence of death; missing alive players stay unknown.
        for pid in self.known_dead:
            if "health" not in obj(allplayers[pid].get("state")):
                health[pid] = 0
        if any(value is None or value > 100 for value in health.values()):
            return skip("unknown_life_state")
        alive = {pid: p for pid, p in allplayers.items() if health[pid] > 0}
        counts = {side: sum(p["team"].lower() == side for p in alive.values()) for side in ("t", "ct")}
        current_position = position(bomb.get("position"))
        if post_plant:
            if current_position is not None:
                self.planted_position = current_position
            elif self.last_state in POST_PLANT:
                current_position = self.planted_position  # A planted bomb is stationary within this round.
        else:
            self.planted_position = None
        self.seen_states.add(bomb_state)
        if bomb_state == "defusing":
            self.seen_states.add("planted")
        self.last_state, self.last_phase = bomb_state, phase
        self.last_countdown = remaining
        self.known_dead = {pid for pid, value in health.items() if value == 0}
        result.update(roundNumber=raw_round + 1, alive=counts)
        if not all(counts.values()) or sum(counts.values()) > 7:
            return {**result, "reason": "not_endgame"}

        features = dict.fromkeys(self.feature_names)
        features.update({"mapName": "de_mirage", "roundNumber": raw_round + 1,
                         "features.phase": "post-plant" if post_plant else "live"})

        def put(prefix, values):
            features.update({f"{prefix}.{key}": value for key, value in values.items()})

        bomb_countdown = countdown(bomb.get("countdown"))
        explosion = remaining if phase == "bomb" else bomb_countdown if bomb_state == "planted" else None
        defuse = remaining if phase == "defuse" else bomb_countdown if bomb_state == "defusing" else None
        put("features.clock", dict(roundRemainingSeconds=None if post_plant else remaining,
            bombRemainingSeconds=explosion, defuseRemainingSeconds=defuse,
            clockSource="gsi-phase-countdown", pauseContext=None))
        carrier = bomb.get("player")
        carried = bomb_state in ("carried", "planting")
        carrier_known = isinstance(carrier, str) and carrier in alive and alive[carrier]["team"] == "T"
        defuser_known = isinstance(carrier, str) and carrier in alive and alive[carrier]["team"] == "CT"
        put("features.bomb", dict(hasCarrier=(True if carrier_known else None) if carried else False,
            hasDefuser=(True if defuser_known else None) if bomb_state == "defusing" else False))
        put("features.baseline", dict(bombState=bomb_state,
            hasExplosionTimer=(explosion is not None) if explosion is not None or not post_plant else None,
            hasDefuseTimer=(defuse is not None) if defuse is not None or bomb_state != "defusing" else None))
        for state in ("dropped", "planting", "planted", "defusing"):
            features[f"features.baseline.bombWas{state.title()}"] = True if state in self.seen_states else None
        for side, score in zip(("t", "ct"), scores):
            features[f"features.score{side.upper()}"] = score
            features[f"features.consecutiveLosses{side.upper()}"] = number(
                obj(map_data.get(f"team_{side}")).get("consecutive_round_losses"), integer=True)

        equips = {pid: equipment(p) for pid, p in alive.items()}
        eq_known = sum(all(v is not None for v in eq.values()) for eq in equips.values())
        coords = {pid: position(p.get("position")) for pid, p in alive.items()}
        position_known = sum(p is not None for p in coords.values())
        put("features.quality", dict(tMembers=5, ctMembers=5, lifeKnown=10, aliveKnown=len(alive),
            alivePositionKnown=position_known, aliveEquipmentKnown=eq_known,
            quality="usable" if eq_known == position_known == len(alive) else "partial"))
        for side in ("t", "ct"):
            ids = [pid for pid, p in alive.items() if p["team"].lower() == side]
            summary = dict(alive=len(ids), totalHealth=sum(health[pid] for pid in ids),
                totalKills=total([number(obj(alive[pid].get("match_stats")).get("kills"), integer=True) for pid in ids]),
                totalDeaths=total([number(obj(alive[pid].get("match_stats")).get("deaths"), integer=True) for pid in ids]))
            # WinFeatureSampleBuilder masks both teams if any alive equipment is unknown.
            summary.update({field: sum(equips[pid][field] for pid in ids) if eq_known == len(alive) else None
                            for field in EQUIPMENT_FIELDS})
            summary["equipmentKnownPlayers"] = len(ids) if eq_known == len(alive) else None
            put(f"features.{side}", summary)
            put("features.baseline", {f"{side}MeanHealth": summary["totalHealth"] / len(ids),
                f"{side}EquipmentCoverage": 1 if eq_known == len(alive) else None})

        for name, field in (("equipmentValue", "equipmentValue"), ("money", "totalMoney"),
                ("armor", "totalArmor"), ("helmetCount", "helmetCount"), ("defuserCount", "defuserCount"),
                ("grenadeCount", "grenadeCount"), ("rifleCount", "rifleCount"), ("sniperCount", "sniperCount"),
                ("health", "totalHealth"), ("alive", "alive")):
            features[f"features.baseline.{name}Difference"] = difference(features[f"features.t.{field}"], features[f"features.ct.{field}"])
        put("features.baseline", dict(totalAlive=len(alive), scoreDifference=scores[0] - scores[1],
            lossStreakDifference=difference(features["features.consecutiveLossesT"], features["features.consecutiveLossesCT"]),
            equipmentCoverageDifference=0 if eq_known == len(alive) else None))
        self._spatial(features, alive, coords, bomb_state, current_position, carrier_known)
        warnings = ["gsi_accuracy_not_validated", "demo_clock_origin_unavailable", "bomb_history_incomplete", "bomb_site_region_unavailable"]
        if eq_known != len(alive):
            warnings.append("equipment_incomplete")
        if position_known != len(alive):
            warnings.append("positions_incomplete")
        return {**result, "status": "ready", "warnings": warnings,
            "missingFeatures": [key for key, value in features.items() if value is None],
            "sample": {"featureContract": ENDGAME_CONTRACT,
                       "spatialConfigSha256": self.spatial_hash, "features": features}}

    def _spatial(self, f, alive, coords, bomb_state, bomb_position, carrier_known):
        status = "missing-carrier" if bomb_state in ("carried", "planting") and not carrier_known else (
            "known" if bomb_position is not None else "missing-position")
        f.update({"spatial.bombState": bomb_state, "spatial.bombPositionStatus": status, "spatial.rosterStatus": "complete"})
        complete = all(p is not None for p in coords.values())
        teams = {side: [coords[pid] for pid, p in alive.items() if p["team"].lower() == side] for side in ("t", "ct")}
        for side, positions in teams.items():
            valid = [p for p in positions if p is not None]
            distances = sorted(math.dist(p[:2], bomb_position[:2]) for p in valid) if status == "known" else []
            team_complete = len(valid) == len(positions)
            for key, value in dict(observedAliveCount=len(positions), validPositionCount=len(valid),
                    positionStatus="complete" if team_complete else "missing-position", c4DistanceCount=len(distances),
                    nearestC4Distance=distances[0] if team_complete and distances else None,
                    secondC4Distance=distances[1] if team_complete and len(distances) >= 2 else None).items():
                f[f"spatial.{side}.{key}"] = value
            f[f"features.baseline.{side}PositionDataMissing"] = not complete
            if complete:
                # Distances/dispersion use 5 * 1024 normalization in MapFeatureGeometry;
                # translation and the Y reflection do not change Euclidean distance.
                center = tuple(sum(p[axis] for p in valid) / len(valid) for axis in (0, 1))
                f[f"features.baseline.{side}PositionDispersion"] = math.sqrt(sum(math.dist(p[:2], center) ** 2 for p in valid) / len(valid)) / 5120
                for site, target in self.sites.items():
                    ds = [math.dist(p[:2], target) / 5120 for p in valid]
                    f[f"features.baseline.{side}MeanDistanceToSite{site}"] = sum(ds) / len(ds)
                    f[f"features.baseline.{side}MinDistanceToSite{site}"] = min(ds)
                f[f"features.baseline.{side}ClosestSiteDistance"] = min(f[f"features.baseline.{side}MinDistanceToSite{s}"] for s in ("A", "B"))
        for key in ("nearestC4Distance", "secondC4Distance"):
            f[f"spatial.{key}Difference"] = difference(f[f"spatial.t.{key}"], f[f"spatial.ct.{key}"])
        prefix = "features.baseline."
        f[prefix + "nearestOpponentDistanceMissing"] = not complete
        if complete:
            f[prefix + "nearestOpponentDistance"] = min(math.dist(t[:2], ct[:2]) for t in teams["t"] for ct in teams["ct"]) / 5120
            f[prefix + "positionDispersionDifference"] = f[prefix + "tPositionDispersion"] - f[prefix + "ctPositionDispersion"]
            for site in ("A", "B"):
                f[prefix + f"site{site}ProximityDifference"] = f[prefix + f"ctMeanDistanceToSite{site}"] - f[prefix + f"tMeanDistanceToSite{site}"]
