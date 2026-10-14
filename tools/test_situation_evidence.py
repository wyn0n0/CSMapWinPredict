"""Real byte-drift checks, including the isolated held-out verifier."""
from pathlib import Path
import tempfile
import unittest

from situation_evidence import EvidenceError, EvidenceStore, binding
from situation_environment import digest


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.repo, self.storage = root / 'repo', root / 'storage'
        self.repo.mkdir()
        self.storage.mkdir()
        self.store = EvidenceStore(self.repo, self.storage)

    def test_same_size_source_edit_fails_even_after_success(self):
        source = self.repo / 'source.py'
        source.write_bytes(b'old')
        reference = binding(self.store, source)
        self.assertEqual(self.store.verify(reference)['actualSha256'], digest(source))
        source.write_bytes(b'new')
        with self.assertRaisesRegex(EvidenceError, 'binding-hash-mismatch'):
            self.store.verify(reference)

    def test_malformed_hash_and_path_escape_fail(self):
        for reference in [dict(logicalPath='repo:source.py', sha256='invalid'),
                          dict(logicalPath='repo:../escape', sha256='a' * 64)]:
            with self.assertRaises(ValueError):
                self.store.verify(reference)

    def test_held_out_bytes_verified_in_real_child(self):
        source = self.storage / 'test-records.jsonl'
        source.write_bytes(b'original')
        reference = binding(self.store, source, split='test')
        self.assertTrue(self.store.verify(reference)['verified'])
        source.write_bytes(b'modified')
        with self.assertRaisesRegex(EvidenceError, 'isolated-test-binding-failed'):
            self.store.verify(reference)

    def test_report_cannot_claim_success_with_modified_dependency(self):
        from situation_environment import write_new_json
        source = self.repo / 'source.py'
        source.write_bytes(b'old')
        report = self.storage / 'report.json'
        write_new_json(report, dict(status='passed', hashesVerified=True,
                                   evidenceBindings=[binding(self.store, source)]))
        reference = binding(self.store, report)
        source.write_bytes(b'new')
        with self.assertRaisesRegex(EvidenceError, 'binding-hash-mismatch'):
            self.store.report(reference)


if __name__ == '__main__':
    unittest.main()
