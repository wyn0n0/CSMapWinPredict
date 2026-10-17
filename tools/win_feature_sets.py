"""Fixed M4 feature contracts; experimental bundles are separate from production v4."""
import json
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.preprocessing import FunctionTransformer

from tools import train_win_baseline_v4 as v4
from tools.win_evaluation import BROAD_ENDGAME_DEFINITION, broad_endgame_mask
from tools.win_inference_service import WinModelRuntime, ModelLoadError, RequestError

CONTRACT = "win-m4-v1"
ENDGAME_CONTRACT = "win-broad-endgame-v1"
SPATIAL_CONTRACT = "win-spatial-v1"
SPATIAL_NUMERIC = tuple(
    f"spatial.{side}.{field}" for side in ("t", "ct") for field in (
        "observedAliveCount", "validPositionCount", "c4DistanceCount", "nearestC4Distance",
        "secondC4Distance", "siteANearbyCount", "siteBNearbyCount",
        "meanNearestTeammateDistance", "maxNearestTeammateDistance")) + (
    "spatial.nearestC4DistanceDifference", "spatial.secondC4DistanceDifference",
    "spatial.carrier.teammateCount", "spatial.carrier.validTeammatePositionCount",
    "spatial.carrier.nearestTeammateDistance", "spatial.carrier.nearbyTeammateCount")
SPATIAL_CATEGORICAL = (
    "spatial.bombState", "spatial.bombPositionStatus", "spatial.rosterStatus",
    "spatial.t.positionStatus", "spatial.ct.positionStatus", "spatial.carrier.status")
EXISTING_SPATIAL = tuple(n for n in v4.NUMERIC_FEATURES if n.startswith("features.baseline.") and any(s in n for s in (
    "Position", "positionDispersion", "nearestOpponent", "DistanceToSite", "ClosestSite", "Proximity")))
KD = tuple(f"features.{side}.{field}" for side in ("t", "ct") for field in ("totalKills", "totalDeaths"))
PROGRESS = ("roundNumber", "features.scoreT", "features.scoreCT", "features.consecutiveLossesT",
            "features.consecutiveLossesCT", "features.baseline.scoreDifference", "features.baseline.lossStreakDifference")
SMALL_NUMERIC = tuple(f"features.{side}.{field}" for side in ("t", "ct") for field in (
    "alive", "totalHealth", "equipmentValue", "totalArmor", "helmetCount", "defuserCount",
    "grenadeCount", "equipmentKnownPlayers")) + (
    "features.clock.liveElapsedSeconds", "features.clock.roundRemainingSeconds",
    "features.clock.bombRemainingSeconds", "features.clock.defuseRemainingSeconds",
    "features.bomb.hasCarrier", "features.bomb.hasDefuser", "features.quality.aliveKnown",
    "features.quality.aliveEquipmentKnown")
SMALL_CATEGORICAL = ("features.phase", "features.baseline.bombState", "features.clock.clockSource",
                     "features.clock.pauseContext", "features.quality.quality")
WEAPONS = tuple(f"features.{side}.{field}" for side in ("t", "ct") for field in ("rifleCount", "sniperCount"))
FEATURE_SETS = {
    "E0": (v4.NUMERIC_FEATURES, v4.CATEGORICAL_FEATURES),
    "E1": (SMALL_NUMERIC, SMALL_CATEGORICAL),
    "E2": (SMALL_NUMERIC + EXISTING_SPATIAL + ("features.quality.alivePositionKnown",),
           SMALL_CATEGORICAL + ("features.bomb.site", "features.bomb.region")),
    "E3": (v4.NUMERIC_FEATURES + SPATIAL_NUMERIC, v4.CATEGORICAL_FEATURES + SPATIAL_CATEGORICAL),
    "E4_no_kd": (tuple(n for n in v4.NUMERIC_FEATURES if n not in KD), v4.CATEGORICAL_FEATURES),
    "E4_no_progress": (tuple(n for n in v4.NUMERIC_FEATURES if n not in PROGRESS), v4.CATEGORICAL_FEATURES),
    "E5_weapon_counts": (SMALL_NUMERIC + WEAPONS, SMALL_CATEGORICAL),
}
M4_FEATURE_SETS = tuple(FEATURE_SETS)
# Keep availability/count metadata separate from the four geometric groups.
SPATIAL_GROUPS = {
    "quality": tuple(f"spatial.{side}.{field}" for side in ("t", "ct") for field in (
        "observedAliveCount", "validPositionCount", "positionStatus")) + ("spatial.rosterStatus",),
    "c4": tuple(n for n in SPATIAL_NUMERIC if "C4" in n or "c4" in n) + (
        "spatial.bombState", "spatial.bombPositionStatus"),
    "carrier": tuple(n for n in SPATIAL_NUMERIC + SPATIAL_CATEGORICAL if n.startswith("spatial.carrier.")),
    "site": tuple(n for n in SPATIAL_NUMERIC if "siteANearby" in n or "siteBNearby" in n),
    "formation": tuple(n for n in SPATIAL_NUMERIC if "NearestTeammateDistance" in n),
}


