"""Admission must reject damaged evidence even without running a tokenizer."""
import tempfile
from pathlib import Path
import unittest

from freeze_situation_commentary_protocol import verify
from situation_evidence import binding
from situation_environment import write_new_json
from validate_situation_stage6_preparation import EvidenceStore


class AdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='commentary-admission-')
        self.root = Path(self.temp.name)
        self.repo, self.storage = self.root / 'repo', self.root / 'storage'
        self.repo.mkdir(); self.storage.mkdir()
        self.store = EvidenceStore(self.repo, self.storage)
        self.source = self.repo / 'source.txt'; self.source.write_text('frozen', encoding='utf-8')

    def tearDown(self): self.temp.cleanup()

    def artifact(self, *, over=False, failed=False, executed=False):
        budget = dict(status='failed' if failed else 'passed', failureCount=int(failed),
            counts=dict(train=20, dev=30), diagnosticInputCount=200, groupInputCount=200,
            distributions={s: {g: dict(max=4609 if over else 4608) for g in 'ABCD'} for s in ('train', 'dev')})
        write_new_json(self.storage / 'budget.json', budget)
        admission = dict(schemaVersion='situation-commentary-protocol-admission-v1', status='passed', protocolReady=True,
            generationExecutionReady=executed, trainingExecutionReady=False, modelGenerationExecuted=False, optimizerUpdates=0,
            evidenceBindings=[binding(self.store, self.source)],
            artifacts={'budget.json': binding(self.store, self.storage / 'budget.json')})
        path = self.storage / 'admission.json'; write_new_json(path, admission)
        artifact = binding(self.store, path)
        write_new_json(self.storage / 'complete.json', dict(status='complete', admission=artifact))
        return artifact

    def test_valid_boundary_is_protocol_ready_only(self):
        result = verify(self.store, self.artifact())
        self.assertTrue(result['protocolReady'])
        self.assertFalse(result['generationExecutionReady'])
        self.assertFalse(result['trainingExecutionReady'])

    def test_source_drift_closes_gate(self):
        artifact = self.artifact(); self.source.write_text('changed', encoding='utf-8')
        with self.assertRaises(ValueError): verify(self.store, artifact)

    def test_missing_completion_closes_gate(self):
        artifact = self.artifact(); (self.storage / 'complete.json').unlink()
        with self.assertRaises(FileNotFoundError): verify(self.store, artifact)

    def test_wrong_top_level_hash_closes_gate(self):
        artifact = self.artifact(); artifact['sha256'] = '0' * 64
        with self.assertRaises(ValueError): verify(self.store, artifact)

    def test_over_budget_closes_gate(self):
        with self.assertRaisesRegex(ValueError, 'over-budget'): verify(self.store, self.artifact(over=True))

    def test_failed_budget_closes_gate(self):
        with self.assertRaisesRegex(ValueError, 'budget-invalid'): verify(self.store, self.artifact(failed=True))

    def test_protocol_cannot_claim_generation_readiness(self):
        with self.assertRaisesRegex(ValueError, 'scope-invalid'): verify(self.store, self.artifact(executed=True))


if __name__ == '__main__': unittest.main()
