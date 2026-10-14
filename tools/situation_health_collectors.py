"""Read-only stage-five observations. No models, tensors, installs or repairs.

All paths/errors in this result are PRIVATE. The CLI must apply public redaction.
CUDA opt-in inspects capability metadata only; it is not a resource/training probe.
"""
import csv
import io
import json
import os
from pathlib import Path
import shutil
import subprocess

try:
    from .situation_environment import child_environment, digest, safe_child, storage_root
except ImportError:
    from situation_environment import child_environment, digest, safe_child, storage_root

CHECK_IDS = ('python', 'dependencies', 'cuda', 'base', 'cache', 'disk', 'isolation')
MODULE_DIR = Path(__file__).resolve().parent

# Executed by the explicitly selected interpreter, isolated from user/global site.
# Each section fails independently, so dependency errors retain Python identity.
WORKER_CODE = r'''
import importlib.metadata, json, pathlib, site, sys
settings=json.loads(sys.argv[1])
sys.path.insert(0,settings['moduleDir'])
result={}
def observe(name, function):
    try: result[name]=dict(ok=True,data=function())
    except Exception as exc: result[name]=dict(ok=False,error=dict(type=type(exc).__name__,message=str(exc)))
def identity():
    return dict(version=sys.version.split()[0],executable=sys.executable,prefix=sys.prefix,
                basePrefix=sys.base_prefix,userSite=site.ENABLE_USER_SITE,
                isolated=bool(sys.flags.isolated),sysPath=sys.path)
def dependencies():
    import situation_dependency_profile as profile
    profile.REPO=pathlib.Path(settings['repository'])
    verified=profile.verify_installed_profile()
    verified['packageLocations']=[str(d.locate_file('')) for d in importlib.metadata.distributions()]
    return verified
def cuda():
    import torch
    value=dict(torchVersion=str(torch.__version__),cudaRuntime=torch.version.cuda,
               capabilityStatus='not-tested',devices=[],modelLoaded=False,tensorsAllocated=False)
    if settings['probeCuda']:
        available=bool(torch.cuda.is_available())
        value.update(available=available,capabilityStatus='tested')
        if available:
            for index in range(torch.cuda.device_count()):
                with torch.cuda.device(index):
                    value['devices'].append(dict(index=index,name=torch.cuda.get_device_name(index),
                        computeCapability=list(torch.cuda.get_device_capability(index)),
                        nativeBf16=bool(torch.cuda.is_bf16_supported(including_emulation=False))))
    return value
observe('python',identity)
observe('dependencies',dependencies)
observe('cuda',cuda)
print(json.dumps(result))
'''


def _check(check_id, status, reasons=(), details=None, evidence=None):
    return dict(checkId=check_id, status=status, reasonCodes=sorted(set(reasons)),
                details={} if details is None else details,
                evidence={} if evidence is None else evidence)


def _error(exc):
    return dict(type=type(exc).__name__, message=str(exc))


def _run(command, env, timeout=60):
    return subprocess.run(command, env=env, capture_output=True, text=True,
                          encoding='utf-8', errors='replace', timeout=timeout, check=False)


def _worker(python, repository, env, probe_cuda):
    settings=dict(moduleDir=str(MODULE_DIR),repository=str(repository),probeCuda=probe_cuda)
    command=[str(python), '-I', '-B', '-c', WORKER_CODE, json.dumps(settings)]
    result=_run(command,env)
    if result.returncode != 0:
        raise RuntimeError('interpreter-observation-failed: '+result.stderr.strip())
    value=json.loads(result.stdout)
    if not isinstance(value,dict):
        raise ValueError('interpreter-observation-invalid')
    return value


def _section(worker, name):
    value=worker.get(name)
    if not isinstance(value,dict) or value.get('ok') is not True or not isinstance(value.get('data'),dict):
        raise ValueError('observation-missing-or-failed: '+name+' '+json.dumps(value))
    return value['data']


def _gpu_inventory(env):
    command=['nvidia-smi','--query-gpu=index,name,driver_version,memory.total,memory.free,compute_cap',
             '--format=csv,noheader,nounits']
    result=_run(command,env,timeout=20)
    if result.returncode != 0:
        raise RuntimeError('nvidia-smi-failed: '+result.stderr.strip())
    values=[]
    for row in csv.reader(io.StringIO(result.stdout)):
        if len(row)!=6:
            raise ValueError('nvidia-smi-fields-missing')
        index,name,driver,total,free,capability=(item.strip() for item in row)
        total_bytes,free_bytes=int(total)*1024**2,int(free)*1024**2
        if not name or not driver or total_bytes<=0 or not 0<=free_bytes<=total_bytes:
            raise ValueError('nvidia-smi-values-invalid')
        major,minor=(int(part) for part in capability.split('.'))
        values.append(dict(index=int(index),name=name,driverVersion=driver,
                           totalMemoryBytes=total_bytes,freeMemoryBytes=free_bytes,
                           computeCapability=[major,minor]))
    if not values:
        raise ValueError('nvidia-smi-no-devices')
    return values


