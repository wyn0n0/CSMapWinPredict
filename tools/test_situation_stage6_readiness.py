import copy
import json
from pathlib import Path
import unittest
from situation_stage6_readiness import evaluate_preparation_evidence, validate_runtime, public_report, estimate_training_budgets

ROOT=Path(__file__).resolve().parents[1]
H='a'*64

def fixture():
    def report(check,details):
        return dict(schemaVersion='situation-stage6-budget-report-v1' if check == 'paired-budget' else 'situation-stage6-evidence-v1',checkId=check,status='passed',reasonCodes=[],details=dict(verified=True,**details),
                    evidenceBindings=[dict(logicalPath='repo:input.json',sha256=H)],
                    _artifactBinding=dict(logicalPath='repo:'+check+'.json',sha256=H))
    stage4=report('stage4-closeout',dict(baseComplete=True,reviewComplete=True,reviewFrozen=True))
    corpus=report('corpus-pairing',dict(complete=True,frozen=True,purposeIsolationVerified=True,pairingVerified=True,
                  weightsVerified=True,splitVerified=True,versions={}))
    budget=report('paired-budget',dict(maxSequenceLength=2560,generationReserveTokens=512,
                  representationVersion='facts-only-utf8-v1',trainValidated=True,devValidated=True,
                  isolatedTestStatus='passed',overBudgetCount=0))
    runtime=json.loads((ROOT/'situation-implementation/stage6-goals-handoff/runtime-config-draft.json').read_text(encoding='utf-8-sig'))
    runtime.update(status='frozen',selectedRuntime='nf4-rank16',pendingFields=[],
                   _evidenceResolver=lambda binding: dict(binding,actualSha256=binding['sha256']),
                   _artifactBinding=dict(logicalPath='repo:runtime.json',sha256=H),
                   evidenceBindings=[dict(logicalPath='repo:input.json',sha256=H)])
    runtime['expectedBudgets']=estimate_training_budgets([dict(sampleId=str(i),totalTokens=100) for i in range(36)], {str(i) for i in range(36)}, original_rounds=2, adapter_parameters=1000)
    runtime['weightNormalizer']=dict(policy='global-round-expectation',originalRounds=2,expandedEntries=180,
        windowFormula='actualMicrosteps * originalRounds / expandedEntries',automaticAccumulationDivision=False,
        tailPolicy='use actual microsteps; retain every occurrence')
    corpus['details']['versions']={key:runtime[key] for key in ('analysisRuleVersion','reviewVersion','recordSchemaVersion')}
    budget['details'].update({key:runtime[key] for key in ('analysisRuleVersion','tokenizerRevision','recordSchemaVersion')})
    budget['details']['counts']={'train':36}
    corpus['details']['counts']={'train':36}
    budget['evidenceBindings'].append(dict(logicalPath='repo:isolated-test-report.json',sha256=H,scope='isolated-test'))
    resource=dict(status='passed',verified=True,resourceAccepted=True,resourceProbeReady=False,
                  evidenceBindings=[dict(logicalPath='repo:resource.json',sha256=H)])
    return [stage4,corpus,budget,runtime,resource]

