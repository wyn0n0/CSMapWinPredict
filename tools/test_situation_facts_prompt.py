import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from situation_facts_prompt import render_facts


class FactsPromptTests(unittest.TestCase):
    def setUp(self):
        self.source = dict(facts=dict(alive=dict(t=None, ct=2), formation=dict(t='unknown', ct='grouped'),
            evidence=[dict(id='a', textZh='未知', sourcePaths=['/players/0'], ruleId='rule-a')],
            dataQuality=[dict(code='missing', severity='warning', fieldPaths=['/players'])]), allowedEvidenceIds=['a'])

    def test_preserves_complete_facts_without_scene_or_labels(self):
        before = copy.deepcopy(self.source)
        text = render_facts(self.source)
        self.assertEqual(json.loads(text), before['facts'])
        self.assertIn('未知', text)
        self.assertEqual(self.source, before)
        self.source.update(scene={'irrelevant': 'changed'}, output={'mustNotBeRead': True})
        self.assertEqual(render_facts(self.source), text)

    def test_rejects_permission_widening_and_duplicate_ids(self):
        for ids in ([], ['other'], ['a', 'a']):
            source = copy.deepcopy(self.source)
            source['allowedEvidenceIds'] = ids
            with self.assertRaisesRegex(ValueError, 'allowed-set'):
                render_facts(source)
        self.source['facts']['evidence'] *= 2
        with self.assertRaisesRegex(ValueError, 'duplicate-evidence'):
            render_facts(self.source)

    def test_rejects_nonfinite_numbers(self):
        self.source['facts']['value'] = float('nan')
        with self.assertRaises(ValueError):
            render_facts(self.source)


if __name__ == '__main__':
    unittest.main()
