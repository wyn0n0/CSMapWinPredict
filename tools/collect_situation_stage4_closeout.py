"""Run bounded stage-four regressions and collect private, actual closeout proof.

This is an isolated preparation command, never a training or tuning consumer.
Review payloads are inspected only for fixed version/use checks; stdout contains
gate names and status, never test examples, labels or distributions.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys
from datetime import datetime, timezone

from audit_situation_stage4_closeout import audit_stage4_closeout, CHECKS
from build_situation_stage6 import build_input_paths, verify
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json


def logical_binding(path, root, kind="input", **extra):
    path = Path(path).resolve()
    scheme, base = ("repo", REPO) if path.is_relative_to(REPO) else ("storage", root)
    logical = scheme + ":" + path.relative_to(base).as_posix()
    actual = digest(path)
    return dict(logicalPath=logical, sha256=actual, actualSha256=actual, kind=kind, **extra)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--build-evidence", required=True, help="successful build JSON with sourceBindings")
    args = parser.parse_args()
    config = read_json(CONFIG)
    root = storage_root(config["storageRoot"])
    out = safe_child(root, "reports/" + args.run_id)
    out.mkdir(exist_ok=False)
    build_path = Path(args.build_evidence)
    build = read_json(build_path)
    if build.get("exitCode") != 0:
        raise ValueError("build-not-passed")
    if {b['logicalPath'] for b in build['sourceBindings']} != {'repo:' + p.relative_to(REPO).as_posix() for p in build_input_paths()}:
        raise ValueError('build-input-coverage-incomplete')
    binary_paths = {b['logicalPath'] for b in build.get('binaryBindings', [])}
    for folder, stem in [('apps/api', 'CsDemoMap.Api'), ('apps/cli', 'CsDemoMap.Cli'), ('tests/CsDemoMap.Api.Tests', 'CsDemoMap.Api.Tests')]:
        for suffix in ('.dll', '.deps.json', '.runtimeconfig.json'):
            if f'repo:{folder}/bin/Release/net10.0/{stem}{suffix}' not in binary_paths:
                raise ValueError('build-binary-coverage-incomplete')
    verify(build['sourceBindings'] + build['binaryBindings'])
    expected = read_json(REPO / "situation-implementation/stage6-goals-handoff/frozen-contract.json")
    base_dir = safe_child(root, expected["baseline"]["base_dataset"])
    review_dir = safe_child(root, expected["baseline"]["frozen_review"])
    split_path = REPO / expected["baseline"]["split_file"]
    base, review, split = read_json(base_dir / "manifest.json"), read_json(review_dir / "manifest.json"), read_json(split_path)
    integrity_path = REPO / "situation-implementation/stage6-goals-handoff/source-integrity.json"
    integrity = read_json(integrity_path)
    bindings = [logical_binding(p, root) for p in (base_dir / "manifest.json", review_dir / "manifest.json",
                                                split_path, integrity_path, build_path)]
    for folder, manifest in ((base_dir, base), (review_dir, review)):
        for entry in manifest["files"]:
            path = safe_child(folder, entry["path"])
            # Shared review files include test rows. Keep their hash checks in
            # the isolated child when health consumers later verify this report.
            isolated = folder == review_dir or path.name.startswith("test")
            binding = logical_binding(path, root, **({"scope": "isolated-test"} if isolated else {}))
            if binding["sha256"] != entry["sha256"]:
                raise ValueError("frozen-artifact-drift")
            bindings.append(binding)
    for schema in base["schemaFiles"] + review["identity"]["schemaFiles"]:
        binding = logical_binding(REPO / schema["path"], root)
        if binding["sha256"] != schema["sha256"]:
            raise ValueError("frozen-schema-drift")
        bindings.append(binding)
    stats = read_json(review_dir / "review-stats.json")
    effective = stats["effective"]
    versions = set()
    with (review_dir / "review-inputs.jsonl").open(encoding="utf-8") as source:
        for line in source:
            versions.add(json.loads(line)["input"]["facts"]["analysisRuleVersion"])
    if len(versions) != 1:
        raise ValueError("review-rules-mixed")
    uses = {}
    for role in ("train", "dev", "test"):
        allowed = None
        with (review_dir / (role + "-labels.jsonl")).open(encoding="utf-8") as source:
            for line in source:
                row = json.loads(line)
                current = set(row["allowedUse"])
                if allowed is not None and current != allowed:
                    raise ValueError("review-purpose-mixed")
                allowed = current
        uses[role] = sorted(allowed or [])
    selected_uses = dict(train="sft", dev="checkpoint-selection", test="final-evaluation")
    permitted = dict(train=['sft'], dev=['checkpoint-selection', 'evaluation', 'prompt-selection'], test=['final-evaluation'])
    if any(uses[role] != permitted[role] for role in uses):
        raise ValueError("review-purpose-mismatch")
    metadata = dict(analysisRuleVersion=versions.pop(), purposes=selected_uses,
                    approvedCount=effective["approved"],
                    unreviewedCount=stats["target"] - sum(effective[k] for k in ("approved", "modified", "rejected")),
                    rejectedCount=effective["rejected"], blockingIssue=effective["blockingIssues"] != 0,
                    actualAllowedUses=uses)
    write_new_json(out / "review-metadata.json", metadata)
    bindings.append(logical_binding(out / "review-metadata.json", root))
    dependencies = [logical_binding(REPO / p, root, "dependency") for p in
                    ("apps/api/CsDemoMap.Api.csproj", "apps/cli/CsDemoMap.Cli.csproj",
                     "tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj", "package-lock.json")]
    sources = [dict(b, actualSha256=digest(REPO / b["logicalPath"].removeprefix("repo:")), kind="source")
               for b in build["sourceBindings"]]
    common = sources + dependencies + [dict(b, actualSha256=b['sha256'], kind='dependency') for b in build['binaryBindings']] + [logical_binding(split_path, root), logical_binding(build_path, root)]
    regressions = {}
    for name in CHECKS:
        flag = "--verify-situation-stage-four-" + name
        if name == "review": flag = "--verify-situation-review-automatic"
        if name == "freeze": flag = "--verify-situation-review-freezer"
        commands = [["dotnet", "run", "--project", "tests/CsDemoMap.Api.Tests/CsDemoMap.Api.Tests.csproj",
                     "-c", "Release", "--no-launch-profile", "--no-build", "--no-restore", "--", flag]]
        if name == "review": commands.append(["node", "--test", "tests/situation-review-ui/core.test.mjs"])
        results = []
        for command in commands:
            process = subprocess.run(command, cwd=REPO, capture_output=True, text=True, encoding="utf-8", errors="replace")
            results.append(dict(command=command, exitCode=process.returncode, stdout=process.stdout, stderr=process.stderr))
        passed = all(r["exitCode"] == 0 for r in results)
        command_path = out / (name + "-command.json")
        write_new_json(command_path, dict(status="passed" if passed else "failed", results=results))
        regressions[name] = dict(status="passed" if passed else "failed", exitCode=0 if passed else 2,
                                 command=" && ".join(" ".join(r["command"]) for r in results), mode="rerun",
                                 bindings=common + [logical_binding(command_path, root)])
        print(json.dumps(dict(checkId=name, status=regressions[name]["status"])), flush=True)
    verify(build['sourceBindings'] + build['binaryBindings'])
    for binding in bindings + dependencies:
        prefix, relative = binding['logicalPath'].split(':', 1)
        if digest(safe_child(REPO if prefix == 'repo' else root, relative)) != binding['sha256']:
            raise ValueError('regression-input-drift')
    write_new_json(out / "regressions.json", regressions)
    bindings.append(logical_binding(out / "regressions.json", root))
    bindings += [logical_binding(REPO / p, root, "source") for p in
                 ("tools/audit_situation_stage4_closeout.py", "tools/collect_situation_stage4_closeout.py")]
    expected["requiredBindings"] = [b["logicalPath"] for b in bindings]
    report = audit_stage4_closeout(dict(base=base, review=review, split=split, reviewMetadata=metadata,
                                        integrity=integrity, regressions=regressions, bindings=bindings), expected)
    report["checkedAtUtc"] = datetime.now(timezone.utc).isoformat()
    write_new_json(out / "stage4-report.json", report)
    print(json.dumps(dict(status=report["status"], reasonCodes=report["reasonCodes"],
                          reportSha256=digest(out / "stage4-report.json"))), flush=True)
    return 0 if report["status"] == "passed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