def spatial_feature_set(names):
    return (v4.NUMERIC_FEATURES + tuple(n for n in SPATIAL_NUMERIC if n in names),
            v4.CATEGORICAL_FEATURES + tuple(n for n in SPATIAL_CATEGORICAL if n in names))


FEATURE_SETS["S_quality"] = spatial_feature_set(SPATIAL_GROUPS["quality"])
for _group, _names in SPATIAL_GROUPS.items():
    if _group != "quality":
        FEATURE_SETS[f"S_only_{_group}"] = spatial_feature_set(SPATIAL_GROUPS["quality"] + _names)
        FEATURE_SETS[f"S_without_{_group}"] = spatial_feature_set(
            set(SPATIAL_NUMERIC + SPATIAL_CATEGORICAL) - set(_names))
SPATIAL_ABLATION_SETS = ("E0", "E3") + tuple(n for n in FEATURE_SETS if n.startswith("S_"))
ENDGAME_BASES = {"B0": "E0", "B_c4": "S_only_c4"}
for _name, _base in ENDGAME_BASES.items():
    _numeric, _categorical = FEATURE_SETS[_base]
    FEATURE_SETS[_name] = (tuple(n for n in _numeric if n != "features.baseline.isClutch"), _categorical)
INTERACTIONS = {
    "phase_alive": ("interaction.postPlantAliveDifference",),
    "bomb_kit": ("interaction.bombRemainingHasKit",),
    "broad_endgame": ("derived.isBroadEndgame",),
}


def add_interactions(frame, group):
    result = frame.copy()
    if group == "phase_alive":
        phase = frame["features.phase"]
        result[INTERACTIONS[group][0]] = (frame["features.t.alive"] - frame["features.ct.alive"]) * phase.eq("post-plant")
        result.loc[~phase.isin(["live", "post-plant"]), INTERACTIONS[group][0]] = np.nan
    elif group == "bomb_kit":
        kits = frame["features.ct.defuserCount"]
        result[INTERACTIONS[group][0]] = frame["features.clock.bombRemainingSeconds"] * kits.gt(0)
        result.loc[kits.isna(), INTERACTIONS[group][0]] = np.nan
    elif group == "broad_endgame":
        result[INTERACTIONS[group][0]] = broad_endgame_mask(
            frame["features.phase"], frame["features.t.alive"], frame["features.ct.alive"]).astype(float)
    else:
        raise ValueError("Unknown interaction group")
    return result


def candidate_specs(suite="m4"):
    if suite == "broad-endgame":
        return [dict(id=f"{name}_logistic", featureSet=name, model="logistic", parameters={},
                     interaction="broad_endgame" if name in ENDGAME_BASES else None)
                for name in ("E0", "B0", "S_only_c4", "B_c4")]
    if suite not in ("m4", "spatial-ablation"):
        raise ValueError("Unknown experiment suite")
    feature_sets = M4_FEATURE_SETS if suite == "m4" else SPATIAL_ABLATION_SETS
    specs = [dict(id=f"{feature_set}_{model}", featureSet=feature_set, model=model, parameters={}, interaction=None)
             for feature_set in feature_sets for model in ("logistic", "lightgbm")]
    if suite == "spatial-ablation":
        return specs
    for name, model, params in (
        ("C03", "logistic", {"C": 0.3}), ("C3", "logistic", {"C": 3.0}),
        ("leaves7", "lightgbm", {"num_leaves": 7, "max_depth": 3}),
        ("leaves31", "lightgbm", {"num_leaves": 31, "max_depth": 7})):
        specs.append(dict(id=f"E0_{model}_{name}", featureSet="E0", model=model, parameters=params, interaction=None))
    for group in ("phase_alive", "bomb_kit"):
        specs.append(dict(id=f"E0_logistic_{group}", featureSet="E0", model="logistic", parameters={}, interaction=group))
    return specs


