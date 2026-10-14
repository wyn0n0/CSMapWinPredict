import copy
import json
import unittest

from situation_commentary_protocol import (build_plan, verify_plan, render_groups, select_diagnostics,
    features, repair_prompt, configs)
from situation_core_context import project_scene
from situation_stage6_corpus import bind_record_hashes
from test_situation_core_context import record


def fixture(split='train'):
    row = record(split)
    facts = row['input']['facts']
    facts.update(alive=dict(t=3, ct=2), bomb=dict(state='carried', site=None), spatialAdvantage='CT',
                 confidence='high', contactRisk='low', dataQuality=[])
    ids = ['alive.t', 'alive.ct', 'bomb.state', 'spatial-advantage', 'contact-risk', 'input-quality', 'confidence']
    facts['evidence'] = [dict(id=i, ruleId='fixture', sourcePaths=['/teams'], textZh='合成测试依据。') for i in ids]
    row['input']['allowedEvidenceIds'] = ids
    row['metadata']['roundRef'] = split + '-round'
    return bind_record_hashes(row)


class RepairAdapter:
    def __init__(self, input_tokens=100):
        self.tokenizer = self
        self.input_tokens = input_tokens
    def encode(self, text, **kwargs): return list(text)
    def count_prompt(self, system, user): return dict(inferencePromptTokens=self.input_tokens)


