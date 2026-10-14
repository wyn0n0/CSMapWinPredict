"""Stage-five Facts-only budget acceptance: pilot plus every pinned v1 train row."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import sys
import traceback

from situation_token_measurement import count_sequence, distribution, sha_text, summary
from prepare_situation_base import destination, reference, require
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from situation_facts_prompt import render_facts
from situation_lossless_table import canonical


def train_refs(split, version):
    groups = [{m['matchId'] for m in split[k]} for k in ('train', 'dev', 'test')]
    require([len(g) for g in groups] == [71, 8, 8] and len(set.union(*groups)) == 87, 'invalid-split')
    return {'match-' + sha_text('situation-training-match-ref-v1\ndata=' + version + '\nmatch=' + m) for m in groups[0]}


def grouped(rows, budgets):
    return dict(summary=summary(rows, budgets),
                factsStandaloneTokens=distribution([r['factsStandaloneTokens'] for r in rows]),
                byPhase={p: summary([r for r in rows if r['phase'] == p], budgets) for p in sorted({r['phase'] for r in rows})},
                bySelectionTag={t: summary([r for r in rows if t in r['selectionTags']], budgets)
                                for t in sorted({t for r in rows for t in r['selectionTags']})})


def run(out, report):
    policy_path = REPO / 'situation-implementation/situation-token-budget-v2.json'
    policy = read_json(policy_path)
    prompt_path = REPO / policy['representationConfig']
    require(digest(prompt_path) == policy['representationConfigSha256'], 'prompt-config-drift')
    prompt = read_json(prompt_path)
    require(prompt['enableThinking'] is False and prompt['truncation'] is False and
            prompt['addSpecialTokensAfterRendering'] is False, 'unsupported-template-policy')
    config = read_json(CONFIG)
    require(Path(config['python']).resolve() == Path(sys.executable).resolve(), 'wrong-interpreter')
    root = storage_root(config['storageRoot'])
    require(os.environ.get('SITUATION_STORAGE_ROOT') == str(root), 'use-run-situation-launcher')
    pinned = {policy_path: digest(policy_path), prompt_path: digest(prompt_path)}
    from jsonschema import Draft202012Validator
    validators = []
    for key in ('payloadSchema', 'targetSchema'):
        path = REPO / prompt[key]
        require(digest(path) == prompt[key + 'Sha256'], 'schema-drift')
        pinned[path] = digest(path)
        validators.append(Draft202012Validator(read_json(path)))
    ref = reference()
    require(ref['modelId'] == policy['modelId'] and ref['revision'] == policy['revision'], 'base-reference-drift')
    base = destination(root, ref)
    pinned[base / 'base-manifest.json'] = policy['baseManifestSha256']
    require(digest(base / 'base-manifest.json') == policy['baseManifestSha256'], 'base-manifest-drift')
    bm = read_json(base / 'base-manifest.json')
    pinned.update({base / name: value for name, value in bm['tokenizerFiles'].items()})
    for path, expected in pinned.items():
        require(digest(path) == expected, 'pinned-file-drift')
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')
    from transformers import AutoTokenizer
    tokenizer = AutoTokenizer.from_pretrained(str(base), local_files_only=True, trust_remote_code=False)
    require(sha_text(tokenizer.chat_template) == bm['chatTemplateSha256'], 'chat-template-drift')
    code = ('tools/validate_situation_facts_budget.py', 'tools/situation_evidence.py', 'tools/situation_token_measurement.py', 'tools/situation_facts_prompt.py',
            'tools/measure_situation_tokens.py', 'tools/situation_lossless_table.py')
    pinned.update({REPO / name: digest(REPO / name) for name in code})
    report.update(policy=policy, policySha256=digest(policy_path), promptConfig=prompt,
                  codeSha256={name: digest(REPO / name) for name in code},
                  chatTemplateSha256=bm['chatTemplateSha256'], representations={})
    write_new_json(out / 'measurement-config.json', report)
    for ds in policy['datasets']:
        source = safe_child(root, 'datasets/' + ds['name'])
        require(Path(config['datasets'][ds['name']]).resolve() == source.resolve(), 'dataset-path-mismatch')
        localpins = {source / 'manifest.json': ds['manifestSha256'], source / 'train.jsonl': ds['trainSha256'],
                     source / 'split.json': policy['splitSha256']}
        for path, expected in localpins.items():
            require(digest(path) == expected, 'frozen-data-drift')
        pinned.update(localpins)
        manifest = read_json(source / 'manifest.json')
        require(manifest['status'] == 'complete' and manifest['versions']['analysisRules'] == policy['analysisRuleVersion'], 'source-version-mismatch')
        if ds['role'] == 'pilot':
            require(manifest['trainable'] is False, 'pilot-must-remain-nontrainable')
        else:
            require(manifest['counts']['train']['samples'] == ds['samples'], 'manifest-count-mismatch')
        allowed = train_refs(read_json(source / 'split.json'), manifest['versions']['data'])
        rows, seen, matches = [], set(), set()
        lengths = out / (ds['role'] + '-token-lengths.jsonl')
        inputs = out / (ds['role'] + '-model-inputs.jsonl')
        with (source / 'train.jsonl').open(encoding='utf-8') as src, lengths.open('x', encoding='utf-8', newline='\n') as lf, inputs.open('x', encoding='utf-8', newline='\n') as inf:
            for line in src:
                record = json.loads(line)
                md = record['metadata']
                require(md['split'] == 'train' and md['matchRef'] in allowed, 'non-train-row')
                require(record['sampleId'] not in seen, 'duplicate-sample')
                seen.add(record['sampleId']); matches.add(md['matchRef'])
                facts = record['input']['facts']
                require(facts['analysisRuleVersion'] == policy['analysisRuleVersion'], 'mixed-rule-versions')
                user = render_facts(record['input'])
                validators[0].validate(facts)
                target = canonical(record['output'])
                require(json.loads(target) == record['output'], 'target-changed')
                validators[1].validate(record['output'])
                ids = {e['id'] for e in facts['evidence']}
                require(all(set(h['evidenceIds']) <= ids for h in record['output']['highlights']), 'unknown-target-evidence')
                row = count_sequence(tokenizer, prompt['system'], user, target)
                require(row['promptTokenPrefixExact'] and row['boundaryCrossingTokens'] == 0, 'token-prefix-mismatch')
                row.update(sampleId=record['sampleId'], matchRef=md['matchRef'], phase=md['phase'],
                           selectionTags=md['selectionTags'], factsSha256=sha_text(user),
                           sourceRecordSha256=sha_text(line.rstrip('\r\n')),
                           factsStandaloneTokens=len(tokenizer.encode(user, add_special_tokens=False, truncation=False)))
                rows.append(row)
                lf.write(json.dumps(row, ensure_ascii=False, separators=(',', ':')) + '\n')
                inf.write(json.dumps(dict(sampleId=record['sampleId'], representationVersion=prompt['representationVersion'],
                    trainable=False, purpose='budget-acceptance-only', facts=facts, target=record['output'],
                    sourceRecordSha256=row['sourceRecordSha256'], sourceTrainSha256=ds['trainSha256']),
                    ensure_ascii=False, separators=(',', ':')) + '\n')
                if len(rows) % 1000 == 0:
                    print(f"{ds['role']}: {len(rows)}/{ds['samples']}", flush=True)
        require(len(rows) == ds['samples'] and matches == allowed, 'incomplete-source-coverage')
        result = grouped(rows, policy['budgets'])
        result.update(matches=len(matches), schemaAndFactsAndEvidenceChecks=len(rows),
                      inferenceReserveTokens=policy['inferenceOutputReserve'],
                      inferencePromptPlusReserve=distribution([r['inferencePromptTokens'] + policy['inferenceOutputReserve'] for r in rows]),
                      inferenceOverSelectedBudget=sum(r['inferencePromptTokens'] + policy['inferenceOutputReserve'] > policy['selectedBudget'] for r in rows),
                      artifacts={lengths.name: digest(lengths), inputs.name: digest(inputs)})
        report['representations'][ds['role']] = result
        write_new_json(out / (ds['role'] + '-summary.json'), result)
        print(json.dumps(dict(role=ds['role'], total=result['summary']['distributions']['totalTokens'],
                              overSelected=result['summary']['overBudget'][str(policy['selectedBudget'])])), flush=True)
    for path, expected in pinned.items():
        require(digest(path) == expected, 'file-changed-during-measurement')
    passed = all(v['summary']['overBudget'][str(policy['selectedBudget'])]['total'] == 0 and
                 v['inferenceOverSelectedBudget'] == 0 for v in report['representations'].values())
    report.update(status='measurement-complete', inputBudgetReady=passed, fullTrainValidated=passed,
                  validatedCorpusScope=policy['corpusScope'], originalFilesUnchanged=True,
                  blockers=['GPU resource probe at 2560 not yet performed.',
                            'Stage-four closure and any future v8/human-label corpus must be validated separately.'],
                  exitCode=0 if passed else 2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', default='facts-budget-2560-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    args = parser.parse_args()
    require(bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,95}', args.run_id)), 'invalid-run-id')
    root = storage_root(read_json(CONFIG)['storageRoot'])
    out = safe_child(root, 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    report = dict(status='started', startedUtc=datetime.now(timezone.utc).isoformat(), inputBudgetReady=False,
                  fullTrainValidated=False, trainingReady=False, devTestContentRead=False, exitCode=1)
    write_new_json(out / 'started.json', report)
    try:
        run(out, report)
    except Exception as exc:
        report.update(status='failed', inputBudgetReady=False, fullTrainValidated=False, exitCode=1,
                      error=dict(message=str(exc), traceback=traceback.format_exc()))
    report['finishedUtc'] = datetime.now(timezone.utc).isoformat()
    write_new_json(out / 'token-budget-report.json', report)
    print(json.dumps(dict(report=str(out / 'token-budget-report.json'), status=report['status'],
                         inputBudgetReady=report['inputBudgetReady'], error=report.get('error')), ensure_ascii=True), flush=True)
    return report['exitCode']


if __name__ == '__main__':
    sys.exit(main())
