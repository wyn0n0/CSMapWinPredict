"""Independent synthetic closeout evidence tests; never open real test payloads."""
import copy
import unittest

from audit_situation_stage4_closeout import audit_stage4_closeout, CHECKS, SPLITS, APPROVED, PURPOSES


def fixture():
    h = 'a' * 64
    expected = {'base_dataset': 'datasets/base', 'frozen_review': 'datasets/review',
                'split_file': 'split.json', 'split_sha256': h, 'review_version': 'review-v8', 'target_rule': 'rules-v8'}
    def binding(path, kind=None):
        result = {'logicalPath': path, 'sha256': h, 'actualSha256': h}
        if kind:
            result['kind'] = kind
        return result
    schema = [{'path': 'schemas/facts.json', 'sha256': h}]
    sources = {
        'bindings': [binding(p) for p in ('storage:datasets/base/manifest.json', 'storage:datasets/review/manifest.json',
                                         'repo:split.json', 'repo:schemas/facts.json')],
        'base': {'status': 'complete', 'mode': 'full', 'purpose': 'template-prelabel-training', 'trainable': True,
                 'sampleLimit': None, 'schemaVersion': 'situation-training-manifest-v1', 'splitSha256': h,
                 'versions': {'analysisRules': 'situation-analysis-rules-v1', 'trainingRecord': 'situation-training-record-v1',
                              'facts': 'situation-facts-v1', 'scene': 'minimap-scene-v1', 'narrative': 'situation-narrative-v1'},
                 'counts': {k: {'matches': v, 'samples': {'train':24416,'dev':3216,'test':2752}[k],
                                'rounds': {'train':1526,'dev':201,'test':172}[k]} for k, v in SPLITS.items()}, 'schemaFiles': schema,
                 'files': [{'path': 'train.jsonl', 'sha256': h}]},
        'review': {'status': 'complete', 'reviewStatus': 'frozen', 'schemaVersion': 'situation-frozen-review-manifest-v2',
                   'workspaceRevision': 51, 'target': 50, 'counts': dict(APPROVED), 'reviewVersion': 'review-v8',
                   'baseDatasetSha256': h, 'approvedContentSha256': h, 'contentSha256': h,
                   'identity': {'splitSha256': h, 'schemaFiles': schema}, 'files': [{'path': 'labels.jsonl', 'sha256': h}]},
        'split': {'schemaVersion': 'situation-stage4-split-v1', **{k: [{'matchId': k + str(i), 'role': k} for i in range(v)] for k, v in SPLITS.items()}},
        'reviewMetadata': {'analysisRuleVersion': 'rules-v8', 'purposes': dict(PURPOSES), 'approvedCount': 50,
                           'unreviewedCount': 0, 'rejectedCount': 0, 'blockingIssue': False},
        'integrity': {'status': 'passed', 'artifacts': {}},
        'regressions': {k: {'status': 'passed', 'exitCode': 0, 'command': 'synthetic ' + k,
                            'bindings': [binding('repo:' + kind, kind) for kind in ('source', 'dependency', 'input')]} for k in CHECKS}}
    for key, file in [('base', 'train.jsonl'), ('review', 'labels.jsonl')]:
        sources['integrity']['artifacts'][key] = {'manifestSha256': h, 'files': [
            {'logicalPath': 'storage:datasets/' + key + '/' + file, 'sha256': h, 'expectedSha256': h, 'status': 'passed'}]}
    return sources, expected


class CloseoutTests(unittest.TestCase):
    def test_complete_fixture_and_input_immutability(self):
        s, e = fixture()
        original = copy.deepcopy(s)
        result = audit_stage4_closeout(s, e)
        self.assertEqual(result['status'], 'passed', result)
        self.assertEqual(result['details']['historicalCandidateCount'], 300)
        self.assertEqual(result['details']['approvedScopeCount'], 50)
        self.assertFalse(result['details']['trainingExecutionReady'])
        self.assertEqual(s, original)

    def test_each_missing_source_fails(self):
        for key in fixture()[0]:
            s, e = fixture()
            del s[key]
            with self.subTest(key=key):
                self.assertEqual(audit_stage4_closeout(s, e)['status'], 'failed')

    def test_mutations_fail_closed(self):
        mutations = [
            lambda s: s['base'].update(status='writing'),
            lambda s: s['base']['versions'].update(analysisRules='rules-v8'),
            lambda s: s['review'].update(target=300),
            lambda s: s['review'].update(workspaceRevision=50),
            lambda s: s['review'].update(reviewVersion='stale'),
            lambda s: s['review'].update(schemaVersion='wrong'),
            lambda s: s['review'].update(reviewStatus='preview'),
            lambda s: s['reviewMetadata']['purposes'].update(test='sft'),
            lambda s: s['reviewMetadata'].update(analysisRuleVersion='rules-v1'),
            lambda s: s['reviewMetadata'].update(blockingIssue=True),
            lambda s: s['split']['test'][0].update(matchId='train0'),
            lambda s: s['bindings'][0].update(actualSha256='b' * 64),
            lambda s: s['bindings'][0].pop('actualSha256'),
            lambda s: s['bindings'][0].update(logicalPath='storage:../../secret'),
            lambda s: s['integrity']['artifacts']['base']['files'].clear(),
            lambda s: s['integrity']['artifacts']['review']['files'][0].update(sha256='b' * 64),
            lambda s: s['regressions']['freeze'].update(exitCode=1),
            lambda s: s['regressions']['pilot']['bindings'].pop(),
            lambda s: s['regressions']['review']['bindings'][0].update(actualSha256='b' * 64),
            lambda s: s['base']['schemaFiles'][0].update(sha256='b' * 64),
        ]
        for index, mutate in enumerate(mutations):
            s, e = fixture()
            mutate(s)
            with self.subTest(index=index):
                self.assertEqual(audit_stage4_closeout(s, e)['status'], 'failed')

    def test_required_bindings(self):
        s, e = fixture()
        e = {'baseline': e, 'requiredBindings': ['repo:missing-evidence.json']}
        self.assertIn('required-binding-missing', audit_stage4_closeout(s, e)['reasonCodes'])

    def test_binding_isolation_and_version_fields_preserved(self):
        s, e = fixture()
        metadata = {'scope': 'isolated-test', 'split': 'test', 'purpose': 'final-evaluation',
                    'schemaVersion': 'record-v1', 'analysisRuleVersion': 'rules-v8',
                    'reviewVersion': 'review-v8', 'tokenizerRevision': 'fixed-revision',
                    'complete': True, 'frozen': True}
        s['bindings'][0].update(metadata)
        result = audit_stage4_closeout(s, e)
        self.assertEqual(result['status'], 'passed')
        output = next(b for b in result['evidenceBindings']
                      if b['logicalPath'] == s['bindings'][0]['logicalPath'])
        for key, value in metadata.items():
            self.assertEqual(output[key], value)
        self.assertNotIn('actualSha256', output)

    def test_malformed_never_passes(self):
        for value in (None, [], {'base': []}, {'base': {'versions': None}}):
            self.assertEqual(audit_stage4_closeout(value, {})['status'], 'failed')


if __name__ == '__main__':
    unittest.main()
