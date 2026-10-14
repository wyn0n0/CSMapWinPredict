"""CPU-only acceptance tests using isolated synthetic files, never real data."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from tools.audit_situation_stage5_acceptance import audit_acceptance


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding="utf-8")
    return digest(path)


class AcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / "repo"
        self.root = Path(self.temp.name) / "storage"
        self.repo.mkdir(); self.root.mkdir()
        self.codes = {}
        for name in ("tools/probe_situation_lora.py", "tools/situation_probe_inputs.py", "tools/situation_training_memory.py", "tools/situation_dependency_profile.py"):
            self.codes[name] = write(self.repo / name, {"synthetic": name})
        self.packages = {name: "1.0" for name in ["pip", "setuptools"] + ["p" + str(i) for i in range(51)]}
        self.profile = "requirements-situation-win-cu126-qlora"
        for stem, count, run in (("requirements-situation-win-cu126", 52, "dependencies-20260927-r3"), (self.profile, 53, "qlora-dependencies-20260929-r1")):
            lock_path = self.repo / (stem + ".lock.txt")
            lock_path.write_text("\n".join(n + "==" + v + " --hash=sha256:" + "f"*64 for n, v in list(self.packages.items())[:count]),encoding="utf-8")
            lock_hash = digest(lock_path)
            src = {"lockSha256": lock_hash, "packages": [{"name": n, "version": v, "sha256": "f"*64} for n, v in list(self.packages.items())[:count]]}
            if count == 53:
                src["baselineLockSha256"] = digest(self.repo / "requirements-situation-win-cu126.lock.txt")
            write(self.repo / (stem + ".sources.json"), src)
            folder = self.root / "reports" / run
            write(folder / ("pip-rebuild-install.json" if count == 52 else "pip-rebuild.json"), {"install": [dict(metadata=dict(name=n,version=v),download_info=dict(url="file:synthetic.whl",archive_info=dict(hashes=dict(sha256="f"*64)))) for n,v in list(self.packages.items())[:count] if n not in ("pip","setuptools")]})
            write(folder / "complete.json", dict(lockSha256=lock_hash, packages=count, offlineRebuildPassed=True, cpuSmokePassed=True))
            write(folder / "rebuild-smoke.json", dict(status="passed", installedVersions=dict(list(self.packages.items())[:count]), checks=["adapter-updated", "finite-loss", "completion-mask"]))
            for command in ("rebuild-create", "rebuild-install", "rebuild-check", "rebuild-smoke"):
                write(folder / (command + ".command.json"), dict(exitCode=0, command=["python", "--no-index", "--require-hashes", "-r", str(self.repo / (stem + ".lock.txt"))]))
        self.lock = digest(self.repo / (self.profile + ".lock.txt"))
        self.base = self.root / "bases/Qwen3-1.7B-70d244cc86ccca08cf5af4e1e306ecf908b1ad5e/base-manifest.json"
        file_hash = write(self.base.parent / "model.bin", {"randomSynthetic": True})
        self.basehash = write(self.base, dict(status="complete", modelId="Qwen/Qwen3-1.7B", revision="70d244cc86ccca08cf5af4e1e306ecf908b1ad5e", files=[dict(path="model.bin", bytes=(self.base.parent / "model.bin").stat().st_size, sha256=file_hash)]))
        self.budget = self.root / "reports/budget"
        self.budget.mkdir(parents=True)
        self.rows = [{"sampleId": "synthetic-" + str(i)} for i in range(16)]
        records = self.budget / "full-train-model-inputs.jsonl"
        records.write_text("\n".join(json.dumps(row) for row in self.rows), encoding="utf-8")
        self.selected_hash = hashlib.sha256(json.dumps(self.rows, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
        train_hash = write(self.root / "datasets/synthetic-train/train.jsonl", {"synthetic": True})
        policy_hash = write(self.repo / "situation-implementation/situation-token-budget-v2.json", {"synthetic": True})
        prompt_hash = write(self.repo / "prompt.json", {"synthetic": True})
        self.budgethash = write(self.budget / "token-budget-report.json", dict(inputBudgetReady=True, fullTrainValidated=True, devTestContentRead=False, policySha256=policy_hash, codeSha256=self.codes,
            policy=dict(selectedBudget=2560, representationConfig="prompt.json", representationConfigSha256=prompt_hash, datasets=[dict(role="full-train", name="synthetic-train", trainSha256=train_hash)]),
            representations={"full-train": {"artifacts": {"full-train-model-inputs.jsonl": digest(records)}}}))
        cuda = self.root / "reports/memory-cuda-equivalence-20261001-r1/verification.json"
        self.cuda_hash = write(cuda, dict(status="passed", codeSha256=self.codes, dependencyProfile=dict(installedPackages=self.packages)))
        write(self.root / "reports/situation-stage5-preflight-20260927-r1/preflight.json", {"winEnvironment": dict(requirementsAndStartupHashes=self.codes, resolvedInThisShell="original-python", installedVersions={"p0": "1.0"})})
        self.cfg = dict(maxSequenceLength=2560, rank=16, gradientAccumulationSteps=16, optimizerSteps=2, microBatchSize=1, minimumHeadroomFraction=.1, saveAdapter=False, sdpaBackend="cudnn", lossImplementation="assistant-positions", budgetRun="budget")
        self.cfghash = write(self.repo / "situation-implementation/probe.json", self.cfg)
        self.reports = []
        self.run_names = ["synthetic-bf16-a", "synthetic-bf16-b", "synthetic-nf4-r16", "synthetic-nf4-r8"]
        for precision, rank in (("bf16", 16), ("bf16", 16), ("nf4", 16), ("nf4", 8)):
            cfg = dict(self.cfg, rank=rank)
            memory = dict(freeBytes=50, totalBytes=1000, reservedBytes=100, peakReservedBytes=200, peakAllocatedBytes=150)
            events = [dict(microStep=i+1, phase="synthetic" if i < 16 else "real-train", tokens=2560 if i<16 else 1500, supervisedTokens=512, projectedTokens=512, loss=1., memory=memory,
                           sampleId="synthetic-" + str(i-16)) for i in range(32)]
            self.reports.append(dict(config=cfg, configPath="situation-implementation/probe.json", configSha256=self.cfghash, codeSha256=self.codes,
                precision=precision, rank=rank, trainableParameters=17432576*rank//16, operationsPassed=True, phase="complete", adapterSaved=False, trainingReady=False,
                status="insufficient-headroom", resourceReady=False, exitCode=2, microSteps=events, optimizerSteps=[dict(step=i+1, gradientNorm=1., changedAdapterTensors=1, stateDtypes={"torch.float32": 1}, memory=memory) for i in range(2)],
                attentionVerification=[dict(microStep=i, backend="cudnn", includesCheckpointRecomputation=True, operators={"aten::_scaled_dot_product_cudnn_attention":56, "aten::_scaled_dot_product_cudnn_attention_backward":28}) for i in (1,17)],
                quantization=dict(type="nf4", doubleQuant=True, linear4bitModules=196, computeDtype="torch.bfloat16", bitsandbytes="0.48.1"), qlora="passed",
                memoryBefore=dict(freeBytes=800,totalBytes=1000,reservedBytes=100), memoryAfter=memory, nvidiaSmiBefore=dict(freeBytes=800), nvidiaSmiAfter=dict(freeBytes=50), conservativeHeadroomBytes=50, headroomFraction=.05,
                tokensPerSecond=1., elapsedTrainingSeconds=1., modelId="Qwen/Qwen3-1.7B", revision="70d244cc86ccca08cf5af4e1e306ecf908b1ad5e", baseManifestSha256=self.basehash,
                lockSha256=self.lock, installedPackages=self.packages, dependencyProfile=dict(installedPackages=self.packages, sourcesSha256=digest(self.repo / (self.profile+".sources.json"))),
                inputs=dict(devTestRead=False, representationVersion="facts-only-utf8-v1", budgetReportSha256=self.budgethash, trainSha256=train_hash, selectedRows=self.rows, selectedInputSha256=self.selected_hash)))
        self.status_path = self.repo / "situation-implementation/situation-resource-status-v4.json"
        self.bindings = dict(repository=str(self.repo), storageRoot=str(self.root), acceptancePath=str(self.status_path), dependencyLockSha256=self.lock, maxSequenceLength=2560, representationVersion="facts-only-utf8-v1")
        self.rebind()
        self.probe = patch("tools.audit_situation_stage5_acceptance._installed_packages", return_value=self.packages)
        self.probe.start(); self.addCleanup(self.probe.stop)

    def rebind(self):
        attempts = []
        for run, r in zip(self.run_names, self.reports):
            folder = self.root / "reports" / run
            report_hash = write(folder / "memory-probe.json", r)
            events_path = folder / "events.jsonl"
            events_path.write_text("\n".join(json.dumps(e) for e in r["microSteps"]), encoding="utf-8")
            attempts.append(dict(run=run,reportSha256=report_hash,eventsSha256=digest(events_path), precision=r["precision"],rank=r["rank"],operationsPassed=True,resourceReady=False,status="insufficient-headroom",exitCode=2,microSteps=32,optimizerSteps=2,headroomFraction=.05,
                peakAllocatedBytes=150,peakReservedBytes=200,driverFreeBeforeBytes=800,driverFreeAfterBytes=50,cudaFreeAfterBytes=50,tokensPerSecond=1.,elapsedSeconds=1.))
        verifier_path = self.root / "reports/verification/verify.py"
        verifier_hash = write(verifier_path, {"synthetic": True})
        vhash = write(verifier_path.parent / "verification.json",dict(status="passed",attempts=attempts,cases=128,optimizerSteps=8,resourceProbeReady=False,selectedRuntime=None,verifierSha256=verifier_hash,baseManifestSha256=self.basehash,selectedInputSha256=self.selected_hash))
        original = dict(schemaVersion="situation-resource-status-v3",verifiedDate="2026-10-02",attempts=attempts,summaryRun="verification",verificationSha256=vhash,dependencyLockSha256=self.lock,reusedCudaVerificationSha256=self.cuda_hash)
        original_path="situation-implementation/situation-resource-status-v3.json"
        oldhash=write(self.repo / original_path, original)
        self.overlay = dict(original,schemaVersion="situation-resource-status-v4",resourceProbeReady=False,trainingReady=False,selectedRuntime=None,maxSequenceLength=2560,representationVersion="facts-only-utf8-v1",dependencyProfile=self.profile,resourceAccepted=True,
            previousStatus=dict(path=original_path,sha256=oldhash),userAcceptance=dict(status="accepted",authority="direct-user-instruction",date="2026-10-03",sourceStatusPath=original_path,sourceStatusSha256=oldhash,attemptReportSha256=[a["reportSha256"] for a in attempts]))
        self.overlay["attempts"]=[dict(a,acceptance="accepted",acceptanceAuthority="user",acceptedDate="2026-10-03") for a in attempts]
        write(self.status_path,self.overlay)

    def audit(self):
        return audit_acceptance(self.status_path,self.bindings)

    def assert_rejected(self):
        result=self.audit()
        self.assertEqual(result["status"],"failed",result)
        self.assertFalse(result["verified"])
        self.assertFalse(result["resourceAccepted"])
        self.assertEqual(result["postProbeDependencyAcceptance"],"deferred")

    def test_acceptance_preserves_numerical_failure(self):
        result=self.audit()
        self.assertEqual(result["status"],"passed",result)
        self.assertTrue(result["verified"]); self.assertTrue(result["resourceAccepted"])
        self.assertFalse(result["resourceProbeReady"])
        self.assertEqual(result["postProbeDependencyAcceptance"],"passed")
        self.assertEqual(result["reasonCodes"],["insufficient-headroom"])

    def test_missing_report(self):
        (self.root / "reports" / self.run_names[0] / "memory-probe.json").unlink()
        self.assert_rejected()

    def test_missing_acceptance(self):
        del self.overlay["userAcceptance"]
        write(self.status_path,self.overlay); self.assert_rejected()

    def test_old_acceptance(self):
        self.overlay["userAcceptance"]["date"]="2026-10-01"
        write(self.status_path,self.overlay); self.assert_rejected()

    def test_original_hash_mismatch(self):
        self.overlay["previousStatus"]["sha256"]="0"*64
        write(self.status_path,self.overlay); self.assert_rejected()

    def test_accepted_subset_rejected(self):
        self.overlay["userAcceptance"]["attemptReportSha256"].pop()
        write(self.status_path,self.overlay); self.assert_rejected()

    def test_numerical_status_cannot_be_promoted(self):
        self.overlay["resourceProbeReady"]=True
        write(self.status_path,self.overlay); self.assert_rejected()

    def test_mutable_measurement_rejected(self):
        self.overlay["attempts"][0]["headroomFraction"]=.15
        write(self.status_path,self.overlay); self.assert_rejected()

    def test_actual_cudnn_evidence_required(self):
        self.reports[0]["attentionVerification"]=[]
        self.rebind(); self.assert_rejected()

    def test_actual_nf4_required(self):
        self.reports[2]["quantization"]["doubleQuant"]=False
        self.rebind(); self.assert_rejected()

    def test_actual_update_required(self):
        self.reports[0]["optimizerSteps"][0]["changedAdapterTensors"]=0
        self.rebind(); self.assert_rejected()

    def test_formula_recomputed(self):
        self.reports[0]["memoryBefore"]["freeBytes"]=100
        self.rebind(); self.assert_rejected()

    def test_events_hash_mismatch(self):
        (self.root / "reports" / self.run_names[0] / "events.jsonl").write_text("{}",encoding="utf-8")
        self.assert_rejected()

    def test_current_package_set_mismatch(self):
        with patch("tools.audit_situation_stage5_acceptance._installed_packages",return_value={"p0":"1.0"}):
            self.assert_rejected()

    def test_rebuild_does_not_cover_current_lock(self):
        folder=self.root / "reports/qlora-dependencies-20260929-r1"
        complete=json.loads((folder / "complete.json").read_text())
        complete["packages"]=52; write(folder / "complete.json",complete)
        self.assert_rejected()

    def test_rebuild_must_be_offline(self):
        file=self.root / "reports/qlora-dependencies-20260929-r1/rebuild-install.command.json"
        command=json.loads(file.read_text()); command["command"].remove("--no-index")
        write(file,command); self.assert_rejected()

    def test_rebuild_wheel_hash_mismatch(self):
        file=self.root / "reports/qlora-dependencies-20260929-r1/pip-rebuild.json"
        report=json.loads(file.read_text())
        report["install"][0]["download_info"]["archive_info"]["hashes"]["sha256"]="0"*64
        write(file,report); self.assert_rejected()

    def test_rebuild_package_version_mismatch(self):
        file=self.root / "reports/qlora-dependencies-20260929-r1/rebuild-smoke.json"
        report=json.loads(file.read_text()); report["installedVersions"]["p0"]="2.0"
        write(file,report); self.assert_rejected()

    def test_old_selected_runtime_rejected(self):
        write(self.root / "reports" / self.run_names[0] / "selected-runtime.json", {"resourceReady":True})
        self.assert_rejected()

    def test_actual_input_selection_mismatch(self):
        self.reports[0]["microSteps"][16]["sampleId"]="unselected"
        self.rebind(); self.assert_rejected()

    def test_isolation_package_drift(self):
        def packages(python):
            return {} if str(python)=="original-python" else self.packages
        with patch("tools.audit_situation_stage5_acceptance._installed_packages",side_effect=packages):
            self.assert_rejected()

    def test_duplicate_and_nonfinite_report_fields_rejected(self):
        for loss in (float("nan"), float("inf")):
            with self.subTest(loss=loss):
                self.reports[0]["microSteps"][0]["loss"]=loss
                self.rebind(); self.assert_rejected()

    def test_core_source_drift(self):
        write(self.repo / "tools/situation_training_memory.py", {"changed":True})
        self.assert_rejected()

    def test_base_bytes_drift(self):
        write(self.base.parent / "model.bin", {"changed":True})
        self.assert_rejected()

    def test_test_purpose_rejected(self):
        self.reports[0]["inputs"]["devTestRead"]=True
        self.rebind(); self.assert_rejected()

    def test_wrong_representation_binding(self):
        self.bindings["representationVersion"]="old-compact"
        self.assert_rejected()

    def test_wrong_acceptance_path(self):
        self.bindings["acceptancePath"]=str(self.repo / "old.json")
        self.assert_rejected()

    def test_malformed_json_and_shape(self):
        for content in ('{"status":', '{"x":1,"x":2}', '[]', '{"x":NaN}'):
            with self.subTest(content=content):
                self.status_path.write_text(content,encoding="utf-8")
                self.assert_rejected()


if __name__ == "__main__":
    unittest.main()
