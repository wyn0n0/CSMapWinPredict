"""Run the real health CLI against completed preparation and fault copies.

All mutation fixtures are new private reports. Formal corpus, budget, runtime,
resource acceptance and historical artifacts remain read-only. No model loads,
tokenizer measurements, CUDA tensors or training occur here.
"""
import argparse
from copy import deepcopy
import json
from pathlib import Path
import re
import subprocess
import sys
import traceback
from datetime import datetime

from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from validate_situation_stage6_preparation import BUNDLE_SCHEMA, EvidenceStore, require

RULE = 'situation-analysis-rules-v8-close-exposure-1'
RESOURCE = 'situation-implementation/situation-resource-status-v4.json'
RESOURCE_SHA = '3eaf0ae57871913eeeba5cf7c69fb94b35a48f32a7b09d10347508ef5f335ffd'
SOURCES = ['tools/check_situation_env.py', 'tools/validate_situation_stage6_preparation.py',
           'tools/situation_health_policy.py', 'tools/situation_health_collectors.py',
           'tools/situation_stage6_readiness.py', 'tools/verify_situation_stage6_admission.py', RESOURCE]


def binding(store, path, **extra):
    path = Path(path).resolve()
    root, prefix = (store.repository, 'repo:') if path.is_relative_to(store.repository) else (store.storage, 'storage:')
    return dict(logicalPath=prefix + path.relative_to(root).as_posix(), sha256=digest(path), **extra)


def wait_for_finish(args, store, out):
    """Wait only for the exact running predecessor; never restart anything."""
    import psutil
    run = safe_child(store.storage, 'runs/' + args.source_run_id)
    launch = read_json(run / 'finish-launch.json')
    sources = [binding(store, REPO / p) for p in SOURCES]
    write_new_json(out / 'wait-started.json', dict(status='waiting-finish', pid=launch['pid'],
                   launch=binding(store, run / 'finish-launch.json'), sourceBindings=sources))
    try:
        process = psutil.Process(launch['pid'])
        command = process.cmdline()
        require('finish' in command and args.source_run_id in command
                and abs(process.create_time() - datetime.fromisoformat(launch['startedUtc'].replace('Z', '+00:00')).timestamp()) < 5,
                'finish-process-identity-mismatch')
        print(json.dumps(dict(status='waiting-finish', pid=process.pid)), flush=True)
        while True:
            try:
                code = process.wait(timeout=30)
                require(code in (None, 0), 'finish-process-failed')
                break
            except psutil.TimeoutExpired:
                continue
    except psutil.NoSuchProcess:
        pass
    require((run / 'finish-complete.json').is_file(), 'finish-completion-missing')
    for b in sources: store.verify(b)
    print(json.dumps(dict(status='finish-complete-starting-real-cli-audit')), flush=True)


def verify_public(report, config):
    """Inspect the serialized real result, including nested keys and failures."""
    def inspect(value):
        if isinstance(value, dict):
            require(not set(value).intersection({'allowedEvidenceIds', 'sourcePaths', 'testDetails',
                'modelInput', 'reviewedInput', 'narrative', 'sampleId', 'rows'}), 'public-payload-detail-leak')
            for key, child in value.items(): inspect(key); inspect(child)
        elif isinstance(value, list):
            for child in value: inspect(child)
        elif isinstance(value, str):
            require(not re.search(r'[A-Za-z]:[\\/]|\\\\[A-Za-z0-9]', value), 'public-absolute-path-leak')
            for name in ('repository', 'storageRoot', 'python'):
                private = config.get(name)
                if isinstance(private, str) and private:
                    require(private.replace('\\', '/').lower() not in value.replace('\\', '/').lower(), 'public-config-path-leak')
    inspect(report)
    operations = report.get('operations', {})
    require(operations.get('modelLoaded') is False and operations.get('trainingStarted') is False
            and operations.get('devTestContentRead') is False, 'unexpected-health-operations')


def invoke_case(out, name, bundle_path, expected_exit, config):
    command = [sys.executable, '-B', str(REPO / 'tools/check_situation_env.py'),
               '--probe-cuda', '--public', '--format', 'json', '--gate', 'stage6', '--preparation-evidence', str(bundle_path)]
    result = subprocess.run(command, cwd=REPO, capture_output=True, text=True, encoding='utf-8')
    # Keep diagnostic text private even when the CLI itself failed to redact it.
    (out / (name + '.stdout.log')).write_text(result.stdout, encoding='utf-8')
    (out / (name + '.stderr.log')).write_text(result.stderr, encoding='utf-8')
    write_new_json(out / (name + '-command.json'), dict(command=command, exitCode=result.returncode,
        stdoutSha256=digest(out / (name + '.stdout.log')), stderrSha256=digest(out / (name + '.stderr.log'))))
    report = json.loads(result.stdout)
    require(result.returncode == expected_exit == report['exitCode'], 'health-exit-mismatch-' + name)
    require(report.get('preparationReady') is (expected_exit == 0)
            and report.get('stage6Ready') is (expected_exit == 0)
            and report.get('trainingExecutionReady') is False, 'health-admission-mismatch-' + name)
    require(report.get('resourceAccepted') is True and report.get('resourceProbeReady') is False,
            'resource-acceptance-rewritten-' + name)
    require(report.get('environmentReady') is True and report.get('baseReady') is True,
            'unrelated-environment-failure-' + name)
    if expected_exit:
        expected_reason = {'missing': 'stage6-preparation-evidence-missing',
                           'hash-drift': 'stage6-preparation-evidence-invalid',
                           'wrong-purpose': 'evidence-binding-purpose-invalid',
                           'over-budget': 'budget-overBudgetCount-invalid',
                           'wrong-version': 'version-analysisRuleVersion-conflict-or-missing'}[name]
        require(expected_reason in report.get('reasonCodes', []), 'target-negative-reason-missing-' + name)
    else:
        require(report.get('selectedRuntime') == 'nf4-rank16', 'selected-runtime-missing')
    verify_public(report, config)
    write_new_json(out / (name + '-public.json'), report)
    print(json.dumps(dict(case=name, status='passed', observedExitCode=result.returncode)), flush=True)
    return dict(case=name, status='passed', observedExitCode=result.returncode,
                reasonCodes=report['reasonCodes'], publicRedactionVerified=True)