def _base(repository, root):
    # Reuse full pinned-file/manifest/reference/lock verification, without invoking
    # the old check_situation_base CLI (that CLI loads and generates with a model).
    import sys
    if str(MODULE_DIR) not in sys.path:
        sys.path.insert(0,str(MODULE_DIR))
    import prepare_situation_base as base
    # This legacy module has repository globals. Reject a different repository
    # instead of silently checking files belonging to another checkout.
    if repository != base.REPO.resolve():
        raise ValueError('base-repository-mismatch')
    ref=base.reference()
    path=base.destination(root,ref)
    if not path.is_dir():
        return _check('base','failed',['base-missing'],dict(directory=str(path),revision=ref['revision']))
    manifest=base.verify_base(path,ref)
    return _check('base','passed',details=dict(directory=str(path),modelId=ref['modelId'],
        revision=ref['revision'],filesVerified=len(manifest['files']),modelLoaded=False),
        evidence=dict(manifestSha256=digest(path/'base-manifest.json'),
                      referenceSha256=digest(base.REFERENCE),dependencyLockSha256=manifest['dependencyLockSha256'],
                      files=manifest['files']))


def _baseline(repository, baseline, env):
    if baseline is None:
        return dict(status='not-tested',reasonCodes=['isolation-baseline-not-provided'])
    if not isinstance(baseline,dict):
        raise ValueError('isolation-baseline-invalid')
    baseline=baseline.get('winEnvironment',baseline)
    hashes=baseline.get('requirementsAndStartupHashes')
    if not isinstance(hashes,dict) or not hashes:
        raise ValueError('isolation-baseline-hashes-missing')
    files={}
    for relative,expected in hashes.items():
        path=safe_child(repository,relative)
        actual=digest(path)
        files[relative]=dict(expectedSha256=expected,actualSha256=actual,unchanged=actual==expected)
    reasons=[]
    if not all(item['unchanged'] for item in files.values()):
        reasons.append('isolation-baseline-mismatch')
    values=dict(status='passed',files=files,reasonCodes=reasons)
    expected_env=baseline.get('selectedProcessEnv')
    if expected_env is not None:
        if not isinstance(expected_env,dict):
            raise ValueError('isolation-baseline-environment-invalid')
        comparison={}
        for key,value in expected_env.items():
            actual=os.environ.get(key)
            unchanged=actual==value
            override=(key=='SITUATION_STORAGE_ROOT' and value is None
                      and actual is not None and actual==env['SITUATION_STORAGE_ROOT'])
            comparison[key]=dict(expected=value,actual=actual,unchanged=unchanged,
                                 overrideApplied=override,accepted=unchanged or override)
        values['selectedProcessEnv']=comparison
        if not all(item['accepted'] for item in comparison.values()):
            reasons.append('isolation-process-environment-mismatch')
    interpreter=baseline.get('resolvedInThisShell')
    packages=baseline.get('installedVersions')
    values['originalInterpreterStatus']='not-tested'
    if interpreter and packages:
        if not Path(interpreter).is_absolute() or not isinstance(packages,dict):
            raise ValueError('isolation-baseline-interpreter-invalid')
        code='import importlib.metadata,json,sys; names=json.loads(sys.argv[1]); print(json.dumps({n:importlib.metadata.version(n) for n in names}))'
        original_env=dict(os.environ)
        original_env.pop('PYTHONHOME',None)
        original_env.pop('PYTHONPATH',None)
        original_env['PYTHONDONTWRITEBYTECODE']='1'
        result=_run([interpreter,'-I','-B','-c',code,json.dumps(list(packages))],original_env)
        if result.returncode!=0:
            raise RuntimeError('original-interpreter-inspection-failed: '+result.stderr.strip())
        actual=json.loads(result.stdout)
        values.update(originalInterpreterStatus='passed' if actual==packages else 'failed',
                      originalInterpreter=str(interpreter),originalInstalledVersions=actual,
                      expectedOriginalInstalledVersions=packages)
        if actual!=packages:
            reasons.append('isolation-original-dependencies-mismatch')
    complete=expected_env is not None and values['originalInterpreterStatus']=='passed'
    if not reasons and not complete:
        reasons.append('isolation-baseline-incomplete')
    values['status']='failed' if any(reason!='isolation-baseline-incomplete' for reason in reasons) else ('passed' if complete else 'not-tested')
    return values


