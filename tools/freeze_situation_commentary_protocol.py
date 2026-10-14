"""Freeze step 4 input-only evidence. Never loads model weights or emits labels."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import traceback

from measure_situation_core_context import verify_admission, verify_all
from situation_tokenizer import tokenizer
from situation_token_measurement import distribution
from situation_evidence import binding
from situation_commentary_protocol import (configs, features, select_diagnostics, render_groups,
    repair_prompt, stable_key, GROUPS, PROMPTS, RULES, PROTOCOL, PLAN_SCHEMA)
from situation_core_budget import stream_partition
from situation_core_context import require
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from situation_lossless_table import canonical
from situation_stage6_corpus import object_sha256
from validate_situation_stage6_preparation import EvidenceStore, DEFAULT_BUNDLE


def write_line(stream, value):
    stream.write(canonical(value) + '\n')


def coverage(selected):
    return {s: dict(rows=sum(r['split'] == s for r in selected),
                    matches=len({r['matchRef'] for r in selected if r['split'] == s}),
                    reviewed=sum(r['split'] == s and r['reviewed'] for r in selected),
                    categories=dict(Counter(c for r in selected if r['split'] == s for c in r['categories'])))
            for s in ('train', 'dev')}


def freeze(args, store):
    require(args.run_id and Path(args.run_id).name == args.run_id, 'simple-new-run-id-required')
    out = safe_child(store.storage, 'reports/' + args.run_id)
    require(not out.exists(), 'output-already-exists')
    require(shutil.disk_usage(store.storage).free >= 1024**3, 'report-storage-reserve-unavailable')
    out.mkdir(parents=True, exist_ok=False)
    args.created_output = out
    write_new_json(out / 'started.json', dict(status='incomplete', startedUtc=datetime.now(timezone.utc).isoformat(),
        modelWeightsLoaded=False, modelGenerationExecuted=False, optimizerUpdates=0))
    prompt, rules, protocol = configs()
    core = read_json(REPO / 'situation-implementation/situation-core-input-admission-v1.json')['artifact']
    verify_admission(store, core)
    corpus_binding = read_json(REPO / DEFAULT_BUNDLE)['artifacts']['corpus']
    report = store.report(corpus_binding)
    require(report['status'] == 'passed' and report['details']['complete'] and report['details']['frozen'], 'corpus-incomplete')
    corpus = store.path(corpus_binding['logicalPath']).parent
    prefix = corpus_binding['logicalPath'].rsplit('/', 1)[0] + '/'
    def pinned(relative):
        return next(b for b in report['evidenceBindings'] if b['logicalPath'] == prefix + relative)
    parts = {s: pinned(s + '/manifest.json') for s in ('train', 'dev')}
    corpus_contract = read_json(store.path(pinned('contract.json')['logicalPath']))
    sources = [PROMPTS, RULES, PROTOCOL, PLAN_SCHEMA,
        'tools/situation_commentary_protocol.py', 'tools/freeze_situation_commentary_protocol.py', 'tools/situation_evidence.py', 'tools/situation_token_measurement.py', 'tools/situation_tokenizer.py',
        'tools/test_situation_commentary_protocol.py',
        'docs/situation-commentary-evaluation-protocol-v1.md',
        'apps/api/Features/Situation/Analysis/SituationTemplateNarrator.cs',
        'apps/api/Features/Situation/Analysis/Rules/situation-analysis-rules-v8-close-exposure-1.json']
    evidence = [core, corpus_binding, pinned('contract.json')]
    evidence += [binding(store, REPO / p) for p in sources + rules['sourceDocuments']]
    evidence += [pinned(s + '/' + f) for s in parts for f in ('manifest.json', 'records.jsonl')]
    adapter, tokenizer_bindings = tokenizer(store, full_verification=True)
    evidence += tokenizer_bindings
    # Bind step3A's own dependencies transitively; do not rewrite its frozen gate.
    core_report = read_json(store.path(core['logicalPath']))
    evidence += core_report['evidenceBindings']
    evidence = list({b['logicalPath']: b for b in evidence}.values())
    verify_all(store, evidence)
    def records():
        for split, purpose in [('train', 'sft'), ('dev', 'prompt-selection')]:
            yield from stream_partition(corpus / split, split, purpose, parts[split]['sha256'])
    candidates = []
    for record, witness in records():
        f = features(record)
        require(f['matchRef'] in corpus_contract['splitMembers'][f['split']], 'match-membership')
        candidates.append(dict(f, witness=witness))
    require(Counter(r['split'] for r in candidates) == {s: report['details']['counts'][s] for s in parts}, 'source-count')
    inventory = {s: dict(rows=sum(r['split'] == s for r in candidates),
        categories=dict(Counter(c for r in candidates if r['split'] == s for c in r['categories'])),
        categoryRounds={c: len({(r['matchRef'], r['roundRef']) for r in candidates
                               if r['split'] == s and c in r['categories']})
                        for c in protocol['diagnostics']['categories']}) for s in parts}
    write_new_json(out / 'selection-inventory.json', inventory)
    selected = select_diagnostics(candidates)
    write_new_json(out / 'selection.json', dict(schemaVersion='commentary-diagnostics-v1',
        inputOnly=True, noTest=True, selectionPolicy=protocol['diagnostics'], coverage=coverage(selected), samples=selected))
    print(json.dumps(dict(selection=coverage(selected)), ensure_ascii=False), flush=True)
    # Snapshot exact configs before rendering; publication never overwrites.
    for name, value in [('protocol.json', protocol), ('prompts.json', prompt), ('rules.json', rules)]:
        write_new_json(out / name, value)
    wanted = {r['sampleId']: r for r in selected}
    values = {(s, g): [] for s in parts for g in GROUPS}
    diagnostic_values = {(s, g): [] for s in parts for g in GROUPS}
    failures = []; counts = Counter(); selected_count = 0
    # Every full input and every group is represented, including failures.
    with (out / 'budget-rows.jsonl').open('x', encoding='utf-8', newline='\n') as audit, \
         (out / 'diagnostic-inputs.jsonl').open('x', encoding='utf-8', newline='\n') as inputs, \
         (out / 'rendered-prompts.jsonl').open('x', encoding='utf-8', newline='\n') as prompts:
        for record, witness in records():
            sample, split = record['sampleId'], record['metadata']['split']
            counts[split] += 1
            try:
                rendered, provenance = render_groups(record, witness)
                if sample in wanted:
                    require(features(record)['inputSha256'] == wanted[sample]['inputSha256'], 'selection-input-drift')
                    # Deliberately omit all original output/label text from diagnostics.
                    write_line(inputs, dict(sampleId=sample, split=split, input=record['input'], provenance=provenance))
                    selected_count += 1
                for group, entry in rendered.items():
                    measured = adapter.count_prompt(entry['system'], entry['user'])
                    tokens = measured['inferencePromptTokens']
                    result = dict(sampleId=sample, split=split, group=group, **measured,
                        generationTokensWithReserve=tokens + 512, status='passed' if tokens <= 4608 else 'failed',
                        payloadSha256=provenance['payloadSha256'], sourceWitness=witness)
                    values[split, group].append(tokens)
                    if tokens > 4608: failures.append(dict(sampleId=sample, group=group, reason='input-over-budget'))
                    if sample in wanted:
                        diagnostic_values[split, group].append(tokens)
                        write_line(prompts, dict(sampleId=sample, split=split, group=group, **entry, **measured))
                    write_line(audit, result)
            except Exception as exc:
                failure = dict(sampleId=sample, split=split, status='failed', reason=type(exc).__name__ + ':' + str(exc))
                failures.append(failure); write_line(audit, failure)
            if sum(counts.values()) % 1000 == 0:
                print(json.dumps(dict(measured=dict(counts), failures=len(failures))), flush=True)
    require(dict(counts) == {s: report['details']['counts'][s] for s in parts} and selected_count == 50, 'budget-scope-count')
    def distributions(v):
        return {s: {g: distribution(v[s, g]) for g in GROUPS} for s in parts}
    repair_cases = []
    # 512 single-token characters at the guard boundary, plus malformed JSON.
    # This is stress evidence, not a universal bound on arbitrary future output.
    for name, raw in [('single-token-boundary', ' a' * 512), ('malformed-json', '{"summaryZh":"局势尚不明确。",')]:
        rendered = repair_prompt(raw, adapter)
        if name == 'single-token-boundary':
            require(len(adapter.tokenizer.encode(raw, add_special_tokens=False)) == 512, 'repair-stress-not-boundary')
        repair_cases.append(dict(name=name, sourceOutputTokens=len(adapter.tokenizer.encode(raw, add_special_tokens=False)),
                                 rawSha256=object_sha256(raw), **rendered))
    budget = dict(schemaVersion='commentary-budget-v1', status='failed' if failures else 'passed',
        counts=dict(counts), groups=list(GROUPS), groupInputCount=sum(len(v) for v in values.values()),
        diagnosticInputCount=sum(len(v) for v in diagnostic_values.values()),
        failures=failures, failureCount=len(failures), distributions=distributions(values),
        diagnosticDistributions=distributions(diagnostic_values), repairCases=repair_cases,
        futureRepairRuntimeGuard=True, futureNewLabels='pending: full assistant/EOS <=5120, completion <=512',
        maxSequenceLength=5120, generationReserveTokens=512, truncation=False,
        tokenizer=adapter.metadata, modelWeightsLoaded=False, modelGenerationExecuted=False, optimizerUpdates=0)
    write_new_json(out / 'budget.json', budget)
    # All four group records and three pairwise comparisons are pending, never approved.
    blind, alias_map, pairs = [], [], []
    for row in selected:
        sample = row['sampleId']
        order = sorted(GROUPS, key=lambda g: stable_key(sample + '|' + g, 'blind'))
        alias_by_group = {}
        for i, group in enumerate(order):
            alias = stable_key(sample + '|' + group, 'alias')[:20]
            alias_by_group[group] = alias
            alias_map.append(dict(sampleId=sample, alias=alias, group=group, displayOrder=i + 1))
            blind.append(dict(sampleId=sample, alias=alias, displayOrder=i + 1, status='pending',
                output=None, scores={k: None for k in protocol['rubric']['scores']},
                judgments={k: None for k in protocol['rubric']['separateJudgments']},
                reason=None, claimSupport=[], reviewer=None, reviewedUtc=None))
        for group in ('B', 'C', 'D'):
            left, right = sorted((alias_by_group['A'], alias_by_group[group]), key=lambda a: stable_key(a, 'pair'))
            pairs.append(dict(sampleId=sample, left=left, right=right, preference=None, reason=None, status='pending'))
    write_new_json(out / 'blind-review-template.json', dict(status='pending', rubric=protocol['rubric'], rows=blind, pairs=pairs))
    write_new_json(out / 'private-alias-map.json', dict(scope='review-coordinator-only', rows=alias_map))
    verify_all(store, evidence)
    if failures:
        print(json.dumps(dict(status='failed', failureCount=len(failures))))
        return 2
    output_names = ['selection-inventory.json', 'selection.json', 'protocol.json', 'prompts.json', 'rules.json', 'budget-rows.jsonl',
                    'diagnostic-inputs.jsonl', 'rendered-prompts.jsonl', 'budget.json',
                    'blind-review-template.json', 'private-alias-map.json']
    admission = dict(schemaVersion='situation-commentary-protocol-admission-v1', status='passed',
        protocolReady=True, generationExecutionReady=False, trainingExecutionReady=False,
        B2='unselected', humanReviews='pending-step5', modelGenerationExecuted=False, optimizerUpdates=0,
        evidenceBindings=evidence, artifacts={p: binding(store, out / p) for p in output_names},
        completedUtc=datetime.now(timezone.utc).isoformat())
    write_new_json(out / 'admission.json', admission)
    artifact = binding(store, out / 'admission.json')
    write_new_json(out / 'complete.json', dict(status='complete', admission=artifact))
    print(json.dumps(dict(status='passed', protocolReady=True, admission=artifact,
                         distributions=budget['distributions'], repair=[r['inferencePromptTokens'] for r in repair_cases])))
    return 0


def verify(store, artifact):
    store.verify(artifact)
    report = read_json(store.path(artifact['logicalPath']))
    require(report['schemaVersion'] == 'situation-commentary-protocol-admission-v1'
            and report['status'] == 'passed' and report['protocolReady'] is True, 'protocol-not-ready')
    require(report['generationExecutionReady'] is False and report['trainingExecutionReady'] is False
            and report['modelGenerationExecuted'] is False and report['optimizerUpdates'] == 0, 'protocol-scope-invalid')
    verify_all(store, report['evidenceBindings'])
    verify_all(store, report['artifacts'].values())
    marker = read_json(store.path(artifact['logicalPath']).parent / 'complete.json')
    require(marker == dict(status='complete', admission=artifact), 'protocol-completion-invalid')
    budget = read_json(store.path(report['artifacts']['budget.json']['logicalPath']))
    require(budget['status'] == 'passed' and budget['failureCount'] == 0
            and budget['diagnosticInputCount'] == 200
            and budget['groupInputCount'] == 4 * sum(budget['counts'].values()), 'protocol-budget-invalid')
    require(all(d['max'] <= 4608 for groups in budget['distributions'].values() for d in groups.values()), 'protocol-over-budget')
    return dict(status='passed', protocolReady=True, generationExecutionReady=False, trainingExecutionReady=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id'); parser.add_argument('--verify'); parser.add_argument('--sha256')
    args = parser.parse_args()
    store = EvidenceStore(REPO, storage_root(read_json(CONFIG)['storageRoot']))
    os.environ['TOKENIZERS_PARALLELISM'] = 'false'
    try:
        if args.verify:
            print(json.dumps(verify(store, dict(logicalPath=args.verify, sha256=args.sha256))))
            return 0
        return freeze(args, store)
    except Exception:
        out = getattr(args, 'created_output', None)
        if out and not (out / 'failure.json').exists():
            write_new_json(out / 'failure.json', dict(status='failed', traceback=traceback.format_exc()))
        print(json.dumps(dict(status='failed', reason='see-private-failure-evidence')))
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