class CommentaryTests(unittest.TestCase):
    def test_all_groups_preserve_identical_core_and_do_not_render_label(self):
        row = fixture(); before = copy.deepcopy(row)
        rendered, _ = render_groups(row)
        base = json.loads(rendered['A']['user'])
        self.assertEqual(base['facts'], row['input']['facts'])
        self.assertEqual(len({r['payloadSha256'] for r in rendered.values()}), 1)
        self.assertEqual(rendered['A']['user'], rendered['C']['user'])
        self.assertEqual(rendered['D']['user'].split('\n<content_plan>')[0], rendered['A']['user'])
        self.assertNotIn('ex.numbers', rendered['C']['user'])
        self.assertIn('independent-synthetic-teaching-material', rendered['C']['system'])
        self.assertEqual(row, before)
        del row['output']
        self.assertEqual(render_groups(row)[0], rendered)

    def test_test_is_refused_before_reading_payload(self):
        row = dict(metadata=dict(split='test'))
        for fn in (render_groups, features):
            with self.assertRaisesRegex(ValueError, 'commentary-test-forbidden'): fn(row)

    def test_conflict_uses_both_numbers_and_spatial_sources(self):
        row = fixture(); facts, core = row['input']['facts'], project_scene(row['input']['scene'])
        plan = build_plan(facts, core)
        self.assertEqual(plan['focus'], 'factor-conflict')
        self.assertEqual(plan['supportedClaims'][0]['evidenceIds'], ['alive.t', 'alive.ct', 'spatial-advantage'])
        verify_plan(plan, facts, core)
        plan['supportedClaims'][0]['textZh'] = 'T必胜。'
        with self.assertRaisesRegex(ValueError, 'planner-content-drift'): verify_plan(plan, facts, core)

    def test_postplant_unknown_clock_stays_unknown_and_independently_referenced(self):
        row = fixture(); facts, core = row['input']['facts'], project_scene(row['input']['scene'])
        facts['spatialAdvantage'] = 'even'; facts['bomb']['state'] = 'planted'
        core['bomb']['secondsToExplosion'] = None
        plan = build_plan(facts, core)
        self.assertEqual(plan['focus'], 'post-plant')
        self.assertIn('scene:/bomb/secondsToExplosion', plan['limitations'][0]['sceneRefs'])
        self.assertNotIn('evidenceIds', plan['limitations'][0])
        core['bomb']['secondsToExplosion'] = 12
        self.assertFalse(any('secondsToExplosion' in str(x) for x in build_plan(facts, core)['limitations']))

    def test_number_change_changes_plan_without_future_or_win_claim(self):
        row = fixture(); facts, core = row['input']['facts'], project_scene(row['input']['scene'])
        before = build_plan(facts, core)
        facts['alive']['t'] = 1
        after = build_plan(facts, core)
        self.assertNotEqual(before, after)
        self.assertEqual(after['focus'], 'numbers')
        self.assertIn('T方1人', after['supportedClaims'][0]['textZh'])

    def test_unknown_counts_do_not_become_zero(self):
        row = fixture(); facts, core = row['input']['facts'], project_scene(row['input']['scene'])
        facts['alive']['t'] = None
        self.assertEqual(build_plan(facts, core)['supportedClaims'], [])

    def test_contact_is_risk_not_observed_combat(self):
        row = fixture(); facts, core = row['input']['facts'], project_scene(row['input']['scene'])
        facts['spatialAdvantage'] = 'even'; facts['contactRisk'] = 'high'
        plan = build_plan(facts, core)
        self.assertEqual(plan['focus'], 'contact')
        self.assertIn('不证明已经交火', plan['supportedClaims'][0]['textZh'])

    def test_fabricated_evidence_scene_or_rule_is_rejected(self):
        row = fixture(); facts, core = row['input']['facts'], project_scene(row['input']['scene'])
        for field, value in [('evidenceIds', 'invented'), ('ruleIds', 'invented')]:
            plan = build_plan(facts, core); plan['supportedClaims'][0][field] = [value]
            with self.assertRaises(ValueError): verify_plan(plan, facts, core)
        plan = build_plan(facts, core); plan['limitations'][0]['sceneRefs'] = ['scene:/effects']
        with self.assertRaises(ValueError): verify_plan(plan, facts, core)

    def test_missing_observed_evidence_does_not_get_synthesized(self):
        row = fixture(); facts, core = row['input']['facts'], project_scene(row['input']['scene'])
        facts['evidence'] = []
        with self.assertRaisesRegex(ValueError, 'planner-evidence-missing'): build_plan(facts, core)

    def test_repair_includes_no_facts_and_enforces_both_boundaries(self):
        self.assertEqual(repair_prompt('x' * 512, RepairAdapter(4608))['inferencePromptTokens'], 4608)
        for raw, adapter, reason in [('x' * 513, RepairAdapter(), 'source-over-budget'),
                                     ('x', RepairAdapter(4609), 'input-over-budget'),
                                     (' ', RepairAdapter(), 'repair-empty')]:
            with self.assertRaisesRegex(ValueError, reason): repair_prompt(raw, adapter)
        rendered = repair_prompt('{bad}', RepairAdapter())
        self.assertEqual(rendered['user'], '<raw_output>\n{bad}\n</raw_output>')

    def candidates(self):
        cats = configs()[2]['diagnostics']['categories']
        rows = []
        for split, matches in [('train', 15), ('dev', 8)]:
            for i in range(150):
                rows.append(dict(sampleId=f'{split}-{i}', split=split, matchRef=f'{split}-match-{i % matches}',
                    roundRef=f'round-{i}', sourceSceneSha256=f'{split}-scene-{i}',
                    inputSha256='a'*64, categories=[cats[i % len(cats)]], reviewed=split == 'dev' and i < 7))
        return rows

    def test_selection_deterministic_scoped_and_mandatory_reviewed(self):
        rows = self.candidates(); selected = select_diagnostics(rows)
        self.assertEqual(select_diagnostics(list(reversed(rows))), selected)
        self.assertEqual(len(selected), 50)
        for split, count in [('train', 20), ('dev', 30)]:
            subset = [r for r in selected if r['split'] == split]
            self.assertEqual(len(subset), count)
            for category in configs()[2]['diagnostics']['categories']:
                policy = configs()[2]['diagnostics']
                minimum = policy['minimumPerCategoryOverrides'].get(split, {}).get(category, policy['minimumPerCategory'][split])
                self.assertGreaterEqual(sum(category in r['categories'] for r in subset), minimum)
        self.assertEqual(sum(r['split'] == 'dev' and r['reviewed'] for r in selected), 7)

    def test_single_real_conflict_is_reserved_and_absent_dev_is_not_fabricated(self):
        rows = self.candidates()
        for r in rows:
            r['categories'] = [c for c in r['categories'] if c != 'factor-conflict']
        rows[0]['categories'].append('factor-conflict')
        selected = select_diagnostics(rows)
        self.assertIn(rows[0], selected)
        self.assertEqual(sum('factor-conflict' in r['categories'] for r in selected), 1)

    def test_selection_fails_closed_on_test_duplicate_missing_category_or_review(self):
        original = self.candidates()
        for mode in ('test', 'duplicate', 'category', 'review'):
            rows = copy.deepcopy(original)
            if mode == 'test': rows[0]['split'] = 'test'
            if mode == 'duplicate': rows.append(rows[0])
            if mode == 'category':
                for r in rows: r['categories'] = []
            if mode == 'review': rows[-1]['reviewed'] = True
            with self.assertRaises(ValueError): select_diagnostics(rows)


if __name__ == '__main__': unittest.main()