def collect_observations(config, *, probe_cuda=False):
    """Return all frozen observations; exceptions always fail the affected check.

    Required config: repository, python and storageRoot absolute paths.
    Optional: minimumFreeDiskBytes (default 4 GiB), isolationBaseline (the
    preflight winEnvironment or full preflight). Missing baseline comparison is
    explicitly not-tested, independently of selected-venv identity validation.
    Cache passed means read-only os.access inspection; file creation is untested.
    """
    checks={}
    try:
        repository=Path(config['repository'])
        python=Path(config['python'])
        if not repository.is_absolute() or not python.is_absolute():
            raise ValueError('repository-and-python-must-be-absolute')
        repository=repository.resolve(strict=True)
        python=python.resolve(strict=True)
        root=storage_root(config['storageRoot'],repo=repository)
        env=child_environment(root)
        env.update(PYTHONDONTWRITEBYTECODE='1',HF_HUB_OFFLINE='1',HF_DATASETS_OFFLINE='1',
                   TRANSFORMERS_OFFLINE='1',HF_HUB_DISABLE_TELEMETRY='1')
    except Exception as exc:
        return dict(schemaVersion='situation-health-observations-v1',checks=[
            _check(name,'failed',['configuration-invalid'],details=dict(error=_error(exc))) for name in CHECK_IDS])
    try:
        worker=_worker(python,repository,env,probe_cuda)
    except Exception as exc:
        worker={}
        for name in ('python','dependencies','cuda','isolation'):
            checks[name]=_check(name,'failed',['interpreter-inspection-failed'],details=dict(error=_error(exc)))
    if worker:
        try:
            identity=_section(worker,'python')
            if Path(identity['executable']).resolve()!=python:
                raise ValueError('wrong-interpreter')
            valid=identity['version'].split('.')[:2]==['3','11']
            checks['python']=_check('python','passed' if valid else 'failed',
                [] if valid else ['python-version-mismatch'],identity,
                dict(explicitInterpreter=True,isolatedFlag=identity.get('isolated') is True))
        except Exception as exc:
            checks['python']=_check('python','failed',['python-inspection-failed'],details=dict(error=_error(exc)))
        try:
            dependency=_section(worker,'dependencies')
            count=len(dependency['installedPackages'])
            expected={'requirements-situation-win-cu126':52,'requirements-situation-win-cu126-qlora':53}
            if expected.get(dependency['profile'])!=count:
                raise ValueError('unexpected-dependency-profile-count')
            pip=_run([str(python),'-I','-B','-m','pip','check'],env)
            checks['dependencies']=_check('dependencies','passed' if pip.returncode==0 else 'failed',
                [] if pip.returncode==0 else ['dependency-conflict'],
                dict(profile=dependency['profile'],packageCount=count,installedVersions=dependency['installedPackages'],
                     pipCheck=dict(exitCode=pip.returncode,stdout=pip.stdout.strip(),stderr=pip.stderr.strip())),
                dict(lockSha256=dependency['lockSha256'],sourcesSha256=dependency['sourcesSha256'],exactProfile=True))
        except Exception as exc:
            checks['dependencies']=_check('dependencies','failed',['dependency-conflict'],details=dict(error=_error(exc)))
        try:
            identity=_section(worker,'python')
            prefix=Path(identity['prefix']).resolve()
            expected_prefix=python.parent.parent if python.parent.name.lower() in ('scripts','bin') else python.parent
            cfg=prefix/'pyvenv.cfg'
            cfg_text=cfg.read_text(encoding='utf-8').lower()
            dependency=_section(worker,'dependencies')
            isolated=(prefix==expected_prefix and prefix!=Path(identity['basePrefix']).resolve()
                      and identity['userSite'] is False and identity['isolated'] is True
                      and 'include-system-site-packages = false' in cfg_text
                      and bool(dependency['packageLocations'])
                      and all(Path(path).resolve().is_relative_to(prefix) for path in dependency['packageLocations']))
            comparison=_baseline(repository,config.get('isolationBaseline'),env)
            reasons=[] if isolated else ['interpreter-not-isolated']
            failed=bool(reasons) or comparison['status']=='failed'
            reasons.extend(comparison.get('reasonCodes',[]))
            checks['isolation']=_check('isolation','failed' if failed else comparison['status'],reasons,
                dict(selectedInterpreterIsolated=isolated,baselineComparison=comparison),
                dict(pyvenvConfigSha256=digest(cfg),prefix=str(prefix),basePrefix=identity['basePrefix'],
                     packageLocations=dependency['packageLocations'],baselineStatus=comparison['status']))
        except Exception as exc:
            checks['isolation']=_check('isolation','failed',['isolation-inspection-failed'],details=dict(error=_error(exc)))
    cuda={}
    try:
        cuda=_section(worker,'cuda')
        inventory=_gpu_inventory(env)
        cuda['nvidiaSmi']=dict(status='passed',devices=inventory)
        reasons=[]
        if cuda['cudaRuntime']!='12.6':
            reasons.append('cuda-runtime-mismatch')
        if probe_cuda:
            if cuda.get('available') is not True or not cuda.get('devices'):
                reasons.append('cuda-unavailable')
            elif any(device['computeCapability']!=[8,9] for device in cuda['devices']):
                reasons.append('cuda-capability-mismatch')
            if cuda.get('devices') and any(device['nativeBf16'] is not True for device in cuda['devices']):
                reasons.append('bf16-unsupported')
        else:
            reasons.append('cuda-capability-not-tested')
        status='failed' if any(reason!='cuda-capability-not-tested' for reason in reasons) else ('passed' if probe_cuda else 'not-tested')
        checks['cuda']=_check('cuda',status,reasons,cuda,
            dict(scope='metadata-only; no allocation, model, operations or training',resourceProbePerformed=False,
                 torchCudaStatus='passed' if probe_cuda and status=='passed' else ('failed' if probe_cuda else 'not-tested')))
    except Exception as exc:
        previous=cuda or checks.get('cuda',{}).get('details',{})
        checks['cuda']=_check('cuda','failed',['cuda-unavailable'],dict(previous,error=_error(exc)),
                             dict(resourceProbePerformed=False))
    try:
        checks['base']=_base(repository,root)
    except Exception as exc:
        checks['base']=_check('base','failed',['base-missing' if isinstance(exc,FileNotFoundError) else 'base-hash-mismatch'],
                              details=dict(error=_error(exc),modelLoaded=False))
    try:
        directories=[]
        for relative in ('cache/huggingface','cache/huggingface/hub','cache/huggingface/xet',
                         'cache/huggingface/assets','cache/torch','cache/pip','tmp'):
            path=safe_child(root,relative)
            directories.append(dict(path=str(path),exists=path.is_dir(),
                readable=path.is_dir() and os.access(path,os.R_OK),
                writable=path.is_dir() and os.access(path,os.W_OK),
                searchable=path.is_dir() and os.access(path,os.X_OK)))
        accessible=all(item['exists'] and item['readable'] and item['writable'] and item['searchable'] for item in directories)
        checks['cache']=_check('cache','passed' if accessible else 'failed',[] if accessible else ['cache-not-writable'],
            dict(directories=directories,environment={key:env[key] for key in
                ('HF_HOME','HF_HUB_CACHE','HF_XET_CACHE','HF_ASSETS_CACHE','TORCH_HOME','PIP_CACHE_DIR','TEMP')}),
            dict(method='read-only os.access; Windows ACL and real creation not verified',actualWriteTest='not-tested'))
    except Exception as exc:
        checks['cache']=_check('cache','failed',['cache-not-writable'],details=dict(error=_error(exc)))
    try:
        minimum=config.get('minimumFreeDiskBytes',4*1024**3)
        if type(minimum) is not int or minimum<0:
            raise ValueError('minimum-free-disk-bytes-invalid')
        if not root.is_dir():
            raise FileNotFoundError('storage-root-missing')
        disks=[]
        for path in (root,repository):
            usage=shutil.disk_usage(path)
            if usage.total<=0 or not 0<=usage.free<=usage.total:
                raise ValueError('disk-usage-invalid')
            disks.append(dict(path=str(path),totalBytes=usage.total,freeBytes=usage.free,requiredBytes=minimum))
        enough=all(item['freeBytes']>=minimum for item in disks)
        checks['disk']=_check('disk','passed' if enough else 'failed',[] if enough else ['disk-insufficient'],
                             dict(volumes=disks),dict(scope='diagnostic minimum; no training reservation'))
    except Exception as exc:
        checks['disk']=_check('disk','failed',['disk-insufficient'],details=dict(error=_error(exc)))
    return dict(schemaVersion='situation-health-observations-v1',checks=[checks.get(name,
        _check(name,'failed',['observation-missing'])) for name in CHECK_IDS])
