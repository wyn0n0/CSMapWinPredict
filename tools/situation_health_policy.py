"""Pure, fail-closed situation health policy and recursive public redaction.

The policy consumes observations and already audited evidence. It performs no I/O,
GPU work, hashing, installation or training; ``verified`` is the auditor's claim.
Readiness does not approve a runtime or choose an adapter.
"""

from __future__ import annotations

import copy
import re
from collections.abc import Mapping
from typing import Any


CHECK_IDS = ("python", "dependencies", "cuda", "base", "cache", "disk", "isolation")
STATUSES = frozenset(("passed", "failed", "not-tested", "not-required"))
ENVIRONMENT_CHECKS = ("python", "dependencies", "cuda", "cache", "disk", "isolation")
READINESS_FIELDS = (
    "environmentReady", "baseReady", "inputBudgetReady", "resourceProbeReady",
    "resourceAccepted", "stage6Ready", "preparationReady",
)
_DEFAULT_REQUIREMENTS = {
    "pythonVersion": "3.11", "maxSequenceLength": 2560,
    "representationVersion": "facts-only-utf8-v1", "purpose": "training",
    "allowUserResourceAcceptance": True, "minimumHeadroomFraction": 0.10,
}


def evaluate_gates(observations: dict, evidence: dict, requirements: dict) -> dict:
    """Evaluate required gates without mutating inputs.

    Exit 0 requires stage-six readiness; 2 denotes known missing, deferred or
    failed gates; 1 denotes malformed/contradictory diagnostic inputs. A verified
    user acceptance can satisfy the resource gate while preserving a false
    numerical ``resourceProbeReady``. Neither overrides upstream data gates.
    """
    reasons: set[str] = set()
    diagnostic = False

    def error(code: str) -> None:
        nonlocal diagnostic
        diagnostic = True
        reasons.add(code)

    def codes(value: Any, source: str) -> list[str]:
        if not isinstance(value, list) or any(not isinstance(v, str) or not v for v in value):
            error(f"{source}-reason-codes-invalid")
            return []
        return value

    def object_input(value: Any, source: str) -> dict:
        if not isinstance(value, dict):
            error(f"{source}-invalid")
            return {}
        return value

    observations = object_input(observations, "observations")
    evidence = object_input(evidence, "evidence")
    supplied_requirements = object_input(requirements, "requirements")
    requirements = {**_DEFAULT_REQUIREMENTS, **supplied_requirements}
    limit = requirements.get("maxSequenceLength")
    if type(limit) is not int or limit <= 0:
        error("max-sequence-length-invalid")
        limit = 2560
    for key in ("pythonVersion", "representationVersion", "purpose"):
        if not isinstance(requirements.get(key), str) or not requirements[key]:
            error("requirements-invalid")
    if type(requirements.get("allowUserResourceAcceptance")) is not bool:
        error("requirements-invalid")
    headroom = requirements.get("minimumHeadroomFraction")
    if type(headroom) not in (int, float) or not 0 <= headroom <= 1:
        error("requirements-invalid")
    if requirements.get("purpose") != "training":
        reasons.add("purpose-mismatch")

    checks = observations.get("checks", [])
    if not isinstance(checks, list):
        error("observation-checks-invalid")
        checks = []
    by_id: dict[str, dict] = {}
    for check in checks:
        if not isinstance(check, dict) or not isinstance(check.get("checkId"), str):
            error("observation-check-invalid")
            continue
        check_id = check["checkId"]
        if check_id in by_id:
            error("observation-check-duplicate")
            by_id[check_id] = {"checkId": check_id, "status": "failed"}
            continue
        by_id[check_id] = check
        reasons.update(codes(check.get("reasonCodes", []), check_id))
        if not isinstance(check.get("status"), str) or check.get("status") not in STATUSES:
            error("observation-status-invalid")
        for key in ("details", "evidence"):
            if key in check and not isinstance(check[key], dict):
                error("observation-check-invalid")

    normalized_checks = []
    ready_checks: dict[str, bool] = {}
    for check_id in CHECK_IDS:
        check = by_id.get(check_id)
        if check is None:
            reasons.add(f"{check_id}-check-missing")
            check = {"checkId": check_id, "status": "not-tested",
                     "reasonCodes": [f"{check_id}-check-missing"], "details": {}, "evidence": {}}
        ready_checks[check_id] = (
            check.get("status") == "passed"
            and isinstance(check.get("reasonCodes", []), list)
            and all(isinstance(code, str) and code for code in check.get("reasonCodes", []))
            and isinstance(check.get("details", {}), dict)
            and isinstance(check.get("evidence", {}), dict)
        )
        if not ready_checks[check_id]:
            reasons.add(f"{check_id}-check-{check.get('status', 'invalid')}")
        # Explicit stale/unverified metadata cannot be hidden behind passed.
        for metadata in (check.get("evidence", {}), check.get("details", {})):
            if isinstance(metadata, dict):
                for flag in ("stale", "current", "verified", "hashMismatch", "hashesVerified"):
                    if flag in metadata and type(metadata[flag]) is not bool:
                        error("observation-evidence-invalid")
                        ready_checks[check_id] = False
            if isinstance(metadata, dict) and (
                metadata.get("stale") is True or metadata.get("current") is False
                or metadata.get("verified") is False or metadata.get("hashMismatch") is True
                or metadata.get("hashesVerified") is False
            ):
                ready_checks[check_id] = False
                reasons.add(f"{check_id}-evidence-mismatch")
        normalized_checks.append(copy.deepcopy(check))

    python_details = by_id.get("python", {}).get("details", {})
    if isinstance(python_details, dict):
        actual_version = python_details.get("version", python_details.get("pythonVersion"))
        if actual_version is not None and not isinstance(actual_version, str):
            error("python-version-invalid")
            ready_checks["python"] = False
        elif isinstance(actual_version, str) and not (
            actual_version == requirements.get("pythonVersion")
            or actual_version.startswith(str(requirements.get("pythonVersion")) + ".")
        ):
            ready_checks["python"] = False
            reasons.add("python-version-mismatch")

    def section(name: str, *, verified: bool = False) -> tuple[dict, bool]:
        value = evidence.get(name)
        if value is None:
            reasons.add(f"{name}-evidence-missing")
            return {}, False
        if not isinstance(value, dict):
            error(f"{name}-evidence-invalid")
            return {}, False
        reasons.update(codes(value.get("reasonCodes", []), name))
        status = value.get("status")
        status_valid = isinstance(status, str) and status in STATUSES
        if not status_valid:
            error(f"{name}-status-invalid")
        valid = status == "passed"
        if not valid:
            reasons.add(f"{name}-evidence-{status if status_valid else 'invalid'}")
        if verified:
            if "verified" in value and type(value["verified"]) is not bool:
                error(f"{name}-verified-invalid")
            if value.get("verified") is not True:
                reasons.add(f"{name}-evidence-unverified")
                valid = False
        nested = value.get("evidence", {})
        if not isinstance(nested, dict):
            error(f"{name}-evidence-invalid")
            valid = False
            nested = {}
        for metadata in (value, nested):
            for flag in ("stale", "current", "verified", "hashMismatch", "hashesVerified", "bindingsVerified"):
                if flag in metadata and type(metadata[flag]) is not bool:
                    error(f"{name}-evidence-invalid")
                    valid = False
            if (metadata.get("stale") is True or metadata.get("current") is False or metadata.get("verified") is False
                    or metadata.get("hashMismatch") is True or metadata.get("hashesVerified") is False
                    or metadata.get("bindingsVerified") is False):
                reasons.add(f"{name}-evidence-mismatch")
                valid = False
        return value, valid

    budget, budget_valid = section("inputBudget", verified=True)
    for flag in ("inputBudgetReady", "fullTrainValidated"):
        if flag in budget and type(budget[flag]) is not bool:
            error("input-budget-evidence-invalid")
        if budget.get(flag) is not True:
            budget_valid = False
            reasons.add("input-budget-incomplete")
    if budget.get("maxSequenceLength") != limit or type(budget.get("maxSequenceLength")) is not int:
        budget_valid = False
        reasons.add("input-budget-limit-mismatch")
    if budget.get("representationVersion") != requirements.get("representationVersion"):
        budget_valid = False
        reasons.add("input-representation-mismatch")
    scope = budget.get("scope")
    # Accept the frozen report's explicit longer scope, not arbitrary train/dev/test.
    train_scope = isinstance(scope, str) and bool(re.search(r"\btrain\b", scope.lower()))
    if not train_scope or re.search(r"\b(?:dev|test|validation|evaluation)\b", str(scope).lower()):
        budget_valid = False
        reasons.add("purpose-mismatch")
    if budget.get("purpose", "training") != "training":
        budget_valid = False
        reasons.add("purpose-mismatch")
    for key in ("overBudgetCount", "overLimitCount", "maxObservedSequenceLength", "maxSequenceTokens", "maxTotalTokens"):
        if key not in budget:
            continue
        value = budget[key]
        if type(value) is not int or value < 0:
            error("input-budget-evidence-invalid")
            budget_valid = False
        elif (key in ("overBudgetCount", "overLimitCount") and value > 0) or (key not in ("overBudgetCount", "overLimitCount") and value > limit):
            reasons.add("input-over-budget")
            budget_valid = False
    if "inputOverBudget" in budget and type(budget["inputOverBudget"]) is not bool:
        error("input-budget-evidence-invalid")
        budget_valid = False
    if budget.get("inputOverBudget") is True or "input-over-budget" in codes(budget.get("reasonCodes", []), "inputBudget"):
        reasons.add("input-over-budget")
        budget_valid = False

    resource, resource_valid = section("resource", verified=True)
    for flag in ("resourceProbeReady", "resourceAccepted"):
        if flag in resource and type(resource[flag]) is not bool:
            error("resource-evidence-invalid")
            resource_valid = False
    probe_ready = resource_valid and resource.get("resourceProbeReady") is True
    accepted = resource_valid and resource.get("resourceAccepted") is True
    if not resource_valid:
        reasons.add("resource-evidence-mismatch")
    if not probe_ready:
        reasons.add("resource-probe-not-ready")
    if not accepted:
        reasons.add("resource-not-accepted")
    if probe_ready and any(code in codes(resource.get("reasonCodes", []), "resource") for code in ("insufficient-headroom", "oom")):
        error("resource-evidence-contradiction")
        probe_ready = False
    dep_acceptance = resource.get("postProbeDependencyAcceptance")
    if isinstance(dep_acceptance, dict):
        dependency_accepted = dep_acceptance.get("status") == "passed" and dep_acceptance.get("verified") is True
        if dep_acceptance.get("stale") is True or dep_acceptance.get("current") is False:
            dependency_accepted = False
    else:
        dependency_accepted = dep_acceptance == "passed"
    dependency_accepted = resource_valid and dependency_accepted
    if not dependency_accepted:
        reasons.add("post-probe-dependency-acceptance-pending")

    stage4, stage4_valid = section("stage4")
    if "closeoutComplete" in stage4 and type(stage4["closeoutComplete"]) is not bool:
        error("stage4-evidence-invalid")
    stage4_ready = stage4_valid and stage4.get("closeoutComplete") is True
    if not stage4_ready:
        reasons.add("stage4-closeout-pending")
    corpus, corpus_valid = section("corpus")
    if "paired" in corpus and type(corpus["paired"]) is not bool:
        error("corpus-evidence-invalid")
    corpus_ready = corpus_valid and corpus.get("paired") is True
    if corpus.get("purpose") != "training" or requirements.get("purpose") != "training":
        reasons.add("purpose-mismatch")
        corpus_ready = False
    for source in (corpus, budget):
        for flag in ("usedTestForTraining", "usedTestForTuning"):
            if flag in source and type(source[flag]) is not bool:
                error("purpose-evidence-invalid")
                corpus_ready = False
                budget_valid = False
        if source.get("usedTestForTraining") is True or source.get("usedTestForTuning") is True:
            reasons.add("test-purpose-misuse")
            corpus_ready = False
            budget_valid = False
        if source.get("split") in ("test", "dev"):
            reasons.add("purpose-mismatch")
            corpus_ready = False
    input_rule = corpus.get("inputRuleVersion")
    label_rule = corpus.get("labelRuleVersion")
    if not isinstance(input_rule, str) or not input_rule or input_rule != label_rule:
        reasons.add("rule-version-mismatch")
        corpus_ready = False
    if not corpus_ready:
        reasons.add("corpus-pairing-pending")

    preparation, preparation_valid = section("preparation", verified=True)
    preparation_ready = (preparation_valid and preparation.get("preparationReady") is True
                         and preparation.get("trainingExecutionReady") is False
                         and preparation.get("selectedRuntime") is not None)
    if not preparation_ready:
        reasons.add("stage6-preparation-not-ready")

    environment_ready = all(ready_checks[key] for key in ENVIRONMENT_CHECKS)
    base_ready = ready_checks["base"]
    resource_gate = probe_ready or (requirements.get("allowUserResourceAcceptance") is True and accepted)
    stage6_ready = all((environment_ready, base_ready, budget_valid, resource_gate,
                        dependency_accepted, stage4_ready, corpus_ready, preparation_ready)) and not diagnostic
    result = {
        "schemaVersion": "situation-health-report-v1", "checks": normalized_checks,
        "environmentReady": environment_ready, "baseReady": base_ready,
        "inputBudgetReady": bool(budget_valid), "resourceProbeReady": bool(probe_ready),
        "resourceAccepted": bool(accepted), "stage6Ready": stage6_ready,
        "preparationReady": stage6_ready, "trainingExecutionReady": False,
        "selectedRuntime": copy.deepcopy(preparation.get("selectedRuntime")) if stage6_ready else None,
        "reasonCodes": sorted(reasons), "exitCode": 1 if diagnostic else (0 if stage6_ready else 2),
        "evidence": copy.deepcopy(evidence),
    }
    return result


