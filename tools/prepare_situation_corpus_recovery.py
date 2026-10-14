"""Pair the frozen r3 export using its original producer's canonical hash domain.

The original exporter stays byte-identical because the completed source evidence
binds it. This versioned continuation changes only review record/scene hash
verification: an independently built bridge calls the frozen .NET implementation.
"""
import argparse
from collections import Counter
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys
import traceback

from prepare_situation_corpus import (binding, command_evidence, json_lines, canonical_property_text,
                                      CONTRACT_PATH, PURPOSE)
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from validate_situation_stage6_preparation import EvidenceStore, require


def load_review_hash_index(run, store, base, review):
    build_path = run / 'bridge-build.json'
    build = read_json(build_path)
    require(build['exitCode'] == 0, 'review-bridge-build-failed')
    build_bindings = build['sourceBindings'] + build['binaryBindings']
    for b in build_bindings: store.verify(b)
    index_path = run / 'review-source-hashes.json'
    index = read_json(index_path)
    require(index.get('schemaVersion') == 'situation-stage6-review-source-hashes-v1'
            and index.get('status') == 'passed' and index.get('complete') is True
            and len(index.get('records', {})) == 50, 'review-hash-index-invalid')
    api = REPO / 'apps/api/bin/Release/net10.0/CsDemoMap.Api.dll'
    require(index['canonicalizerAssemblySha256'] == digest(api)
            and index['baseManifestSha256'] == digest(base / 'manifest.json')
            and index['reviewInputsSha256'] == digest(review / 'review-inputs.jsonl'), 'review-hash-index-input-drift')
    for split in PURPOSE:
        require(index['sourcePartitionSha256'][split] == digest(base / (split + '.jsonl')), 'review-source-partition-drift')
    command_path = run / 'review-hash-command.json'
    command = read_json(command_path)
    expected = ['dotnet', str(run / 'bridge-bin/Stage6ReviewHashBridge.dll'), str(api), str(base),
                str(review / 'review-inputs.jsonl'), str(index_path)]
    log_path = command_path.with_suffix('.log')
    require(command['command'] == expected and command['exitCode'] == 0
            and command['logSha256'] == digest(log_path)
            and json.loads(log_path.read_text(encoding='utf-8')) == dict(status='passed', reviewedRecords=50),
            'review-hash-index-command-invalid')
    return index, build_bindings + [binding(store, p, scope='isolated-test') for p in
                                   (build_path, index_path, command_path, log_path)]


def verify_review_source(line, row, candidate, split, witness, reviewed_scene_sha):
    require(isinstance(witness, dict), 'review-canonical-witness-missing')
    require(witness['split'] == split and witness['rawLineSha256'] == hashlib.sha256(line.rstrip('\r\n').encode('utf-8')).hexdigest()
            and witness['canonicalRecordSha256'] == candidate['recordSha256'], 'original-review-record-hash-drift')
    require(candidate['split'] == split and candidate['matchRef'] == row['metadata']['matchRef']
            and candidate['roundRef'] == row['metadata']['roundRef']
            and witness['sceneSha256'] == row['metadata']['modelInputSha256']
            and witness['reviewedSceneSha256'] == reviewed_scene_sha == witness['sceneSha256'],
            'review-input-lineage-mismatch')


