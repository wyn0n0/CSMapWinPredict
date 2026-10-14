"""Filesystem and process-boundary checks for stage-six integration."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from tools.validate_situation_stage6_preparation import EvidenceError, EvidenceStore, collect_preparation_evidence
import measure_situation_stage6_preparation as orchestration
from prepare_situation_corpus import canonical_property_text
from test_situation_stage6_readiness import fixture as readiness_fixture


class EvidenceStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo, self.storage = self.root / "repo", self.root / "storage"
        self.repo.mkdir()
        self.storage.mkdir()
        self.store = EvidenceStore(self.repo, self.storage)

    def binding(self, name, content=b"frozen"):
        (self.storage / name).write_bytes(content)
        return {"logicalPath": "storage:" + name,
                "sha256": hashlib.sha256(content).hexdigest()}

    def test_actual_bytes_must_match_binding(self):
        binding = self.binding("train.jsonl")
        self.assertTrue(self.store.verify(binding)["verified"])
        (self.storage / "train.jsonl").write_bytes(b"changed")
        with self.assertRaisesRegex(EvidenceError, "binding-hash-mismatch"):
            self.store.verify(binding)

    def test_absolute_and_parent_paths_cannot_escape_roots(self):
        for name in ("storage:../outside", "storage:/absolute", "storage:C:/outside", "other:x"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.store.path(name)

    def test_test_hash_runs_in_separate_process_even_without_split_flag(self):
        binding = self.binding("test.jsonl", b"private test payload")
        with patch("tools.validate_situation_stage6_preparation.digest",
                   side_effect=AssertionError("parent must not hash test")):
            verified = self.store.verify(binding)
        self.assertEqual(verified["actualSha256"], binding["sha256"])

    def test_test_hash_drift_is_nonzero_without_payload_disclosure(self):
        binding = self.binding("test.jsonl", b"private payload")
        (self.storage / "test.jsonl").write_bytes(b"modified private payload")
        with self.assertRaisesRegex(EvidenceError, "isolated-test-binding-failed") as result:
            self.store.verify(binding)
        self.assertNotIn("payload", str(result.exception))

    def test_missing_report_bindings_fail_closed(self):
        binding = self.binding("report.json", json.dumps({"status": "passed"}).encode())
        with self.assertRaisesRegex(EvidenceError, "report-bindings-missing"):
            self.store.report(binding)

    def test_test_report_cannot_be_read_by_omitting_split_flag(self):
        binding = self.binding("test-detail.json", b'{"private":"payload"}')
        with patch("tools.validate_situation_stage6_preparation.read_json",
                   side_effect=AssertionError("parent must not read test detail")):
            with self.assertRaisesRegex(EvidenceError, "test-detail-report-forbidden"):
                self.store.report(binding)

    def test_nested_source_drift_rejects_passed_report(self):
        source = self.binding("train.jsonl")
        report = {"status": "passed", "evidenceBindings": [source]}
        binding = self.binding("report.json", json.dumps(report).encode())
        self.assertTrue(self.store.report(binding)["hashesVerified"])
        (self.storage / "train.jsonl").write_bytes(b"changed")
        with self.assertRaisesRegex(EvidenceError, "binding-hash-mismatch"):
            self.store.report(binding)

    def test_missing_bundle_is_a_closed_preparation_gate(self):
        result = collect_preparation_evidence(
            {"repository": str(self.repo), "storageRoot": str(self.storage)}, {})
        self.assertEqual(result["status"], "not-tested")
        self.assertFalse(result["preparationReady"])
        self.assertFalse(result["trainingExecutionReady"])

    def test_present_invalid_bundle_does_not_fall_back_to_old_budget(self):
        path = self.repo / "bundle.json"
        path.write_text('{"status":"complete","frozen":true}', encoding="utf-8")
        result = collect_preparation_evidence(
            {"repository": str(self.repo), "storageRoot": str(self.storage)}, {}, path)
        self.assertEqual(result["status"], "failed")
        self.assertFalse(result["preparationReady"])
        self.assertNotIn("inputBudget", result)

    def test_all_split_label_stats_is_hashed_in_isolation(self):
        for name in ('label-stats.json', 'statistics.json', 'overlays.json', 'review-inputs.jsonl', 'duplicate-audit-private.json'):
            binding = self.binding(name, b'private held-out statistics')
            with self.subTest(name=name), patch('tools.validate_situation_stage6_preparation.digest', side_effect=AssertionError('parent read')):
                self.assertEqual(self.store.verify(binding)['sha256'], binding['sha256'])

    def test_test_receipt_requires_matching_exit_status_and_actual_digest(self):
        binding = self.binding('isolated-test-budget.json', b'{"status":"passed"}')
        for status, sha, code in [('passed', None, 0), ('passed', 'x', 0), ('passed', binding['sha256'], 2),
                                  ('failed', binding['sha256'], 0), ('unknown', None, 2), ('passed', '0'*64, 0)]:
            process = SimpleNamespace(stdout=json.dumps(dict(status=status, sha256=sha)), returncode=code)
            with self.subTest(status=status, sha=sha, code=code):
                result, actual = orchestration.consume_test_receipt(process, self.store, binding['logicalPath'])
                self.assertEqual(result['status'], 'failed')
                self.assertIsNone(actual)
        process = SimpleNamespace(stdout=json.dumps(dict(status='passed', sha256=binding['sha256'])), returncode=0)
        result, actual = orchestration.consume_test_receipt(process, self.store, binding['logicalPath'])
        self.assertEqual(result['status'], 'passed')
        self.assertEqual(actual['sha256'], binding['sha256'])

    def test_existing_budget_run_is_never_written_by_failure_handler(self):
        folder = self.storage / 'reports/existing'
        folder.mkdir(parents=True)
        (folder / 'sentinel').write_bytes(b'original')
        config = dict(repository=str(self.repo), storageRoot=str(self.storage))
        with patch.object(orchestration, 'read_json', return_value=config), \
             patch.object(orchestration, 'measure', side_effect=FileExistsError('existing')), \
             patch('sys.argv', ['measure', '--run-id', 'existing', '--corpus-report', 'storage:report.json', '--corpus-sha256', 'a'*64]):
            self.assertEqual(orchestration.main(), 2)
        self.assertEqual(sorted(p.name for p in folder.iterdir()), ['sentinel'])
        self.assertEqual((folder / 'sentinel').read_bytes(), b'original')

    def test_original_dotnet_candidate_hash_uses_unchanged_canonical_bytes(self):
        text = r'{"candidate":{"summaryZh":"\u4E2D\u6587","number":1.25},"other":"candidate"}'
        extracted = canonical_property_text(text, 'candidate')
        self.assertEqual(extracted, r'{"summaryZh":"\u4E2D\u6587","number":1.25}')
        self.assertEqual(json.loads(extracted), {'summaryZh':'中文','number':1.25})

    def make_frozen_bundle(self, mutate=None):
        stage4, corpus, budget, runtime, resource = readiness_fixture()
        source = self.binding('input.json', b'actual synthetic source')
        isolated = self.binding('isolated-test-budget.json', b'actual synthetic isolated proof')
        isolated.update(split='test', purpose='final-evaluation', scope='isolated-test')
        records = dict(stage4=stage4, corpus=corpus, budget=budget, runtime=runtime)
        for value in records.values():
            for key in [k for k in value if k.startswith('_')]: del value[key]
            value['evidenceBindings'] = [dict(source)]
        budget['evidenceBindings'].append(isolated)
        manifest = self.binding('manifest.json', b'{"status":"complete","frozen":true}')
        corpus['evidenceBindings'].append(manifest)
        resource['evidenceBindings'] = [dict(source)]
        if mutate: mutate(records)
        artifacts = {key: self.binding(key + '.json', json.dumps(value).encode()) for key, value in records.items()}
        self.binding('complete.json', json.dumps(dict(complete=True, frozen=True,
            reportSha256=artifacts['corpus']['sha256'], manifestSha256=manifest['sha256'])).encode())
        bundle = dict(schemaVersion='situation-stage6-preparation-bundle-v1', status='complete', frozen=True,
                      analysisRuleVersion=runtime['analysisRuleVersion'], artifacts=artifacts)
        path = self.repo / 'bundle.json'
        path.write_text(json.dumps(bundle), encoding='utf-8')
        return path, resource

    def test_file_backed_admission_and_required_failure_paths(self):
        mutations = [None,
            lambda r: r['corpus']['details'].update(frozen=False),
            lambda r: r['corpus']['details']['counts'].update(train=35),
            lambda r: r['budget']['details'].update(overBudgetCount=1),
            lambda r: r['budget']['evidenceBindings'][-1].update(purpose='sft'),
            lambda r: r['budget']['evidenceBindings'].pop(),
            lambda r: r['runtime'].update(selectedRuntime=None),
            lambda r: r['corpus']['details']['versions'].update(analysisRuleVersion='v1')]
        config = dict(repository=str(self.repo), storageRoot=str(self.storage))
        for index, mutation in enumerate(mutations):
            path, resource = self.make_frozen_bundle(mutation)
            result = collect_preparation_evidence(config, resource, path)
            with self.subTest(case=index):
                self.assertEqual(result['preparationReady'], mutation is None, result)
                self.assertFalse(result['trainingExecutionReady'])
                if mutation is None:
                    self.assertEqual(result['selectedRuntime'], 'nf4-rank16')
                    self.assertFalse(result['readiness']['details']['resourceProbeReady'])
        path, resource = self.make_frozen_bundle()
        (self.storage / 'input.json').write_bytes(b'changed after freeze')
        self.assertFalse(collect_preparation_evidence(config, resource, path)['preparationReady'])
        path, resource = self.make_frozen_bundle()
        (self.storage / 'complete.json').unlink()
        self.assertFalse(collect_preparation_evidence(config, resource, path)['preparationReady'])


if __name__ == "__main__":
    unittest.main()
