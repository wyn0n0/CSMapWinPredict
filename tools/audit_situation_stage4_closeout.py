"""Metadata-only stage-four closeout audit. No payload access or worker imports.

The collector owns filesystem reads and actualSha256 calculation. This pure
validator requires those measured bindings; a claimed status alone never passes.
"""
import re

CHECKS = ('split', 'contracts', 'eligibility', 'selection', 'pilot', 'review', 'freeze')
SPLITS = {'train': 71, 'dev': 8, 'test': 8}
APPROVED = {'train': 36, 'dev': 7, 'test': 7}
PURPOSES = {'train': 'sft', 'dev': 'checkpoint-selection', 'test': 'final-evaluation'}


def audit_stage4_closeout(sources, expected):
    """Audit metadata, returning failed (never raising) for malformed evidence."""
    try:
        return _audit(sources, expected)
    except (AttributeError, KeyError, TypeError, ValueError):
        return {'schemaVersion': 'situation-stage6-evidence-v1', 'checkId': 'stage4-closeout',
                'status': 'failed', 'reasonCodes': ['malformed-evidence'], 'evidenceBindings': [],
                'details': {'verified': False, 'baseComplete': False, 'reviewComplete': False,
                            'reviewFrozen': False, 'approvedCounts': {}, 'splitCounts': {},
                            'regressionChecks': {}, 'trainingExecutionReady': False}}


