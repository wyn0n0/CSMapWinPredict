"""Read-only stage-five health and stage-six admission diagnostics.

No installation, download, model loading or training occurs. By default the
CUDA tensor/runtime probe is not requested; --probe-cuda queries capabilities
only. Reports are private unless --public is specified, and writes are opt-in.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys

try:
    from .situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root
    from .situation_health_collectors import collect_observations
    from .situation_health_policy import evaluate_gates, redact_public
    from .audit_situation_stage5_acceptance import audit_acceptance
    from .validate_situation_stage6_preparation import collect_preparation_evidence
except ImportError:
    from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root
    from situation_health_collectors import collect_observations
    from situation_health_policy import evaluate_gates, redact_public
    from audit_situation_stage5_acceptance import audit_acceptance
    from validate_situation_stage6_preparation import collect_preparation_evidence


RESOURCE_STATUS = 'situation-implementation/situation-resource-status-v4.json'
BUDGET_CONFIG = 'situation-implementation/situation-token-budget-v2.json'
PROBE_CONFIG = 'situation-implementation/situation-lora-probe-v2.json'


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def verify_input_budget(config):
    try:
        return _verify_input_budget(config)
    except (KeyError, TypeError, ValueError, OSError) as exc:
        return dict(status='failed', verified=False, inputBudgetReady=False, fullTrainValidated=False,
                    reasonCodes=['input-evidence-mismatch'], details={'error': str(exc)}, evidence={})


def _verify_input_budget(config):
    """Re-read hashes and approved train-only scope, without running tokenizer.

    Only train bytes are hashed. Dev/test contents are never opened. The prior
    independent full-record verification is reused only with unchanged bindings.
    """
    repo = Path(config['repository']).resolve()
    root = storage_root(config['storageRoot'], repo)
    probe = read_json(safe_child(repo, PROBE_CONFIG))
    report_dir = safe_child(root, 'reports/' + probe['budgetRun'])
    report_path = safe_child(report_dir, 'token-budget-report.json')
    verification_path = safe_child(report_dir, 'verification.json')
    evidence = {'path': str(report_path), 'sha256': digest(report_path),
                'verificationPath': str(verification_path),
                'verificationSha256': digest(verification_path)}
    try:
        require(evidence['sha256'] == probe['budgetReportSha256'], 'input-evidence-hash-mismatch')
        report = read_json(report_path)
        verification = read_json(verification_path)
        policy = read_json(safe_child(repo, BUDGET_CONFIG))
        require(report['policySha256'] == digest(safe_child(repo, BUDGET_CONFIG)), 'input-policy-hash-mismatch')
        require(report['policy'] == policy, 'input-policy-mismatch')
        require(policy['selectedBudget'] == probe['maxSequenceLength'] == 2560, 'input-over-budget')
        require(verification['status'] == 'passed' and verification['reportSha256'] == evidence['sha256'],
                'input-verification-mismatch')
        require(report['status'] == 'measurement-complete' and report['exitCode'] == 0
                and report['inputBudgetReady'] is True and report['fullTrainValidated'] is True
                and report['devTestContentRead'] is False and report['originalFilesUnchanged'] is True,
                'input-budget-not-accepted')
        prompt_path = safe_child(repo, policy['representationConfig'])
        require(digest(prompt_path) == policy['representationConfigSha256'], 'input-representation-hash-mismatch')
        prompt = read_json(prompt_path)
        require(prompt == report['promptConfig'] and prompt['representationVersion'] == 'facts-only-utf8-v1'
                and prompt['preserveAllFactsFields'] is True and prompt['truncation'] is False,
                'input-representation-mismatch')
        for name, expected in report['codeSha256'].items():
            require(digest(safe_child(repo, name)) == expected, 'input-source-hash-mismatch')
        for field, hash_field in [('payloadSchema', 'payloadSchemaSha256'), ('targetSchema', 'targetSchemaSha256')]:
            require(digest(safe_child(repo, prompt[field])) == prompt[hash_field], 'input-schema-hash-mismatch')
        split = safe_child(repo, 'situation-implementation/situation-stage4-split-v1.json')
        require(digest(split) == policy['splitSha256'], 'input-split-hash-mismatch')
        # Revision and template are still bound even though no model is loaded.
        base_name = 'Qwen3-1.7B-' + policy['revision']
        base = safe_child(root, 'bases/' + base_name)
        require(digest(safe_child(base, 'base-manifest.json')) == policy['baseManifestSha256'],
                'input-base-hash-mismatch')
        template = read_json(safe_child(base, 'tokenizer_config.json'))['chat_template']
        require(hashlib.sha256(template.encode('utf-8')).hexdigest() == report['chatTemplateSha256'],
                'input-template-hash-mismatch')
        checks = {c['role']: c for c in verification['checks']}
        for item in policy['datasets']:
            require(item['role'] in ('pilot', 'full-train'), 'purpose-mismatch')
            dataset = safe_child(root, 'datasets/' + item['name'])
            manifest_path = safe_child(dataset, 'manifest.json')
            require(digest(manifest_path) == item['manifestSha256'], 'input-manifest-hash-mismatch')
            manifest = read_json(manifest_path)
            require(manifest['status'] == 'complete', 'input-manifest-incomplete')
            require(digest(safe_child(dataset, 'train.jsonl')) == item['trainSha256'], 'input-train-hash-mismatch')
            measured = report['representations'][item['role']]
            summary = measured['summary']
            require(summary['samples'] == item['samples'] == checks[item['role']]['rows'], 'input-count-mismatch')
            require(checks[item['role']]['fullFactsAndTargetsEqual'] is True
                    and checks[item['role']]['aggregatesAndGroupsRecomputed'] is True,
                    'input-verification-incomplete')
            require(summary['overBudget']['2560']['total'] == 0
                    and summary['distributions']['totalTokens']['max'] <= 2560
                    and measured['inferenceOverSelectedBudget'] == 0
                    and measured['inferencePromptPlusReserve']['max'] <= 2560, 'input-over-budget')
            for name, expected in measured['artifacts'].items():
                require(digest(safe_child(report_dir, name)) == expected, 'input-artifact-hash-mismatch')
        return dict(status='passed', verified=True, inputBudgetReady=True, fullTrainValidated=True,
                    maxSequenceLength=2560, representationVersion='facts-only-utf8-v1',
                    scope='existing v1 base train only', reasonCodes=[], evidence=evidence)
    except (KeyError, TypeError, ValueError, OSError) as exc:
        return dict(status='failed', verified=False, inputBudgetReady=False, fullTrainValidated=False,
                    reasonCodes=['input-evidence-mismatch'], details={'error': str(exc)}, evidence=evidence)


def run_health(config, *, probe_cuda=False, gate='stage6', preparation_bundle=None):
    repo = Path(config['repository']).resolve()
    require(repo == REPO.resolve(), 'local-config-repository-mismatch')
    root = storage_root(config['storageRoot'], repo)
    preflight_path = safe_child(root, 'reports/situation-stage5-preflight-20260927-r1/preflight.json')
    preflight = read_json(preflight_path)
    # This is an immutable historical baseline, not a newly inferred snapshot.
    require(digest(preflight_path) == 'd7d4d162ec777c99018545eb37793d284e60e405c89436f82d28014bf9331d69',
            'isolation-baseline-hash-mismatch')
    collector_config = dict(config, isolationBaseline=preflight['winEnvironment'])
    observations = collect_observations(collector_config, probe_cuda=probe_cuda)
    resource_path = safe_child(repo, RESOURCE_STATUS)
    resource_status = read_json(resource_path)
    resource = audit_acceptance(resource_path, {
        'repository': str(repo), 'storageRoot': str(root),
        'python': config['python'],
        'acceptancePath': str(resource_path),
        'dependencyLockSha256': resource_status['dependencyLockSha256'],
        'maxSequenceLength': 2560, 'representationVersion': 'facts-only-utf8-v1',
    })
    resource['evidenceBindings'] = [dict(logicalPath='repo:' + RESOURCE_STATUS,
                                        sha256=digest(resource_path))]
    preparation = collect_preparation_evidence(config, resource, preparation_bundle)
    evidence = {
        'inputBudget': verify_input_budget(config),
        'resource': resource,
        'stage4': preparation['stage4'],
        'corpus': preparation['corpus'],
        'preparation': preparation,
    }
    if gate == 'stage6' and 'inputBudget' in preparation:
        evidence['inputBudget'] = preparation['inputBudget']
    requirements = dict(pythonVersion='3.11', maxSequenceLength=2560,
                        representationVersion='facts-only-utf8-v1', purpose='training',
                        allowUserResourceAcceptance=True, minimumHeadroomFraction=0.10)
    gates = evaluate_gates(observations, evidence, requirements)
    report = dict(gates, schemaVersion='situation-health-report-v1',
                  checkedAtUtc=datetime.now(timezone.utc).isoformat(), requestedGate=gate,
                  observations=observations, evidence=evidence, requirements=requirements,
                  operations={'readOnly': True, 'modelLoaded': False, 'trainingStarted': False,
                              'devTestContentRead': False, 'cudaCapabilityQueryRequested': probe_cuda})
    report['consumerCodeSha256'] = {name: digest(safe_child(repo, name)) for name in (
        'tools/check_situation_env.py', 'tools/situation_health_collectors.py',
        'tools/situation_health_policy.py', 'tools/audit_situation_stage5_acceptance.py',
        'tools/validate_situation_stage6_preparation.py')}
    # Environment delivery can finish while the distinct stage-six gate is closed.
    if gate == 'environment' and gates.get('exitCode') != 1:
        report['exitCode'] = 0 if (gates.get('environmentReady') is True
            and gates.get('baseReady') is True and gates.get('inputBudgetReady') is True
            and gates.get('resourceAccepted') is True
            and resource.get('postProbeDependencyAcceptance') == 'passed') else 2
    return report


def main(argv=None):
    # JSON pipes must remain UTF-8 on Windows even when the console locale is GBK.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', default=str(CONFIG))
    parser.add_argument('--public', action='store_true')
    parser.add_argument('--format', choices=['json'], default='json')
    parser.add_argument('--probe-cuda', action='store_true', help='query torch CUDA/BF16 capability; no tensor computation')
    parser.add_argument('--gate', choices=['stage6', 'environment'], default='stage6')
    parser.add_argument('--preparation-evidence', help='frozen stage-six preparation bundle; default is the repository binding')
    parser.add_argument('--output', help='new JSON file under ignored datasets/ or configured private storage')
    args = parser.parse_args(argv)
    config = {}
    context = {'repository': str(REPO)}
    try:
        config = read_json(args.config)
        require(isinstance(config, dict), 'invalid-local-config')
        context.update(config)
        options = dict(probe_cuda=args.probe_cuda, gate=args.gate)
        if args.preparation_evidence:
            options['preparation_bundle'] = args.preparation_evidence
        report = run_health(config, **options)
    except Exception as exc:
        report = dict(schemaVersion='situation-health-report-v1', status='failed', exitCode=1,
                      environmentReady=False, baseReady=False, inputBudgetReady=False,
                      resourceProbeReady=False, resourceAccepted=False, stage6Ready=False,
                      reasonCodes=['diagnostic-error'], error={'type': type(exc).__name__, 'message': str(exc)})
    if args.public:
        report = redact_public(report, context)
    if args.output:
        try:
            target = Path(args.output).absolute()
            if target.is_relative_to(REPO / 'datasets'):
                target = safe_child(REPO / 'datasets', target.relative_to(REPO / 'datasets'))
            else:
                root = storage_root(config['storageRoot'])
                require(target.is_relative_to(root), 'report-output-must-be-private-or-ignored')
                target = safe_child(root, target.relative_to(root))
            require(target.parent.is_dir(), 'report-output-parent-missing')
            with target.open('x', encoding='utf-8', newline='\n') as stream:
                json.dump(report, stream, ensure_ascii=False, indent=2, allow_nan=False)
                stream.write('\n')
        except Exception as exc:
            report['exitCode'] = 1
            report['reasonCodes'] = sorted(set(report.get('reasonCodes', []) + ['report-output-error']))
            report['outputError'] = {'type': type(exc).__name__, 'message': str(exc)}
            if args.public:
                report = redact_public(report, context)
    print(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False))
    return report['exitCode']


if __name__ == '__main__':
    raise SystemExit(main())
