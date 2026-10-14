"""Streaming, purpose-scoped 5120 input admission; no generation/training."""
import hashlib
import json

from jsonschema.exceptions import ValidationError

from situation_core_context import (REPRESENTATION, render_core, system_prompt,
                                    validators, require, observation_map, project_scene)
from situation_environment import digest, read_json
from situation_lossless_table import canonical
from situation_stage6_budget import REVISION
from situation_stage6_corpus import PURPOSES, RULE, PAIRED_SCHEMA_PATH, object_sha256
from situation_token_measurement import distribution


def stream_partition(directory, split, purpose, manifest_sha256, *, isolated=False):
    # Check purpose and process scope before touching any manifest or record.
    require(split in PURPOSES and purpose in PURPOSES[split], 'purpose-forbidden')
    require((split == 'test') == isolated, 'partition-process-scope-forbidden')
    path = directory / 'manifest.json'
    require(digest(path) == manifest_sha256, 'partition-manifest-hash')
    manifest = read_json(path)
    require(manifest.get('schemaVersion') == 'situation-stage6-partition-v1'
            and manifest.get('complete') is True and manifest.get('frozen') is True
            and manifest.get('kind') == 'records', 'partition-incomplete')
    require(manifest['split'] == split and purpose in manifest['allowedUse']
            and manifest['analysisRuleVersion'] == RULE and manifest['hashDomain'] == 'stage6-json-v1',
            'partition-purpose-version')
    require(manifest['file'] == 'records.jsonl'
            and manifest['recordSchemaVersion'] == 'situation-stage6-paired-record-v1'
            and manifest['recordSchemaSha256'] == digest(PAIRED_SCHEMA_PATH), 'partition-schema-drift')
    payload = directory / 'records.jsonl'
    require(digest(payload) == manifest['sha256'], 'partition-payload-hash')
    count, observed = 0, hashlib.sha256()
    with payload.open('rb') as stream:
        for line in stream:
            count += 1
            observed.update(line)
            row = json.loads(line)
            require(row['metadata']['split'] == split and row['purpose'] in PURPOSES[split], 'partition-row-split')
            if row.get('labelSource') in ('human-approved', 'human-modified'):
                require(row['reviewVersion'] == manifest['reviewVersion'], 'review-version-drift')
                require(row['pairingHashes']['inputSha256'] == object_sha256(row['input']), 'review-input-drift')
            yield row, dict(recordSha256=object_sha256(row), split=split,
                            manifestSha256=manifest_sha256, partitionSha256=manifest['sha256'],
                            rawLineSha256=hashlib.sha256(line).hexdigest(), sourceLine=count)
    require(count == manifest['rowCount'], 'partition-row-count')
    require(observed.hexdigest() == manifest['sha256'] and digest(payload) == manifest['sha256']
            and digest(path) == manifest_sha256, 'partition-changed-during-read')


