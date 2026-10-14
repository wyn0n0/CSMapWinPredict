"""Read-only step-seven acceptance audit. Never initialize CUDA or load a model.

The user may accept an observed shortfall; this never changes the numerical gate.
All paths/details are private until the coordinator applies public redaction.
"""
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess


class EvidenceError(ValueError):
    def __init__(self, reason, detail):
        super().__init__(detail)
        self.reason = reason


def _require(condition, detail, reason="resource-evidence-mismatch"):
    if not condition:
        raise EvidenceError(reason, detail)


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        _require(key not in result, "duplicate-json-field:" + key)
        result[key] = value
    return result


def _read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"),
                      object_pairs_hook=_unique_object,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))


def _child(root, relative):
    path = (Path(root) / relative).resolve()
    _require(path.is_relative_to(Path(root).resolve()), "evidence-path-outside-root")
    return path


def _installed_packages(python):
    script = "import importlib.metadata,json,re; print(json.dumps({re.sub(r'[-_.]+','-',d.metadata['Name']).lower():d.version for d in importlib.metadata.distributions()}))"
    result = subprocess.run([str(python), "-I", "-c", script], capture_output=True,
                            text=True, timeout=30, check=False)
    _require(result.returncode == 0, "installed-profile-query-failed", "dependency-conflict")
    return json.loads(result.stdout)


