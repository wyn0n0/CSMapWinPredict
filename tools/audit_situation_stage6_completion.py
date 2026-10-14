"""Read-only final cross-check of the 26-item preparation handoff.

The single new output is a repository audit record. Held-out bindings are hashed
only by EvidenceStore's isolated verifier; no test payload is decoded here.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess

from situation_environment import CONFIG, REPO, digest, read_json, safe_child, storage_root, write_new_json
from validate_situation_stage6_preparation import DEFAULT_BUNDLE, EvidenceStore, require
from verify_situation_stage6_admission import binding, verify_public


def audit(args):
    config = read_json(CONFIG)
    store = EvidenceStore(REPO, storage_root(config['storageRoot']))
    default_path = safe_child(store.storage, 'reports/' + args.default_run_id + '/default-admission-audit.json')
    require(digest(default_path) == args.default_audit_sha256, 'default-audit-drift')
    proof = store.report(binding(store, default_path))
    require(proof.get('status') == 'passed' and proof.get('reasonCodes') == []
            and proof.get('actualDefaultCliExitCode') == 0 and proof.get('preparationReady') is True
            and proof.get('trainingExecutionReady') is False and proof.get('publicRedactionVerified') is True,
            'default-admission-not-passed')
    health = read_json(default_path.parent / 'default-health-public.json')
    verify_public(health, config)
    require(health.get('exitCode') == 0 and health.get('preparationReady') is True
            and health.get('stage6Ready') is True and health.get('trainingExecutionReady') is False
            and health.get('resourceAccepted') is True and health.get('resourceProbeReady') is False
            and health.get('selectedRuntime') == 'nf4-rank16', 'default-health-state-invalid')
    command = read_json(default_path.parent / 'default-health-command.json')
    require(command.get('exitCode') == 0 and '--preparation-evidence' not in command['command']
            and command['command'][-2:] == ['--gate', 'stage6'], 'not-actual-default-command')
    bundle = read_json(REPO / DEFAULT_BUNDLE)
    require(digest(REPO / DEFAULT_BUNDLE) == proof['defaultBundle']['sha256'], 'default-bundle-drift')

    handoff = REPO / 'situation-implementation/stage6-goals-handoff'
    coverage = read_json(handoff / 'coverage.json')
    text = (REPO / 'TODO_6.md').read_text(encoding='utf-8')
    headings = ['## 步骤一：', '## 步骤二：', '## 步骤三：', '## 步骤四：']
    positions = [text.index(h) for h in headings]
    expected = {}
    for step, count in ((1, 6), (2, 12), (3, 8)):
        section = text[positions[step-1]:positions[step]]
        lines = [line for line in section.splitlines() if line.startswith('- [')]
        require(len(lines) == count and all(line.startswith('- [x] ') for line in lines), 'todo-not-complete')
        expected.update({f'S{step}-{index:02d}': line[6:] for index, line in enumerate(lines, 1)})
    require('- [x]' not in text[positions[3]:], 'later-step-marked-complete')
    items = coverage.get('items', [])
    require(len(items) == 26 and {i['id'] for i in items} == set(expected), 'coverage-scope-invalid')
    unique = {}
    for item in items:
        require(item['status'] == 'passed' and item['item'] == expected[item['id']]
                and bool(item['evidenceBindings']), 'coverage-item-invalid')
        for declared in item['evidenceBindings']:
            logical = declared['logicalPath']
            require(logical not in unique or unique[logical]['sha256'] == declared['sha256'], 'coverage-hash-conflict')
            unique[logical] = declared
    for declared in unique.values():
        store.verify(declared)
    for artifact in bundle['artifacts'].values():
        store.verify(artifact)

    protected = {
        'situation-implementation/situation-resource-status-v4.json': '3eaf0ae57871913eeeba5cf7c69fb94b35a48f32a7b09d10347508ef5f335ffd',
        'situation-implementation/situation-stage4-split-v1.json': 'fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f',
        'requirements-situation-win-cu126-qlora.lock.txt': '24363990112a62b37573d0979289ff3d0169f9099a6f6015868289f62e5e972c',
        'tools/prepare_situation_corpus.py': '05872c9cf0245f046ea2b5bf583f88594f2fa42c0358cc8628acc4f600b67a3f',
    }
    for name, sha in protected.items():
        require(digest(REPO / name) == sha, 'protected-input-drift')
    diff = subprocess.run(['git', 'diff', '--check'], cwd=REPO, capture_output=True, text=True)
    require(diff.returncode == 0, 'git-diff-check-failed')
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip()
    require(head == '6db1e3654f4cd1c6e7ab749280082a8ae75130e8', 'unexpected-git-head-change')
    documents = ['TODO.md', 'TODO_6.md', 'PROJECT_INDEX.md', 'docs/context/NOW.md',
        'docs/context/RUNBOOK.md', 'docs/context/MAP.md', 'docs/context/DECISIONS.md',
        'docs/context/RISKS.md', 'docs/context/history/2026-10.md',
        'situation-implementation/stage6-preparation-report-20261003.md',
        'situation-implementation/stage6-goals-handoff/README.md',
        'situation-implementation/stage6-goals-handoff/coverage.json',
        'situation-implementation/situation-stage6-experiment-budget-v1.json']
    bindings = [binding(store, default_path), binding(store, REPO / DEFAULT_BUNDLE),
                binding(store, Path(__file__).resolve())]
    bindings += [binding(store, REPO / name) for name in documents]
    bindings += list(unique.values())
    result = dict(schemaVersion='situation-stage6-completion-audit-v1', status='passed', reasonCodes=[],
        checkedAtUtc=datetime.now(timezone.utc).isoformat(), scope='TODO_6 steps 1-3 only',
        completedItems=26, remainingInScope=[], laterStepsMarkedComplete=False,
        actualDefaultCliExitCode=0, preparationReady=True, trainingExecutionReady=False,
        resourceAccepted=True, resourceProbeReady=False, selectedRuntime='nf4-rank16',
        gitHead=head, gitDiffCheckPassed=True, protectedInputsUnchanged=True,
        evidenceBindings=bindings, nextStep='TODO_6 step 4: evaluation protocol')
    output = handoff / 'completion-audit.json'
    require(not output.exists(), 'completion-audit-already-exists')
    write_new_json(output, result)
    print(json.dumps(dict(status='passed', completedItems=26, reportSha256=digest(output))))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--default-run-id', required=True)
    parser.add_argument('--default-audit-sha256', required=True)
    args = parser.parse_args()
    audit(args)


if __name__ == '__main__':
    main()