def _audit(sources, expected):
    """Return frozen report shape; missing, stale and cross-use evidence fails.

    sources: base/review/split original metadata dictionaries; reviewMetadata;
    integrity (source-integrity-v1); regressions keyed by CHECKS; bindings.
    expected: frozen-contract.json or its baseline dictionary. Additional
    requiredBindings may list logical paths to require in sources.bindings.
    """
    reasons, bindings = [], []
    sources = sources if isinstance(sources, dict) else {}
    expected = expected if isinstance(expected, dict) else {}
    baseline = expected.get('baseline', expected)

    def check(ok, code):
        if not ok and code not in reasons:
            reasons.append(code)

    def bind(item, code):
        if not isinstance(item, dict):
            check(False, code)
            return False
        path, digest = item.get('logicalPath', ''), item.get('sha256', '')
        valid = (isinstance(path, str) and bool(re.fullmatch(r'(repo|storage):[^:\\\s]+', path))
                 and '..' not in path.split('/') and isinstance(digest, str)
                 and bool(re.fullmatch('[0-9a-f]{64}', digest))
                 and item.get('actualSha256') == digest)
        check(valid, code)
        if valid:
            bindings.append({k: item[k] for k in (
                'logicalPath', 'sha256', 'kind', 'schemaVersion', 'analysisRuleVersion',
                'reviewVersion', 'tokenizerRevision', 'split', 'purpose', 'complete',
                'frozen', 'scope') if k in item})
        return valid

    for key in ('base', 'review', 'split', 'reviewMetadata', 'integrity', 'regressions'):
        check(isinstance(sources.get(key), dict) and bool(sources[key]), 'missing-' + key)
    base, review, split, meta, integrity, regressions = [
        sources.get(k) if isinstance(sources.get(k), dict) else {}
        for k in ('base', 'review', 'split', 'reviewMetadata', 'integrity', 'regressions')]
    supplied = sources.get('bindings', [])
    check(isinstance(supplied, list) and bool(supplied), 'missing-bindings')
    if not isinstance(supplied, list):
        supplied = []
    for item in supplied:
        bind(item, 'binding-missing-or-drift')
    bound = {b['logicalPath']: b['sha256'] for b in bindings}
    base_path = 'storage:' + baseline.get('base_dataset', 'datasets/situation-stage4-full-20260919-r1') + '/manifest.json'
    review_path = 'storage:' + baseline.get('frozen_review', 'datasets/situation-stage4-review-frozen-20260927-r1') + '/manifest.json'
    split_path = 'repo:' + baseline.get('split_file', 'situation-implementation/situation-stage4-split-v1.json')
    for path in [base_path, review_path, split_path] + expected.get('requiredBindings', []):
        check(path in bound, 'required-binding-missing')
    check(bool(baseline.get('split_sha256')) and bound.get(split_path) == baseline.get('split_sha256'), 'split-hash-drift')
    check(base.get('status') == 'complete' and base.get('mode') == 'full', 'base-incomplete')
    check(base.get('schemaVersion') == 'situation-training-manifest-v1', 'base-schema-mismatch')
    check(base.get('purpose') == 'template-prelabel-training' and base.get('trainable') is True
          and base.get('sampleLimit') is None, 'base-purpose-mismatch')
    versions = base.get('versions', {})
    for key, value in {'analysisRules': 'situation-analysis-rules-v1', 'trainingRecord': 'situation-training-record-v1',
                       'facts': 'situation-facts-v1', 'scene': 'minimap-scene-v1', 'narrative': 'situation-narrative-v1'}.items():
        check(versions.get(key) == value, 'base-version-mismatch')
    check(base.get('splitSha256') == baseline.get('split_sha256') and bool(base.get('splitSha256')), 'base-split-mismatch')
    check(review.get('status') == 'complete', 'review-incomplete')
    check(review.get('reviewStatus') == 'frozen', 'review-not-frozen')
    check(review.get('schemaVersion') == 'situation-frozen-review-manifest-v2', 'review-schema-mismatch')
    check(review.get('reviewVersion') == baseline.get('review_version') and bool(baseline.get('review_version')), 'review-version-mismatch')
    check(review.get('workspaceRevision') == 51 and review.get('target') == 50 and review.get('counts') == APPROVED, 'review-scope-mismatch')
    check(review.get('baseDatasetSha256') == bound.get(base_path) and base_path in bound, 'review-base-mismatch')
    check(review.get('identity', {}).get('splitSha256') == baseline.get('split_sha256'), 'review-split-mismatch')
    check(bool(review.get('approvedContentSha256')) and review.get('approvedContentSha256') == review.get('contentSha256'), 'review-content-mismatch')
    check(meta.get('analysisRuleVersion') == baseline.get('target_rule') and bool(baseline.get('target_rule')), 'review-rule-mismatch')
    check(meta.get('purposes') == PURPOSES, 'purpose-cross-use')
    check(meta.get('approvedCount') == 50 and meta.get('unreviewedCount') == 0
          and meta.get('rejectedCount') == 0 and meta.get('blockingIssue') is False, 'review-approval-incomplete')
    members, counts = [], {}
    check(split.get('schemaVersion') == 'situation-stage4-split-v1', 'split-schema-mismatch')
    for role, size in SPLITS.items():
        rows = split.get(role, [])
        rows = rows if isinstance(rows, list) else []
        counts[role] = len(rows)
        check(len(rows) == size, 'split-count-mismatch')
        for row in rows:
            good = isinstance(row, dict) and bool(row.get('matchId')) and row.get('role') == role
            check(good, 'split-membership-invalid')
            if good:
                members.append(row['matchId'])
        check(base.get('counts', {}).get(role, {}).get('matches') == size, 'base-membership-count-mismatch')
        historical = {'train': (24416, 1526), 'dev': (3216, 201), 'test': (2752, 172)}[role]
        check((base.get('counts', {}).get(role, {}).get('samples'), base.get('counts', {}).get(role, {}).get('rounds')) == historical,
              'base-historical-count-mismatch')
    check(len(members) == 87 and len(set(members)) == 87, 'split-overlap-or-missing')
    # Bind schemas to actual current bytes, independently of any claimed test pass.
    for schemas in (base.get('schemaFiles'), review.get('identity', {}).get('schemaFiles')):
        check(isinstance(schemas, list) and bool(schemas), 'schema-evidence-missing')
        for schema in schemas or []:
            check(bound.get('repo:' + schema.get('path', '')) == schema.get('sha256') and bool(schema.get('sha256')), 'schema-hash-drift')
    artifacts = integrity.get('artifacts', {})
    check(integrity.get('status') == 'passed', 'integrity-failed')
    for manifest, path in ((base, base_path), (review, review_path)):
        name = path.rsplit('/', 2)[-2]
        artifact = artifacts.get(name, {})
        check(artifact.get('manifestSha256') == bound.get(path) and path in bound, 'integrity-manifest-mismatch')
        files = artifact.get('files', [])
        expected_files = {path.rsplit('/', 1)[0] + '/' + f['path']: f['sha256'] for f in manifest.get('files', [])}
        actual_files = {f.get('logicalPath'): f for f in files if isinstance(f, dict)}
        check(bool(expected_files) and set(actual_files) == set(expected_files) and len(actual_files) == len(files), 'integrity-file-coverage')
        for logical, digest in expected_files.items():
            item = actual_files.get(logical, {})
            check(item.get('status') == 'passed' and item.get('sha256') == digest and item.get('expectedSha256') == digest, 'integrity-file-drift')
    regression_details = {}
    for name in CHECKS:
        entry = regressions.get(name, {})
        valid = entry.get('status') == 'passed' and entry.get('exitCode') == 0 and bool(entry.get('command'))
        entries = entry.get('bindings', [])
        kinds = set()
        for item in entries:
            if bind(item, 'regression-binding-drift'):
                kinds.add(item.get('kind'))
        valid = valid and {'source', 'dependency', 'input'} <= kinds
        check(valid, 'regression-' + name + '-unverified')
        regression_details[name] = {'status': 'passed' if valid else 'not-tested', 'mode': entry.get('mode', 'rerun')}
    return {'schemaVersion': 'situation-stage6-evidence-v1', 'checkId': 'stage4-closeout',
            'status': 'failed' if reasons else 'passed', 'reasonCodes': reasons,
            'evidenceBindings': list({(b['logicalPath'], b['sha256']): b for b in bindings}.values()),
            'details': {'verified': not reasons, 'baseComplete': base.get('status') == 'complete',
                        'reviewComplete': review.get('status') == 'complete', 'reviewFrozen': review.get('reviewStatus') == 'frozen',
                        'approvedCounts': review.get('counts', {}), 'splitCounts': counts,
                        'regressionChecks': regression_details, 'historicalCandidateCount': 300,
                        'approvedScopeCount': 50, 'baseRuleVersion': versions.get('analysisRules'),
                        'reviewRuleVersion': meta.get('analysisRuleVersion'), 'trainingExecutionReady': False}}
