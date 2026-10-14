import copy
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from situation_core_context import load_config
from situation_core_budget import measure_core_budget, stream_partition
from situation_environment import digest
from situation_stage6_budget import REVISION
from situation_stage6_corpus import PAIRED_SCHEMA_PATH, object_sha256
from situation_tokenizer import consume_test_receipt
from test_situation_core_context import record


class Adapter:
    metadata = dict(modelId='Qwen/Qwen3-1.7B', tokenizerRevision=REVISION,
                    chatTemplateSha256='a'*64, enableThinking=False, truncation=False)
    def __init__(self, prompt=4608, completion=512):
        self.prompt, self.completion, self.inputs = prompt, completion, []
    def count_prompt(self, system, user):
        self.inputs.append((system, user))
        return dict(inferencePromptTokens=self.prompt)
    def count_sequence(self, system, user, target):
        return dict(self.count_prompt(system, user), totalTokens=self.prompt+self.completion,
                    completionTokens=self.completion, promptTokenPrefixExact=True,
                    boundaryCrossingTokens=0, assistantSuffixTokens=2)


def contract(isolated=False):
    return dict(scope='isolated-test' if isolated else 'train-dev',
        expectedCounts=dict(test=1) if isolated else dict(train=1, dev=1),
        purposeBySplit=dict(test='final-evaluation') if isolated else dict(train='sft', dev='prompt-selection'),
        chatTemplateSha256='a'*64, evidenceBindings=[dict(logicalPath='repo:synthetic',sha256='b'*64)])


class CoreBudgetTests(unittest.TestCase):
    def measure(self, adapter=None, rows=None, policy=None):
        return measure_core_budget(rows if rows is not None else [record(), record('dev')],
                                   adapter or Adapter(), load_config(), policy or contract())
    def test_exact_5120_and_512_boundaries(self):
        adapter = Adapter(); result = self.measure(adapter)
        self.assertEqual(result['status'], 'passed', result['reasonCodes'])
        self.assertEqual(result['distributions']['train']['totalTokens']['max'], 5120)
        self.assertIn('sceneContext', adapter.inputs[0][1])
        self.assertIn('core-scene-observations-v1', adapter.inputs[0][0])
        self.assertFalse(result['generationExecutionReady'])
        self.assertFalse(result['trainingExecutionReady'])
    def test_prompt_overflow_retained_including_dev(self):
        result = self.measure(Adapter(prompt=4609))
        self.assertEqual(result['overBudgetCount'], 2)
        self.assertEqual(len(result['rows']), 2)
        self.assertEqual(result['status'], 'failed')
    def test_completion_over_512_rejected_even_when_total_fits(self):
        result = self.measure(Adapter(prompt=1000, completion=513))
        self.assertEqual(result['overBudgetCount'], 1)
        self.assertEqual(result['failureCount'], 1)
    def test_legacy_train_domain_uses_partition_proof_without_rebinding(self):
        row = record(); del row['metadata']['payloadHashDomain']
        row['metadata']['modelInputSha256'] = 'd'*64
        row['metadata']['prelabelSha256'] = 'e'*64
        witness = dict(recordSha256=object_sha256(row), split='train', manifestSha256='e'*64,
                       partitionSha256='f'*64, rawLineSha256='a'*64)
        result = self.measure(rows=[(row,witness),record('dev')])
        self.assertEqual(result['status'],'passed',result['reasonCodes'])
    def test_no_silent_skipping_schema_failures(self):
        row = record(); row['input']['facts']['evidence'] = []
        result = self.measure(rows=[row,record('dev')])
        self.assertEqual(result['counts']['train'], 1)
        self.assertEqual(result['failureCount'], 1)
    def test_dev_and_isolated_test_targets_not_read(self):
        row = record('dev'); del row['output']
        self.assertEqual(self.measure(rows=[record(),row])['status'], 'passed')
        row = record('test'); del row['output']
        self.assertEqual(self.measure(rows=[row],policy=contract(True))['status'], 'passed')
    def test_wrong_process_scope_cannot_render_test(self):
        adapter = Adapter()
        with self.assertRaisesRegex(ValueError, 'split-purpose-forbidden'):
            self.measure(adapter, rows=[record('test')])
        self.assertEqual(adapter.inputs, [])
    def test_missing_rows_and_duplicates_rejected(self):
        self.assertEqual(self.measure(rows=[])['status'], 'failed')
        self.assertIn('sample-id-invalid-or-duplicate', self.measure(rows=[record(),record(),record('dev')])['reasonCodes'])
    def test_wrong_revision_policy_and_missing_eos_rejected(self):
        adapter = Adapter(); adapter.metadata = dict(adapter.metadata,tokenizerRevision='wrong')
        with self.assertRaisesRegex(ValueError,'tokenizer-binding-mismatch'): self.measure(adapter)
        adapter = Adapter()
        with patch.object(adapter,'count_sequence',return_value=dict(inferencePromptTokens=1000,totalTokens=1100,
            completionTokens=100,promptTokenPrefixExact=True,boundaryCrossingTokens=0,assistantSuffixTokens=0)):
            self.assertIn('assistant-eos-missing',self.measure(adapter)['reasonCodes'])
    def test_purpose_denied_before_file_access(self):
        with patch('situation_core_budget.digest',side_effect=AssertionError('file opened')):
            for split,purpose,isolated in [('test','sft',False),('test','final-evaluation',False),('train','sft',True)]:
                with self.assertRaises(ValueError): list(stream_partition(Path('missing'),split,purpose,'a'*64,isolated=isolated))
    def test_stream_checks_manifest_payload_and_midstream_drift(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory); payload=root/'records.jsonl'; manifest=root/'manifest.json'
            payload.write_text(json.dumps(record())+'\n',encoding='utf-8')
            value=dict(schemaVersion='situation-stage6-partition-v1',complete=True,frozen=True,kind='records',split='train',
                allowedUse=['sft'],analysisRuleVersion=record()['input']['facts']['analysisRuleVersion'],hashDomain='stage6-json-v1',
                file='records.jsonl',recordSchemaVersion='situation-stage6-paired-record-v1',recordSchemaSha256=digest(PAIRED_SCHEMA_PATH),
                sha256=digest(payload),rowCount=1)
            manifest.write_text(json.dumps(value),encoding='utf-8'); sha=digest(manifest)
            self.assertEqual(len(list(stream_partition(root,'train','sft',sha))),1)
            stream=stream_partition(root,'train','sft',sha); next(stream)
            manifest.write_text(json.dumps(dict(value,frozen=False)),encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'partition-changed-during-read'): next(stream)
            with self.assertRaisesRegex(ValueError,'partition-manifest-hash'): list(stream_partition(root,'train','sft',sha))
    def test_isolated_receipt_cannot_smuggle_details_or_fake_success(self):
        class Store:
            def verify(self,binding): raise ValueError('missing-report')
        for stdout in ('{"status":"passed","sha256":null}', '{"status":"passed","sha256":"'+ 'a'*64 +'","length":9}',
                       '{"status":"passed","sha256":"'+ 'a'*64 +'"}'):
            receipt,binding=consume_test_receipt(subprocess.CompletedProcess([],0,stdout,''),Store(),'storage:isolated-test-budget.json')
            self.assertEqual(receipt['status'],'failed'); self.assertIsNone(binding)


if __name__ == '__main__': unittest.main()
