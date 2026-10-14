"""Stage-six isolated data preparation. No model, optimizer or generation.

The export/prepare processes alone may inspect all splits. Training consumers
receive self-contained train/dev directories through purpose-scoped loaders.
"""
import argparse
from collections import Counter
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import traceback
import shutil
from datetime import datetime

from situation_evidence import binding
from build_situation_stage6 import verify as verify_build
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from validate_situation_stage6_preparation import EvidenceStore, require

PURPOSE = dict(train='sft', dev='checkpoint-selection', test='final-evaluation')
CONTRACT_PATH = REPO / 'situation-implementation/stage6-goals-handoff/frozen-contract.json'


def json_lines(path):
    with Path(path).open(encoding='utf-8') as source:
        for line in source:
            yield json.loads(line)


def command_evidence(command, path, *, stream_progress=False, prior=None):
    if prior is not None:
        previous = read_json(prior)
        require(previous['command'] == command and previous['exitCode'] == 0
                and previous['logSha256'] == digest(prior.with_suffix('.log')), 'prior-validation-mismatch')
        with prior.with_suffix('.log').open('rb') as source, path.with_suffix('.log').open('xb') as target:
            shutil.copyfileobj(source, target)
        write_new_json(path, dict(previous, reusedEvidenceSha256=digest(prior)))
        return
    # Full command output stays private, including failures from isolated test.
    with path.with_suffix('.log').open('x', encoding='utf-8') as log:
        process = subprocess.Popen(command, cwd=REPO, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding='utf-8', errors='replace')
        write_new_json(path.with_name(path.stem + '-launch.json'),
                       dict(pid=process.pid, startedUtc=datetime.now().astimezone().isoformat(), command=command))
        for line in process.stdout:
            log.write(line)
            log.flush()
            if stream_progress and line.startswith('Stage6 source '):
                print(line.strip(), flush=True)
        exit_code = process.wait()
    write_new_json(path, dict(command=command, exitCode=exit_code, logSha256=digest(path.with_suffix('.log'))))
    require(exit_code == 0, 'command-failed-see-private-evidence')


def export_sources(args, store, baseline, run):
    orchestration_bindings = [binding(store, REPO / name) for name in
                              ('tools/prepare_situation_corpus.py', 'tools/situation_evidence.py', 'tools/situation_stage6_parallel.py',
                               'situation-implementation/stage6-goals-handoff/frozen-contract.json')]
    build = read_json(args.build_evidence)
    require(build['exitCode'] == 0, 'build-not-passed')
    verify_build(build['sourceBindings'] + build['binaryBindings'])
    closeout = store.report(binding(store, args.stage4_report))
    require(closeout['status'] == 'passed', 'stage4-not-closed')
    base = safe_child(store.storage, baseline['base_dataset'])
    review = safe_child(store.storage, baseline['frozen_review'])
    # Existing frozen validator uses repository-bound producer snapshots. Require
    # every original byte to match the independently verified private copy.
    for current, relative in [(base, baseline['base_dataset']), (review, baseline['frozen_review'])]:
        original = safe_child(REPO, relative)
        require(digest(original / 'manifest.json') == digest(current / 'manifest.json'), 'original-manifest-drift')
        for entry in read_json(current / 'manifest.json')['files']:
            require(digest(safe_child(original, entry['path'])) == entry['sha256'], 'original-frozen-file-drift')
    test_dll = REPO / 'tests/CsDemoMap.Api.Tests/bin/Release/net10.0/CsDemoMap.Api.Tests.dll'
    cli_dll = REPO / 'apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll'
    prior_run = safe_child(store.storage, 'runs/' + args.reuse_validation_run_id) if args.reuse_validation_run_id else None
    for flag in ('--verify-situation-stage6-source', '--verify-situation-raycast'):
        command_evidence(['dotnet', str(test_dll), flag], run / (flag[2:] + '.json'),
                         prior=prior_run / (flag[2:] + '.json') if prior_run else None)
        print(json.dumps(dict(checkId=flag[2:], status='passed')), flush=True)
    command_evidence(['dotnet', str(cli_dll), '--verify-situation-review', str(REPO / baseline['base_dataset']),
                      str(REPO / baseline['frozen_review']), '--close-exposure-candidates',
                      str(REPO / 'datasets/situation-stage4-review-close-exposure-candidates-20260927-r1')],
                     run / 'frozen-review-verify.json', prior=prior_run / 'frozen-review-verify.json' if prior_run else None)
    print(json.dumps(dict(checkId='actual-frozen-review', status='passed')), flush=True)
    if args.export_workers == 3:
        from situation_stage6_parallel import export_parallel
        export_parallel(store, run, baseline, cli_dll, base, command_evidence, binding,
                        lambda: verify_build(build['sourceBindings'] + build['binaryBindings']), REPO, PURPOSE)
    for split, purpose in (PURPOSE.items() if args.export_workers == 1 else []):
        verify_build(build['sourceBindings'] + build['binaryBindings'])
        output = run / ('source-' + split)
        command_evidence(['dotnet', str(cli_dll), '--export-situation-stage6-source', str(REPO / 'data/mirage'),
                          str(REPO / baseline['split_file']), str(base), str(output), split, purpose],
                         run / ('export-' + split + '.json'), stream_progress=True)
        verify_build(build['sourceBindings'] + build['binaryBindings'])
        print(json.dumps(dict(split=split, status='complete', manifestSha256=digest(output / 'manifest.json'))), flush=True)
    for b in orchestration_bindings: store.verify(b)
    plan_bindings = [binding(store, run / 'parallel-plan.json', scope='isolated-test')] if args.export_workers == 3 else []
    write_new_json(run / 'source-export-complete.json', dict(status='complete', evidenceBindings=orchestration_bindings + plan_bindings + [
        binding(store, run / ('source-' + s) / 'manifest.json', split=s, purpose=PURPOSE[s]) for s in PURPOSE]))