_SECRET_KEY = re.compile(r"(?:password|passwd|pwd|secret|credential|authorization|api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|bearer|private[-_ ]?key|token$|(?:^|[_-])token(?:$|[_-]))", re.I)
_USER_KEY = re.compile(r"^(?:user(?:name)?|login|accountName)$", re.I)
_ABSOLUTE_PATH = re.compile(
    r"(?<![\w$])(?:[A-Za-z]:[\\/]|\\\\)[^\r\n\"'<>|,;}$]*"
    r"|(?<![\w$:/])//[^\r\n\"'<>|,;}$]*"
    r"|(?<![\w$:/])/(?!/)[^\s/\"'<>|,;}$]+(?:/[^\r\n\"'<>|,;}$]*)?"
)
_ASSIGNMENT = re.compile(
    r"\b(password|passwd|pwd|secret|token|api[-_]?key|access[-_]?token|refresh[-_]?token|username|user|login)"
    r"(\s*[:=]\s*)(?:\"[^\"]*\"|'[^']*'|[^\s,;&]+)", re.I,
)


def redact_public(report: dict, redaction_context: dict) -> dict:
    """Return a recursively sanitized copy, including unknown paths and errors.

    Known roots become logical placeholders before generic path removal. Secret
    fields are replaced even when nested inside lists; unrecognized object types
    never leak their repr. Dictionary key collisions retain both sanitized values.
    """
    if not isinstance(report, dict):
        raise TypeError("report must be a dictionary")
    if not isinstance(redaction_context, dict):
        raise TypeError("redaction_context must be a dictionary")
    replacements: list[tuple[str, str]] = []
    for name, replacement in (("storageRoot", "$SITUATION_STORAGE_ROOT"),
                              ("repository", "$REPOSITORY"), ("python", "$SITUATION_PYTHON")):
        value = redaction_context.get(name)
        if isinstance(value, str) and value and not value.startswith("$"):
            replacements.append((value.rstrip("\\/"), replacement))
    replacements.sort(key=lambda pair: len(pair[0]), reverse=True)
    username = redaction_context.get("username")

    def sanitize_string(value: str) -> str:
        for private, logical in replacements:
            # Windows path separator variants and case differences are equivalent.
            pattern = r"[\\/]".join(re.escape(part) for part in re.split(r"[\\/]", private))
            value = re.sub(pattern + r"(?=$|[\\/\s\"',;:)}\]])", lambda _: logical, value, flags=re.I)
        if isinstance(username, str) and username:
            value = re.sub(r"(?<!\w)" + re.escape(username) + r"(?!\w)", "<redacted-user>", value, flags=re.I)
        value = re.sub(r"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", "<redacted-secret>", value)
        value = re.sub(r"\bBearer\s+[^\s\"',;]+", "Bearer <redacted-secret>", value, flags=re.I)
        value = re.sub(r"\bAuthorization\s*[:=]\s*Basic\s+[^\s\"',;]+", "Authorization: Basic <redacted-secret>", value, flags=re.I)
        value = _ASSIGNMENT.sub(lambda match: match[1] + match[2] + "<redacted-secret>", value)
        value = re.sub(r"(https?://)[^\s/@:]+:[^\s/@]+@", r"\1<redacted-credentials>@", value, flags=re.I)
        value = re.sub(r"\b(?:sk-[A-Za-z0-9_-]{8,}|gh[pousr]_[A-Za-z0-9]{8,}|AKIA[A-Z0-9]{16})\b", "<redacted-secret>", value)
        value = re.sub(r"\bfile:(?://)?[^\r\n\"'<>|,;}$]*", "<redacted-path>", value, flags=re.I)
        value = _ABSOLUTE_PATH.sub("<redacted-path>", value)
        return value

    def visit(value: Any) -> Any:
        if isinstance(value, Mapping):
            result = {}
            for original_key, child in value.items():
                key = sanitize_string(original_key) if isinstance(original_key, str) else "<redacted-key>"
                base_key = key
                suffix = 2
                while key in result:
                    key = f"{base_key}#{suffix}"
                    suffix += 1
                if isinstance(original_key, str) and (_SECRET_KEY.search(original_key) or _USER_KEY.match(original_key)):
                    result[key] = "<redacted-secret>" if _SECRET_KEY.search(original_key) else "<redacted-user>"
                else:
                    result[key] = visit(child)
            return result
        if isinstance(value, (list, tuple)):
            return [visit(child) for child in value]
        if isinstance(value, str):
            return sanitize_string(value)
        if value is None or type(value) in (bool, int, float):
            return value
        return "<redacted-unsupported-value>"

    return visit(report)
