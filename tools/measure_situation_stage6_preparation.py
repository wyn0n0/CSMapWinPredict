"""One actual train/dev tokenizer pass and a fixed-policy isolated test pass.

Loads only the offline tokenizer. Candidate settings are frozen from train/dev
before spawning the test process. No test detail returns to this parent.
"""
import argparse
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import traceback

from situation_tokenizer import tokenizer, consume_test_receipt
from situation_evidence import binding
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from situation_stage6_budget import measure_paired_budget
from situation_stage6_corpus import load_corpus_partition
from situation_stage6_readiness import estimate_training_budgets, validate_runtime
from validate_situation_stage6_preparation import EvidenceStore, require

DRAFT = REPO / 'situation-implementation/stage6-goals-handoff/runtime-config-draft.json'
PROMPT = REPO / 'situation-implementation/situation-facts-prompt-v1.json'


def pinned_partition(store, corpus, report, split):
    path = corpus / split / 'manifest.json'
    # Derive identity without opening even the test manifest in this parent.
    logical = 'storage:' + path.relative_to(store.storage).as_posix()
    fixed = next(b for b in report['evidenceBindings'] if b['logicalPath'] == logical)
    store.verify(fixed)
    return fixed


def test_child(args, store):
    # The only process in this module that can open test records/details.
    invocation = read_json(args.isolated_test)
    for b in invocation['evidenceBindings']: store.verify(b)
    policy = invocation['contract']
    store.verify(invocation['candidateConfig'])
    config = read_json(store.path(invocation['candidateConfig']['logicalPath']))
    require(not validate_runtime(config), 'candidate-runtime-invalid')
    adapter, _ = tokenizer(store, full_verification=False)
    part = invocation['partition']
    records = load_corpus_partition(store.path(part['logicalPath']).parent, 'test', 'final-evaluation', part['sha256'])
    report = measure_paired_budget(records, adapter, policy)
    for b in invocation['evidenceBindings']: store.verify(b)
    output = store.path(invocation['output'])
    write_new_json(output, report)
    # No examples, counts, distributions, exception text or selection feedback.
    print(json.dumps(dict(status=report['status'], sha256=digest(output))))
    return 0 if report['status'] == 'passed' else 2


