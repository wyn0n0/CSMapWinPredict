import copy
import json
import unittest
from pathlib import Path
from situation_stage6_budget import measure_paired_budget, REVISION, RULE, sha

ROOT = Path(__file__).resolve().parents[1]

def record(split='train'):
    return dict(schemaVersion='situation-stage6-paired-record-v1', sampleId=split+'-1',
        metadata=dict(split=split, private='DO NOT RENDER'),
        input=dict(scene={'private': 'DO NOT RENDER'}, allowedEvidenceIds=['alive'], facts=dict(
            schemaVersion='situation-facts-v1', analysisRuleVersion=RULE,
            alive=dict(t=None, ct=5), totalHealth=dict(t=None, ct=500), bomb=dict(state='unknown', site=None),
            formation=dict(t='unknown', ct='split'), pressure=dict(side=None, site=None),
            contestedRegions=None, contactRisk='unknown', isolatedSide='unknown', spatialAdvantage='uncertain',
            confidence='low', evidence=[dict(id='alive', sourcePaths=['/alive'], ruleId='alive', textZh='存活人数未知')],
            dataQuality=[dict(code='missing-frame', fieldPaths=['/alive'], severity='warning')])),
        output=dict(schemaVersion='situation-narrative-v1', highlights=[dict(textZh='存活人数未知', evidenceIds=['alive']),
                    dict(textZh='局势信息不足', evidenceIds=['alive'])], summaryZh='信息不足，局势未知。', uncertainties=['缺少帧信息']))

class Adapter:
    metadata = dict(modelId='Qwen/Qwen3-1.7B', tokenizerRevision=REVISION,
                    chatTemplateSha256='a'*64, enableThinking=False, truncation=False)
    def __init__(self, total=2560, prompt=2048):
        self.total, self.prompt, self.users = total, prompt, []
    def count_sequence(self, system, user, target):
        self.users.append(user)
        return dict(totalTokens=self.total, inferencePromptTokens=self.prompt, promptTokenPrefixExact=True,
                    boundaryCrossingTokens=0, assistantSuffixTokens=1)
    def count_prompt(self, system, user):
        self.users.append(user)
        return dict(inferencePromptTokens=self.prompt)

def contract():
    return dict(evidenceBindings=[dict(logicalPath='repo:synthetic.json',sha256='a'*64)],chatTemplateSha256='a'*64, expectedCounts=dict(train=1,dev=1),
                purposeBySplit=dict(train='sft',dev='checkpoint-selection'), isolatedTestStatus='passed')

class BudgetTests(unittest.TestCase):
    def run_budget(self, adapter=None, rows=None, cfg=None):
        return measure_paired_budget(rows or [record(),record('dev')], adapter or Adapter(), cfg or contract())
    def test_exact_boundaries_and_complete_payload(self):
        adapter=Adapter(); result=self.run_budget(adapter)
        self.assertEqual(result['status'],'passed',result['reasonCodes'])
        self.assertEqual(json.loads(adapter.users[0]),record()['input']['facts'])
        self.assertNotIn('DO NOT RENDER',adapter.users[0])
    def test_training_overflow_is_retained(self):
        result=self.run_budget(Adapter(total=2561))
        self.assertEqual(result['status'],'failed'); self.assertEqual(len(result['details']['rows']),2)
        self.assertEqual(result['details']['overBudgetCount'],1)
    def test_generation_overflow(self):
        self.assertEqual(self.run_budget(Adapter(prompt=2049))['details']['overBudgetCount'],2)
    def test_legacy_training_envelope_rejected(self):
        row=record(); row['schemaVersion']='situation-training-record-v1'
        self.assertIn('record-schema-invalid',self.run_budget(rows=[row,record('dev')])['reasonCodes'])
    def test_allowed_ids_mismatch(self):
        row=record(); row['input']['allowedEvidenceIds']=[]
        self.assertIn('allowed-evidence-mismatch',self.run_budget(rows=[row,record('dev')])['reasonCodes'])
    def test_missing_quality_rejected(self):
        row=record(); del row['input']['facts']['dataQuality']
        self.assertEqual(self.run_budget(rows=[row,record('dev')])['status'],'failed')
    def test_dev_does_not_read_target(self):
        row=record('dev'); del row['output']
        self.assertEqual(self.run_budget(rows=[record(),row])['status'],'passed')
    def test_test_forbidden_in_normal_consumer(self):
        self.assertIn('split-purpose-forbidden',self.run_budget(rows=[record(),record('dev'),record('test')])['reasonCodes'])
    def test_full_count_mismatch(self):
        cfg=contract(); cfg['expectedCounts']['train']=2
        self.assertIn('full-scope-count-mismatch',self.run_budget(cfg=cfg)['reasonCodes'])
    def test_missing_isolated_status(self):
        cfg=contract(); del cfg['isolatedTestStatus']
        self.assertEqual(self.run_budget(cfg=cfg)['status'],'failed')
    def test_isolated_test_only(self):
        cfg=dict(evidenceBindings=[dict(logicalPath='repo:synthetic.json',sha256='a'*64)],scope='isolated-test',chatTemplateSha256='a'*64,expectedCounts=dict(test=1),purposeBySplit=dict(test='final-evaluation'))
        result=self.run_budget(rows=[record('test')],cfg=cfg)
        self.assertEqual(result['status'],'passed'); self.assertFalse(result['details']['trainValidated'])
    def test_wrong_revision(self):
        adapter=Adapter(); adapter.metadata=dict(adapter.metadata,tokenizerRevision='wrong')
        self.assertIn('tokenizer-revision-invalid',self.run_budget(adapter)['reasonCodes'])

if __name__ == '__main__': unittest.main()