def canonical_property_text(text, name):
    """Extract an already canonical .NET nested value without re-encoding it."""
    decoder, i = json.JSONDecoder(), 1
    while True:
        while text[i].isspace() or text[i] == ',': i += 1
        key, i = decoder.raw_decode(text, i)
        while text[i].isspace(): i += 1
        require(text[i] == ':', 'json-property-invalid')
        i += 1
        while text[i].isspace(): i += 1
        start = i
        _, i = decoder.raw_decode(text, i)
        if key == name: return text[start:i]
        while text[i].isspace(): i += 1
        require(text[i] != '}', 'json-property-missing')


def prepare(args, store, baseline, run):
    from situation_stage6_corpus import (object_sha256, bind_record_hashes, partition_review_pairs,
                                        load_review_partition, prepare_paired_corpus)
    completion = read_json(run / 'source-export-complete.json')
    require(completion['status'] == 'complete', 'export-incomplete')
    for b in completion['evidenceBindings']: store.verify(b)
    require(args.stage4_report and args.stage4_report_sha256, 'pinned-stage4-report-required')
    stage4_binding = binding(store, args.stage4_report)
    require(stage4_binding['sha256'] == args.stage4_report_sha256, 'stage4-report-drift')
    closeout = store.report(stage4_binding)
    require(closeout['status'] == 'passed', 'stage4-not-closed')
    approved = {b['logicalPath']: b['sha256'] for b in closeout['evidenceBindings']}
    require(read_json(run / 'frozen-review-verify.json')['exitCode'] == 0, 'frozen-review-not-verified')
    base, review = [safe_child(store.storage, baseline[k]) for k in ('base_dataset', 'frozen_review')]
    evidence = [stage4_binding, binding(store, CONTRACT_PATH), binding(store, run / 'frozen-review-verify.json'),
                binding(store, run / 'source-export-complete.json')] + completion['evidenceBindings']
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
        folder = run / ('source-' + split)
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
    for name in ('tools/prepare_situation_corpus.py', 'tools/situation_evidence.py', 'tools/situation_stage6_parallel.py', 'tools/situation_stage6_corpus.py', 'tools/situation_stage6_weights.py'):
        evidence.append(binding(store, REPO / name, kind='source'))
    evidence.append(binding(store, REPO / baseline['split_file']))
    # Deduplicate exact bindings only, retaining isolation flags.
    evidence = list({(b['logicalPath'], b['sha256']): b for b in evidence}.values())
    candidates = {}
    with (review / 'review-inputs.jsonl').open(encoding='utf-8') as source:
        for line in source:
            candidate = json.loads(line)
            require(candidate['sampleId'] not in candidates, 'duplicate-review-input')
            raw = canonical_property_text(line, 'candidate')
            require(hashlib.sha256(raw.encode('utf-8')).hexdigest() == candidate['candidateSha256'], 'original-candidate-hash-drift')
            candidates[candidate['sampleId']] = candidate
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
                require(hashlib.sha256(line.rstrip('\r\n').encode('utf-8')).hexdigest() == c['recordSha256'], 'original-review-record-hash-drift')
                require(c['split'] == split and c['matchRef'] == row['metadata']['matchRef'] and c['roundRef'] == row['metadata']['roundRef']
                        and c['input']['scene'] == row['input']['scene'], 'review-input-lineage-mismatch')
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
            yield from json_lines(run / ('source-' + split) / (split + '.jsonl'))
    corpus_root = safe_child(store.storage, 'datasets/' + args.output_id)
    contract['sourceBindings'] = evidence + review_bindings
    contract['outputLogicalRoot'] = 'storage:datasets/' + args.output_id
    report = prepare_paired_corpus(source_rows(), pairs, contract, corpus_root)
    differences = {}
    for split in PURPOSE:
        old = legacy_stats[split]
        stages = {}
        for name, path in [('v8Source', run / ('source-' + split) / (split + '.jsonl')), ('paired', corpus_root / split / 'records.jsonl')]:
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