def audit(args, config, store, out):
    require(digest(REPO / RESOURCE) == RESOURCE_SHA, 'original-resource-state-drift')
    run = safe_child(store.storage, 'runs/' + args.source_run_id)
    completion = read_json(run / 'finish-complete.json')
    require(completion.get('status') == 'passed' and completion.get('trainingExecutionReady') is False,
            'preparation-not-complete')
    finish = read_json(run / 'finish-started.json')
    stage4 = binding(store, args.stage4_report)
    require(stage4['sha256'] == finish['stage4ReportSha256'], 'stage4-binding-conflict')
    artifacts = dict(stage4=stage4, **{k: completion[k] for k in ('corpus', 'budget', 'runtime')})
    source_bindings = [binding(store, REPO / p) for p in SOURCES]
    bundle = dict(schemaVersion=BUNDLE_SCHEMA, status='complete', frozen=True, analysisRuleVersion=RULE,
                  artifacts=artifacts, evidenceBindings=[binding(store, run / 'finish-complete.json')] + source_bindings)
    bundle_path = out / 'preparation-bundle.json'
    write_new_json(bundle_path, bundle)
    cases = [invoke_case(out, 'positive', bundle_path, 0, config)]
    cases.append(invoke_case(out, 'missing', out / 'intentionally-absent-bundle.json', 2, config))
    drifted = deepcopy(bundle)
    drifted['artifacts']['budget']['sha256'] = '0' * 64
    drift_path = out / 'hash-drift-bundle.json'
    write_new_json(drift_path, drifted)
    cases.append(invoke_case(out, 'hash-drift', drift_path, 2, config))
    budget = read_json(store.path(artifacts['budget']['logicalPath']))
    for case in ('wrong-purpose', 'over-budget'):
        changed = deepcopy(budget)
        if case == 'wrong-purpose':
            declared = next(b for b in changed['evidenceBindings'] if b.get('split') == 'train')
            declared['purpose'] = 'final-evaluation'
        else:
            changed['details']['overBudgetCount'] = 1
        report_path = out / (case + '-budget.json')
        write_new_json(report_path, changed)
        changed_bundle = deepcopy(bundle)
        changed_bundle['artifacts']['budget'] = binding(store, report_path)
        changed_path = out / (case + '-bundle.json')
        write_new_json(changed_path, changed_bundle)
        cases.append(invoke_case(out, case, changed_path, 2, config))
    runtime = read_json(store.path(artifacts['runtime']['logicalPath']))
    runtime['analysisRuleVersion'] = 'situation-analysis-rules-v1'
    runtime_path = out / 'wrong-version-runtime.json'
    write_new_json(runtime_path, runtime)
    changed_bundle = deepcopy(bundle)
    changed_bundle['artifacts']['runtime'] = binding(store, runtime_path)
    changed_path = out / 'wrong-version-bundle.json'
    write_new_json(changed_path, changed_bundle)
    cases.append(invoke_case(out, 'wrong-version', changed_path, 2, config))
    # Verify provenance again after all real CLI invocations. No source or formal
    # report can drift during fault-copy construction and still get admission.
    for b in source_bindings + list(artifacts.values()): store.verify(b)
    require(digest(REPO / RESOURCE) == RESOURCE_SHA, 'original-resource-state-drift')
    output_bindings = [binding(store, p) for p in sorted(out.iterdir()) if p.is_file()]
    report = dict(schemaVersion='situation-stage6-cli-admission-audit-v1', status='passed', reasonCodes=[],
                  bundle=binding(store, bundle_path), cases=cases, evidenceBindings=source_bindings + output_bindings,
                  trainingExecutionReady=False, originalFormalArtifactsUnchanged=True)
    write_new_json(out / 'admission-audit.json', report)
    print(json.dumps(dict(status='passed', cases=len(cases), reportSha256=digest(out / 'admission-audit.json'))), flush=True)
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-run-id', required=True)
    parser.add_argument('--stage4-report', required=True)
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--wait-finish', action='store_true', help='wait on the recorded exact finish PID before auditing')
    args = parser.parse_args()
    config = read_json(CONFIG)
    store = EvidenceStore(REPO, storage_root(config['storageRoot']))
    out = safe_child(store.storage, 'reports/' + args.run_id)
    out.mkdir(exist_ok=False)
    try:
        if args.wait_finish: wait_for_finish(args, store, out)
        return audit(args, config, store, out)
    except Exception:
        write_new_json(out / 'failure.json', dict(status='failed', traceback=traceback.format_exc()))
        print(json.dumps(dict(status='failed', reason='see-private-admission-failure')), flush=True)
        return 2


if __name__ == '__main__': raise SystemExit(main())
