"""File-backed preparation evidence; reading this module never starts training.

Test payload bindings are checked in a dedicated subprocess. The parent receives
only hashes and a pass/fail result, never test records or length statistics.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import subprocess

try:
    from .situation_environment import CONFIG, REPO, digest, read_json, safe_child
except ImportError:
    from situation_environment import CONFIG, REPO, digest, read_json, safe_child


try:
    from .situation_evidence import EvidenceError, EvidenceStore, require
except ImportError:
    from situation_evidence import EvidenceError, EvidenceStore, require

BUNDLE_SCHEMA = "situation-stage6-preparation-bundle-v1"
DEFAULT_BUNDLE = "situation-implementation/situation-stage6-preparation-v1.json"
SHA256 = re.compile(r"[0-9a-f]{64}\Z")


def read_preparation_bundle(config, bundle_path=None):
    """Verify the frozen report/config references and every declared binding."""
    store = EvidenceStore(config["repository"], config["storageRoot"])
    path = Path(bundle_path) if bundle_path else safe_child(store.repository, DEFAULT_BUNDLE)
    bundle = read_json(path)
    require(isinstance(bundle, dict) and bundle.get("schemaVersion") == BUNDLE_SCHEMA,
            "preparation-bundle-schema-invalid")
    require(bundle.get("status") == "complete" and bundle.get("frozen") is True,
            "preparation-bundle-incomplete")
    artifacts = bundle.get("artifacts")
    require(isinstance(artifacts, dict) and all(key in artifacts for key in
            ("stage4", "corpus", "budget", "runtime")), "preparation-artifacts-missing")
    reports = {key: store.report(artifacts[key]) for key in ("stage4", "corpus", "budget")}
    for binding in bundle.get('evidenceBindings', []): store.verify(binding)
    store.verify(artifacts["runtime"])
    runtime = read_json(store.path(artifacts["runtime"]["logicalPath"]))
    require(isinstance(runtime, dict), "runtime-invalid")
    for binding in runtime.get("evidenceBindings", []):
        store.verify(binding)
    return bundle, reports, runtime, store


def collect_preparation_evidence(config, resource_report, bundle_path=None):
    """Return current preparation admission from real, hash-checked artifacts.

    Absence is an ordinary closed gate. A present but invalid bundle remains a
    failure; neither case falls back to old v1 token measurements as v8 proof.
    """
    path = Path(bundle_path) if bundle_path else Path(config["repository"]) / DEFAULT_BUNDLE
    failure = {
        "schemaVersion": "situation-stage6-preparation-observation-v1",
        "status": "not-tested", "verified": False,
        "preparationReady": False, "trainingExecutionReady": False,
        "selectedRuntime": None,
        "reasonCodes": ["stage6-preparation-evidence-missing"],
        "stage4": {"status": "not-tested", "closeoutComplete": False,
                   "reasonCodes": ["stage4-closeout-pending"]},
        "corpus": {"status": "not-tested", "paired": False, "purpose": "training",
                   "reasonCodes": ["corpus-pairing-pending"]},
    }
    if not path.is_file():
        return failure
    try:
        bundle, reports, runtime, store = read_preparation_bundle(config, path)
        try:
            from .situation_stage6_readiness import evaluate_preparation_evidence
        except ImportError:
            from situation_stage6_readiness import evaluate_preparation_evidence
        runtime = dict(runtime, _evidenceResolver=store.verify,
                       _artifactBinding=bundle["artifacts"]["runtime"])
        readiness = evaluate_preparation_evidence(
            reports["stage4"], reports["corpus"], reports["budget"], runtime, resource_report)
        require(isinstance(readiness, dict), "readiness-report-invalid")
        details = readiness.get("details", readiness)
        ready = readiness.get("status") == "passed" and details.get("preparationReady") is True
        require(details.get("trainingExecutionReady") is False, "training-permission-out-of-scope")
        rule = bundle.get("analysisRuleVersion")
        require(rule == "situation-analysis-rules-v8-close-exposure-1", "bundle-rule-version-invalid")
        budget = reports["budget"]["details"]
        result = dict(failure, status="passed" if ready else "failed", verified=True,
                      preparationReady=ready, selectedRuntime=details.get("selectedRuntime"),
                      reasonCodes=readiness.get("reasonCodes", []), readiness=readiness,
                      evidenceBindings=[{"logicalPath": k, "sha256": v} for k, v in store.checked.items()])
        result["stage4"] = dict(status=reports["stage4"]["status"],
                                closeoutComplete=reports["stage4"]["status"] == "passed",
                                verified=True, reasonCodes=reports["stage4"]["reasonCodes"])
        result["corpus"] = dict(status=reports["corpus"]["status"],
                                paired=reports["corpus"]["details"].get("pairingVerified") is True,
                                verified=True, purpose="training", inputRuleVersion=rule,
                                labelRuleVersion=rule, usedTestForTraining=False, usedTestForTuning=False,
                                reasonCodes=reports["corpus"]["reasonCodes"])
        result["inputBudget"] = dict(status=reports["budget"]["status"], verified=True,
                                     inputBudgetReady=reports["budget"]["status"] == "passed",
                                     fullTrainValidated=budget.get("trainValidated") is True,
                                     maxSequenceLength=budget.get("maxSequenceLength"),
                                     representationVersion=budget.get("representationVersion"),
                                     overBudgetCount=budget.get("overBudgetCount"), scope="formal v8 train",
                                     purpose="training", usedTestForTraining=False, usedTestForTuning=False,
                                     reasonCodes=reports["budget"]["reasonCodes"])
        return result
    except (OSError, ValueError, KeyError, TypeError, ImportError, subprocess.SubprocessError) as exc:
        failure.update(status="failed", reasonCodes=["stage6-preparation-evidence-invalid"],
                       details={"errorType": type(exc).__name__, "message": str(exc)})
        return failure


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--isolated-verify-binding", nargs=2, metavar=("LOGICAL_PATH", "SHA256"))
    parser.add_argument("--repository", default=str(REPO))
    parser.add_argument("--storage-root")
    args = parser.parse_args(argv)
    if not args.isolated_verify_binding:
        parser.error("Use the preparation orchestrator or --isolated-verify-binding")
    try:
        require(args.storage_root, "storage-root-required")
        logical, expected = args.isolated_verify_binding
        store = EvidenceStore(args.repository, args.storage_root, isolated=True)
        store.verify({"logicalPath": logical, "sha256": expected, "split": "test"})
        print(json.dumps({"status": "passed", "sha256": expected}))
        return 0
    except (OSError, ValueError, TypeError, KeyError):
        # Keep test filenames, content and error details out of the parent.
        print(json.dumps({"status": "failed", "sha256": None}))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
