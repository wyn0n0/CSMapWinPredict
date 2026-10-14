"""Step 3A: freeze and measure all core inputs with an isolated test receipt.

Only the fixed offline tokenizer is loaded. New immutable reports live in the
private storage root. The legacy 2560 admission bundle is never modified.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import traceback

from situation_tokenizer import tokenizer, consume_test_receipt
from situation_evidence import binding
from situation_core_budget import measure_core_budget, stream_partition
from situation_core_context import CONFIG_PATH, load_config
from situation_environment import CONFIG, REPO, read_json, safe_child, storage_root, write_new_json, digest
from validate_situation_stage6_preparation import EvidenceStore, require, DEFAULT_BUNDLE


def verify_all(store, bindings):
    for item in bindings:
        store.verify(item)


def child(args, store):
    invocation = read_json(args.isolated_test)
    verify_all(store, invocation['evidenceBindings'])
    store.verify(invocation['candidateConfig'])
    candidate = read_json(store.path(invocation['candidateConfig']['logicalPath']))
    config = load_config(store.path(candidate['config']['logicalPath']))
    require(candidate['status'] == 'frozen-before-test'
            and candidate['config'] in invocation['evidenceBindings'], 'candidate-policy-invalid')
    require(invocation['contract']['scope'] == 'isolated-test', 'test-scope-required')
    adapter, _ = tokenizer(store, full_verification=False)
    part = invocation['partition']
    records = stream_partition(store.path(part['logicalPath']).parent, 'test', 'final-evaluation',
                               part['sha256'], isolated=True)
    report = measure_core_budget(records, adapter, config, invocation['contract'])
    verify_all(store, invocation['evidenceBindings'])
    output = store.path(invocation['output'])
    write_new_json(output, report)
    print(json.dumps(dict(status=report['status'], sha256=digest(output))))
    return 0 if report['status'] == 'passed' else 2


def measure(args, store):
    require(args.run_id and Path(args.run_id).name == args.run_id, 'simple-new-run-id-required')
    out = safe_child(store.storage, 'reports/' + args.run_id)
    require(not out.exists(), 'output-already-exists')
    require(shutil.disk_usage(store.storage).free >= 512 * 1024**2, 'report-storage-reserve-unavailable')
    out.mkdir(parents=True, exist_ok=False)
    args.created_output = out
    write_new_json(out / 'started.json', dict(status='incomplete', startedUtc=datetime.now(timezone.utc).isoformat(),
        modelWeightsLoaded=False, modelGeneration=False, optimizerUpdates=0))
    config = load_config()
    old_bundle = read_json(REPO / DEFAULT_BUNDLE)
    corpus_binding = old_bundle['artifacts']['corpus']
    corpus_report = store.report(corpus_binding)
    require(corpus_report['status'] == 'passed' and corpus_report['details']['complete'] is True
            and corpus_report['details']['frozen'] is True, 'corpus-not-frozen')
    corpus = store.path(corpus_binding['logicalPath']).parent
    prefix = 'storage:' + corpus.relative_to(store.storage).as_posix() + '/'
    def pinned(relative):
        return next(b for b in corpus_report['evidenceBindings'] if b['logicalPath'] == prefix + relative)
    partitions = {s: pinned(s + '/manifest.json') for s in ('train', 'dev', 'test')}
    contract_binding = pinned('contract.json')
    store.verify(contract_binding)
    corpus_contract = read_json(store.path(contract_binding['logicalPath']))
    config_binding = binding(store, CONFIG_PATH)
    evidence = [corpus_binding, contract_binding, config_binding]
    sources = [
        'tools/situation_core_context.py', 'tools/situation_core_budget.py', 'tools/measure_situation_core_context.py', 'tools/situation_evidence.py', 'tools/situation_token_measurement.py', 'tools/situation_tokenizer.py',
        'tools/test_situation_core_context.py', 'tools/test_situation_core_budget.py',
        'tools/situation_environment.py', 'tools/situation_facts_prompt.py', 'tools/situation_lossless_table.py',
        'tools/situation_stage6_corpus.py', 'tools/situation_stage6_budget.py', 'tools/measure_situation_tokens.py',
        'tools/measure_situation_stage6_preparation.py', 'tools/prepare_situation_base.py',
        'tools/prepare_situation_corpus.py', 'tools/validate_situation_stage6_preparation.py',
        'situation-implementation/situation-stage6-paired-record-v1.schema.json',
        'requirements-situation-win-cu126.lock.txt', 'requirements-situation-win-cu126-qlora.lock.txt']
    evidence += [binding(store, REPO / p) for p in sources]
    evidence += [binding(store, REPO / b['path']) for b in config['schemaBindings']]
    adapter, token_bindings = tokenizer(store, full_verification=True)
    evidence += token_bindings
    # Only the manifest identity crosses from test; its content is child-only.
    train_dev_bindings = [pinned(s + '/' + f) for s in ('train', 'dev') for f in ('manifest.json', 'records.jsonl')]
    evidence += train_dev_bindings
    verify_all(store, evidence)
    purposes = dict(train='sft', dev='prompt-selection')
    policy = dict(scope='train-dev', expectedCounts={s: corpus_report['details']['counts'][s] for s in purposes},
                  purposeBySplit=purposes, chatTemplateSha256=adapter.metadata['chatTemplateSha256'],
                  splitMembers={s: corpus_contract['splitMembers'][s] for s in purposes}, evidenceBindings=evidence)
    def records():
        for split, purpose in purposes.items():
            yield from stream_partition(corpus / split, split, purpose, partitions[split]['sha256'])
    progress = lambda split, count: print(json.dumps(dict(split=split, recordsMeasured=count)), flush=True)
    budget = measure_core_budget(records(), adapter, config, policy, progress=progress)
    verify_all(store, evidence)
    write_new_json(out / 'train-dev-budget.json', budget)
    if budget['status'] != 'passed':
        print(json.dumps(dict(status='failed', reasonCodes=budget['reasonCodes'], overBudgetCount=budget['overBudgetCount'])))
        return 2
    candidate = dict(status='frozen-before-test', config=config_binding,
                     trainDevBudget=binding(store, out / 'train-dev-budget.json'), evidenceBindings=evidence,
                     generationExecutionReady=False, trainingExecutionReady=False)
    write_new_json(out / 'candidate-config-frozen-before-test.json', candidate)
    test_bindings = [b for b in evidence if b not in train_dev_bindings]
    test_bindings += [pinned('test/' + f) for f in ('manifest.json', 'records.jsonl')]
    test_policy = dict(scope='isolated-test', expectedCounts=dict(test=corpus_report['details']['counts']['test']),
                       purposeBySplit=dict(test='final-evaluation'), chatTemplateSha256=adapter.metadata['chatTemplateSha256'],
                       splitMembers=dict(test=corpus_contract['splitMembers']['test']), evidenceBindings=test_bindings)
    invocation = dict(contract=test_policy, partition=partitions['test'], evidenceBindings=test_bindings,
                      candidateConfig=binding(store, out / 'candidate-config-frozen-before-test.json'),
                      output='storage:' + (out / 'isolated-test-budget.json').relative_to(store.storage).as_posix())
    write_new_json(out / 'isolated-test-invocation.json', invocation)
    process = subprocess.run([sys.executable, '-B', str(Path(__file__).resolve()), '--isolated-test',
                              str(out / 'isolated-test-invocation.json')], cwd=REPO, capture_output=True,
                             text=True, encoding='utf-8')
    receipt, test_binding = consume_test_receipt(process, store, invocation['output'])
    verify_all(store, evidence)
    passed = receipt['status'] == 'passed' and test_binding is not None
    admission = dict(schemaVersion='situation-core-input-admission-v1', status='passed' if passed else 'failed',
                     baseInputReady=passed, generationExecutionReady=False, trainingExecutionReady=False,
                     config=config_binding, trainDevBudget=binding(store, out / 'train-dev-budget.json'),
                     isolatedTestReceipt=receipt, isolatedTestBudget=test_binding,
                     candidateConfig=invocation['candidateConfig'],
                     evidenceBindings=evidence + [invocation['candidateConfig'],
                         binding(store, out / 'isolated-test-invocation.json', scope='isolated-test')],
                     pending=config['pending'], completedUtc=datetime.now(timezone.utc).isoformat())
    write_new_json(out / 'admission.json', admission)
    if passed:
        write_new_json(out / 'complete.json', dict(status='complete', admission=binding(store, out / 'admission.json')))
    print(json.dumps(dict(status=admission['status'], baseInputReady=passed,
                         generationExecutionReady=False, trainingExecutionReady=False,
                         admission=binding(store, out / 'admission.json'))))
    return 0 if passed else 2


def verify_admission(store, artifact):
    store.verify(artifact)
    report = read_json(store.path(artifact['logicalPath']))
    require(report['schemaVersion'] == 'situation-core-input-admission-v1' and report['status'] == 'passed'
            and report['baseInputReady'] is True and report['generationExecutionReady'] is False
            and report['trainingExecutionReady'] is False, 'core-admission-not-passed')
    verify_all(store, report['evidenceBindings'])
    store.verify(report['config'])
    config = load_config(store.path(report['config']['logicalPath']))
    store.verify(report['trainDevBudget'])
    budget = read_json(store.path(report['trainDevBudget']['logicalPath']))
    require(budget['status'] == 'passed' and budget['failureCount'] == 0 and budget['overBudgetCount'] == 0
            and budget['representationVersion'] == config['representationVersion'], 'core-budget-not-passed')
    verify_all(store, budget['evidenceBindings'])
    require(report['isolatedTestReceipt']['status'] == 'passed'
            and report['isolatedTestBudget']['sha256'] == report['isolatedTestReceipt']['sha256'], 'test-receipt-invalid')
    store.verify(report['isolatedTestBudget'])
    marker = read_json(store.path(artifact['logicalPath']).parent / 'complete.json')
    require(marker['status'] == 'complete' and marker['admission'] == artifact, 'core-completion-invalid')
    return dict(status='passed', baseInputReady=True, generationExecutionReady=False, trainingExecutionReady=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id')
    parser.add_argument('--isolated-test')
    parser.add_argument('--verify', help='Admission logical path; requires --sha256')
    parser.add_argument('--sha256')
    args = parser.parse_args()
    local = read_json(CONFIG)
    store = EvidenceStore(REPO, storage_root(local['storageRoot']), isolated=bool(args.isolated_test))
    os.environ['TOKENIZERS_PARALLELISM'] = 'false'
    try:
        if args.isolated_test:
            return child(args, store)
        if args.verify:
            print(json.dumps(verify_admission(store, dict(logicalPath=args.verify, sha256=args.sha256))))
            return 0
        return measure(args, store)
    except Exception:
        if args.isolated_test:
            path = Path(args.isolated_test).resolve()
            if path.is_relative_to(store.storage) and path.is_file():
                failure = path.with_name('isolated-test-failure.json')
                if not failure.exists():
                    write_new_json(failure, dict(status='failed', traceback=traceback.format_exc()))
            print(json.dumps(dict(status='failed', sha256=None)))
        else:
            out = getattr(args, 'created_output', None)
            if out and not (out / 'failure.json').exists():
                write_new_json(out / 'failure.json', dict(status='failed', traceback=traceback.format_exc()))
            print(json.dumps(dict(status='failed', reason='core-admission-failed-see-private-evidence')))
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
