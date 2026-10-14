"""Fail-closed preparation gate. Never grants training execution permission."""
from __future__ import annotations
import re

REVISION = '70d244cc86ccca08cf5af4e1e306ecf908b1ad5e'
RULE = 'situation-analysis-rules-v8-close-exposure-1'
SHA = re.compile(r'[0-9a-f]{64}\Z')


def validate_runtime(config):
    reasons = []
    required = dict(schemaVersion='situation-stage6-runtime-v1', status='frozen',
        modelId='Qwen/Qwen3-1.7B', tokenizerRevision=REVISION, analysisRuleVersion=RULE,
        representationVersion='facts-only-utf8-v1', maxSequenceLength=2560,
        recordSchemaVersion='situation-stage6-paired-record-v1',
        generationReserveTokens=512, quantization='nf4', doubleQuant=True,
        computeDtype='bfloat16', baseFrozen=True, loraRank=16, loraAlpha=32, loraDropout=0.05,
        targetModules='all-linear', microBatchSize=1, gradientAccumulationSteps=16,
        gradientCheckpointing=True, useReentrant=False, useCache=False,
        attentionBackend='cudnn', lossImplementation='assistant-positions',
        learningRate=0.0001, optimizer='AdamW', weightDecay=0.0, maxGradNorm=1.0,
        foreach=False, epochs=2, seed=42, humanTrainOccurrencesPerEpoch=5,
        scheduler='linear', warmupRatio=0.03, saveAdapter=True, saveOptimizerState=True,
        synthetic=False, enableThinking=False, doSample=False, maxNewTokens=512,
        checkpointEveryOptimizerSteps=256, checkpointAtEpochEnd=True,
        devEvaluation='epoch-end', maxSelectionCandidates=2,
        tailBatchPolicy='retain-all-actual-microsteps',
        trainingExecutionReady=False, formatRepairMaxAttempts=1,
        weightNormalization='predetermined-window-or-global-round-normalizer')
    for key, value in required.items():
        if config.get(key) != value or isinstance(value, bool) and config.get(key) is not value:
            reasons.append('runtime-' + key + '-invalid')
    if config.get('optimizerSteps') is not None:
        reasons.append('runtime-fixed-probe-steps-forbidden')
    if config.get('selectedRuntime') != 'nf4-rank16':
        reasons.append('runtime-not-selected')
    budgets = config.get('expectedBudgets', {})
    for key in ('originalTrainRecords', 'expandedEntriesPerEpoch', 'optimizerUpdatesPerEpoch',
                'totalOptimizerUpdates', 'checkpointCount', 'trainingTokens', 'estimatedTrainingSeconds',
                'planningTrainingSeconds', 'estimatedCheckpointBytes', 'storageReserveBytes', 'adapterParameters'):
        value = budgets.get(key)
        if type(value) not in (int, float) or not 0 < value < float('inf'):
            reasons.append('runtime-budget-' + key + '-pending')
    if config.get('pendingFields'):
        reasons.append('runtime-pending-fields')
    if config.get('futureProbe') != dict(originalTrainRecords=512, epochs=1, humanRepeat=False, optimizerUpdates=32):
        reasons.append('runtime-future-probe-budget-invalid')
    normalizer = config.get('weightNormalizer', {})
    normalizer_values = dict(policy='global-round-expectation',
        windowFormula='actualMicrosteps * originalRounds / expandedEntries',
        automaticAccumulationDivision=False, tailPolicy='use actual microsteps; retain every occurrence')
    if not isinstance(normalizer, dict) or any(normalizer.get(k) != v for k, v in normalizer_values.items()):
        reasons.append('runtime-weight-normalizer-invalid')
    elif type(budgets.get('originalTrainRounds')) is not int or budgets['originalTrainRounds'] <= 0 or \
            normalizer.get('originalRounds') != budgets['originalTrainRounds'] or \
            normalizer.get('expandedEntries') != budgets.get('expandedEntriesPerEpoch'):
        reasons.append('runtime-weight-normalizer-count-conflict')
    if not any(code.startswith('runtime-budget-') for code in reasons):
        import math
        b = budgets
        n, e, u = b['originalTrainRecords'], b['expandedEntriesPerEpoch'], b['optimizerUpdatesPerEpoch']
        relations = [n >= 36, e == n + 4 * 36, u == math.ceil(e / 16),
                     b['totalOptimizerUpdates'] == 2 * u,
                     b['checkpointCount'] == len(set(range(256, int(2*u)+1, 256)) | {u, 2*u}),
                     math.isclose(b['estimatedTrainingSeconds'], b['trainingTokens'] / 607.6475, rel_tol=1e-9),
                     math.isclose(b['planningTrainingSeconds'], b['estimatedTrainingSeconds'] * 1.5, rel_tol=1e-9),
                     b['estimatedCheckpointBytes'] == math.ceil(b['checkpointCount'] * (b['adapterParameters'] * 16 + 64 * 1024**2) * 1.5),
                     b['estimatedCheckpointBytes'] <= b['storageReserveBytes'],
                     b['storageReserveBytes'] == 24 * 1024**3]
        if not all(relations):
            reasons.append('runtime-budget-relations-invalid')
        if any(type(b[k]) is not int for k in ('originalTrainRecords', 'expandedEntriesPerEpoch', 'optimizerUpdatesPerEpoch', 'totalOptimizerUpdates', 'checkpointCount', 'trainingTokens')):
            reasons.append('runtime-budget-count-not-integer')
    return reasons


