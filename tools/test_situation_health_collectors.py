"""Synthetic CPU tests; no production data, real CUDA, installs or model loads."""
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import prepare_situation_base as base
import situation_dependency_profile as profile
from tools import situation_health_collectors as health


class CollectorTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.workspace=Path(self.temp.name)
        self.repo=self.workspace/'repo'
        self.repo.mkdir()
        self.root=self.workspace/'storage'
        self.root.mkdir()
        self.prefix=self.repo/'.venv/situation'
        self.python=self.prefix/'Scripts/python.exe'
        self.python.parent.mkdir(parents=True)
        self.python.write_bytes(b'fixture')
        (self.prefix/'pyvenv.cfg').write_text('include-system-site-packages = false\n')
        for relative in ('cache/huggingface/hub','cache/huggingface/xet',
                         'cache/huggingface/assets','cache/torch','cache/pip','tmp'):
            (self.root/relative).mkdir(parents=True,exist_ok=True)
        self.config=dict(repository=str(self.repo),python=str(self.python),storageRoot=str(self.root))
        self.identity=dict(version='3.11.9',executable=str(self.python),prefix=str(self.prefix),
                           basePrefix=str(self.workspace/'original'),userSite=False,isolated=True,sysPath=[])
        self.dependency=dict(profile='requirements-situation-win-cu126',
            installedPackages={f'p{index}':'1' for index in range(52)},lockSha256='a'*64,sourcesSha256='b'*64,
            packageLocations=[str(self.prefix/'Lib/site-packages')])
        self.cuda=dict(torchVersion='2.8.0+cu126',cudaRuntime='12.6',capabilityStatus='not-tested',
                       devices=[],modelLoaded=False,tensorsAllocated=False)
        self.worker={name:dict(ok=True,data=value) for name,value in
                     [('python',self.identity),('dependencies',self.dependency),('cuda',self.cuda)]}
        self.inventory=[dict(index=0,name='fixture GPU',driverVersion='123',
            totalMemoryBytes=8*1024**3,freeMemoryBytes=5*1024**3,computeCapability=[8,9])]
        for name,value in (
                ('_worker',self.worker),('_gpu_inventory',self.inventory),
                ('_base',health._check('base','passed',details=dict(modelLoaded=False))),
                ('_run',SimpleNamespace(returncode=0,stdout='No broken requirements found.\n',stderr=''))):
            mocked=patch.object(health,name,return_value=value).start()
            self.addCleanup(patch.stopall)
            setattr(self,name+'_mock',mocked)
        patch.object(health.shutil,'disk_usage',return_value=SimpleNamespace(total=20*1024**3,free=10*1024**3)).start()

    def collect(self,**kwargs):
        result=health.collect_observations(self.config,**kwargs)
        self.assertEqual(result['schemaVersion'],'situation-health-observations-v1')
        self.assertEqual([item['checkId'] for item in result['checks']],list(health.CHECK_IDS))
        for item in result['checks']:
            self.assertEqual(set(item),{'checkId','status','reasonCodes','details','evidence'})
            self.assertIn(item['status'],{'passed','failed','not-tested','not-required'})
            self.assertIsInstance(item['details'],dict)
            self.assertIsInstance(item['evidence'],dict)
        return {item['checkId']:item for item in result['checks']}

    def test_default_read_only_does_not_enable_torch_cuda(self):
        parent=dict(health.os.environ)
        before={p.relative_to(self.workspace).as_posix():p.read_bytes()
                for p in self.workspace.rglob('*') if p.is_file()}
        values=self.collect()
        self.assertEqual(values['cuda']['status'],'not-tested')
        self.assertFalse(self._worker_mock.call_args.args[3])
        self.assertEqual(values['cuda']['details']['nvidiaSmi']['status'],'passed')
        self.assertEqual(values['cache']['evidence']['actualWriteTest'],'not-tested')
        self.assertEqual(values['isolation']['evidence']['baselineStatus'],'not-tested')
        self.assertEqual(values['isolation']['status'],'not-tested')
        self.assertTrue(values['isolation']['details']['selectedInterpreterIsolated'])
        self.assertEqual(parent,dict(health.os.environ))
        self.assertEqual(before,{p.relative_to(self.workspace).as_posix():p.read_bytes()
                for p in self.workspace.rglob('*') if p.is_file()})

    def enable_cuda(self,**changes):
        self.cuda.update(capabilityStatus='tested',available=True,
                         devices=[dict(index=0,name='fixture GPU',computeCapability=[8,9],nativeBf16=True)])
        self.cuda.update(changes)

    def test_opt_in_metadata_success_does_not_claim_resource_probe(self):
        self.enable_cuda()
        values=self.collect(probe_cuda=True)
        self.assertTrue(self._worker_mock.call_args.args[3])
        self.assertEqual(values['cuda']['status'],'passed')
        self.assertFalse(values['cuda']['evidence']['resourceProbePerformed'])
        self.assertFalse(values['cuda']['details']['modelLoaded'])
        self.assertFalse(values['cuda']['details']['tensorsAllocated'])

    def test_cuda_unavailable_bf16_and_wrong_sm_are_failures(self):
        for change,reason in [(dict(available=False,devices=[]),'cuda-unavailable'),
            (dict(devices=[dict(computeCapability=[8,9],nativeBf16=False)]),'bf16-unsupported'),
            (dict(devices=[dict(computeCapability=[8,6],nativeBf16=True)]),'cuda-capability-mismatch'),
            (dict(cudaRuntime='12.8'),'cuda-runtime-mismatch')]:
            with self.subTest(reason=reason):
                self.enable_cuda(**change)
                value=self.collect(probe_cuda=True)['cuda']
                self.assertEqual(value['status'],'failed')
                self.assertIn(reason,value['reasonCodes'])

    def test_nvidia_failure_retains_torch_version(self):
        self._gpu_inventory_mock.side_effect=FileNotFoundError('missing nvidia-smi')
        value=self.collect()['cuda']
        self.assertEqual(value['status'],'failed')
        self.assertEqual(value['details']['torchVersion'],'2.8.0+cu126')

    def test_missing_cuda_section_never_passes(self):
        del self.worker['cuda']
        self.assertEqual(self.collect(probe_cuda=True)['cuda']['status'],'failed')

    def test_python_version_wrong_interpreter_and_missing_field(self):
        for change in [dict(version='3.12.0'),dict(executable=str(self.repo/'foreign.exe')),dict(version=None)]:
            with self.subTest(change=change):
                original=self.identity.copy()
                self.identity.update(change)
                self.assertEqual(self.collect()['python']['status'],'failed')
                self.identity.clear()
                self.identity.update(original)

    def test_explicit_interpreter_failure_and_empty_worker_fail_all(self):
        self._worker_mock.side_effect=TimeoutError('timeout')
        for name in ('python','dependencies','cuda','isolation'):
            self.assertEqual(self.collect()[name]['status'],'failed')
        self._worker_mock.side_effect=None
        self._worker_mock.return_value={}
        for name in ('python','dependencies','cuda','isolation'):
            self.assertEqual(self.collect()[name]['status'],'failed')

    def test_both_exact_profile_counts_and_pip_check(self):
        self.assertEqual(self.collect()['dependencies']['status'],'passed')
        self.dependency['profile']='requirements-situation-win-cu126-qlora'
        self.assertEqual(self.collect()['dependencies']['status'],'failed')
        self.dependency['installedPackages']['bitsandbytes']='0.48.1'
        self.assertEqual(self.collect()['dependencies']['status'],'passed')
        self.assertEqual(self._run_mock.call_args.args[0],[str(self.python),'-I','-B','-m','pip','check'])
        self._run_mock.return_value=SimpleNamespace(returncode=1,stdout='conflict',stderr='')
        self.assertEqual(self.collect()['dependencies']['status'],'failed')

    def test_dependency_missing_and_exception_fail(self):
        self.worker['dependencies']=dict(ok=False,error=dict(message='lock drift'))
        values=self.collect()
        self.assertEqual(values['dependencies']['reasonCodes'],['dependency-conflict'])
        self.assertEqual(values['isolation']['status'],'failed')

    def test_selected_venv_isolation_user_global_and_foreign_packages(self):
        for key,value in [('userSite',True),('isolated',False),('basePrefix',str(self.prefix))]:
            with self.subTest(key=key):
                original=self.identity[key]
                self.identity[key]=value
                self.assertEqual(self.collect()['isolation']['status'],'failed')
                self.identity[key]=original
        self.dependency['packageLocations']=[str(self.workspace/'foreign/site-packages')]
        self.assertEqual(self.collect()['isolation']['status'],'failed')
        self.dependency['packageLocations']=[]
        self.assertEqual(self.collect()['isolation']['status'],'failed')

    def test_system_site_enabled_rejected(self):
        (self.prefix/'pyvenv.cfg').write_text('include-system-site-packages = true')
        self.assertEqual(self.collect()['isolation']['status'],'failed')

    def test_hash_baseline_and_no_traversal(self):
        path=self.repo/'requirements-inference.txt'
        path.write_bytes(b'old environment')
        self.config['isolationBaseline']=dict(winEnvironment=dict(requirementsAndStartupHashes={
            path.name:health.digest(path)}))
        self.assertEqual(self.collect()['isolation']['evidence']['baselineStatus'],'not-tested')
        self.assertIn('isolation-baseline-incomplete',self.collect()['isolation']['reasonCodes'])
        path.write_bytes(b'drift')
        self.assertEqual(self.collect()['isolation']['status'],'failed')
        self.config['isolationBaseline']['winEnvironment']['requirementsAndStartupHashes']={'../foreign':'a'*64}
        self.assertEqual(self.collect()['isolation']['status'],'failed')

    def test_original_interpreter_versions_and_parent_environment(self):
        path=self.repo/'requirements-inference.txt'
        path.write_bytes(b'baseline')
        self.config['isolationBaseline']=dict(requirementsAndStartupHashes={path.name:health.digest(path)},
            resolvedInThisShell=str(self.workspace/'old/python.exe'),installedVersions={'numpy':'1'},
            selectedProcessEnv={'TEST_HEALTH_PARENT_ENV':None})
        def run(command,env,timeout=60):
            if command[-1]=='check':
                return SimpleNamespace(returncode=0,stdout='ok',stderr='')
            return SimpleNamespace(returncode=0,stdout='{"numpy":"1"}',stderr='')
        self._run_mock.side_effect=run
        values=self.collect()
        self.assertEqual(values['isolation']['status'],'passed')
        self.assertEqual(values['isolation']['details']['baselineComparison']['originalInterpreterStatus'],'passed')
        self.config['isolationBaseline']['installedVersions']['numpy']='2'
        self.assertEqual(self.collect()['isolation']['status'],'failed')
        self.config['isolationBaseline']['installedVersions']['numpy']='1'
        with patch.dict(health.os.environ,TEST_HEALTH_PARENT_ENV='changed'):
            self.assertEqual(self.collect()['isolation']['status'],'failed')

    def test_missing_baseline_hashes_and_original_interpreter_fail(self):
        self.config['isolationBaseline']={}
        self.assertEqual(self.collect()['isolation']['status'],'failed')
        path=self.repo/'requirements-inference.txt'
        path.write_bytes(b'baseline')
        self.config['isolationBaseline']=dict(requirementsAndStartupHashes={path.name:health.digest(path)},
            resolvedInThisShell=str(self.workspace/'old/python.exe'),installedVersions={'numpy':'1'})
        self._run_mock.return_value=SimpleNamespace(returncode=1,stdout='',stderr='error')
        self.assertEqual(self.collect()['isolation']['status'],'failed')

    def test_only_exact_launcher_storage_root_override_is_allowed(self):
        path=self.repo/'requirements-inference.txt'
        path.write_bytes(b'baseline')
        self.config['isolationBaseline']=dict(requirementsAndStartupHashes={path.name:health.digest(path)},
            resolvedInThisShell=str(self.workspace/'old/python.exe'),installedVersions={'numpy':'1'},
            selectedProcessEnv={'SITUATION_STORAGE_ROOT':None,'WinInference__PythonExecutable':None})
        def run(command,env,timeout=60):
            return SimpleNamespace(returncode=0,stdout='ok' if command[-1]=='check' else '{"numpy":"1"}',stderr='')
        self._run_mock.side_effect=run
        with patch.dict(health.os.environ,SITUATION_STORAGE_ROOT=str(self.root)):
            result=self.collect()['isolation']
            self.assertEqual(result['status'],'passed')
            comparison=result['details']['baselineComparison']['selectedProcessEnv']['SITUATION_STORAGE_ROOT']
            self.assertTrue(comparison['overrideApplied'])
            self.assertFalse(comparison['unchanged'])
        with patch.dict(health.os.environ,SITUATION_STORAGE_ROOT=str(self.workspace/'wrong-root')):
            self.assertEqual(self.collect()['isolation']['status'],'failed')
        with patch.dict(health.os.environ,SITUATION_STORAGE_ROOT=str(self.root),WinInference__PythonExecutable='foreign'):
            self.assertEqual(self.collect()['isolation']['status'],'failed')
    def test_base_missing_and_hash_error_fail(self):
        for error,reason in [(FileNotFoundError('missing'),'base-missing'),
                             (ValueError('hash changed'),'base-hash-mismatch')]:
            self._base_mock.side_effect=error
            result=self.collect()['base']
            self.assertEqual(result['status'],'failed')
            self.assertIn(reason,result['reasonCodes'])

    def test_cache_no_write_missing_and_exception_fail(self):
        with patch.object(health.os,'access',return_value=False):
            self.assertEqual(self.collect()['cache']['status'],'failed')
        with patch.object(health.os,'access',side_effect=PermissionError('ACL')):
            self.assertEqual(self.collect()['cache']['status'],'failed')
        (self.root/'cache/torch').rmdir()
        self.assertEqual(self.collect()['cache']['status'],'failed')

    def test_disk_insufficient_invalid_minimum_and_exception(self):
        self.config['minimumFreeDiskBytes']=11*1024**3
        self.assertEqual(self.collect()['disk']['reasonCodes'],['disk-insufficient'])
        for value in (-1,True,'4'):
            self.config['minimumFreeDiskBytes']=value
            self.assertEqual(self.collect()['disk']['status'],'failed')
        self.config['minimumFreeDiskBytes']=0
        with patch.object(health.shutil,'disk_usage',side_effect=OSError('disk unavailable')):
            self.assertEqual(self.collect()['disk']['status'],'failed')

    def test_invalid_configuration_complete_failed_shape(self):
        for key in ('python','repository','storageRoot'):
            original=self.config.pop(key)
            self.assertTrue(all(value['status']=='failed' for value in self.collect().values()))
            self.config[key]=original