def feature_contract(spec):
    return ENDGAME_CONTRACT if spec["featureSet"] in ENDGAME_BASES else CONTRACT


def requires_spatial(feature_set):
    return any(n.startswith("spatial.") for columns in FEATURE_SETS[feature_set] for n in columns)


def make_model(spec, seed=42, threads=4):
    numeric, categorical = FEATURE_SETS[spec["featureSet"]]
    model = v4.make_models(seed, threads)[spec["model"]]
    preprocessor = model.named_steps["preprocessor"]
    generated = INTERACTIONS[spec["interaction"]] if spec["interaction"] else ()
    preprocessor.transformers = [(name, transformer, list(numeric + generated if name == "numeric" else categorical))
                                for name, transformer, _ in preprocessor.transformers]
    model.set_params(**{f"model__{key}": value for key, value in spec["parameters"].items()})
    if spec["interaction"]:
        model.steps.insert(0, ("interactions", FunctionTransformer(add_interactions,
                            kw_args={"group": spec["interaction"]}, validate=False)))
    return model


def spatial_config(manifest):
    return {name: manifest[name] for name in (
        "contractVersion", "baseSchemaVersion", "baseSemanticVersion", "distanceMetric", "radii", "siteA", "siteB")}


def config_sha256(config):
    import hashlib
    return hashlib.sha256(json.dumps(config, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()


def join_spatial(records, directories):
    """Require audited 1:1 companions and identical base features, identities, labels and weights."""
    # The audit module is also a standalone CLI; support its existing sibling imports.
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from audit_win_spatial import audit
    keyed = {(r["matchId"], r["roundId"], r["tick"]): r for r in records}
    seen, config, receipts = set(), None, []
    for directory in directories:
        directory = Path(directory)
        receipt = audit(directory)
        receipts.append(receipt)
        manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
        current = spatial_config(manifest)
        if config is not None and current != config:
            raise ValueError("Spatial configuration differs across shards")
        config = current
        base = v4.load_records(directory / "base/samples.jsonl")
        with (directory / "spatial.jsonl").open(encoding="utf-8") as source:
            for reference, line in zip(base, source, strict=True):
                row = json.loads(line)
                key = row["matchId"], row["roundId"], row["tick"]
                if key not in keyed or key in seen:
                    raise ValueError("Unexpected or duplicate spatial key")
                target = keyed[key]
                if reference != target:
                    raise ValueError("Spatial base differs from frozen v4 input")
                seen.add(key)
                target["__features"].update({n: v4.get_path({"spatial": row["features"]}, n)
                                           for n in (*SPATIAL_NUMERIC, *SPATIAL_CATEGORICAL)})
    if seen != keyed.keys():
        raise ValueError("Missing spatial companions; partial coverage is forbidden")
    return config, receipts


def make_frames(records):
    frame, metadata = v4.make_frames(records)
    if records and SPATIAL_NUMERIC[0] in records[0]["__features"]:
        extra = pd.DataFrame([{n: r["__features"][n] for n in (*SPATIAL_NUMERIC, *SPATIAL_CATEGORICAL)} for r in records])
        for name in SPATIAL_NUMERIC:
            extra[name] = pd.to_numeric(extra[name], errors="raise").astype(float)
        for name in SPATIAL_CATEGORICAL:
            extra[name] = extra[name].fillna("__missing__").astype(str)
        frame = pd.concat([frame, extra], axis=1)
    return frame, metadata


def feature_group(name):
    if name.startswith("missingindicator_"):
        name = name[len("missingindicator_"):]
    if name.startswith("interaction."):
        return "interaction"
    if name.startswith("spatial.") or any(name.startswith(n) for n in EXISTING_SPATIAL + ("features.bomb.site", "features.bomb.region")):
        return "space"
    if name in KD or name in PROGRESS:
        return "matchContext"
    if "quality" in name or "Coverage" in name or "Known" in name:
        return "quality"
    if "Health" in name or "health" in name:
        return "health"
    if "alive" in name.lower() or "Clutch" in name or name == "derived.isBroadEndgame":
        return "numbers"
    if any(s in name.lower() for s in ("clock", "bomb", "phase", "timer")):
        return "clockBomb"
    if name.startswith("mapName"):
        return "map"
    return "equipment"


def grouped_explanation(model, spec, frame, weights):
    """Global model diagnostics, never calibrated percentage-point attribution."""
    transformed = frame
    if spec["interaction"]:
        transformed = add_interactions(frame, spec["interaction"])
    preprocessor = model.named_steps["preprocessor"]
    names = preprocessor.get_feature_names_out()
    groups = np.array([feature_group(n) for n in names])
    estimator = model.named_steps["model"]
    if spec["model"] == "lightgbm":
        gain = estimator.booster_.feature_importance(importance_type="gain")
        return dict(kind="grouped training split gain; not probability change or causality",
                    groups={g: float(gain[groups == g].sum()) for g in sorted(set(groups))})
    terms = preprocessor.transform(transformed).to_numpy() * estimator.coef_[0]
    contributions = {g: terms[:, groups == g].sum(axis=1) for g in sorted(set(groups))}
    reconstructed = sum(contributions.values()) + estimator.intercept_[0]
    error = float(np.max(np.abs(reconstructed - model.decision_function(frame))))
    if error > 1e-8:
        raise ValueError("Grouped explanation does not reconstruct the raw model logit")
    return dict(kind="raw uncalibrated log-odds; not calibrated probability points or causal effects",
                groups={g: float(np.average(np.abs(v), weights=weights)) for g, v in contributions.items()},
                examples=[{g: float(v[i]) for g, v in contributions.items()} for i in range(min(3, len(frame)))],
                intercept=float(estimator.intercept_[0]), maxReconstructionError=error)


class ExperimentalWinRuntime(WinModelRuntime):
    FEATURE_CONTRACT = CONTRACT

    def __init__(self, model_dir):
        manifest = json.loads((Path(model_dir) / "feature-manifest.json").read_text(encoding="utf-8"))
        self.spatial_config_sha256 = manifest.get("spatialConfigSha256")
        spec = manifest["candidate"]
        if feature_contract(spec) != self.FEATURE_CONTRACT:
            raise ModelLoadError("Candidate requires a different experimental feature contract")
        if self.FEATURE_CONTRACT == ENDGAME_CONTRACT:
            if spec not in candidate_specs("broad-endgame") or manifest.get("derivedFeatures") != {
                    "derived.isBroadEndgame": BROAD_ENDGAME_DEFINITION}:
                raise ModelLoadError("Broad endgame candidate or derived feature definition mismatch")
        numeric, categorical = FEATURE_SETS[spec["featureSet"]]
        if manifest["numeric"] != list(numeric) or manifest["categorical"] != list(categorical):
            raise ModelLoadError("Feature list differs from the experiment contract")
        if requires_spatial(spec["featureSet"]) != bool(self.spatial_config_sha256):
            raise ModelLoadError("Missing or unexpected spatial configuration")
        if self.spatial_config_sha256 and config_sha256(manifest["spatialConfig"]) != self.spatial_config_sha256:
            raise ModelLoadError("Spatial configuration hash mismatch")
        super().__init__(model_dir)

    def _flatten_sample(self, sample, index):
        if not isinstance(sample, dict) or sample.get("featureContract") != self.FEATURE_CONTRACT:
            raise RequestError("feature_contract_mismatch", f"Sample requires {self.FEATURE_CONTRACT} contract")
        if self.spatial_config_sha256 and sample.get("spatialConfigSha256") != self.spatial_config_sha256:
            raise RequestError("spatial_config_mismatch", "M4 sample uses different spatial settings")
        return super()._flatten_sample(sample, index)


class BroadEndgameWinRuntime(ExperimentalWinRuntime):
    FEATURE_CONTRACT = ENDGAME_CONTRACT


def load_experimental_runtime(model_dir):
    manifest = json.loads((Path(model_dir) / "feature-manifest.json").read_text(encoding="utf-8"))
    runtime = BroadEndgameWinRuntime if manifest.get("featureContract") == ENDGAME_CONTRACT else ExperimentalWinRuntime
    return runtime(model_dir)
