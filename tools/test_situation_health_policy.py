"""CPU-only synthetic policy and privacy tests; no real dataset content."""

import copy
import json
import unittest

from tools.situation_health_policy import CHECK_IDS, READINESS_FIELDS, evaluate_gates, redact_public


def fixture():
    observations = {"schemaVersion": "situation-health-observations-v1", "checks": [
        {"checkId": name, "status": "passed", "reasonCodes": [], "details": {}, "evidence": {}}
        for name in CHECK_IDS
    ]}
    observations["checks"][0]["details"] = {"version": "3.11.16"}
    evidence = {
        "inputBudget": {"status": "passed", "verified": True, "inputBudgetReady": True,
                        "fullTrainValidated": True, "maxSequenceLength": 2560,
                        "representationVersion": "facts-only-utf8-v1", "scope": "train",
                        "reasonCodes": [], "evidence": {}},
        "resource": {"status": "passed", "verified": True, "resourceProbeReady": True,
                     "resourceAccepted": True, "postProbeDependencyAcceptance": "passed",
                     "reasonCodes": [], "evidence": {}},
        "stage4": {"status": "passed", "closeoutComplete": True, "reasonCodes": []},
        "corpus": {"status": "passed", "paired": True, "purpose": "training",
                   "inputRuleVersion": "situation-analysis-rules-v8-close-exposure-1",
                   "labelRuleVersion": "situation-analysis-rules-v8-close-exposure-1",
                   "reasonCodes": []},
        "preparation": {"status": "passed", "verified": True, "preparationReady": True,
                        "trainingExecutionReady": False, "selectedRuntime": "nf4-rank16",
                        "reasonCodes": []},
    }
    requirements = {"pythonVersion": "3.11", "maxSequenceLength": 2560,
                    "representationVersion": "facts-only-utf8-v1", "purpose": "training",
                    "allowUserResourceAcceptance": True, "minimumHeadroomFraction": 0.10}
    return observations, evidence, requirements