class ReusedVerifierTests(unittest.TestCase):
    def test_real_exact_profile_verifier_52_53_and_extra_package_rejection(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo=Path(temporary)
            for stem,count in [('requirements-situation-win-cu126',52),('requirements-situation-win-cu126-qlora',53)]:
                lock=repo/(stem+'.lock.txt')
                lock.write_text('pinned')
                packages=[dict(name=f'p{i}',version='1') for i in range(count)]
                (repo/(stem+'.sources.json')).write_text(json.dumps(dict(packages=packages,lockSha256=health.digest(lock))))
            with patch.object(profile,'REPO',repo):
                for count in (52,53,54):
                    installed=[SimpleNamespace(metadata={'Name':f'p{i}'},version='1') for i in range(count)]
                    with patch.object(profile.importlib.metadata,'distributions',return_value=installed):
                        if count==54:
                            with self.assertRaisesRegex(ValueError,'known-locks'):
                                profile.verify_installed_profile()
                        else:
                            self.assertEqual(len(profile.verify_installed_profile()['installedPackages']),count)
                (repo/'requirements-situation-win-cu126.lock.txt').write_text('tampered')
                with patch.object(profile.importlib.metadata,'distributions',return_value=[
                        SimpleNamespace(metadata={'Name':f'p{i}'},version='1') for i in range(52)]):
                    with self.assertRaisesRegex(ValueError,'lock-drift'):
                        profile.verify_installed_profile()

    def test_worker_exact_explicit_command_and_bad_json_fail(self):
        with patch.object(health,'_run',return_value=SimpleNamespace(returncode=0,stdout='{}',stderr='')) as run:
            health._worker(Path('fixture.exe'),Path('fixture-repo'),{'KEEP':'yes'},False)
            command=run.call_args.args[0]
            self.assertEqual(command[:4],['fixture.exe','-I','-B','-c'])
            self.assertFalse(json.loads(command[-1])['probeCuda'])
            self.assertEqual(run.call_args.args[1],{'KEEP':'yes'})
            run.return_value.stdout='invalid'
            with self.assertRaises(ValueError):
                health._worker(Path('fixture.exe'),Path('repo'),{},False)
            run.return_value.stdout='[]'
            with self.assertRaisesRegex(ValueError,'observation-invalid'):
                health._worker(Path('fixture.exe'),Path('repo'),{},False)

    def test_nvidia_inventory_requires_complete_numeric_evidence(self):
        with patch.object(health,'_run',return_value=SimpleNamespace(returncode=0,
                stdout='0, Synthetic GPU, 123.4, 8192, 5000, 8.9\n',stderr='')) as run:
            value=health._gpu_inventory({})[0]
            self.assertEqual(value['freeMemoryBytes'],5000*1024**2)
            self.assertEqual(value['computeCapability'],[8,9])
            for output in ('','0, GPU, 123, N/A, 5, 8.9','0, GPU, 123, 100, 101, 8.9','0, GPU, 123'):
                run.return_value.stdout=output
                with self.assertRaises(ValueError): health._gpu_inventory({})

    def test_full_synthetic_base_manifest_success_missing_and_tamper(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo=Path(temporary)/'repo'
            repo.mkdir()
            root=Path(temporary)/'storage'
            root.mkdir()
            lock=repo/'lock.txt'
            lock.write_bytes(b'pinned')
            reference=repo/'reference.json'
            revision='a'*40
            target=root/'bases'/('Qwen3-1.7B-'+revision)
            payloads={'LICENSE':b'Apache License Version 2.0, January 2004',
                'config.json':b'{"model_type":"qwen3","architectures":["Qwen3ForCausalLM"]}',
                'tokenizer_config.json':b'{"chat_template":"fixture template"}',
                'model.safetensors.index.json':b'{"weight_map":{"fixture":"model-00001-of-00001.safetensors"}}',
                'model-00001-of-00001.safetensors':b'synthetic weight bytes'}
            ref=dict(modelId='Qwen/Qwen3-1.7B',revision=revision,dependencyLockSha256=health.digest(lock),files=[])
            target.mkdir(parents=True)
            for name,payload in payloads.items():
                path=target/name
                path.write_bytes(payload)
                ref['files'].append(dict(path=name,bytes=len(payload),sha256=health.digest(path)))
            reference.write_text(json.dumps(ref))
            with patch.object(base,'REPO',repo),patch.object(base,'REFERENCE',reference),patch.object(base,'LOCK',lock):
                files,template_hash=base.validate_files(target,ref)
                hashes={item['path']:item['sha256'] for item in files}
                manifest=dict(status='complete',modelId=ref['modelId'],revision=revision,
                    referenceSha256=health.digest(reference),dependencyLockSha256=health.digest(lock),files=files,
                    chatTemplateSha256=template_hash,license=dict(id='Apache-2.0',path='LICENSE',sha256=hashes['LICENSE']),
                    tokenizerFiles={'tokenizer_config.json':hashes['tokenizer_config.json']})
                (target/'base-manifest.json').write_text(json.dumps(manifest))
                value=health._base(repo,root)
                self.assertEqual(value['status'],'passed')
                self.assertEqual(value['details']['filesVerified'],5)
                (target/'LICENSE').write_bytes(b'changed')
                with self.assertRaises(ValueError): health._base(repo,root)
                self.assertEqual(health._base(repo,root/'missing')['reasonCodes'],['base-missing'])


if __name__=='__main__':
    unittest.main()