def audit_acceptance(status_path, expected_bindings):
    """Verify current acceptance, immutable probes, bindings and offline rebuilds.

    Required bindings: repository, storageRoot, acceptancePath,
    dependencyLockSha256, maxSequenceLength, representationVersion.
    Optional explicit paths: python, baseManifestPath, cudaVerificationPath,
    isolationBaselinePath. Defaults resolve the frozen existing local evidence.
    No outputs are written. No dev/test content is opened.
    """
    output = dict(status="failed", verified=False, resourceProbeReady=False,
                  resourceAccepted=False, postProbeDependencyAcceptance="deferred",
                  reasonCodes=[], details={}, evidence={})
    hashes = {}
    def digest(path):
        path = Path(path).resolve()
        if path not in hashes:
            h = hashlib.sha256()
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
                    h.update(block)
            hashes[path] = h.hexdigest()
        return hashes[path]
    def bound(path, value, detail):
        _require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value), detail + ":invalid-hash")
        _require(digest(path) == value, detail + ":hash-mismatch")
    def code_bindings(values):
        _require(isinstance(values, dict) and bool(values), "missing-source-bindings")
        for name, value in values.items():
            bound(_child(repo, name), value, "source:" + name)
    try:
        for key in ("repository", "storageRoot", "acceptancePath", "dependencyLockSha256",
                    "maxSequenceLength", "representationVersion"):
            _require(key in expected_bindings, "missing-expected-binding:" + key)
        repo = Path(expected_bindings["repository"]).resolve()
        root = Path(expected_bindings["storageRoot"]).resolve()
        path = Path(status_path).resolve()
        acceptance_path = Path(expected_bindings["acceptancePath"])
        if not acceptance_path.is_absolute():
            acceptance_path = _child(repo, str(acceptance_path))
        _require(path == acceptance_path.resolve(), "unexpected-acceptance-path")
        state = _read(path)
        _require(state["schemaVersion"] == "situation-resource-status-v4", "current-user-acceptance-required")
        _require(state["resourceProbeReady"] is False and state["trainingReady"] is False
                 and state["selectedRuntime"] is None, "current-numerical-state-changed")
        _require(state["maxSequenceLength"] == expected_bindings["maxSequenceLength"] == 2560
                 and state["representationVersion"] == expected_bindings["representationVersion"] == "facts-only-utf8-v1",
                 "input-representation-binding-mismatch")
        previous = state["previousStatus"]
        _require(previous["path"] == "situation-implementation/situation-resource-status-v3.json", "wrong-original-status")
        original_path = _child(repo, previous["path"])
        bound(original_path, previous["sha256"], "original-status")
        original = _read(original_path)
        _require(original["schemaVersion"] == "situation-resource-status-v3"
                 and original["verifiedDate"] == "2026-10-02", "old-original-status")
        acceptance = state["userAcceptance"]
        _require(state["resourceAccepted"] is True and acceptance["status"] == "accepted"
                 and acceptance["authority"] == "direct-user-instruction"
                 and acceptance["date"] >= original["verifiedDate"]
                 and acceptance["sourceStatusPath"] == previous["path"]
                 and acceptance["sourceStatusSha256"] == previous["sha256"], "user-acceptance-binding-mismatch")
        attempts = original["attempts"]
        _require(len(attempts) == 4 and len(state["attempts"]) == 4, "latest-four-attempts-required")
        _require([(a["precision"], a["rank"]) for a in attempts] == [("bf16", 16), ("bf16", 16), ("nf4", 16), ("nf4", 8)], "configuration-scope-mismatch")
        _require(len({a["run"] for a in attempts}) == 4, "duplicate-attempt")
        _require(acceptance["attemptReportSha256"] == [a["reportSha256"] for a in attempts], "accepted-report-scope-mismatch")
        for old, new in zip(attempts, state["attempts"]):
            _require(all(new.get(k) == v for k, v in old.items()), "immutable-measurement-changed")
            _require(new["acceptance"] == "accepted" and new["acceptanceAuthority"] == "user"
                     and new["acceptedDate"] == acceptance["date"], "attempt-acceptance-missing")
        verification_path = _child(root, "reports/" + original["summaryRun"] + "/verification.json")
        bound(verification_path, original["verificationSha256"], "independent-verification")
        _require(state["verificationSha256"] == original["verificationSha256"], "verification-overlay-drift")
        verification = _read(verification_path)
        _require(verification["status"] == "passed" and verification["attempts"] == attempts
                 and verification["cases"] == 128 and verification["optimizerSteps"] == 8
                 and verification["resourceProbeReady"] is False
                 and verification["selectedRuntime"] is None, "independent-verification-mismatch")
        bound(verification_path.parent / "verify.py", verification["verifierSha256"], "independent-verifier")
        reports = []
        for attempt in attempts:
            folder = _child(root, "reports/" + attempt["run"])
            report_path, events_path = folder / "memory-probe.json", folder / "events.jsonl"
            bound(report_path, attempt["reportSha256"], "probe-report")
            bound(events_path, attempt["eventsSha256"], "probe-events")
            report = _read(report_path)
            events = [json.loads(line, object_pairs_hook=_unique_object) for line in events_path.read_text(encoding="utf-8").splitlines()]
            _require(events == report["microSteps"] and [e["microStep"] for e in events] == list(range(1, 33)), "microsteps-invalid")
            cfg = report["config"]
            bound(_child(repo, report["configPath"]), report["configSha256"], "probe-config")
            expected_cfg = _read(_child(repo, report["configPath"]))
            expected_cfg["rank"] = report["rank"]
            _require(cfg == expected_cfg, "probe-config-content-mismatch")
            _require(cfg["maxSequenceLength"] == 2560 and cfg["gradientAccumulationSteps"] == 16
                     and cfg["optimizerSteps"] == 2 and cfg["microBatchSize"] == 1
                     and cfg["minimumHeadroomFraction"] == .1 and cfg["saveAdapter"] is False
                     and cfg["sdpaBackend"] == "cudnn" and cfg["lossImplementation"] == "assistant-positions", "probe-configuration-invalid")
            _require(report["adapterSaved"] is False and report["trainingReady"] is False
                     and report["operationsPassed"] is True and report["phase"] == "complete", "operations-not-complete")
            _require(report["precision"] == attempt["precision"] and report["rank"] == attempt["rank"], "attempt-config-mismatch")
            _require(report["trainableParameters"] == 17432576 * report["rank"] // 16, "adapter-capacity-mismatch")
            for i, event in enumerate(events):
                _require(math.isfinite(event["loss"]) and event["supervisedTokens"] > 0
                         and event["supervisedTokens"] == event["projectedTokens"], "loss-or-supervision-invalid")
                _require(0 < event["tokens"] <= 2560, "probe-input-over-budget")
                _require(event["phase"] == ("synthetic" if i < 16 else "real-train"), "probe-phase-mismatch")
                if i < 16:
                    _require(event["tokens"] == 2560 and event["supervisedTokens"] == 512, "synthetic-pressure-missing")
            updates = report["optimizerSteps"]
            _require([s["step"] for s in updates] == [1, 2], "updates-missing")
            _require(all(s["changedAdapterTensors"] > 0 and math.isfinite(s["gradientNorm"])
                         and s["gradientNorm"] > 0 and s["stateDtypes"] for s in updates), "adapter-update-invalid")
            attention = report["attentionVerification"]
            _require([a["microStep"] for a in attention] == [1, 17], "actual-attention-evidence-missing")
            for a in attention:
                operators = a["operators"]
                _require(a["backend"] == "cudnn" and a["includesCheckpointRecomputation"] is True
                         and operators.get("aten::_scaled_dot_product_cudnn_attention") == 56
                         and operators.get("aten::_scaled_dot_product_cudnn_attention_backward") == 28
                         and not any(any(word in op for word in ("math", "flash_attention", "efficient_attention")) for op in operators), "actual-cudnn-evidence-invalid")
            if report["precision"] == "nf4":
                q = report["quantization"]
                _require(q["type"] == "nf4" and q["doubleQuant"] is True and q["linear4bitModules"] == 196
                         and q["computeDtype"] == "torch.bfloat16" and q["bitsandbytes"] == "0.48.1"
                         and report["qlora"] == "passed", "actual-nf4-evidence-invalid")
            baseline, after = report["memoryBefore"], report["memoryAfter"]
            increment = max(0, after["peakReservedBytes"] - baseline["reservedBytes"])
            budget = min(baseline["freeBytes"], report["nvidiaSmiBefore"]["freeBytes"]) - increment
            sampled = min([e["memory"]["freeBytes"] for e in events] + [s["memory"]["freeBytes"] for s in updates]
                          + [after["freeBytes"], report["nvidiaSmiAfter"]["freeBytes"]])
            fraction = min(budget, sampled) / baseline["totalBytes"]
            _require(math.isfinite(fraction) and fraction == report["headroomFraction"] == attempt["headroomFraction"]
                     and min(budget, sampled) == report["conservativeHeadroomBytes"], "conservative-memory-formula-mismatch")
            _require(fraction < .1 and report["resourceReady"] is False and attempt["resourceReady"] is False
                     and report["status"] == attempt["status"] == "insufficient-headroom"
                     and report["exitCode"] == attempt["exitCode"] == 2
                     and not (folder / "selected-runtime.json").exists(), "numerical-outcome-changed")
            for key, actual in {"microSteps": len(events), "optimizerSteps": len(updates), "peakAllocatedBytes": after["peakAllocatedBytes"],
                                "peakReservedBytes": after["peakReservedBytes"], "driverFreeBeforeBytes": report["nvidiaSmiBefore"]["freeBytes"],
                                "driverFreeAfterBytes": report["nvidiaSmiAfter"]["freeBytes"], "cudaFreeAfterBytes": after["freeBytes"],
                                "tokensPerSecond": report["tokensPerSecond"], "elapsedSeconds": report["elapsedTrainingSeconds"]}.items():
                _require(attempt[key] == actual, "summary-measurement-mismatch:" + key)
            code_bindings(report["codeSha256"])
            _require({"tools/probe_situation_lora.py", "tools/situation_probe_inputs.py", "tools/situation_training_memory.py", "tools/situation_dependency_profile.py"} <= report["codeSha256"].keys(), "core-source-binding-missing")
            reports.append(report)

        first = reports[0]
        for report in reports:
            _require(report["codeSha256"] == first["codeSha256"] and report["inputs"] == first["inputs"]
                     and report["baseManifestSha256"] == first["baseManifestSha256"], "cross-probe-binding-mismatch")
        base_path = Path(expected_bindings.get("baseManifestPath", root / "bases" / ("Qwen3-1.7B-" + first["revision"]) / "base-manifest.json"))
        bound(base_path, first["baseManifestSha256"], "base-manifest")
        base = _read(base_path)
        _require(base["status"] == "complete" and base["modelId"] == first["modelId"] == "Qwen/Qwen3-1.7B"
                 and base["revision"] == first["revision"] == "70d244cc86ccca08cf5af4e1e306ecf908b1ad5e"
                 and verification["baseManifestSha256"] == first["baseManifestSha256"], "base-identity-mismatch")
        for item in base["files"]:
            p = _child(base_path.parent, item["path"])
            _require(p.stat().st_size == item["bytes"], "base-size-mismatch")
            bound(p, item["sha256"], "base-file")
        inp = first["inputs"]
        _require(inp["devTestRead"] is False and inp["representationVersion"] == state["representationVersion"], "input-purpose-mismatch")
        budget_folder = _child(root, "reports/" + first["config"]["budgetRun"])
        budget_path = budget_folder / "token-budget-report.json"
        bound(budget_path, inp["budgetReportSha256"], "input-budget")
        budget_report = _read(budget_path)
        _require(budget_report["inputBudgetReady"] is True and budget_report["fullTrainValidated"] is True
                 and budget_report["devTestContentRead"] is False and budget_report["policy"]["selectedBudget"] == 2560, "input-budget-invalid")
        code_bindings(budget_report["codeSha256"])
        bound(repo / "situation-implementation/situation-token-budget-v2.json", budget_report["policySha256"], "input-policy")
        bound(_child(repo, budget_report["policy"]["representationConfig"]), budget_report["policy"]["representationConfigSha256"], "input-prompt")
        for name, value in budget_report["representations"]["full-train"]["artifacts"].items():
            bound(_child(budget_folder, name), value, "input-artifact")
        source = next(d for d in budget_report["policy"]["datasets"] if d["role"] == "full-train")
        _require(source["trainSha256"] == inp["trainSha256"], "train-binding-mismatch")
        bound(_child(root, "datasets/" + source["name"] + "/train.jsonl"), source["trainSha256"], "train-source")
        selected_ids = [r["sampleId"] for r in inp["selectedRows"]]
        _require(len(selected_ids) == len(set(selected_ids)) == 16, "selected-input-scope-mismatch")
        _require(all([e["sampleId"] for e in r["microSteps"][16:]] == selected_ids for r in reports), "actual-train-input-selection-mismatch")
        records = {}
        with (budget_folder / "full-train-model-inputs.jsonl").open(encoding="utf-8") as stream:
            for line in stream:
                row = json.loads(line)
                if row["sampleId"] in selected_ids:
                    _require(row["sampleId"] not in records, "duplicate-selected-input")
                    records[row["sampleId"]] = row
        canonical = json.dumps([records[s] for s in selected_ids], ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)
        _require(hashlib.sha256(canonical.encode()).hexdigest() == inp["selectedInputSha256"] == verification["selectedInputSha256"], "selected-input-hash-mismatch")

        profile = state["dependencyProfile"]
        _require(profile == "requirements-situation-win-cu126-qlora", "wrong-current-profile", "dependency-conflict")
        lock = repo / (profile + ".lock.txt")
        bound(lock, expected_bindings["dependencyLockSha256"], "current-lock")
        _require(state["dependencyLockSha256"] == original["dependencyLockSha256"] == digest(lock), "lock-overlay-drift")
        sources_path = repo / (profile + ".sources.json")
        sources = _read(sources_path)
        packages = {re.sub(r"[-_.]+", "-", p["name"]).lower(): p["version"] for p in sources["packages"]}
        _require(len(packages) == len(sources["packages"]) == 53 and sources["lockSha256"] == digest(lock), "conditional-profile-invalid", "dependency-conflict")
        _require(_installed_packages(expected_bindings.get("python", repo / ".venv/situation/Scripts/python.exe")) == packages, "current-packages-drift", "dependency-conflict")
        for r in reports:
            _require(r["lockSha256"] == digest(lock) and r["installedPackages"] == packages
                     and r["dependencyProfile"]["installedPackages"] == packages
                     and r["dependencyProfile"]["sourcesSha256"] == digest(sources_path), "probe-profile-binding-mismatch", "dependency-conflict")
        rebuild_evidence = []
        for stem, run, count in (("requirements-situation-win-cu126", "dependencies-20260927-r3", 52),
                                 (profile, "qlora-dependencies-20260929-r1", 53)):
            source_path = repo / (stem + ".sources.json")
            source_data = _read(source_path)
            lock_path = repo / (stem + ".lock.txt")
            bound(lock_path, source_data["lockSha256"], "rebuild-lock")
            folder = _child(root, "reports/" + run)
            complete = _read(folder / "complete.json")
            _require(complete["lockSha256"] == digest(lock_path) and complete["packages"] == count
                     and complete["offlineRebuildPassed"] is True and complete["cpuSmokePassed"] is True, "rebuild-not-applicable", "dependency-evidence-mismatch")
            smoke = _read(folder / "rebuild-smoke.json")
            package_set = {re.sub(r"[-_.]+", "-", p["name"]).lower(): p["version"] for p in source_data["packages"]}
            lock_entries = {}
            for line in lock_path.read_text(encoding="utf-8").splitlines():
                line = line.strip()
                if not line or line.startswith(("#", "--")):
                    continue
                match = re.fullmatch(r"([A-Za-z0-9_.-]+)==([^ ]+) --hash=sha256:([0-9a-f]{64})", line)
                _require(match is not None, "non-exact-lock-entry", "dependency-evidence-mismatch")
                name = re.sub(r"[-_.]+", "-", match[1]).lower()
                _require(name not in lock_entries, "duplicate-lock-entry", "dependency-evidence-mismatch")
                lock_entries[name] = (match[2], match[3])
            source_entries = {re.sub(r"[-_.]+", "-", p["name"]).lower(): (p["version"], p["sha256"]) for p in source_data["packages"]}
            _require(lock_entries == source_entries, "lock-source-set-mismatch", "dependency-evidence-mismatch")
            install_path = folder / ("pip-rebuild-install.json" if count == 52 else "pip-rebuild.json")
            install = _read(install_path)
            installed_wheels = {}
            for item in install["install"]:
                name = re.sub(r"[-_.]+", "-", item["metadata"]["name"]).lower()
                _require(name not in installed_wheels, "duplicate-rebuild-install", "dependency-evidence-mismatch")
                installed_wheels[name] = (item["metadata"]["version"], item["download_info"]["archive_info"]["hashes"]["sha256"])
                _require(item["download_info"]["url"].startswith("file:"), "rebuild-installed-from-network", "dependency-evidence-mismatch")
            _require(installed_wheels == {k: v for k, v in source_entries.items() if k not in ("pip", "setuptools")},
                     "rebuild-wheel-source-mismatch", "dependency-evidence-mismatch")
            _require(len(package_set) == count and smoke["status"] == "passed" and smoke["installedVersions"] == package_set
                     and {"adapter-updated", "finite-loss", "completion-mask"} <= set(smoke["checks"]), "rebuild-smoke-invalid", "dependency-evidence-mismatch")
            for name in ("rebuild-create", "rebuild-install", "rebuild-check", "rebuild-smoke"):
                command = _read(folder / (name + ".command.json"))
                _require(command["exitCode"] == 0, "rebuild-command-failed", "dependency-evidence-mismatch")
                if name == "rebuild-install":
                    _require("--no-index" in command["command"] and "--require-hashes" in command["command"]
                             and Path(command["command"][command["command"].index("-r") + 1]).resolve() == lock_path.resolve(), "rebuild-not-offline-or-wrong-lock", "dependency-evidence-mismatch")
            rebuild_evidence.append(dict(profile=stem, packages=count, completeSha256=digest(folder / "complete.json"),
                                         smokeSha256=digest(folder / "rebuild-smoke.json"), installSha256=digest(install_path)))
        _require(sources["baselineLockSha256"] == digest(repo / "requirements-situation-win-cu126.lock.txt"), "conditional-baseline-lock-drift")
        cuda_path = Path(expected_bindings.get("cudaVerificationPath", root / "reports/memory-cuda-equivalence-20261001-r1/verification.json"))
        bound(cuda_path, original["reusedCudaVerificationSha256"], "cuda-equivalence")
        cuda = _read(cuda_path)
        _require(cuda["status"] == "passed" and cuda["dependencyProfile"]["installedPackages"] == packages, "cuda-verification-stale")
        code_bindings(cuda["codeSha256"])
        isolation_path = Path(expected_bindings.get("isolationBaselinePath", root / "reports/situation-stage5-preflight-20260927-r1/preflight.json"))
        isolation = _read(isolation_path)["winEnvironment"]
        for name, value in isolation["requirementsAndStartupHashes"].items():
            bound(_child(repo, name), value, "winrate-isolation:" + name)
        original_packages = _installed_packages(isolation["resolvedInThisShell"])
        _require(all(original_packages.get(name) == version for name, version in isolation["installedVersions"].items()),
                 "winrate-environment-drift", "environment-isolation-mismatch")
        output.update(status="passed", verified=True, resourceAccepted=True,
                      postProbeDependencyAcceptance="passed", reasonCodes=["insufficient-headroom"])
        output["details"] = dict(attempts=attempts, numericalGate="failed", minimumHeadroomFraction=.1,
                                 userAcceptance=acceptance, dependencyProfile=profile, dependencyFunctionalReady=True,
                                 offlineRebuildReuse="unchanged 52/53 package locks; current exact 53 package profile and source/base/input bindings verified",
                                 winrateIsolation="recorded dependency/startup file hashes and original interpreter package whitelist unchanged; live service interpreter was not verified in original baseline",
                                 longRunStability="not-tested", stage6Ready=False)
        output["evidence"] = dict(acceptanceSha256=digest(path), originalStatusSha256=digest(original_path),
                                  verificationSha256=digest(verification_path), dependencyLockSha256=digest(lock),
                                  sourcesSha256=digest(sources_path), baseManifestSha256=digest(base_path),
                                  selectedInputSha256=inp["selectedInputSha256"], codeSha256=first["codeSha256"],
                                  rebuilds=rebuild_evidence, cudaVerificationSha256=digest(cuda_path),
                                  isolationBaselineSha256=digest(isolation_path))
    except EvidenceError as exc:
        output["reasonCodes"] = [exc.reason]
        output["details"] = {"failure": str(exc)}
    except (OSError, ValueError, KeyError, TypeError, AttributeError, IndexError, OverflowError,
            StopIteration, subprocess.SubprocessError, ZeroDivisionError) as exc:
        output["reasonCodes"] = ["resource-evidence-mismatch"]
        output["details"] = {"failure": type(exc).__name__, "message": str(exc)}
    return output