def measure(args, store):
    corpus_binding = dict(logicalPath=args.corpus_report, sha256=args.corpus_sha256)
    report = store.report(corpus_binding)
    require(report['status'] == 'passed', 'corpus-not-passed')
    corpus = store.path(args.corpus_report).parent
    out = safe_child(store.storage, 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    args.created_output = out
    adapter, evidence = tokenizer(store, full_verification=True)
    prompt = read_json(PROMPT)
    evidence += [corpus_binding, binding(store, PROMPT), binding(store, DRAFT)]
    for name in (prompt['payloadSchema'], prompt['targetSchema'],
                 'situation-implementation/situation-stage6-paired-record-v1.schema.json',
                 'requirements-situation-win-cu126-qlora.lock.txt', 'requirements-situation-win-cu126.lock.txt',
                 'tools/measure_situation_stage6_preparation.py', 'tools/situation_evidence.py', 'tools/situation_token_measurement.py', 'tools/situation_tokenizer.py', 'tools/situation_stage6_budget.py',
                 'tools/situation_stage6_readiness.py', 'tools/situation_stage6_corpus.py',
                 'tools/situation_stage6_weights.py', 'tools/measure_situation_tokens.py', 'tools/situation_lossless_table.py'):
        evidence.append(binding(store, REPO / name))
    partitions = {s: pinned_partition(store, corpus, report, s) for s in ('train', 'dev', 'test')}
    for b in report['evidenceBindings']:
        if b.get('split') in ('train', 'dev') and b['logicalPath'].startswith('storage:' + corpus.relative_to(store.storage).as_posix() + '/'):
            evidence.append(b)
    write_new_json(out / 'started.json', dict(status='incomplete', evidenceBindings=evidence))
    humans, rounds = set(), set()
    def records():
        for split, purpose in [('train', 'sft'), ('dev', 'checkpoint-selection')]:
            rows = load_corpus_partition(corpus / split, split, purpose, partitions[split]['sha256'])
            for index, row in enumerate(rows, 1):
                if split == 'train':
                    rounds.add((row['metadata']['matchRef'], row['metadata']['roundRef']))
                    if row['labelSource'] in ('human-approved', 'human-modified'): humans.add(row['sampleId'])
                if index % 1000 == 0: print(json.dumps(dict(split=split, recordsVisited=index)), flush=True)
                yield row
    policy = dict(chatTemplateSha256=adapter.metadata['chatTemplateSha256'], maxSequenceLength=2560,
                  generationReserveTokens=512, promptConfig=prompt, evidenceBindings=evidence,
                  purposeBySplit=dict(train='sft', dev='checkpoint-selection'),
                  expectedCounts={s: report['details']['counts'][s] for s in ('train', 'dev')}, isolatedTestStatus='not-tested')
    budget = measure_paired_budget(records(), adapter, policy)
    for b in evidence: store.verify(b)
    write_new_json(out / 'train-dev-budget.json', budget)
    independent_reasons = [r for r in budget['reasonCodes'] if r != 'isolated-test-budget-not-passed']
    if independent_reasons:
        write_new_json(out / 'budget-report.json', budget)
        print(json.dumps(dict(status='failed', reasonCodes=independent_reasons,
                              overBudgetCount=budget['details']['overBudgetCount'], reportSha256=digest(out / 'budget-report.json'))))
        return 2
    require(len(humans) == 36, 'human-train-count-invalid')
    memory_path = safe_child(store.storage, 'reports/stage7-nf4-optimized-2560-20261002-r1/memory-probe.json')
    require(digest(memory_path) == '86f5a8d05759bd4fc20f2d5f07a3827720c2c9a7f31a41642f872ce275654700', 'accepted-adapter-evidence-drift')
    estimates = estimate_training_budgets((r for r in budget['details']['rows'] if r['split'] == 'train'), humans,
                                           original_rounds=len(rounds), adapter_parameters=17432576)
    require(shutil.disk_usage(store.storage).free >= estimates['storageReserveBytes'], 'training-storage-reserve-unavailable')
    config = read_json(DRAFT)
    config.update(status='frozen', selectedRuntime='nf4-rank16', pendingFields=[], expectedBudgets=estimates,
                  corpusReportSha256=args.corpus_sha256,
                  weightNormalizer=dict(policy='global-round-expectation', originalRounds=len(rounds),
                      expandedEntries=estimates['expandedEntriesPerEpoch'], windowFormula='actualMicrosteps * originalRounds / expandedEntries',
                      objective='sum(perCopyRoundWeight * meanAssistantTokenLoss) / fixedWindowNormalizer',
                      epochObjective='sum(perCopyRoundWeight * meanAssistantTokenLoss) / originalRounds',
                      automaticAccumulationDivision=False, tailPolicy='use actual microsteps; retain every occurrence'),
                  evidenceBindings=evidence + [binding(store, out / 'train-dev-budget.json'), binding(store, memory_path),
                      binding(store, REPO / 'situation-implementation/situation-resource-status-v4.json')])
    require(not validate_runtime(config), 'runtime-validation-failed')
    # Candidate frozen before test. On a test failure this remains a rejected
    # candidate and is never installed as the default runtime or admission bundle.
    write_new_json(out / 'candidate-runtime-frozen-before-test.json', config)
    test_policy = dict(policy, scope='isolated-test', purposeBySplit=dict(test='final-evaluation'),
                       expectedCounts=dict(test=report['details']['counts']['test']))
    test_policy.pop('isolatedTestStatus')
    test_evidence = [b for b in evidence if b.get('split') not in ('train', 'dev')]
    test_evidence += [b for b in report['evidenceBindings'] if b.get('split') == 'test'
                      and b['logicalPath'].startswith('storage:' + corpus.relative_to(store.storage).as_posix() + '/')]
    test_policy['evidenceBindings'] = test_evidence
    invocation = dict(contract=test_policy, evidenceBindings=test_evidence, partition=partitions['test'],
                      candidateConfig=binding(store, out / 'candidate-runtime-frozen-before-test.json'),
                      output='storage:' + (out / 'isolated-test-budget.json').relative_to(store.storage).as_posix())
    write_new_json(out / 'isolated-test-invocation.json', invocation)
    process = subprocess.run([sys.executable, '-B', str(Path(__file__).resolve()), '--isolated-test', str(out / 'isolated-test-invocation.json')],
                             cwd=REPO, capture_output=True, text=True, encoding='utf-8')
    # Only the child's deliberately narrow result may cross the process boundary.
    result, test_binding = consume_test_receipt(process, store, invocation['output'])
    budget['evidenceBindings'] += [binding(store, out / 'candidate-runtime-frozen-before-test.json'),
                                   binding(store, out / 'isolated-test-invocation.json', scope='isolated-test')]
    if test_binding is not None:
        budget['evidenceBindings'].append(test_binding)
    budget['details']['isolatedTestStatus'] = result['status']
    if result['status'] == 'passed' and process.returncode == 0:
        budget['reasonCodes'].remove('isolated-test-budget-not-passed')
    budget['status'] = 'passed' if not budget['reasonCodes'] else 'failed'
    budget['details']['verified'] = budget['status'] == 'passed'
    for b in evidence: store.verify(b)
    write_new_json(out / 'budget-report.json', budget)
    if budget['status'] == 'passed':
        config['evidenceBindings'] += [binding(store, out / 'budget-report.json')]
        write_new_json(out / 'runtime.json', config)
        write_new_json(out / 'complete.json', dict(status='complete', budget=binding(store, out / 'budget-report.json'), runtime=binding(store, out / 'runtime.json')))
    print(json.dumps(dict(status=budget['status'], reasonCodes=budget['reasonCodes'], reportSha256=digest(out / 'budget-report.json'))))
    return 0 if budget['status'] == 'passed' else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id')
    parser.add_argument('--corpus-report')
    parser.add_argument('--corpus-sha256')
    parser.add_argument('--isolated-test')
    args = parser.parse_args()
    config = read_json(CONFIG)
    store = EvidenceStore(REPO, storage_root(config['storageRoot']), isolated=bool(args.isolated_test))
    if args.isolated_test:
        try: return test_child(args, store)
        except Exception:
            # The invocation belongs to this new run. Preserve detailed failures
            # inside its isolated directory, never in stdout or the caller.
            invocation_path = Path(args.isolated_test).resolve()
            if invocation_path.is_relative_to(store.storage) and invocation_path.is_file():
                failure = invocation_path.with_name('isolated-test-failure.json')
                if not failure.exists():
                    write_new_json(failure, dict(status='failed', traceback=traceback.format_exc()))
            # No arbitrary exception diagnostics or held-out lengths reach parent.
            print(json.dumps(dict(status='failed', sha256=None)))
            return 2
    require(args.run_id and args.corpus_report and args.corpus_sha256, 'explicit-budget-inputs-required')
    try: return measure(args, store)
    except Exception:
        out = safe_child(store.storage, 'reports/' + args.run_id)
        if getattr(args, 'created_output', None) == out and not (out / 'failure.json').exists():
            write_new_json(out / 'failure.json', dict(status='failed', traceback=traceback.format_exc()))
        print(json.dumps(dict(status='failed', reason='see-private-failure')))
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