def prepare_recovered(args, store, baseline, run):
    source_run = safe_child(store.storage, "runs/" + args.source_run_id)
    from situation_stage6_corpus import (object_sha256, bind_record_hashes, partition_review_pairs,
                                        load_review_partition, prepare_paired_corpus)
    completion = read_json(source_run / 'source-export-complete.json')
    require(completion['status'] == 'complete', 'export-incomplete')
    for b in completion['evidenceBindings']: store.verify(b)
    require(args.stage4_report and args.stage4_report_sha256, 'pinned-stage4-report-required')
    stage4_binding = binding(store, args.stage4_report)
    require(stage4_binding['sha256'] == args.stage4_report_sha256, 'stage4-report-drift')
    closeout = store.report(stage4_binding)
    require(closeout['status'] == 'passed', 'stage4-not-closed')
    approved = {b['logicalPath']: b['sha256'] for b in closeout['evidenceBindings']}
    require(read_json(source_run / 'frozen-review-verify.json')['exitCode'] == 0, 'frozen-review-not-verified')
    base, review = [safe_child(store.storage, baseline[k]) for k in ('base_dataset', 'frozen_review')]
    hash_index, hash_bindings = load_review_hash_index(run, store, base, review)
    evidence = [stage4_binding, binding(store, CONTRACT_PATH), binding(store, source_run / 'frozen-review-verify.json'),
                binding(store, source_run / 'source-export-complete.json')] + completion['evidenceBindings']
    manifests = {}
    for folder in (base, review):
        manifest = read_json(folder / 'manifest.json')
        require(manifest['status'] == 'complete', 'source-incomplete')
        frozen_binding = binding(store, folder / 'manifest.json')
        require(approved.get(frozen_binding['logicalPath']) == frozen_binding['sha256'], 'approved-frozen-manifest-drift')
        evidence.append(frozen_binding)
        for entry in manifest['files']:
            b = binding(store, safe_child(folder, entry['path']), **({'scope': 'isolated-test'} if folder == review or entry['path'].startswith('test') or entry['path'] == 'label-stats.json' else {}))
            require(b['sha256'] == entry['sha256'], 'frozen-artifact-drift')
            evidence.append(b)
    review_manifest = read_json(review / 'manifest.json')
    require(review_manifest['reviewStatus'] == 'frozen' and review_manifest['reviewVersion'] == baseline['review_version'], 'review-version-or-state')
    for split in PURPOSE:
        folder = source_run / ('source-' + split)
        manifest = read_json(folder / 'manifest.json')
        require(manifest['complete'] is True and manifest['frozen'] is True and manifest['split'] == split
                and manifest['purpose'] == PURPOSE[split] and manifest['analysisRuleVersion'] == baseline['target_rule'], 'source-manifest-invalid')
        require(manifest['baseManifestSha256'] == digest(base / 'manifest.json') and manifest['splitSha256'] == baseline['split_sha256'], 'source-lineage-drift')
        evidence.append(binding(store, folder / 'manifest.json', split=split, purpose=PURPOSE[split]))
        for item in manifest['files']:
            b = binding(store, safe_child(folder, item['path']), split=split, purpose=PURPOSE[split])
            require(b['sha256'] == item['sha256'], 'source-record-drift')
            evidence.append(b)
        for b in manifest['sourceFiles']:
            store.verify(b)
            evidence.append(b)
        for b in manifest.get('shardBindings', []):
            store.verify(b)
            evidence.append(b)
        manifests[split] = manifest
    for name in ('tools/prepare_situation_corpus.py', 'tools/prepare_situation_corpus_recovery.py', 'tools/situation_evidence.py', 'tools/situation_stage6_parallel.py', 'tools/situation_stage6_corpus.py', 'tools/situation_stage6_weights.py'):
        evidence.append(binding(store, REPO / name, kind='source'))
    evidence.append(binding(store, REPO / baseline['split_file']))
    # Deduplicate exact bindings only, retaining isolation flags.
    evidence = list({(b['logicalPath'], b['sha256']): b for b in evidence}.values())
    evidence.extend(hash_bindings)
    candidates = {}
    candidate_scene_hashes = {}
    with (review / 'review-inputs.jsonl').open(encoding='utf-8') as source:
        for line in source:
            candidate = json.loads(line)
            require(candidate['sampleId'] not in candidates, 'duplicate-review-input')
            raw = canonical_property_text(line, 'candidate')
            require(hashlib.sha256(raw.encode('utf-8')).hexdigest() == candidate['candidateSha256'], 'original-candidate-hash-drift')
            candidates[candidate['sampleId']] = candidate
            raw_scene = canonical_property_text(canonical_property_text(line, 'input'), 'scene')
            candidate_scene_hashes[candidate['sampleId']] = hashlib.sha256(raw_scene.encode('utf-8')).hexdigest()
    reviewed_records, legacy_stats = {}, {}
    for split in PURPOSE:
        phase, tags, selected, count = Counter(), Counter(), set(), 0
        with (base / (split + '.jsonl')).open(encoding='utf-8') as source:
            for line in source:
                row = json.loads(line)
                count += 1
                selected.add(row['sampleId'])
                phase[row['metadata']['phase']] += 1
                tags.update(row['metadata']['selectionTags'])
                c = candidates.get(row['sampleId'])
                if c is None: continue
                verify_review_source(line, row, c, split, hash_index['records'].get(row['sampleId']), candidate_scene_hashes[row['sampleId']])
                row['input'], row['output'] = deepcopy(c['input']), deepcopy(c['candidate'])
                reviewed_records[row['sampleId']] = bind_record_hashes(row)
        legacy_stats[split] = dict(count=count, phase=dict(phase), selectionTags=dict(tags), ids=selected)
    require(len(reviewed_records) == len(candidates) == 50, 'review-record-coverage')
    pairs = []
    for split in PURPOSE:
        for label in json_lines(review / (split + '-labels.jsonl')):
            key = label['sampleId']
            c, row = candidates[key], reviewed_records[key]
            require(c['split'] == split and label['reviewVersion'] == baseline['review_version'], 'review-label-identity')
            require(label['labelSource'] in ('human-approved', 'human-modified'), 'review-label-not-approved')
            pair = dict(sampleId=key, split=split, reviewVersion=label['reviewVersion'], analysisRuleVersion=c['input']['facts']['analysisRuleVersion'],
                        record=row, label=label['narrative'], allowedUse=label['allowedUse'],
                        reviewStatus='modified' if label['labelSource'] == 'human-modified' else 'approved',
                        sourceHashes=dict(recordSha256=object_sha256(row), inputSha256=object_sha256(row['input']), labelSha256=object_sha256(label['narrative']),
                                          originalRecordSha256=c['recordSha256'], originalCandidateSha256=c['candidateSha256']))
            pairs.append(pair)
    contract = dict(complete=True, frozen=True, analysisRuleVersion=baseline['target_rule'], reviewVersion=baseline['review_version'],
                    splitMembers={s: manifests[s]['matches'] for s in PURPOSE}, expectedReviewCounts=dict(train=36, dev=7, test=7),
                    sourceBindings=evidence, bindingResolver=store.path)
    review_root = safe_child(store.storage, 'datasets/' + args.output_id + '-review')
    contract['outputLogicalRoot'] = 'storage:datasets/' + args.output_id + '-review'
    review_bindings = partition_review_pairs(pairs, contract, review_root)
    pairs = []
    for split in PURPOSE:
        pairs += load_review_partition(review_root / split, split, PURPOSE[split], digest(review_root / split / 'manifest.json'))
    def source_rows():
        for split in PURPOSE:
            yield from json_lines(source_run / ('source-' + split) / (split + '.jsonl'))
    corpus_root = safe_child(store.storage, 'datasets/' + args.output_id)
    contract['sourceBindings'] = evidence + review_bindings
    contract['outputLogicalRoot'] = 'storage:datasets/' + args.output_id
    report = prepare_paired_corpus(source_rows(), pairs, contract, corpus_root)
    differences = {}
    for split in PURPOSE:
        old = legacy_stats[split]
        stages = {}
        for name, path in [('v8Source', source_run / ('source-' + split) / (split + '.jsonl')), ('paired', corpus_root / split / 'records.jsonl')]:
            phase, tags, ids = Counter(), Counter(), set()
            for row in json_lines(path):
                ids.add(row['sampleId'])
                phase[row['metadata']['phase']] += 1
                tags.update(row['metadata']['selectionTags'])
            stages[name] = dict(count=len(ids), phase=dict(phase), selectionTags=dict(tags), ids=ids)
        source, paired = stages['v8Source'], stages['paired']
        differences[split] = dict(v1={k: v for k, v in old.items() if k != 'ids'},
            v8Source={k: v for k, v in source.items() if k != 'ids'}, paired={k: v for k, v in paired.items() if k != 'ids'},
            v1ToV8=dict(retained=len(source['ids'] & old['ids']), added=len(source['ids'] - old['ids']), removed=len(old['ids'] - source['ids'])),
            v8ToPaired=dict(retained=len(paired['ids'] & source['ids']), appendedReviewedTicks=len(paired['ids'] - source['ids']), removed=len(source['ids'] - paired['ids'])))
    write_new_json(run / 'selection-differences-isolated.json', differences)
    write_new_json(run / 'selection-differences-train-dev.json', {s: differences[s] for s in ('train', 'dev')})
    write_new_json(run / 'paired-complete.json', dict(status=report['status'], corpus=binding(store, corpus_root / 'report.json'),
                                                    differences=binding(store, run / 'selection-differences-isolated.json', scope='isolated-test')))
    print(json.dumps(dict(status=report['status'], reportSha256=digest(corpus_root / 'report.json'))), flush=True)