class GateTests(unittest.TestCase):
    def test_preparation_never_grants_training_execution(self):
        result = evaluate_gates(*fixture())
        self.assertTrue(result["preparationReady"])
        self.assertFalse(result["trainingExecutionReady"])

    def test_legacy_upstream_flags_cannot_replace_preparation_evidence(self):
        observations, evidence, requirements = fixture()
        del evidence["preparation"]
        result = evaluate_gates(observations, evidence, requirements)
        self.assertFalse(result["preparationReady"])
        self.assertFalse(result["stage6Ready"])
        self.assertEqual(result["exitCode"], 2)

    def test_all_required_gates_and_shape(self):
        result = evaluate_gates(*fixture())
        for field in READINESS_FIELDS:
            self.assertIs(result[field], True, field)
        self.assertEqual(result["exitCode"], 0)
        self.assertEqual(result["reasonCodes"], [])
        self.assertEqual([check["checkId"] for check in result["checks"]], list(CHECK_IDS))

    def test_missing_all_evidence_is_known_deferred(self):
        observations, _, requirements = fixture()
        result = evaluate_gates(observations, {}, requirements)
        self.assertIs(result["environmentReady"], True)
        self.assertIs(result["stage6Ready"], False)
        self.assertEqual(result["exitCode"], 2)

    def test_missing_observation_is_not_ready(self):
        for name in CHECK_IDS:
            with self.subTest(name=name):
                observations, evidence, requirements = fixture()
                observations["checks"] = [check for check in observations["checks"] if check["checkId"] != name]
                result = evaluate_gates(observations, evidence, requirements)
                self.assertFalse(result["baseReady"] if name == "base" else result["environmentReady"])
                self.assertEqual(result["exitCode"], 2)

    def test_all_nonpassing_statuses_are_fail_closed(self):
        for name in CHECK_IDS:
            for status in ("failed", "not-tested", "not-required"):
                with self.subTest(name=name, status=status):
                    observations, evidence, requirements = fixture()
                    next(check for check in observations["checks"] if check["checkId"] == name)["status"] = status
                    result = evaluate_gates(observations, evidence, requirements)
                    self.assertFalse(result["stage6Ready"])
                    self.assertEqual(result["exitCode"], 2)

    def test_known_fault_codes_preserved_sorted_unique(self):
        observations, evidence, requirements = fixture()
        for check, code in zip(observations["checks"], ("python-version-mismatch", "dependency-conflict", "cuda-unavailable", "base-hash-mismatch", "cache-not-writable", "disk-insufficient", "isolation-mismatch")):
            check.update(status="failed", reasonCodes=[code, code])
        result = evaluate_gates(observations, evidence, requirements)
        self.assertFalse(result["environmentReady"])
        self.assertFalse(result["baseReady"])
        self.assertEqual(result["reasonCodes"], sorted(set(result["reasonCodes"])))
        for code in ("base-hash-mismatch", "cache-not-writable", "disk-insufficient"):
            self.assertIn(code, result["reasonCodes"])

    def test_python_minor_version_not_prefix_guess(self):
        observations, evidence, requirements = fixture()
        observations["checks"][0]["details"]["version"] = "3.110.2"
        result = evaluate_gates(observations, evidence, requirements)
        self.assertFalse(result["environmentReady"])
        self.assertIn("python-version-mismatch", result["reasonCodes"])

    def test_current_numeric_failure_rejects_historical_selection(self):
        observations, evidence, requirements = fixture()
        evidence["resource"].update(resourceProbeReady=False, resourceAccepted=False,
                                    postProbeDependencyAcceptance="deferred", selectedRuntime="old-accepted-nf4",
                                    reasonCodes=["insufficient-headroom"])
        result = evaluate_gates(observations, evidence, requirements)
        self.assertTrue(result["environmentReady"])
        self.assertFalse(result["resourceProbeReady"])
        self.assertFalse(result["resourceAccepted"])
        self.assertFalse(result["stage6Ready"])
        self.assertEqual(result["exitCode"], 2)

    def test_user_acceptance_preserves_numerical_false(self):
        observations, evidence, requirements = fixture()
        evidence["resource"].update(resourceProbeReady=False, reasonCodes=["insufficient-headroom"])
        result = evaluate_gates(observations, evidence, requirements)
        self.assertFalse(result["resourceProbeReady"])
        self.assertTrue(result["resourceAccepted"])
        self.assertTrue(result["stage6Ready"])
        self.assertEqual(result["exitCode"], 0)
        self.assertIn("insufficient-headroom", result["reasonCodes"])
        requirements["allowUserResourceAcceptance"] = False
        self.assertFalse(evaluate_gates(observations, evidence, requirements)["stage6Ready"])

    def test_acceptance_cannot_bypass_upstream_pending_or_budget(self):
        for blocker in ("stage4", "corpus", "inputBudget"):
            with self.subTest(blocker=blocker):
                observations, evidence, requirements = fixture()
                evidence["resource"]["resourceProbeReady"] = False
                evidence[blocker]["status"] = "not-tested"
                result = evaluate_gates(observations, evidence, requirements)
                self.assertTrue(result["resourceAccepted"])
                self.assertFalse(result["stage6Ready"])
                self.assertEqual(result["exitCode"], 2)

    def test_unverified_stale_and_hash_mismatch_refuse_evidence(self):
        mutations = ({"verified": False}, {"stale": True}, {"current": False},
                     {"hashMismatch": True}, {"hashesVerified": False}, {"bindingsVerified": False},
                     {"evidence": {"hashMismatch": True}})
        for name in ("inputBudget", "resource"):
            for mutation in mutations:
                with self.subTest(name=name, mutation=mutation):
                    observations, evidence, requirements = fixture()
                    evidence[name].update(mutation)
                    result = evaluate_gates(observations, evidence, requirements)
                    self.assertFalse(result["inputBudgetReady"] if name == "inputBudget" else result["resourceAccepted"])
                    self.assertFalse(result["stage6Ready"])

    def test_verified_flag_missing_is_not_implicit(self):
        observations, evidence, requirements = fixture()
        del evidence["resource"]["verified"]
        self.assertFalse(evaluate_gates(observations, evidence, requirements)["resourceAccepted"])

    def test_observation_hash_contradiction(self):
        observations, evidence, requirements = fixture()
        observations["checks"][3]["evidence"] = {"hashesVerified": False}
        result = evaluate_gates(observations, evidence, requirements)
        self.assertFalse(result["baseReady"])

    def test_malformed_observation_metadata_is_not_ready(self):
        for field, value in (("status", []), ("reasonCodes", None), ("details", []),
                             ("evidence", {"verified": "true"})):
            with self.subTest(field=field):
                observations, evidence, requirements = fixture()
                observations["checks"][0][field] = value
                result = evaluate_gates(observations, evidence, requirements)
                self.assertEqual(result["exitCode"], 1)
                self.assertFalse(result["environmentReady"])

    def test_unverified_nested_budget_evidence_refused(self):
        observations, evidence, requirements = fixture()
        evidence["inputBudget"]["evidence"] = {"verified": False}
        self.assertFalse(evaluate_gates(observations, evidence, requirements)["inputBudgetReady"])

    def test_oversize_count_and_measured_length(self):
        for key, value in (("maxSequenceLength", 4096), ("overBudgetCount", 1),
                           ("maxObservedSequenceLength", 2561), ("maxTotalTokens", 2561)):
            with self.subTest(key=key):
                observations, evidence, requirements = fixture()
                evidence["inputBudget"][key] = value
                result = evaluate_gates(observations, evidence, requirements)
                self.assertFalse(result["inputBudgetReady"])
                self.assertFalse(result["stage6Ready"])

    def test_valid_longer_train_scope(self):
        observations, evidence, requirements = fixture()
        evidence["inputBudget"]["scope"] = "existing v1 base train only"
        self.assertTrue(evaluate_gates(observations, evidence, requirements)["inputBudgetReady"])

    def test_test_used_for_training_or_tuning(self):
        for source, field in (("corpus", "purpose"), ("corpus", "usedTestForTraining"),
                              ("corpus", "usedTestForTuning"), ("inputBudget", "usedTestForTuning")):
            with self.subTest(source=source, field=field):
                observations, evidence, requirements = fixture()
                evidence[source][field] = "test" if field == "purpose" else True
                self.assertFalse(evaluate_gates(observations, evidence, requirements)["stage6Ready"])
        observations, evidence, requirements = fixture()
        evidence["inputBudget"]["scope"] = "train/dev/test"
        self.assertFalse(evaluate_gates(observations, evidence, requirements)["inputBudgetReady"])

    def test_v1_input_cannot_pair_with_v8_label(self):
        observations, evidence, requirements = fixture()
        evidence["corpus"]["inputRuleVersion"] = "situation-analysis-rules-v1"
        result = evaluate_gates(observations, evidence, requirements)
        self.assertFalse(result["stage6Ready"])
        self.assertIn("rule-version-mismatch", result["reasonCodes"])

    def test_no_post_probe_dependency_acceptance(self):
        observations, evidence, requirements = fixture()
        del evidence["resource"]["postProbeDependencyAcceptance"]
        result = evaluate_gates(observations, evidence, requirements)
        self.assertFalse(result["stage6Ready"])
        self.assertIn("post-probe-dependency-acceptance-pending", result["reasonCodes"])

    def test_malformed_inputs_diagnostic_exit(self):
        for index in range(3):
            for bad in (None, [], "passed"):
                with self.subTest(index=index, bad=bad):
                    inputs = list(fixture())
                    inputs[index] = bad
                    result = evaluate_gates(*inputs)
                    self.assertFalse(result["stage6Ready"])
                    self.assertEqual(result["exitCode"], 1)

    def test_malformed_fields_never_raise(self):
        mutations = (
            ("observations", "checks", "passed"),
            ("resource", "status", []), ("resource", "reasonCodes", None),
            ("resource", "resourceAccepted", "true"), ("resource", "evidence", []),
            ("inputBudget", "inputBudgetReady", 1), ("inputBudget", "fullTrainValidated", "true"),
            ("inputBudget", "maxObservedSequenceLength", True),
            ("stage4", "closeoutComplete", "true"), ("corpus", "paired", 1),
        )
        for section, key, value in mutations:
            with self.subTest(section=section, key=key):
                observations, evidence, requirements = fixture()
                (observations if section == "observations" else evidence[section])[key] = value
                result = evaluate_gates(observations, evidence, requirements)
                self.assertEqual(result["exitCode"], 1)
                self.assertFalse(result["stage6Ready"])

    def test_duplicate_checks_and_contradiction(self):
        observations, evidence, requirements = fixture()
        observations["checks"].append(copy.deepcopy(observations["checks"][0]))
        self.assertEqual(evaluate_gates(observations, evidence, requirements)["exitCode"], 1)
        observations, evidence, requirements = fixture()
        evidence["resource"]["reasonCodes"] = ["oom"]
        result = evaluate_gates(observations, evidence, requirements)
        self.assertEqual(result["exitCode"], 1)
        self.assertFalse(result["resourceProbeReady"])

    def test_inputs_and_result_are_independent(self):
        inputs = fixture()
        before = copy.deepcopy(inputs)
        result = evaluate_gates(*inputs)
        self.assertEqual(inputs, before)
        result["evidence"]["corpus"]["purpose"] = "test"
        result["checks"][0]["details"]["version"] = "4.0"
        self.assertEqual(inputs, before)


