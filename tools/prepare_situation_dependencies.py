"""Resolve, install and reproduce the Windows text dependency lock.

Run each phase explicitly. Uses only official wheel sources, no model downloads.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from urllib.parse import unquote, urlparse

from situation_environment import (CONFIG, REPO, child_environment, digest,
    read_json, safe_child, storage_root, write_new_json)

LOCK = REPO/'requirements-situation-win-cu126.lock.txt'
SOURCES = REPO/'requirements-situation-win-cu126.sources.json'
HOSTS = {'files.pythonhosted.org','download.pytorch.org','download-r2.pytorch.org'}


def report_entries(report):
    entries=[]
    seen=set()
    for item in report['install']:
        name=re.sub(r'[-_.]+','-',item['metadata']['name']).lower()
        version=item['metadata']['version']
        url=item['download_info']['url']
        parsed=urlparse(url)
        sha=item['download_info']['archive_info']['hashes']['sha256']
        wheel=unquote(parsed.path.rsplit('/',1)[-1])
        if (parsed.scheme!='https' or parsed.hostname not in HOSTS or parsed.username
                or parsed.query or not wheel.endswith('.whl') or not re.fullmatch(r'[0-9a-f]{64}',sha)):
            raise ValueError('unexpected-wheel-source')
        if name in seen or not re.fullmatch(r'[A-Za-z0-9.+!_-]+',version):
            raise ValueError('duplicate-or-invalid-package')
        seen.add(name)
        entries.append(dict(name=name,version=version,url=url,sha256=sha,wheel=wheel))
    if not entries:
        raise ValueError('empty-resolution')
    return sorted(entries,key=lambda x:x['name'])


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('phase',choices=['resolve','install','rebuild'])
    p.add_argument('--run-id',required=True)
    p.add_argument('--torch-wheel',help='Previously downloaded exact official torch wheel; SHA-256 checked before use.')
    args=p.parse_args()
    if not re.fullmatch('[a-z0-9-]+',args.run_id):
        raise ValueError('invalid-run-id')
    config=read_json(CONFIG)
    root=storage_root(config['storageRoot'])
    run=safe_child(root,'reports/'+args.run_id)
    env=child_environment(root)
    env.update(PIP_CONFIG_FILE=os.devnull,PIP_DISABLE_PIP_VERSION_CHECK='1',
               HF_HUB_OFFLINE='1',HF_DATASETS_OFFLINE='1')
    # Explicit indexes/URLs are used below. Do not inherit global pip routing.
    for key in ('PIP_INDEX_URL','PIP_EXTRA_INDEX_URL','PIP_FIND_LINKS','PIP_TRUSTED_HOST'):
        env.pop(key,None)
    python=Path(config['python'])
    if python.resolve()!=(REPO/'.venv/situation/Scripts/python.exe').resolve():
        raise ValueError('unexpected-interpreter')
    wheels=safe_child(root,'cache/wheels/'+args.run_id)

    def command(label,argv):
        log=run/(label+'.log')
        if log.exists():
            raise ValueError('command-log-already-exists: '+label)
        print(label,flush=True)
        with log.open('x',encoding='utf-8') as stream:
            result=subprocess.run(argv,cwd=REPO,env=env,stdout=stream,stderr=subprocess.STDOUT)
        write_new_json(run/(label+'.command.json'),dict(command=[str(x) for x in argv],exitCode=result.returncode))
        if result.returncode:
            raise RuntimeError('command-failed: '+label+'; see private log')

    if args.phase=='resolve':
        if LOCK.exists() or SOURCES.exists():
            raise ValueError('lock-already-exists; use a reviewed new revision')
        run.mkdir(parents=True,exist_ok=False)
        write_new_json(run/'started.json',dict(status='incomplete',createdAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
            inputSha256=digest(REPO/'requirements-situation.in'),resolver='pip==24.0',platform='cp311-win_amd64',cudaRuntime='12.6'))
        resolver_input=REPO/'requirements-situation.in'
        if args.torch_wheel:
            seed=Path(args.torch_wheel).resolve(strict=True)
            if digest(seed)!='8afd366413aeb51a4732042719f168fae6f4c72326e59e9bdbe20a5c5be52418':
                raise ValueError('torch-seed-hash-mismatch')
            lines=resolver_input.read_text(encoding='utf-8').splitlines()
            official=next(line for line in lines if line.startswith('torch @ '))
            resolver_input=run/'seeded-requirements.in'
            with resolver_input.open('x',encoding='utf-8') as f:
                f.write('\n'.join('torch @ '+seed.as_uri()+'#sha256='+digest(seed) if line.startswith('torch @ ') else line for line in lines)+'\n')
            write_new_json(run/'torch-seed.json',dict(localPath=str(seed),sha256=digest(seed),officialRequirement=official))
        command('resolve',[str(python),'-m','pip','install','--dry-run','--ignore-installed','--only-binary=:all:',
            '--report',str(run/'pip-resolve.json'),'-r',str(resolver_input)])
        report=read_json(run/'pip-resolve.json')
        if report['pip_version']!='24.0':
            raise ValueError('resolver-version-drift')
        if args.torch_wheel:
            torch_entry=next(i for i in report['install'] if i['metadata']['name'].lower()=='torch')
            if unquote(torch_entry['download_info']['url'])!=unquote(seed.as_uri()):
                raise ValueError('unexpected-resolved-torch-seed')
            # Preserve the original resolver report. Public source provenance identifies
            # the official URL whose expected hash was verified against the local bytes.
            torch_entry['download_info']['url']=official.split(' @ ',1)[1].split('#',1)[0]
        entries=report_entries(report)
        lock=['# Windows x64 / CPython 3.11 / CUDA 12.6; wheels only.',
              '# Install offline from the verified wheelhouse with --require-hashes.',
              '# Official URLs and per-wheel hashes: requirements-situation-win-cu126.sources.json',
              '--only-binary :all:']
        for item in entries:
            lock.append(f"{item['name']}=={item['version']} --hash=sha256:{item['sha256']}")
        with LOCK.open('x',encoding='utf-8',newline='\n') as f:
            f.write('\n'.join(lock)+'\n')
        write_new_json(SOURCES,dict(schemaVersion='situation-wheel-sources-v1',resolver='pip==24.0',
            lockGenerator='tools/prepare_situation_dependencies.py',lockGeneratorSha256=digest(__file__),
            inputsSha256=digest(REPO/'requirements-situation.in'),lockSha256=digest(LOCK),packages=entries))
        print(json.dumps(dict(phase='resolved',packages=len(entries),lockSha256=digest(LOCK))),flush=True)
        return

    if not (run/'pip-resolve.json').is_file():
        raise ValueError('resolve-first')
    source=read_json(SOURCES)
    if source['lockSha256']!=digest(LOCK):
        raise ValueError('lock-hash-mismatch')
    entries=source['packages']
    if args.phase=='install':
        wheels.mkdir(parents=True,exist_ok=True)
        download_list=run/'wheel-downloads.txt'
        with download_list.open('x',encoding='utf-8') as f:
            f.write('\n'.join(f"{i['url']} --hash=sha256:{i['sha256']}" for i in entries)+'\n')
        # Report URL list uses pinned wheel URLs, no dependency re-resolution.
        command('download',[str(python),'-m','pip','download','--no-deps','--require-hashes','--dest',str(wheels),'-r',str(download_list)])
        sizes=[]
        for i in entries:
            path=safe_child(wheels,i['wheel'])
            if digest(path)!=i['sha256']:
                raise ValueError('wheel-hash-mismatch')
            sizes.append(dict(wheel=i['wheel'],bytes=path.stat().st_size,sha256=i['sha256']))
        compressed=sum(i['bytes'] for i in sizes)
        if shutil.disk_usage(REPO).free < compressed*3 or shutil.disk_usage(root).free < compressed*4:
            raise ValueError('insufficient-space-for-install-and-rebuild')
        write_new_json(run/'wheel-inventory.json',dict(files=sizes,totalBytes=compressed,
            repoFree=shutil.disk_usage(REPO).free,storageFree=shutil.disk_usage(root).free))
        command('install',[str(python),'-m','pip','install','--no-index','--find-links',str(wheels),
            '--require-hashes','--report',str(run/'pip-install.json'),'-r',str(LOCK)])
        command('pip-check',[str(python),'-m','pip','check'])
        command('pip-freeze',[str(python),'-m','pip','freeze','--all'])
        command('smoke',[str(python),str(REPO/'tools/check_situation_dependencies.py'),'--output',str(run/'smoke.json')])
        write_new_json(run/'installed.json',dict(status='installed-smoke-passed',lockSha256=digest(LOCK),
            resourceProbePassed=False,scope='dependency-API-only'))
    else:
        checkenv=safe_child(root,'tmp/'+args.run_id+'-rebuild')
        if checkenv.exists():
            raise ValueError('rebuild-environment-already-exists')
        command('rebuild-create',[config['python311'],'-I','-m','venv',str(checkenv)])
        checkpython=checkenv/'Scripts/python.exe'
        command('rebuild-install',[str(checkpython),'-m','pip','install','--no-index','--find-links',str(wheels),
            '--require-hashes','--report',str(run/'pip-rebuild-install.json'),'-r',str(LOCK)])
        command('rebuild-check',[str(checkpython),'-m','pip','check'])
        command('rebuild-smoke',[str(checkpython),str(REPO/'tools/check_situation_dependencies.py'),'--output',str(run/'rebuild-smoke.json')])
        a,b=read_json(run/'smoke.json'),read_json(run/'rebuild-smoke.json')
        if a['installedVersions']!=b['installedVersions']:
            raise ValueError('rebuild-version-mismatch')
        write_new_json(run/'complete.json',dict(status='dependency-lock-reproduced',lockSha256=digest(LOCK),
            sourcesSha256=digest(SOURCES),packages=len(entries),cpuSmokePassed=True,offlineRebuildPassed=True,
            stage7ResourceProbePassed=False,finalLockAcceptance='pending-real-base-memory-probe-in-step-seven',
            rebuildEnvironment=str(checkenv)))
    print(json.dumps(dict(phase=args.phase,status='passed',reportDirectory=str(run))),flush=True)


if __name__=='__main__':
    main()