def measure_core_budget(records, adapter, config, contract, *, progress=None):
    isolated = contract.get('scope') == 'isolated-test'
    allowed = {'test'} if isolated else {'train', 'dev'}
    require(set(contract['expectedCounts']) == allowed and set(contract['purposeBySplit']) == allowed,
            'budget-scope-invalid')
    require(all(contract['purposeBySplit'][s] in PURPOSES[s] for s in allowed), 'purpose-forbidden')
    require(config['maxSequenceLength'] == 5120 and config['generationReserveTokens'] == 512
            and config['maxInferenceInputTokens'] == 4608 and config['maxCompletionTokens'] == 512,
            'core-budget-policy')
    require(adapter.metadata['tokenizerRevision'] == REVISION
            and adapter.metadata['modelId'] == 'Qwen/Qwen3-1.7B'
            and adapter.metadata['chatTemplateSha256'] == contract['chatTemplateSha256']
            and adapter.metadata['enableThinking'] is False and adapter.metadata['truncation'] is False,
            'tokenizer-binding-mismatch')
    require(bool(contract['evidenceBindings']), 'missing-evidence-bindings')
    system = system_prompt(config)
    rows, reasons, seen = [], set(), set()
    counts = {s: 0 for s in sorted(allowed)}
    for entry in records:
        record, witness = entry if isinstance(entry, tuple) else (entry, None)
        split = record.get('metadata', {}).get('split')
        # Do not parse, project, or render an out-of-scope input.
        require(split in allowed, 'split-purpose-forbidden')
        counts[split] += 1
        row = dict(sampleId=record.get('sampleId'), split=split, status='failed')
        rows.append(row)
        try:
            require(isinstance(row['sampleId'], str) and row['sampleId'] and row['sampleId'] not in seen,
                    'sample-id-invalid-or-duplicate')
            seen.add(row['sampleId'])
            if 'splitMembers' in contract:
                require(record['metadata']['matchRef'] in contract['splitMembers'][split], 'split-membership-invalid')
            user, provenance = render_core(record, witness)
            row.update(provenance)
            # This expanded audit map is reproducible from the compact rule in
            # the system prompt; it adds no hidden facts or model instructions.
            support = observation_map(project_scene(record['input']['scene']))
            row['supportMapSha256'] = object_sha256(support)
            row['observationCount'] = len(support)
            if split == 'train':
                target = record['output']
                validators()[2].validate(target)
                if record['metadata'].get('payloadHashDomain') == 'stage6-json-v1':
                    require(record['metadata']['prelabelSha256'] == object_sha256(target), 'paired-label-hash-drift')
                ids = set(record['input']['allowedEvidenceIds'])
                require(all(set(h['evidenceIds']) <= ids for h in target['highlights']), 'target-evidence-invalid')
                measured = adapter.count_sequence(system, user, canonical(target))
                require(measured.get('promptTokenPrefixExact') is True
                        and measured.get('boundaryCrossingTokens') == 0, 'token-boundary-mismatch')
                require(measured.get('assistantSuffixTokens', 0) > 0, 'assistant-eos-missing')
                require(type(measured.get('completionTokens')) is int and measured['completionTokens'] > 0,
                        'completion-count-invalid')
            else:
                # Neither dev nor test label content is tokenized or scored.
                measured = adapter.count_prompt(system, user)
            require(type(measured.get('inferencePromptTokens')) is int
                    and measured['inferencePromptTokens'] > 0, 'prompt-count-invalid')
            if split == 'train':
                require(type(measured.get('totalTokens')) is int
                        and measured['totalTokens'] == measured['inferencePromptTokens'] + measured['completionTokens'],
                        'sequence-count-invalid')
            row.update(measured)
            row['generationTokensWithReserve'] = measured['inferencePromptTokens'] + 512
            row['overBudget'] = (row['generationTokensWithReserve'] > 5120 or
                                split == 'train' and (measured['totalTokens'] > 5120 or measured['completionTokens'] > 512))
            require(not row['overBudget'], 'sequence-over-budget')
            row['status'] = 'passed'
        except (KeyError, TypeError, ValueError, ValidationError) as exc:
            code = str(exc) if type(exc) is ValueError else 'core-record-schema-invalid'
            row['reasonCode'] = code
            reasons.add(code)
        if progress is not None and counts[split] % 1000 == 0:
            progress(split, counts[split])
    if any(type(contract['expectedCounts'][s]) is not int or contract['expectedCounts'][s] <= 0
           or counts[s] != contract['expectedCounts'][s] for s in allowed):
        reasons.add('full-scope-count-mismatch')
    metrics = ('inferencePromptTokens', 'generationTokensWithReserve', 'totalTokens', 'completionTokens')
    distributions = {s: {k: distribution([r[k] for r in rows if r['split'] == s and k in r])
                          for k in metrics if any(r['split'] == s and k in r for r in rows)} for s in sorted(allowed)}
    return dict(schemaVersion='situation-core-budget-report-v1', status='failed' if reasons else 'passed',
                reasonCodes=sorted(reasons), scope=contract.get('scope', 'train-dev'), counts=counts,
                representationVersion=REPRESENTATION, maxSequenceLength=5120, generationReserveTokens=512,
                maxInferenceInputTokens=4608, maxCompletionTokens=512,
                distributions=distributions, overBudgetCount=sum(r.get('overBudget', False) for r in rows),
                failureCount=sum(r['status'] != 'passed' for r in rows),
                evidenceBindings=contract['evidenceBindings'], rows=rows,
                generationExecutionReady=False, trainingExecutionReady=False,
                pending=config['pending'])