def evaluate_preparation_evidence(stage4_report, corpus_report, budget_report, runtime_config, resource_report):
    missing = any(not isinstance(value, dict) for value in (stage4_report, corpus_report, budget_report, runtime_config, resource_report))
    stage4_report, corpus_report, budget_report, runtime_config, resource_report = [value if isinstance(value, dict) else {} for value in (stage4_report, corpus_report, budget_report, runtime_config, resource_report)]
    reasons, bindings, observed = (['report-missing'] if missing else []), [], {}
    resolver = runtime_config.get('_evidenceResolver')
    def fail(code):
        if code not in reasons:
            reasons.append(code)
    def verify_binding(binding):
        if not isinstance(binding, dict):
            fail('evidence-binding-invalid'); return
        logical, expected = binding.get('logicalPath'), binding.get('sha256')
        if 'split' in binding:
            uses = {'train': {'sft'}, 'dev': {'checkpoint-selection', 'prompt-selection', 'evaluation'}, 'test': {'final-evaluation'}}
            if binding['split'] not in uses or binding.get('purpose') not in uses[binding['split']]:
                fail('evidence-binding-purpose-invalid')
        if not isinstance(logical, str) or not re.fullmatch(r'(repo|storage):[^\\:]+', logical) or '..' in logical.split('/') or not isinstance(expected, str) or not SHA.fullmatch(expected):
            fail('evidence-binding-invalid'); return
        if logical in observed and observed[logical] != expected:
            fail('evidence-binding-conflict')
        observed[logical] = expected
        if not callable(resolver):
            fail('evidence-rehash-resolver-missing'); return
        try:
            result = resolver(binding)
            actual = result.get('actualSha256') if isinstance(result, dict) else result
            if actual != expected:
                fail('evidence-hash-drift')
            else:
                bindings.append(dict(logicalPath=logical, sha256=expected))
        except Exception:
            fail('evidence-rehash-failed')
    def verify_source(report, check_id=None):
        if not isinstance(report, dict):
            fail('report-missing'); return {}
        expected_schema = {'stage4-closeout': 'situation-stage6-evidence-v1', 'corpus-pairing': 'situation-stage6-evidence-v1', 'paired-budget': 'situation-stage6-budget-report-v1'}
        if check_id and report.get('schemaVersion') != expected_schema[check_id]:
            fail(check_id + '-schema-invalid')
        if check_id and (report.get('checkId') != check_id or report.get('status') != 'passed' or report.get('reasonCodes') != []):
            fail((check_id or 'report') + '-not-passed')
        declared = report.get('evidenceBindings')
        if not isinstance(declared, list) or not declared:
            fail('evidence-bindings-missing')
        else:
            for binding in declared:
                verify_binding(binding)
        if '_artifactBinding' in report:
            verify_binding(report['_artifactBinding'])
        elif check_id:
            fail('report-artifact-binding-missing')
        details = report.get('details', {})
        if check_id and details.get('verified') is not True:
            fail(check_id + '-not-verified')
        return details
    stage4 = verify_source(stage4_report, 'stage4-closeout')
    corpus = verify_source(corpus_report, 'corpus-pairing')
    budget = verify_source(budget_report, 'paired-budget')
    verify_source(runtime_config)
    if '_artifactBinding' not in runtime_config:
        fail('runtime-artifact-binding-missing')
    verify_source(resource_report)
    for key in ('baseComplete', 'reviewComplete', 'reviewFrozen'):
        if stage4.get(key) is not True:
            fail('stage4-' + key + '-missing')
    for key in ('complete', 'frozen', 'purposeIsolationVerified', 'pairingVerified', 'weightsVerified', 'splitVerified'):
        if corpus.get(key) is not True:
            fail('corpus-' + key + '-missing')
    for key, value in dict(maxSequenceLength=2560, generationReserveTokens=512,
                           representationVersion='facts-only-utf8-v1', trainValidated=True,
                           devValidated=True, isolatedTestStatus='passed', overBudgetCount=0).items():
        if budget.get(key) != value:
            fail('budget-' + key + '-invalid')
    versions = corpus.get('versions', {})
    for key in ('analysisRuleVersion', 'reviewVersion', 'recordSchemaVersion'):
        if not versions.get(key) or versions[key] != runtime_config.get(key):
            fail('version-' + key + '-conflict-or-missing')
    for key in ('analysisRuleVersion', 'tokenizerRevision', 'recordSchemaVersion'):
        if not budget.get(key) or budget[key] != runtime_config.get(key):
            fail('version-' + key + '-conflict-or-missing')
    if budget.get('counts', {}).get('train') != runtime_config.get('expectedBudgets', {}).get('originalTrainRecords'):
        fail('budget-runtime-train-count-conflict')
    if corpus.get('counts', {}).get('train') != budget.get('counts', {}).get('train'):
        fail('corpus-budget-train-count-conflict')
    if not any(b.get('split') == 'test' or b.get('scope') == 'isolated-test' for b in budget_report.get('evidenceBindings', [])):
        fail('isolated-test-binding-missing')
    reasons.extend(validate_runtime(runtime_config))
    accepted = resource_report.get('resourceAccepted') is True
    numeric_ready = resource_report.get('resourceProbeReady')
    if not accepted or resource_report.get('verified') is not True or resource_report.get('status') != 'passed':
        fail('resource-acceptance-unverified')
    if type(numeric_ready) is not bool:
        fail('resource-numeric-result-missing')
    ready = not reasons
    return dict(schemaVersion='situation-stage6-readiness-report-v1', checkId='stage6-preparation',
                status='passed' if ready else 'failed', reasonCodes=reasons, evidenceBindings=bindings,
                details=dict(preparationReady=ready, trainingExecutionReady=False,
                             selectedRuntime=runtime_config.get('selectedRuntime') if ready else None,
                             resourceAccepted=accepted, resourceProbeReady=numeric_ready))


