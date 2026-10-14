"""Install a proven preparation bundle, then verify the actual default CLI.

Requires the completed six-case private CLI audit. Never changes its formal
artifacts, starts training, or treats preparation admission as training permission.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import traceback

from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from validate_situation_stage6_preparation import DEFAULT_BUNDLE, EvidenceStore, require
from verify_situation_stage6_admission import binding, verify_public


def publish(args, config, store, out):
    audit_path = safe_child(store.storage, 'reports/' + args.admission_run_id + '/admission-audit.json')
    require(digest(audit_path) == args.audit_sha256, 'admission-audit-drift')
    audit = store.report(binding(store, audit_path))
    expected = {'positive': 0, 'missing': 2, 'hash-drift': 2,
                'wrong-purpose': 2, 'over-budget': 2, 'wrong-version': 2}
    require(audit.get('status') == 'passed' and audit.get('reasonCodes') == []
            and audit.get('trainingExecutionReady') is False
            and audit.get('originalFormalArtifactsUnchanged') is True,
            'admission-audit-not-passed')
    cases = audit.get('cases', [])
    require(len(cases) == len(expected) and {c.get('case') for c in cases} == set(expected)
            and all(c.get('status') == 'passed' and c.get('publicRedactionVerified') is True
                    and c.get('observedExitCode') == expected[c['case']] for c in cases),
            'real-cli-cases-incomplete')
    store.verify(audit['bundle'])
    source = store.path(audit['bundle']['logicalPath'])
    destination = REPO / DEFAULT_BUNDLE
    require(not destination.exists(), 'default-bundle-already-exists')
    payload = source.read_bytes()
    # Exclusive creation preserves any concurrently created or pre-existing file.
    with destination.open('xb') as handle:
        handle.write(payload)
    require(digest(destination) == audit['bundle']['sha256'], 'default-bundle-copy-drift')
    command = [sys.executable, '-B', str(REPO / 'tools/check_situation_env.py'),
               '--probe-cuda', '--public', '--format', 'json', '--gate', 'stage6']
    result = subprocess.run(command, cwd=REPO, capture_output=True, text=True, encoding='utf-8')
    stdout, stderr = out / 'default-health.stdout.log', out / 'default-health.stderr.log'
    stdout.write_text(result.stdout, encoding='utf-8')
    stderr.write_text(result.stderr, encoding='utf-8')
    write_new_json(out / 'default-health-command.json', dict(command=command,
        exitCode=result.returncode, stdoutSha256=digest(stdout), stderrSha256=digest(stderr)))
    health = json.loads(result.stdout)
    require(result.returncode == health.get('exitCode') == 0, 'default-health-cli-failed')
    require(all(health.get(k) is True for k in ('environmentReady', 'baseReady',
        'stage6Ready', 'preparationReady', 'resourceAccepted'))
        and all(health.get(k) is False for k in ('trainingExecutionReady', 'resourceProbeReady'))
        and health.get('selectedRuntime') == 'nf4-rank16', 'default-health-state-invalid')
    verify_public(health, config)
    write_new_json(out / 'default-health-public.json', health)
    for declared in audit['evidenceBindings']:
        store.verify(declared)
    require(digest(audit_path) == args.audit_sha256, 'admission-audit-drift')
    store.verify(audit['bundle'])
    require(digest(destination) == audit['bundle']['sha256'], 'default-bundle-drift')
    bindings = [binding(store, audit_path), binding(store, destination),
                binding(store, Path(__file__).resolve())]
    bindings += [binding(store, p) for p in sorted(out.iterdir()) if p.is_file()]
    report = dict(schemaVersion='situation-stage6-default-admission-v1', status='passed',
        reasonCodes=[], evidenceBindings=bindings, defaultBundle=binding(store, destination),
        actualDefaultCliExitCode=0, preparationReady=True, trainingExecutionReady=False,
        selectedRuntime='nf4-rank16', resourceAccepted=True, resourceProbeReady=False,
        publicRedactionVerified=True, nextStep='TODO_6 step 4: evaluation protocol')
    write_new_json(out / 'default-admission-audit.json', report)
    print(json.dumps(dict(status='passed', actualDefaultCliExitCode=0,
        reportSha256=digest(out / 'default-admission-audit.json'))), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--admission-run-id', required=True)
    parser.add_argument('--audit-sha256', required=True)
    parser.add_argument('--run-id', required=True)
    args = parser.parse_args()
    config = read_json(CONFIG)
    store = EvidenceStore(REPO, storage_root(config['storageRoot']))
    out = safe_child(store.storage, 'reports/' + args.run_id)
    out.mkdir(exist_ok=False)
    try:
        publish(args, config, store, out)
        return 0
    except Exception:
        write_new_json(out / 'failure.json', dict(status='failed', traceback=traceback.format_exc()))
        print(json.dumps(dict(status='failed', reason='see-private-default-admission-failure')))
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
