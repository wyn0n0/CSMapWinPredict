"""Extend the fixed lock with one pinned bitsandbytes wheel after two BF16 failures."""
import argparse
from datetime import datetime, timezone
import importlib.metadata
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import urlparse

from prepare_situation_base import require
from situation_environment import CONFIG, REPO, child_environment, digest, read_json, safe_child, storage_root, write_new_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--bf16-runs', nargs=2, required=True)
    args = parser.parse_args()
    require(all(re.fullmatch(r'[a-z0-9-]{1,96}', s) for s in [args.run_id, *args.bf16_runs]), 'invalid-run-id')
    require(len(set(args.bf16_runs)) == 2, 'need-independent-failures')
    config = read_json(CONFIG)
    root = storage_root(config['storageRoot'])
    require(Path(sys.executable).resolve() == Path(config['python']).resolve(), 'wrong-interpreter')
    failures = []
    for name in args.bf16_runs:
        path = safe_child(root, 'reports/' + name + '/memory-probe.json')
        report = read_json(path)
        require(report['precision'] == 'bf16' and report['status'] in ('oom', 'insufficient-headroom'), 'bf16-resource-failure-required')
        failures.append(dict(run=name, reportSha256=digest(path), pid=report['pid']))
    require(len({f['pid'] for f in failures}) == 2, 'fresh-processes-required')
    run = safe_child(root, 'reports/' + args.run_id)
    run.mkdir(parents=True, exist_ok=False)
    source = read_json(REPO / 'requirements-situation-win-cu126.sources.json')
    old_lock = REPO / 'requirements-situation-win-cu126.lock.txt'
    require(digest(old_lock) == source['lockSha256'], 'baseline-lock-drift')
    normalize = lambda s: re.sub(r'[-_.]+', '-', s).lower()
    require({normalize(d.metadata['Name']): d.version for d in importlib.metadata.distributions()} ==
            {normalize(p['name']): p['version'] for p in source['packages']}, 'baseline-environment-drift')
    candidate = read_json(REPO / 'situation-implementation/situation-qlora-candidate-v1.json')
    require(urlparse(candidate['url']).hostname == 'files.pythonhosted.org', 'unapproved-wheel-host')
    wheel_name = candidate['url'].rsplit('/', 1)[1]
    wheels = safe_child(root, 'cache/wheels/' + args.run_id)
    wheels.mkdir(parents=True, exist_ok=False)
    downloads = run / 'wheel-download.txt'
    downloads.write_text(candidate['url'] + ' --hash=sha256:' + candidate['sha256'] + '\n', encoding='utf-8')
    env = child_environment(root)
    env.update(PIP_CONFIG_FILE=os.devnull, PIP_DISABLE_PIP_VERSION_CHECK='1')
    for key in ('PIP_INDEX_URL', 'PIP_EXTRA_INDEX_URL', 'PIP_FIND_LINKS', 'PIP_TRUSTED_HOST'):
        env.pop(key, None)
    def command(label, argv):
        print(label, flush=True)
        with (run / (label + '.log')).open('x', encoding='utf-8') as handle:
            result = subprocess.run(argv, cwd=REPO, env=env, stdout=handle, stderr=subprocess.STDOUT)
        write_new_json(run / (label + '.command.json'), dict(command=argv, exitCode=result.returncode))
        require(result.returncode == 0, label + '-failed')
    write_new_json(run / 'started.json', dict(status='incomplete', failures=failures, candidate=candidate,
                                            createdUtc=datetime.now(timezone.utc).isoformat()))
    command('download', [sys.executable, '-m', 'pip', 'download', '--no-deps', '--require-hashes', '--dest', str(wheels), '-r', str(downloads)])
    wheel = wheels / wheel_name
    require(digest(wheel) == candidate['sha256'] and wheel.stat().st_size == candidate['bytes'], 'wheel-integrity-failed')
    lock = REPO / 'requirements-situation-win-cu126-qlora.lock.txt'
    with lock.open('x', encoding='utf-8', newline='\n') as handle:
        handle.write('# Conditional NF4 extension; original 52 package pins retained.\n' + old_lock.read_text(encoding='utf-8'))
        handle.write('\nbitsandbytes==' + candidate['version'] + ' --hash=sha256:' + candidate['sha256'] + '\n')
    packages = source['packages'] + [dict(name='bitsandbytes', version=candidate['version'], url=candidate['url'],
                                        sha256=candidate['sha256'], wheel=wheel_name)]
    write_new_json(REPO / 'requirements-situation-win-cu126-qlora.sources.json', dict(
        schemaVersion='situation-qlora-wheel-sources-v1', lockSha256=digest(lock), baselineLockSha256=source['lockSha256'],
        packages=sorted(packages, key=lambda p:p['name']), activationEvidence=failures, resourceProbePassed=False))
    command('install', [sys.executable, '-m', 'pip', 'install', '--no-index', '--find-links', str(wheels), '--require-hashes',
                        '--report', str(run / 'pip-install.json'), '-r', str(lock)])
    command('pip-check', [sys.executable, '-m', 'pip', 'check'])
    installed = {normalize(d.metadata['Name']): d.version for d in importlib.metadata.distributions()}
    require(installed == {normalize(p['name']): p['version'] for p in packages}, 'extended-environment-drift')
    write_new_json(run / 'installed.json', dict(status='installed', lockSha256=digest(lock), packages=installed,
        wheelSha256=digest(wheel), failures=failures, nf4ProbePassed=False, offlineRebuildPending=True))
    # Reuse the verified original wheelhouse, then reproduce the extended lock
    # in a NEW empty environment; no baseline package is upgraded.
    candidates = [p for p in (root / 'cache/wheels').iterdir() if p.is_dir() and
                  all((p / entry['wheel']).is_file() for entry in source['packages'])]
    require(bool(candidates), 'baseline-wheelhouse-missing')
    baseline_wheels = sorted(candidates)[0]
    for entry in source['packages']:
        require(digest(baseline_wheels / entry['wheel']) == entry['sha256'], 'baseline-wheelhouse-drift')
    rebuild = safe_child(root, 'tmp/' + args.run_id + '-rebuild')
    require(not rebuild.exists(), 'rebuild-already-exists')
    command('rebuild-create', [config['python311'], '-I', '-m', 'venv', str(rebuild)])
    rebuild_python = str(rebuild / 'Scripts/python.exe')
    command('rebuild-install', [rebuild_python, '-m', 'pip', 'install', '--no-index', '--find-links', str(wheels),
        '--find-links', str(baseline_wheels), '--require-hashes', '--report', str(run / 'pip-rebuild.json'), '-r', str(lock)])
    command('rebuild-check', [rebuild_python, '-m', 'pip', 'check'])
    command('smoke', [sys.executable, str(REPO / 'tools/check_situation_dependencies.py'), '--output', str(run / 'smoke.json')])
    command('rebuild-smoke', [rebuild_python, str(REPO / 'tools/check_situation_dependencies.py'), '--output', str(run / 'rebuild-smoke.json')])
    require(read_json(run / 'smoke.json')['installedVersions'] == read_json(run / 'rebuild-smoke.json')['installedVersions'], 'rebuild-versions-differ')
    write_new_json(run / 'complete.json', dict(status='conditional-lock-reproduced', lockSha256=digest(lock),
        packages=53, offlineRebuildPassed=True, cpuSmokePassed=True, resourceProbePassed=False,
        failures=failures, wheelSha256=digest(wheel)))
    print('Conditional 53-package lock installed and reproduced offline; NF4 resource probe still required.', flush=True)


if __name__ == '__main__':
    main()
