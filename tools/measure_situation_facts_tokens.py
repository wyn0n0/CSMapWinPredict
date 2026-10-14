"""Measure complete Facts-only prompts on the frozen train-only pilot; never train."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import sys

from situation_token_measurement import count_sequence, distribution, sha_text, summary, train_membership
from prepare_situation_base import destination, reference, require
from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from situation_lossless_table import canonical

SYSTEM = ('仅依据输入的完整 SituationFactsV1 生成中文局势摘要，输出完整 situation-narrative-v1 JSON：'
          'schemaVersion、highlights（2至4项 textZh 和 evidenceIds）、summaryZh、uncertainties。'
          '只引用输入 evidence 中已有的 id；保留未知与数据质量限制；不推算新事实，不预测胜率，不提供战术指令。')
BUDGETS = [768, 1024, 1536, 2048, 3072, 4096]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', default='facts-tokens-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    args = parser.parse_args()
    require(bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,95}', args.run_id)), 'invalid-run-id')
    config = read_json(CONFIG)
    require(Path(sys.executable).resolve() == Path(config['python']).resolve(), 'wrong-interpreter')
    root = storage_root(config['storageRoot'])
    require(os.environ.get('SITUATION_STORAGE_ROOT') == str(root), 'use-run-situation-launcher')
    out = safe_child(root, 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    policy_path = REPO / 'situation-implementation/situation-token-budget-v1.json'
    policy = read_json(policy_path)
    pilot = safe_child(root, 'datasets/' + policy['pilot'])
    pinned = {pilot / name: policy[key] for name, key in (
        ('manifest.json', 'pilotManifestSha256'), ('train.jsonl', 'trainSha256'), ('split.json', 'splitSha256'))}
    for path, expected in pinned.items():
        require(digest(path) == expected, 'frozen-data-hash-mismatch')
    manifest = read_json(pilot / 'manifest.json')
    require(manifest['trainable'] is False and manifest['purpose'] == 'representation-measurement', 'pilot-purpose-mismatch')
    records = [json.loads(line) for line in (pilot / 'train.jsonl').read_text(encoding='utf-8').splitlines()]
    train_membership(manifest, read_json(pilot / 'split.json'), records)
    ref = reference()
    base = destination(root, ref)
    base_manifest_path = base / 'base-manifest.json'
    require(digest(base_manifest_path) == '7ceb125e23e6ddb1b98d1541dede19d5c2fc7efd935ca6966c85454dd69ea612', 'base-manifest-drift')
    base_manifest = read_json(base_manifest_path)
    pinned.update({base / name: expected for name, expected in base_manifest['tokenizerFiles'].items()})
    for path, expected in pinned.items():
        require(digest(path) == expected, 'pinned-input-drift')
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')
    from transformers import AutoTokenizer
    from jsonschema import Draft202012Validator
    tokenizer = AutoTokenizer.from_pretrained(str(base), local_files_only=True, trust_remote_code=False)
    require(sha_text(tokenizer.chat_template) == base_manifest['chatTemplateSha256'], 'chat-template-drift')
    schema_path = REPO / 'schemas/situation/situation-narrative-v1.schema.json'
    validator = Draft202012Validator(read_json(schema_path))
    report = dict(status='started', representation='facts-only-utf8-candidate-v1', trainable=False,
                  trainingReady=False, fullTrainValidated=False, productionAdopted=False,
                  startedUtc=datetime.now(timezone.utc).isoformat(), sourcePilot=policy['pilot'],
                  devTestContentRead=False, sourceHashes={p.name: h for p, h in pinned.items()},
                  modelId=ref['modelId'], revision=ref['revision'], chatTemplateSha256=base_manifest['chatTemplateSha256'],
                  policySha256=digest(policy_path), narrativeSchemaSha256=digest(schema_path),
                  codeSha256={name: digest(REPO / name) for name in (
                      'tools/measure_situation_facts_tokens.py', 'tools/measure_situation_tokens.py',
                      'tools/situation_lossless_table.py')},
                  system=SYSTEM, enableThinking=False, truncation=False,
                  payload='input.facts object only; every field retained; no wrapper or external allowedEvidenceIds',
                  serialization='literal UTF-8 compact sorted JSON for both Facts and complete original Narrative',
                  inferenceReservesIncludeEos=True)
    write_new_json(out / 'started.json', report)
    rows = []
    with (out / 'sample-token-lengths.jsonl').open('x', encoding='utf-8', newline='\n') as handle:
        for record in records:
            facts = record['input']['facts']
            user = canonical(facts)
            require(json.loads(user) == facts, 'facts-content-changed')
            ids = [e['id'] for e in facts['evidence']]
            require(len(ids) == len(set(ids)), 'duplicate-evidence-id')
            require(set(ids) == set(record['input']['allowedEvidenceIds']), 'facts-ids-differ-from-allowed-set')
            # Only verify the target after constructing the complete Facts-only input.
            target = canonical(record['output'])
            require(json.loads(target) == record['output'], 'target-content-changed')
            validator.validate(record['output'])
            require(all(set(h['evidenceIds']) <= set(ids) for h in record['output']['highlights']), 'unsupported-target-id')
            row = count_sequence(tokenizer, SYSTEM, user, target)
            row.update(sampleId=record['sampleId'], matchRef=record['metadata']['matchRef'],
                       factsStandaloneTokens=len(tokenizer.encode(user, add_special_tokens=False, truncation=False)),
                       factsSha256=sha_text(user), evidenceCount=len(ids),
                       analysisRuleVersion=facts['analysisRuleVersion'])
            require(row['promptTokenPrefixExact'] and row['boundaryCrossingTokens'] == 0, 'token-boundary-mismatch')
            rows.append(row)
            handle.write(json.dumps(row, ensure_ascii=False, separators=(',', ':')) + '\n')
    for path, expected in pinned.items():
        require(digest(path) == expected, 'source-changed-during-measurement')
    report.update(status='measurement-complete', finishedUtc=datetime.now(timezone.utc).isoformat(),
                  summary=summary(rows, BUDGETS), factsStandaloneTokens=distribution([r['factsStandaloneTokens'] for r in rows]),
                  checks=dict(fullFactsEqual=500, targetEqual=500, targetSchemaValid=500,
                              allowedIdSetEqual=500, targetEvidenceValid=500, tokenRoundTrip=500, sourceHashesUnchanged=True),
                  inference={str(reserve): dict(
                      reserveTokens=reserve,
                      promptPlusReserve=distribution([r['inferencePromptTokens'] + reserve for r in rows]),
                      overBudget={str(b): sum(r['inferencePromptTokens'] + reserve > b for r in rows) for b in BUDGETS})
                      for reserve in (256, 512)},
                  lengthsSha256=digest(out / 'sample-token-lengths.jsonl'))
    write_new_json(out / 'facts-token-report.json', report)
    print(json.dumps(dict(output=str(out), summary=report['summary'], factsStandaloneTokens=report['factsStandaloneTokens'],
                         inference=report['inference'], trainingReady=False), ensure_ascii=True, indent=2))


if __name__ == '__main__':
    main()
