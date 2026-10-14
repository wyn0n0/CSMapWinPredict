"""Read-only train-pilot research; candidates are NOT adopted training contracts."""
import json
import os
from pathlib import Path

from situation_token_measurement import count_sequence, summary, train_membership, sha_text
from situation_environment import REPO, digest, read_json
from situation_lossless_table import canonical


def main():
    root = Path(os.environ['SITUATION_STORAGE_ROOT'])
    policy = read_json(REPO / 'situation-implementation/situation-token-budget-v1.json')
    pilot = root / 'datasets' / policy['pilot']
    for filename, expected in [('manifest.json', policy['pilotManifestSha256']),
                               ('train.jsonl', policy['trainSha256']), ('split.json', policy['splitSha256'])]:
        assert digest(pilot / filename) == expected
    records = [json.loads(s) for s in (pilot / 'train.jsonl').read_text(encoding='utf-8').splitlines()]
    train_membership(read_json(pilot / 'manifest.json'), read_json(pilot / 'split.json'), records)
    rendered_path = root / 'reports/token-budget-20260929-r2/rendered/rendered-pilot.jsonl'
    assert digest(rendered_path) == 'b29cea3ec18dc849e982f8388f91561064491dd4f16e9b16d167e02696f0ed2b'
    rendered = [json.loads(s) for s in rendered_path.read_text(encoding='utf-8').splitlines()]
    base = root / 'bases/Qwen3-1.7B-70d244cc86ccca08cf5af4e1e306ecf908b1ad5e'
    assert digest(base / 'base-manifest.json') == '7ceb125e23e6ddb1b98d1541dede19d5c2fc7efd935ca6966c85454dd69ea612'
    manifest = read_json(base / 'base-manifest.json')
    for filename, expected in manifest['tokenizerFiles'].items():
        assert digest(base / filename) == expected
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1')
    from transformers import AutoTokenizer
    tokenizer = AutoTokenizer.from_pretrained(str(base), local_files_only=True, trust_remote_code=False)
    assert sha_text(tokenizer.chat_template) == manifest['chatTemplateSha256']
    system = ('仅依据输入事实和证据生成中文局势摘要，输出完整 situation-narrative-v1 JSON：'
              'schemaVersion、highlights（2至4项 textZh 和 evidenceIds）、summaryZh、uncertainties。'
              '只引用 allowedEvidenceIds；保留未知与数据质量限制；不推算新事实，不预测胜率，不提供战术指令。')
    groups = {name: [] for name in ('utf8-full-scene', 'full-facts', 'narrative-view')}
    coverage = 0
    for record, old in zip(records, rendered, strict=True):
        assert record['sampleId'] == old['sampleId']
        source = record['input']
        facts = source['facts']
        # All evidence is retained; no output/label is used to construct inputs.
        full = {'facts': facts, 'allowedEvidenceIds': source['allowedEvidenceIds']}
        view = {'facts': {k: v for k, v in facts.items() if k not in ('evidence', 'dataQuality')},
                'evidence': [[e['id'], e['textZh']] for e in facts['evidence']],
                'dataQuality': facts['dataQuality'], 'allowedEvidenceIds': source['allowedEvidenceIds']}
        assert {e[0] for e in view['evidence']} == {e['id'] for e in facts['evidence']}
        target = canonical(record['output'])
        assert json.loads(target) == json.loads(old['target'])
        assert old['user'].count(old['compactJson']) == 1
        users = {
            'utf8-full-scene': (old['system'], old['user'].replace(old['compactJson'], canonical(json.loads(old['compactJson'])))),
            'full-facts': (system, canonical(full)),
            'narrative-view': (system + ' evidence 每行为[证据ID,证据原文]。', canonical(view)),
        }
        # Evaluate current template-label support only after constructing every candidate.
        texts = {e['id']: e['textZh'] for e in facts['evidence']}
        coverage += all(h['evidenceIds'] and all(i in texts and i in source['allowedEvidenceIds'] for i in h['evidenceIds'])
                        and any(h['textZh'] == texts[i] for i in h['evidenceIds']) for h in record['output']['highlights'])
        for name, (sys_text, user_text) in users.items():
            result = count_sequence(tokenizer, sys_text, user_text, target)
            result.update(sampleId=record['sampleId'], phase=old['phase'], selectionTags=old['selectionTags'])
            groups[name].append(result)
    report = dict(status='research-only-not-adopted', trainable=False, trainingReady=False, samples=500,
                  devTestContentRead=False, inputSelectionUsesTargets=False,
                  currentTemplateHighlightExactEvidenceSupport=coverage,
                  tokenizerManifestSha256=digest(base / 'base-manifest.json'),
                  sourceTrainSha256=policy['trainSha256'], scriptSha256=digest(Path(__file__)),
                  promptSystem=system, targetSerialization='literal UTF-8 compact sorted JSON; semantic equality checked',
                  variants={name: summary(rows, [768, 1024, 1536, 2048, 3072, 4096]) for name, rows in groups.items()})
    # A new workspace-only aggregate; never overwrite original data or reports.
    output = REPO / 'situation-implementation/stage5-input-view-research-20260929.json'
    with output.open('x', encoding='utf-8', newline='\n') as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)
        handle.write('\n')
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