def finish_after_export(args, store, baseline, run):
    """Resume this authorized pipeline once the exact source process exits.

    This one-shot supervisor persists across desktop conversation reconnections;
    it never restarts an exporter or treats a wait timeout as process failure.
    """
    import psutil
    require(args.output_id and args.budget_run_id and args.stage4_report_sha256, 'finish-arguments-required')
    names = ['tools/prepare_situation_corpus.py', 'tools/situation_evidence.py', 'tools/situation_stage6_parallel.py', 'tools/situation_stage6_corpus.py', 'tools/situation_stage6_weights.py',
        'tools/measure_situation_stage6_preparation.py', 'tools/situation_stage6_budget.py', 'tools/situation_stage6_readiness.py',
        'tools/validate_situation_stage6_preparation.py', 'tools/measure_situation_tokens.py', 'tools/situation_lossless_table.py',
        'situation-implementation/stage6-goals-handoff/frozen-contract.json',
        'situation-implementation/stage6-goals-handoff/runtime-config-draft.json',
        'situation-implementation/situation-stage6-paired-record-v1.schema.json',
        'situation-implementation/situation-facts-prompt-v1.json']
    sources = [binding(store, REPO / name) for name in names]
    require(digest(args.stage4_report) == args.stage4_report_sha256, 'stage4-report-drift')
    launch = read_json(run / 'launch.json')
    write_new_json(run / 'finish-started.json', dict(status='waiting-source', sourcePid=launch['pid'],
        outputId=args.output_id, budgetRunId=args.budget_run_id, sourceBindings=sources,
        stage4ReportSha256=args.stage4_report_sha256))
    try:
        process = psutil.Process(launch['pid'])
        require(args.run_id in process.cmdline() and
                abs(process.create_time() - datetime.fromisoformat(launch['startedUtc'].replace('Z', '+00:00')).timestamp()) < 5,
                'source-process-identity-mismatch')
        print(json.dumps(dict(status='waiting-source', pid=process.pid)), flush=True)
        while True:
            try:
                code = process.wait(timeout=30)
                require(code in (None, 0), 'source-process-failed')
                break
            except psutil.TimeoutExpired:
                continue
    except psutil.NoSuchProcess:
        # The completion marker and its bound manifests must independently prove
        # successful exit. No process or artifact is ever restarted here.
        pass
    completion = read_json(run / 'source-export-complete.json')
    require(completion['status'] == 'complete', 'source-export-not-complete')
    for b in completion['evidenceBindings'] + sources: store.verify(b)
    print(json.dumps(dict(status='source-complete-starting-pairing')), flush=True)
    command_evidence([sys.executable, '-B', str(Path(__file__).resolve()), 'prepare', '--run-id', args.run_id,
        '--output-id', args.output_id, '--stage4-report', args.stage4_report,
        '--stage4-report-sha256', args.stage4_report_sha256], run / 'prepare-command.json')
    paired = read_json(run / 'paired-complete.json')
    require(paired['status'] == 'passed', 'pairing-not-passed')
    store.verify(paired['corpus'])
    for b in sources: store.verify(b)
    print(json.dumps(dict(status='paired-complete-starting-budget')), flush=True)
    command_evidence([sys.executable, '-B', str(REPO / 'tools/measure_situation_stage6_preparation.py'),
        '--run-id', args.budget_run_id, '--corpus-report', paired['corpus']['logicalPath'],
        '--corpus-sha256', paired['corpus']['sha256']], run / 'budget-command.json')
    budget_done = read_json(safe_child(store.storage, 'reports/' + args.budget_run_id + '/complete.json'))
    for b in [paired['corpus'], budget_done['budget'], budget_done['runtime']] + sources: store.verify(b)
    write_new_json(run / 'finish-complete.json', dict(status='passed', corpus=paired['corpus'],
        budget=budget_done['budget'], runtime=budget_done['runtime'], trainingExecutionReady=False))
    print(json.dumps(dict(status='preparation-artifacts-complete-cli-audit-pending')), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('export', 'prepare', 'finish'))
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--build-evidence')
    parser.add_argument('--stage4-report')
    parser.add_argument('--stage4-report-sha256')
    parser.add_argument('--output-id')
    parser.add_argument('--reuse-validation-run-id')
    parser.add_argument('--budget-run-id')
    parser.add_argument('--export-workers', type=int, choices=(1, 3), default=1)
    args = parser.parse_args()
    config = read_json(CONFIG)
    store = EvidenceStore(REPO, storage_root(config['storageRoot']), isolated=True)
    run = safe_child(store.storage, 'runs/' + args.run_id)
    require(run.is_dir(), 'new-run-directory-missing')
    baseline = read_json(CONTRACT_PATH)['baseline']
    try:
        if args.mode == 'export': export_sources(args, store, baseline, run)
        elif args.mode == 'finish': finish_after_export(args, store, baseline, run)
        else:
            require(args.output_id, 'new-output-id-required')
            prepare(args, store, baseline, run)
        return 0
    except Exception:
        path = run / (args.mode + '-failure.json')
        if not path.exists(): write_new_json(path, dict(status='failed', traceback=traceback.format_exc()))
        print(json.dumps(dict(status='failed', reason='see-private-' + args.mode + '-failure')), flush=True)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