class RedactionTests(unittest.TestCase):
    def setUp(self):
        self.context = {"repository": r"E:\Map", "storageRoot": r"D:\Private Training",
                        "python": r"E:\Map\.venv\situation\Scripts\python.exe", "username": "alice"}

    def test_known_paths_become_logical_and_survive_repeat(self):
        report = {"storage": r"D:\Private Training\bases\qwen", "repo": r"e:/map/tools/tool.py",
                  "python": self.context["python"], "logical": "$SITUATION_STORAGE_ROOT/bases/qwen/config.json"}
        public = redact_public(report, self.context)
        self.assertEqual(public["storage"], r"$SITUATION_STORAGE_ROOT\bases\qwen")
        self.assertEqual(public["repo"], "$REPOSITORY/tools/tool.py")
        self.assertEqual(public["python"], "$SITUATION_PYTHON")
        self.assertEqual(public["logical"], report["logical"])
        self.assertEqual(redact_public(public, self.context), public)

    def test_unknown_windows_posix_unc_paths_keys_errors_traceback(self):
        report = {
            r"C:\Users\bob\private.json": [
                {"error": 'File "C:\\Users\\charlie\\private dir\\model.py", line 1\nTraceback: /home/dana/private/file.py'},
                r"\\private-host\share\private\file.json", "/mnt/private/models/qwen/model.bin",
            ], "errors": "missing Z:/unknown/private.txt", "reasonCodes": ["base-missing"]}
        public = redact_public(report, self.context)
        encoded = json.dumps(public)
        for private in ("bob", "charlie", "dana", "private-host", "Z:/", "C:", "/mnt/private", "/home/"):
            self.assertNotIn(private, encoded)
        self.assertEqual(public["reasonCodes"], ["base-missing"])

    def test_username_secrets_nested_fields_and_free_text(self):
        report = {"username": "unknown-user", "nested": [
            {"apiKey": "private-api-value", "Authorization": "Bearer private-bearer-value"},
            {"credentials": {"arbitrary": "private-credential"}, "message": "alice: token=private-token password: private-pass login=bob"},
        ], "message": "Bearer free-bearer-secret sk-abcdefghijk ghp_abcdefghijk",
            "url": "https://bob:private-pass@example.com/endpoint?token=private-query"}
        encoded = json.dumps(redact_public(report, self.context))
        for private in ("unknown-user", "private-api-value", "private-bearer-value", "private-credential", "alice", "private-token", "private-pass", "bob", "free-bearer-secret", "sk-abcdefghijk", "ghp_abcdefghijk", "private-query"):
            self.assertNotIn(private, encoded)

    def test_sanitized_key_collisions_preserve_values(self):
        public = redact_public({r"C:\private\a": 1, r"C:\private\b": 2}, {})
        self.assertEqual(sorted(public.values()), [1, 2])
        self.assertEqual(len(public), 2)

    def test_safe_urls_and_logical_paths_preserved(self):
        report = {"url": "https://example.com/path/info", "logical": "$SITUATION_STORAGE_ROOT/runs/test/report.json"}
        self.assertEqual(redact_public(report, {}), report)

    def test_unicode_paths_file_uris_and_credential_variants(self):
        report = {"unix": "/用户/私有目录/file.json", "uri": "file:///home/private-user/file.json",
                  "api_token_value": "private-token", "hfToken": "private-hf",
                  "traceback": "Authorization: Basic dXNlcjpwYXNz", "tokenizerVersion": "safe"}
        public = redact_public(report, {})
        encoded = json.dumps(public, ensure_ascii=False)
        for private in ("用户", "私有目录", "private-user", "private-token", "private-hf", "dXNlcjpwYXNz"):
            self.assertNotIn(private, encoded)
        self.assertEqual(public["tokenizerVersion"], "safe")

    def test_private_input_not_mutated(self):
        report = {"nested": [{"token": "private", "path": r"C:\Users\alice\x"}]}
        before = copy.deepcopy(report)
        redact_public(report, self.context)
        self.assertEqual(report, before)

    def test_unsupported_objects_cannot_leak_repr(self):
        class Secret:
            def __repr__(self):
                raise AssertionError("must not expose repr")
        self.assertEqual(redact_public({"unknown": Secret()}, {})["unknown"], "<redacted-unsupported-value>")


if __name__ == "__main__":
    unittest.main()
