"""Token-boundary, train-isolation and lossless representation tests; no model download."""
import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from situation_token_measurement import distribution, summary, train_membership, sha_text
from situation_lossless_table import canonical, encode, decode


class TokenBudgetTests(unittest.TestCase):
    def test_lossless_nested_unknown_precision_and_order(self):
        fixtures = [None, [], {}, True, [True, 1, False, 0], ['o', 'a', 't'],
            {'trajectory': [{'tick': 8*i, 'position': {'x': .539004+i*.000001, 'y': .745023, 'z': -.03515}} for i in range(10)],
             'facts': {'unknown': None, 'floor': 'unknown'}, 'evidence': ['证据甲', '证据乙'], 'repeat': ['A']*5},
            [{'k': i} for i in range(10)], [{}, {}], [None, None], [1, 2.0, 3]]
        for value in fixtures:
            encoded = encode(value)
            self.assertEqual(canonical(decode(encoded)), canonical(value))
            self.assertEqual(canonical(encode(decode(encoded))), canonical(encoded))

    def test_rejects_malformed_column_counts_and_tags(self):
        for encoded in (['o', ['x', 'x'], [1, 2]], ['t', ['x'], [['a', [1]]], 2], ['missing', 1]):
            with self.assertRaises(ValueError):
                decode(encoded)

    def test_nearest_rank_and_budget_boundary(self):
        self.assertEqual(distribution(range(1, 101)), dict(min=1, p50=50, p95=95, p99=99, max=100))
        fields = ['inputTokens', 'inferencePromptTokens', 'targetTokens', 'targetStandaloneTokens', 'completionTokens',
                  'assistantSuffixTokens', 'systemStandaloneTokens', 'userStandaloneTokens', 'specialTokens', 'totalTokens']
        rows = [dict.fromkeys(fields, n) | {'promptTokenPrefixExact': True} for n in (768, 769, 1024, 1025)]
        result = summary(rows, [1024, 768])
        self.assertEqual(result['overBudget']['1024']['total'], 1)
        self.assertEqual(result['overBudget']['768']['total'], 3)

    def test_candidate_schema_dispatch_and_rejection(self):
        import json
        from jsonschema import Draft202012Validator, ValidationError
        schema = json.loads((Path(__file__).resolve().parents[1] / 'schemas/situation/situation-lossless-table-v2.schema.json').read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema)
        value = {'points': [{'tick': i * 8, 'p': {'x': i / 1000, 'y': 1.0, 'z': None}} for i in range(100)], 'flags': [True, 1, False, 0]}
        validator.validate(encode(value))
        for invalid in (['unknown', []], ['r', 0, .5, 4], ['c', 'x', 0], ['o', ['x', 'x'], [1, 2]]):
            with self.assertRaises(ValidationError):
                validator.validate(invalid)

    def test_train_membership_rejects_dev_and_duplicates(self):
        split = {k: [dict(matchId=f'{k}-{i}') for i in range(n)] for k, n in [('train', 71), ('dev', 8), ('test', 8)]}
        manifest = {'versions': {'data': 'situation-training-data-v1'}}
        refs = ['match-' + sha_text('situation-training-match-ref-v1\ndata=situation-training-data-v1\nmatch=' + m['matchId']) for m in split['train']]
        rows = [dict(sampleId=str(i), metadata=dict(split='train', matchRef=refs[i % 71])) for i in range(500)]
        train_membership(manifest, split, rows)
        for field, bad in (('split', 'dev'), ('matchRef', 'match-forbidden')):
            changed = copy.deepcopy(rows)
            changed[0]['metadata'][field] = bad
            with self.assertRaisesRegex(ValueError, 'non-train-member'):
                train_membership(manifest, split, changed)
        rows[1]['sampleId'] = rows[0]['sampleId']
        with self.assertRaisesRegex(ValueError, 'unique'):
            train_membership(manifest, split, rows)


if __name__ == '__main__':
    unittest.main()
