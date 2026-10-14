"""Train-only pilot token measurement, with a separate reversible representation candidate."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import traceback

from situation_token_measurement import sha_text, train_membership, distribution, count_sequence, summary
from situation_environment import CONFIG, REPO, dataset_inventory, digest, read_json, safe_child, storage_root, write_new_json
from prepare_situation_base import destination, reference, require, verify_base
from situation_lossless_table import canonical, encode, decode

POLICY = REPO / 'situation-implementation/situation-token-budget-v1.json'
CANDIDATE = REPO / 'situation-implementation/situation-lossless-table-v2-candidate.json'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', default='token-budget-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    args = parser.parse_args()
    require(bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,95}', args.run_id)), 'invalid-run-id')
    config = read_json(CONFIG)
    require(Path(sys.executable).resolve() == Path(config['python']).resolve(), 'wrong-interpreter')
    root = storage_root(config['storageRoot'])
    require(os.environ.get('SITUATION_STORAGE_ROOT') == str(root), 'use-run-situation-launcher')
    out = safe_child(root, 'reports/' + args.run_id)
    out.mkdir(parents=True, exist_ok=False)
    policy, candidate = read_json(POLICY), read_json(CANDIDATE)
    report = dict(schemaVersion='situation-token-budget-report-v1', status='incomplete', inputBudgetReady=False,
                  trainingReady=False, fullTrainValidated=False, candidateAdopted=False,
                  startedUtc=datetime.now(timezone.utc).isoformat(), command=[sys.executable, *sys.argv],
                  policy=policy, policySha256=digest(POLICY), candidateConfigSha256=digest(CANDIDATE),
                  codeSha256={p: digest(REPO / p) for p in ('tools/measure_situation_tokens.py', 'tools/situation_evidence.py', 'tools/situation_token_measurement.py', 'tools/situation_lossless_table.py',
                    'apps/cli/SituationTokenizerCommands.cs', 'apps/api/Features/Situation/Training/SituationPromptRepresentation.cs')})
    write_new_json(out / 'started.json', report)
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')
    try:
        pilot = safe_child(root, 'datasets/' + policy['pilot'])
        require(Path(config['datasets'][policy['pilot']]).resolve() == pilot.resolve(), 'pilot-config-path-mismatch')
        inventory = dataset_inventory(pilot, policy['pilotManifestSha256'])
        manifest, split = read_json(pilot/'manifest.json'), read_json(pilot/'split.json')
        require(manifest['purpose'] == 'representation-measurement' and manifest['trainable'] is False, 'pilot-purpose-mismatch')
        require(digest(pilot/'train.jsonl') == policy['trainSha256'] and digest(pilot/'split.json') == policy['splitSha256'], 'frozen-data-hash-mismatch')
        records = [json.loads(line) for line in (pilot/'train.jsonl').read_text(encoding='utf-8').splitlines()]
        train_membership(manifest, split, records)
        provenance = read_json(pilot/'provenance.json')
        config_source = next(p for p in provenance['sourceFiles'] if p['path'].endswith('/situation-prompt-representation-v1.json'))
        require(digest(REPO/config_source['path']) == config_source['sha256'], 'frozen-renderer-config-source-drift')
        report['pilot'] = dict(manifestSha256=digest(pilot/'manifest.json'), trainSha256=policy['trainSha256'],
                              splitSha256=policy['splitSha256'], trainMatches=71, samples=500, trainable=False,
                              devTestContentRead=False, representationSource=config_source)
        export = out/'rendered'
        command = ['dotnet', str(REPO/'apps/cli/bin/Release/net10.0/CsDemoMap.Cli.dll'),
                   '--export-situation-tokenizer-inputs', str(pilot), str(export)]
        process = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', errors='replace', cwd=REPO, timeout=180)
        write_new_json(out/'renderer-command.json', dict(command=command, exitCode=process.returncode, stdout=process.stdout, stderr=process.stderr))
        require(process.returncode == 0, 'frozen-renderer-export-failed-see-renderer-command')
        exported = read_json(export/'export-manifest.json')
        require(exported['representationConfigSha256'] == policy['representationConfigSha256'], 'representation-canonical-hash-mismatch')
        require(exported['renderedSha256'] == digest(export/'rendered-pilot.jsonl'), 'rendered-file-hash-mismatch')
        rendered = [json.loads(line) for line in (export/'rendered-pilot.jsonl').read_text(encoding='utf-8').splitlines()]
        measurements = [json.loads(line) for line in (pilot/'representation-measurements.jsonl').read_text(encoding='utf-8').splitlines()]
        require([r['sampleId'] for r in rendered] == [r['sampleId'] for r in records], 'rendered-order-mismatch')
        require(all(r['sampleId'] == m['sampleId'] and r['frozenRenderedUtf8Bytes'] == m['compactPrompt']['utf8Bytes']
                    for r, m in zip(rendered, measurements)), 'frozen-prompt-byte-measurements-differ')
        report['renderer'] = exported
        ref = reference()
        base = destination(root, ref)
        base_manifest = verify_base(base, ref)
        from transformers import AutoTokenizer
        from jsonschema import Draft202012Validator
        tokenizer = AutoTokenizer.from_pretrained(str(base), local_files_only=True, trust_remote_code=False)
        require(sha_text(tokenizer.chat_template) == base_manifest['chatTemplateSha256'], 'loaded-chat-template-drift')
        report['tokenizer'] = dict(modelId=ref['modelId'], revision=ref['revision'],
            manifestSha256=digest(base/'base-manifest.json'), chatTemplateSha256=base_manifest['chatTemplateSha256'],
            files=base_manifest['tokenizerFiles'], enableThinking=False, addSpecialTokens=False, truncation=False,
            eosToken=tokenizer.eos_token, eosTokenId=tokenizer.eos_token_id)
        schema = REPO/candidate['payloadSchema']
        validator = Draft202012Validator(read_json(schema))
        report['candidateSchemaSha256'] = digest(schema)
        baseline, revised = [], []
        with (out/'sample-token-lengths.jsonl').open('x', encoding='utf-8') as lengths, (out/'candidate-inputs.jsonl').open('x', encoding='utf-8') as candidates:
            for index, row in enumerate(rendered):
                identity = {k: row[k] for k in ('sampleId', 'matchRef', 'phase', 'selectionTags')}
                original = count_sequence(tokenizer, row['system'], row['user'], row['target'])
                original.update(identity, representation='compact-v1')
                baseline.append(original)
                payload = json.loads(row['compactJson'])
                encoded = encode(payload)
                validator.validate(encoded)
                restored = decode(encoded)
                require(canonical(restored) == canonical(payload), 'lossless-candidate-round-trip-failed')
                revised_user = candidate['inputPrefix'] + '\n' + canonical(encoded) + '\n' + candidate['outputPrefix']
                changed = count_sequence(tokenizer, row['system'] + '\n' + candidate['systemSuffix'], revised_user, row['target'])
                changed.update(identity, representation=candidate['version'], roundTripPassed=True,
                               restoredInputSha256=sha_text(canonical(restored)))
                revised.append(changed)
                for item in (original, changed):
                    lengths.write(json.dumps(item, ensure_ascii=False) + '\n')
                candidates.write(json.dumps(dict(sampleId=row['sampleId'], schemaVersion=candidate['codecVersion'],
                    purpose='representation-measurement-only', trainable=False, payload=encoded,
                    restoredInputSha256=changed['restoredInputSha256']), ensure_ascii=False) + '\n')
                if (index + 1) % 100 == 0:
                    print(f'Measured {index + 1}/500 train-only samples', flush=True)
        report['representations'] = {}
        for version, rows in (('compact-v1', baseline), (candidate['version'], revised)):
            tags = sorted({t for r in rows for t in r['selectionTags']})
            report['representations'][version] = dict(summary=summary(rows, policy['budgets']),
                byPhase={p: summary([r for r in rows if r['phase'] == p], policy['budgets']) for p in sorted({r['phase'] for r in rows})},
                bySelectionTag={t: summary([r for r in rows if t in r['selectionTags']], policy['budgets']) for t in tags})
        require(dataset_inventory(pilot, policy['pilotManifestSha256']) == inventory, 'pilot-changed-during-measurement')
        report.update(status='measurement-complete', inputBudgetReady=False, candidateRoundTripPassed=500,
                      blockers=['No adopted representation and full-train budget acceptance yet.',
                                'Pilot is non-trainable; no real training or resource-probe samples released.',
                                'Stage-four closure and v1/v8 corpus decision remain pending.'],
                      artifacts={p.name: digest(p) for p in (out/'sample-token-lengths.jsonl', out/'candidate-inputs.jsonl')}, exitCode=0)
        if any(r['totalTokens'] > 1024 for r in revised):
            report['blockers'].insert(0, 'Lossless candidate still exceeds 1024 total training tokens; requires separate representation design.')
    except Exception as exc:
        report.update(status='failed', exitCode=1, error=dict(type=type(exc).__name__, message=str(exc), traceback=traceback.format_exc()))
    report['finishedUtc'] = datetime.now(timezone.utc).isoformat()
    write_new_json(out/'token-budget-report.json', report)
    print(json.dumps(dict(status=report['status'], inputBudgetReady=report['inputBudgetReady'],
                          report=str(out/'token-budget-report.json'), error=report.get('error')), ensure_ascii=False))
    return report['exitCode']


if __name__ == '__main__':
    sys.exit(main())
