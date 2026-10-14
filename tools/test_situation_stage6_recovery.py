from copy import deepcopy
import hashlib
import unittest

from prepare_situation_corpus_recovery import verify_review_source
from validate_situation_stage6_preparation import EvidenceError


class ReviewCanonicalDomainTests(unittest.TestCase):
    def fixture(self):
        line = '{"sampleId":"same-source","input":{"scene":{"x":0.12345678}}}\n'
        row = dict(metadata=dict(matchRef='m', roundRef='r', modelInputSha256='a' * 64),
                   input=dict(scene=dict(x=0.12345678)))
        candidate = dict(split='train', matchRef='m', roundRef='r', recordSha256='b' * 64,
                         input=dict(scene=dict(x=0.123457)))
        witness = dict(split='train', rawLineSha256=hashlib.sha256(line.rstrip('\n').encode()).hexdigest(),
                       canonicalRecordSha256='b' * 64, sceneSha256='a' * 64, reviewedSceneSha256='a' * 64)
        return line, row, candidate, witness

    def test_distinct_raw_and_canonical_domains_require_the_verified_bridge(self):
        line, row, candidate, witness = self.fixture()
        self.assertNotEqual(witness['rawLineSha256'], candidate['recordSha256'])
        self.assertNotEqual(row['input']['scene'], candidate['input']['scene'])
        verify_review_source(line, row, candidate, 'train', witness, 'a' * 64)
        with self.assertRaisesRegex(EvidenceError, 'review-canonical-witness-missing'):
            verify_review_source(line, row, candidate, 'train', None, 'a' * 64)

    def test_raw_bytes_or_canonical_record_drift_still_rejected(self):
        line, row, candidate, witness = self.fixture()
        for changed_line, changed_witness in [(line + ' ', witness),
                (line, dict(witness, canonicalRecordSha256='c' * 64)),
                (line, dict(witness, split='test'))]:
            with self.subTest(lineChanged=changed_line != line), self.assertRaisesRegex(EvidenceError, 'original-review-record-hash-drift'):
                verify_review_source(changed_line, row, candidate, 'train', changed_witness, 'a' * 64)

    def test_scene_and_identity_cannot_be_relaxed_by_canonicalization(self):
        line, row, candidate, witness = self.fixture()
        for changed_candidate, changed_witness, scene_sha in [
                (dict(candidate, matchRef='other'), witness, 'a' * 64),
                (dict(candidate, roundRef='other'), witness, 'a' * 64),
                (dict(candidate, split='test'), witness, 'a' * 64),
                (candidate, dict(witness, sceneSha256='c' * 64), 'a' * 64),
                (candidate, witness, 'c' * 64)]:
            with self.subTest(scene=scene_sha), self.assertRaisesRegex(EvidenceError, 'review-input-lineage-mismatch'):
                verify_review_source(line, row, changed_candidate, 'train', changed_witness, scene_sha)


if __name__ == '__main__': unittest.main()
