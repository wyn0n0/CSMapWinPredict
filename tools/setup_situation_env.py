"""Stage-five step two. No package/model download, no training, no deletion."""
import argparse
import datetime
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys

from situation_environment import (CONFIG, LAYOUT, REPO, VENV, child_environment,
    copy_verified, dataset_inventory, digest, nearest_existing, read_json,
    safe_child, storage_root, write_new_json)

GIB = 1024**3
IDENTITY_CODE = '''import sys,site,json,importlib.util,ensurepip
print(json.dumps(dict(version=sys.version.split()[0],executable=sys.executable,
prefix=sys.prefix,basePrefix=sys.base_prefix,userSite=site.ENABLE_USER_SITE,
ensurepip=ensurepip.version(),torchVisible=importlib.util.find_spec('torch') is not None)))'''


def identity(python, env=None):
    p = subprocess.run([str(python), '-I', '-c', IDENTITY_CODE], env=env,
                       capture_output=True, text=True, check=True, timeout=60)
    return json.loads(p.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--storage-root', required=True)
    parser.add_argument('--python311', required=True)
    parser.add_argument('--preflight', default='datasets/situation-stage5-preflight-20260927-r1/preflight.json')
    parser.add_argument('--plan', action='store_true', help='Read-only; validate sources and print disk budget.')
    args = parser.parse_args()
    root = storage_root(args.storage_root)
    base = Path(args.python311).resolve(strict=True)
    base_identity = identity(base)
    if not base_identity['version'].startswith('3.11.'):
        raise ValueError('python-3.11-required')
    preflight_path = safe_child(REPO, args.preflight)
    preflight = read_json(preflight_path)
    if preflight['status'] != 'complete':
        raise ValueError('preflight-not-complete')
    for relative, expected in preflight['winEnvironment']['requirementsAndStartupHashes'].items():
        if digest(REPO/relative) != expected:
            raise ValueError('win-baseline-drift: '+relative)
    sources = [
        ('situation-stage4-pilot-20260919-r10', preflight['pilot']['manifestSha256']),
        ('situation-stage4-full-20260919-r1', preflight['base']['manifestSha256']),
        ('situation-stage4-review-frozen-20260927-r1', preflight['review']['manifestSha256']),
    ]
    inventories = {name:dataset_inventory(safe_child(REPO, 'datasets/'+name), expected) for name,expected in sources}
    for relative in LAYOUT:
        safe_child(root, relative)
    safe_child(REPO, '.venv/situation')
    safe_child(REPO, '.venv/situation.local.json')
    data_bytes = sum(x['bytes'] for items in inventories.values() for x in items)
    # File listing on 2026-09-27: two weight shards 3.44 GB + 622 MB,
    # plus tokenizer/config/license (~16 MB). Reserve 4 GiB per complete copy.
    budget = dict(datasetCopies=data_bytes*2, modelSnapshotAndCache=8*GIB,
                  dependencyCacheAndInstall=8*GIB, initialRuns=4*GIB, venvBootstrap=GIB//2)
    required = math.ceil(sum(budget.values())*1.2)
    free = shutil.disk_usage(nearest_existing(root)).free
    repo_free = shutil.disk_usage(REPO).free
    if free < required or repo_free < GIB:
        raise ValueError('disk-insufficient-for-budget')
    planned = dict(storageRoot=str(root), dataBytes=data_bytes, fileCount=sum(map(len,inventories.values())),
                   budgetBytes=budget, margin=0.2, requiredBytes=required, freeBytes=free,
                   repoFreeBytes=repo_free, python311=base_identity['version'],
                   estimateScope='step-two planning reservation; remeasure exact wheels/revision before installation/download')
    if args.plan:
        print(json.dumps(planned, ensure_ascii=False, indent=2))
        return
    if CONFIG.exists():
        prior = read_json(CONFIG)
        if Path(prior['storageRoot']).resolve()!=root or Path(prior['python311']).resolve()!=base:
            raise ValueError('local-config-conflict; existing config preserved')
    root.mkdir(parents=True, exist_ok=True)
    for relative in LAYOUT:
        safe_child(root, relative).mkdir(parents=True, exist_ok=True)
    run_id = 'setup-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    report_dir = safe_child(root, 'reports/'+run_id)
    report_dir.mkdir(exist_ok=False)
    write_new_json(report_dir/'started.json',dict(status='incomplete', plan=planned))
    env = child_environment(root)
    python = VENV/'Scripts/python.exe'
    try:
        reused = VENV.exists()
        if reused:
            ident = identity(python,env)
            if not ident['version'].startswith('3.11.') or Path(ident['prefix']).resolve()!=VENV.resolve() or Path(ident['basePrefix']).resolve()!=Path(base_identity['prefix']).resolve():
                raise ValueError('existing-venv-identity-mismatch')
        else:
            subprocess.run([str(base),'-I','-m','venv',str(VENV)],env=env,check=True,timeout=180)
            ident = identity(python,env)
        cfg_text=(VENV/'pyvenv.cfg').read_text(encoding='utf-8').lower()
        if ident['prefix']==ident['basePrefix'] or ident['userSite'] is not False or 'include-system-site-packages = false' not in cfg_text:
            raise ValueError('venv-not-isolated')
        if not reused and ident['torchVisible']:
            raise ValueError('new-venv-unexpected-torch')
        pip = subprocess.run([str(python),'-I','-m','pip','--version'],env=env,capture_output=True,text=True,check=True,timeout=60).stdout.strip()
        copies=[]
        for name,_ in sources:
            result=copy_verified(REPO/'datasets'/name,safe_child(root,'datasets/'+name),inventories[name])
            copies.append(dict(name=name,**result,manifestSha256=digest(REPO/'datasets'/name/'manifest.json')))
        evidence_source=preflight_path.parent
        evidence_items=[dict(path=p.name,bytes=p.stat().st_size,sha256=digest(p)) for p in evidence_source.iterdir() if p.is_file()]
        evidence=copy_verified(evidence_source,safe_child(root,'reports/'+evidence_source.name),evidence_items)
        for relative,expected in preflight['winEnvironment']['requirementsAndStartupHashes'].items():
            if digest(REPO/relative)!=expected:
                raise ValueError('win-baseline-changed')
        config=dict(schemaVersion='situation-local-paths-v1',storageRoot=str(root),python311=str(base),python=str(python),
                    repository=str(REPO),datasets={name:str(root/'datasets'/name) for name,_ in sources})
        if not CONFIG.exists():
            write_new_json(CONFIG,config)
        elif read_json(CONFIG)!=config:
            raise ValueError('local-config-content-conflict')
        result=dict(status='complete',runId=run_id,plan=planned,identity=ident,pip=pip,venvReused=reused,
                    copies=copies,preflightEvidence=evidence,sourceAndWinBaselineUnchanged=True,
                    privateConfig=str(CONFIG),cacheEnvironment={k:env[k] for k in ('SITUATION_STORAGE_ROOT','HF_HOME','HF_HUB_CACHE','TORCH_HOME','PIP_CACHE_DIR','TEMP')},
                    freeBytesAfter=shutil.disk_usage(root).free,
                    trainingReady=False,dependenciesInstalled=False,
                    sourceHashes={p.name:digest(p) for p in (Path(__file__),REPO/'tools/situation_environment.py')})
        write_new_json(report_dir/'complete.json',result)
        print(json.dumps(dict(status='complete',report=str(report_dir/'complete.json'),python=ident['version'],copiedFiles=sum(x['files'] for x in copies)),ensure_ascii=False))
    except Exception as exc:
        write_new_json(report_dir/'failed.json',dict(status='failed',errorType=type(exc).__name__,message=str(exc)))
        raise


if __name__=='__main__':
    main()