class ReadinessTests(unittest.TestCase):
    def test_resource_acceptance_independent_and_never_training(self):
        report=evaluate_preparation_evidence(*fixture())
        self.assertEqual(report['status'],'passed',report['reasonCodes'])
        self.assertTrue(report['details']['resourceAccepted']); self.assertFalse(report['details']['resourceProbeReady'])
        self.assertFalse(report['details']['trainingExecutionReady'])
    def test_no_resolver_even_forged_flags_fails(self):
        args=fixture(); del args[3]['_evidenceResolver']; args[0]['hashesVerified']=True
        self.assertIn('evidence-rehash-resolver-missing',evaluate_preparation_evidence(*args)['reasonCodes'])
    def test_rehash_drift(self):
        args=fixture(); args[3]['_evidenceResolver']=lambda b: dict(actualSha256='b'*64)
        self.assertIn('evidence-hash-drift',evaluate_preparation_evidence(*args)['reasonCodes'])
    def test_conflicting_hash(self):
        args=fixture(); args[1]['evidenceBindings'][0]['sha256']='b'*64
        self.assertIn('evidence-binding-conflict',evaluate_preparation_evidence(*args)['reasonCodes'])
    def test_missing_file(self):
        args=fixture()
        def missing(b): raise FileNotFoundError('private path')
        args[3]['_evidenceResolver']=missing
        self.assertIn('evidence-rehash-failed',evaluate_preparation_evidence(*args)['reasonCodes'])
    def test_draft_pending_and_probe_shortcuts(self):
        for key,value in [('status','draft'),('synthetic',True),('saveAdapter',False),('optimizerSteps',2),('selectedRuntime',None),
                          ('trainingExecutionReady',True),('futureProbe',dict(optimizerUpdates=2)),('weightNormalizer',{})]:
            with self.subTest(key=key):
                args=fixture(); args[3][key]=value
                self.assertEqual(evaluate_preparation_evidence(*args)['status'],'failed')
    def test_incomplete_and_wrong_budget(self):
        for index,key,value in [(0,'reviewFrozen',False),(1,'purposeIsolationVerified',False),(2,'overBudgetCount',1),(2,'isolatedTestStatus','not-tested')]:
            args=fixture(); args[index]['details'][key]=value
            self.assertEqual(evaluate_preparation_evidence(*args)['status'],'failed')
    def test_version_conflict(self):
        args=fixture(); args[1]['details']['versions']['analysisRuleVersion']='v1'
        self.assertTrue(any('version-' in x for x in evaluate_preparation_evidence(*args)['reasonCodes']))
    def test_budget_repeat_tail_and_checkpoint_arithmetic(self):
        result=estimate_training_budgets([dict(sampleId=str(i),totalTokens=100) for i in range(17)], {'0'}, original_rounds=2, adapter_parameters=1000)
        self.assertEqual(result['expandedEntriesPerEpoch'],21)
        self.assertEqual(result['tailMicrostepsPerEpoch'],5)
        self.assertEqual(result['totalOptimizerUpdates'],4)
        self.assertEqual(result['trainingTokens'],4200)
        self.assertEqual(result['checkpointCount'],2)
        self.assertAlmostEqual(result['planningTrainingSeconds'],4200/607.6475*1.5)
    def test_budget_checkpoint_deduplication(self):
        result=estimate_training_budgets([dict(sampleId=str(i),totalTokens=10) for i in range(4096)], set(), original_rounds=1, adapter_parameters=1000)
        self.assertEqual(result['checkpointCount'],2)
    def test_recursive_redaction(self):
        result=public_report(dict(nested=[{'path':'E:\\secret\\foo','rows':[1],'testDetails':{'x':1}}],_resolver='private'))
        self.assertEqual(result,dict(nested=[dict(path='[local-path-redacted]')]))
    def test_report_schema_and_required_version(self):
        for index in range(3):
            args=fixture(); args[index]['schemaVersion']='old-schema'
            self.assertEqual(evaluate_preparation_evidence(*args)['status'],'failed')
        args=fixture(); del args[1]['details']['versions']['reviewVersion']
        self.assertIn('version-reviewVersion-conflict-or-missing',evaluate_preparation_evidence(*args)['reasonCodes'])
        args=fixture(); del args[2]['details']['tokenizerRevision']
        self.assertIn('version-tokenizerRevision-conflict-or-missing',evaluate_preparation_evidence(*args)['reasonCodes'])
    def test_positive_but_inconsistent_budget_rejected(self):
        for key in ('expandedEntriesPerEpoch','optimizerUpdatesPerEpoch','totalOptimizerUpdates','trainingTokens','checkpointCount','planningTrainingSeconds','estimatedCheckpointBytes'):
            args=fixture(); args[3]['expectedBudgets'][key]=10**15 if key == 'estimatedCheckpointBytes' else 1
            self.assertEqual(evaluate_preparation_evidence(*args)['status'],'failed',key)
    def test_missing_report_hash(self):
        args=fixture(); del args[0]['_artifactBinding']
        self.assertIn('report-artifact-binding-missing',evaluate_preparation_evidence(*args)['reasonCodes'])

if __name__ == '__main__': unittest.main()