def public_report(report):
    """Recursive redaction, including strings nested inside arrays."""
    if isinstance(report, dict):
        return {key: public_report(value) for key, value in report.items()
                if not key.startswith('_') and key not in {'rows', 'sampleId', 'error', 'traceback', 'testDetails'}}
    if isinstance(report, list):
        return [public_report(value) for value in report]
    if isinstance(report, str) and (re.search(r'[A-Za-z]:[\\/]', report) or report.startswith(('/', '\\\\'))):
        return '[local-path-redacted]'
    return report


def estimate_training_budgets(train_token_rows, human_sample_ids, *, original_rounds, adapter_parameters):
    """Deterministic future allocation, not measured training performance.

    Keeps every occurrence and every tail microstep; checkpoint boundaries are
    global and epoch ends deduplicate coincident regular recovery checkpoints.
    Optimizer-state estimate uses conservative 16 bytes/trainable parameter plus
    64 MiB/checkpoint for metadata/RNG and a 1.5x storage planning margin.
    """
    import math
    rows = list(train_token_rows)
    ids = [r['sampleId'] for r in rows]
    humans = set(human_sample_ids)
    if not rows or len(ids) != len(set(ids)) or not humans <= set(ids) or type(original_rounds) is not int or original_rounds <= 0 or type(adapter_parameters) is not int or adapter_parameters <= 0:
        raise ValueError('budget-estimation-input-invalid')
    if any(type(r.get('totalTokens')) is not int or r['totalTokens'] <= 0 for r in rows):
        raise ValueError('budget-estimation-token-invalid')
    entries = len(rows) + 4 * len(humans)
    tokens = 2 * sum(r['totalTokens'] * (5 if r['sampleId'] in humans else 1) for r in rows)
    updates = math.ceil(entries / 16)
    checkpoints = set(range(256, 2 * updates + 1, 256)) | {updates, 2 * updates}
    checkpoint_bytes = math.ceil(len(checkpoints) * (adapter_parameters * 16 + 64 * 1024**2) * 1.5)
    return dict(originalTrainRecords=len(rows), originalTrainRounds=original_rounds, adapterParameters=adapter_parameters,
                expandedEntriesPerEpoch=entries, optimizerUpdatesPerEpoch=updates,
                totalOptimizerUpdates=2 * updates, tailMicrostepsPerEpoch=entries % 16 or 16,
                checkpointCount=len(checkpoints), trainingTokens=tokens,
                estimatedTrainingSeconds=tokens / 607.6475,
                planningTrainingSeconds=tokens / 607.6475 * 1.5,
                estimatedCheckpointBytes=checkpoint_bytes, storageReserveBytes=24 * 1024**3,
                referenceTrainingTokensPerSecond=607.6475, planningMargin=1.5,
                evaluationOverheadSeconds=None, evaluationOverheadStatus='unknown-until-step4-and-baselines')