def finish(args, store, baseline, run):
    names = ['tools/prepare_situation_corpus.py', 'tools/prepare_situation_corpus_recovery.py', 'tools/situation_evidence.py',
        'tools/situation_stage6_parallel.py', 'tools/situation_stage6_corpus.py', 'tools/situation_stage6_weights.py',
        'tools/measure_situation_stage6_preparation.py', 'tools/situation_stage6_budget.py', 'tools/situation_stage6_readiness.py',
        'tools/validate_situation_stage6_preparation.py', 'tools/measure_situation_tokens.py', 'tools/situation_lossless_table.py',
        'tools/stage6-review-hash-bridge/Program.cs', 'tools/stage6-review-hash-bridge/Stage6ReviewHashBridge.csproj',
        'situation-implementation/stage6-goals-handoff/frozen-contract.json',
        'situation-implementation/stage6-goals-handoff/runtime-config-draft.json',
        'situation-implementation/situation-stage6-paired-record-v1.schema.json',
        'situation-implementation/situation-facts-prompt-v1.json']
    sources = [binding(store, REPO / name) for name in names]
    require(args.output_id and args.budget_run_id and args.stage4_report_sha256, 'finish-arguments-required')
    require(digest(args.stage4_report) == args.stage4_report_sha256, 'stage4-report-drift')
    source_run = safe_child(store.storage, 'runs/' + args.source_run_id)
    completion = read_json(source_run / 'source-export-complete.json')
    require(completion['status'] == 'complete', 'source-export-not-complete')
    for b in completion['evidenceBindings']: store.verify(b)
    write_new_json(run / 'finish-started.json', dict(status='source-complete', sourceRunId=args.source_run_id,
        stage4ReportSha256=args.stage4_report_sha256, sourceBindings=sources,
        recoveryReason='original RecordSha256 and scene lineage use the producer .NET canonical domain'))
    print(json.dumps(dict(status='reused-source-starting-pairing')), flush=True)
    command_evidence([sys.executable, '-B', str(Path(__file__).resolve()), 'prepare', '--run-id', args.run_id,
        '--source-run-id', args.source_run_id, '--output-id', args.output_id, '--stage4-report', args.stage4_report,
        '--stage4-report-sha256', args.stage4_report_sha256], run / 'prepare-command.json')
    paired = read_json(run / 'paired-complete.json')
    require(paired['status'] == 'passed', 'pairing-not-passed')
    store.verify(paired['corpus'])
    for b in sources: store.verify(b)
    print(json.dumps(dict(status='paired-complete-starting-budget')), flush=True)
    command_evidence([sys.executable, '-B', str(REPO / 'tools/measure_situation_stage6_preparation.py'),
        '--run-id', args.budget_run_id, '--corpus-report', paired['corpus']['logicalPath'],
        '--corpus-sha256', paired['corpus']['sha256']], run / 'budget-command.json')
    budget = read_json(safe_child(store.storage, 'reports/' + args.budget_run_id + '/complete.json'))
    for b in [paired['corpus'], budget['budget'], budget['runtime']] + sources: store.verify(b)
    write_new_json(run / 'finish-complete.json', dict(status='passed', corpus=paired['corpus'],
        budget=budget['budget'], runtime=budget['runtime'], trainingExecutionReady=False))
    print(json.dumps(dict(status='preparation-artifacts-complete-cli-audit-pending')), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('prepare', 'finish'))
    for name in ('run-id', 'source-run-id', 'stage4-report', 'stage4-report-sha256', 'output-id'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--budget-run-id')
    args = parser.parse_args()
    config = read_json(CONFIG)
    store = EvidenceStore(REPO, storage_root(config['storageRoot']), isolated=args.mode == 'prepare')
    run = safe_child(store.storage, 'runs/' + args.run_id)
    require(run.is_dir(), 'recovery-run-directory-missing')
    baseline = read_json(CONTRACT_PATH)['baseline']
    try:
        if args.mode == 'finish': finish(args, store, baseline, run)
        else: prepare_recovered(args, store, baseline, run)
        return 0
    except Exception:
        path = run / (args.mode + '-failure.json')
        if not path.exists(): write_new_json(path, dict(status='failed', traceback=traceback.format_exc()))
        print(json.dumps(dict(status='failed', reason='see-private-' + args.mode + '-failure')), flush=True)
        return 2


if __name__ == '__main__': raise SystemExit(main())
