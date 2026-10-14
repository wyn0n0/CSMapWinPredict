"""CLI wiring, train evidence integrity and public failure regression tests."""
import contextlib
import copy
import io
import json
import os
from pathlib import Path
import tempfile
import subprocess
import sys
import unittest
from unittest.mock import patch

from tools import check_situation_env as cli


class HealthCliTests(unittest.TestCase):
    def invoke(self, arguments, *, config=None, report=None, error=None):
        stdout = io.StringIO()
        value = report or dict(exitCode=2, environmentReady=True, baseReady=True,
                               inputBudgetReady=True, resourceProbeReady=False,
                               resourceAccepted=True, stage6Ready=False,
                               reasonCodes=['stage4-closeout-pending'])
        with patch.object(cli, 'read_json', return_value=config or {
                'repository': str(cli.REPO), 'storageRoot': r'G:\Private\Alice',
                'python': r'E:\Map\.venv\situation\Scripts\python.exe'}), \
             patch.object(cli, 'run_health', return_value=value, side_effect=error), \
             contextlib.redirect_stdout(stdout):
            code = cli.main(arguments)
        return code, json.loads(stdout.getvalue())

    def test_private_report_preserves_data_and_known_gate_failure(self):
        value = dict(exitCode=2, path=r'G:\Private\Alice\reports\run.json', stage6Ready=False)
        code, report = self.invoke([], report=value)
        self.assertEqual(code, 2)
        self.assertEqual(report['path'], value['path'])

    def test_public_exception_is_structured_and_redacted(self):
        code, report = self.invoke(['--public'], error=RuntimeError(
            r'failed at C:\Users\Alice\Secret\run.json token=abc123supersecret'))
        payload = json.dumps(report)
        self.assertEqual(code, 1)
        self.assertFalse(report['stage6Ready'])
        for private in ['Alice', 'abc123supersecret', r'C:\\Users']:
            self.assertNotIn(private, payload)

    def test_public_report_redacts_nested_keys_and_values(self):
        value = dict(exitCode=2, details={r'C:\Users\Alice\x.json': {
            'traceback': r'File "D:\Other\private.py", line 4', 'access_token': 'topsecretvalue'}})
        code, report = self.invoke(['--public', '--format', 'json'], report=value)
        payload = json.dumps(report)
        self.assertEqual(code, 2)
        for private in ['Alice', 'topsecretvalue', 'private.py']:
            self.assertNotIn(private, payload)

    def test_existing_output_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)
            target = repo / 'datasets' / 'health.json'
            target.parent.mkdir()
            target.write_text('original', encoding='utf-8')
            with patch.object(cli, 'REPO', repo):
                code, report = self.invoke(['--public', '--output', str(target)])
            self.assertEqual(code, 1)
            self.assertIn('report-output-error', report['reasonCodes'])
            self.assertEqual(target.read_text(encoding='utf-8'), 'original')

    def test_output_parent_escape_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp) / 'repo'
            target = Path(temp) / 'public.json'
            with patch.object(cli, 'REPO', repo):
                code, report = self.invoke(['--public', '--output', str(target)])
            self.assertEqual(code, 1)
            self.assertFalse(target.exists())

    def test_invalid_configuration_is_nonzero(self):
        code, report = self.invoke(['--public'], config=['not', 'a', 'mapping'])
        self.assertEqual(code, 1)
        self.assertEqual(report['reasonCodes'], ['diagnostic-error'])

    def test_pipe_json_is_utf8_independent_of_windows_locale(self):
        with tempfile.TemporaryDirectory() as temp:
            missing = Path(temp) / '\u4e2d\u6587\u7f3a\u5931.json'
            result = subprocess.run([sys.executable, str(cli.REPO / 'tools/check_situation_env.py'),
                                     '--config', str(missing)], capture_output=True,
                                    env=dict(os.environ, PYTHONIOENCODING='cp1252'))
        self.assertEqual(result.returncode, 1)
        report = json.loads(result.stdout.decode('utf-8'))
        self.assertIn('\u4e2d\u6587\u7f3a\u5931', report['error']['message'])

    def test_environment_gate_still_requires_dependency_acceptance(self):
        observations = {'checks': []}
        gates = dict(exitCode=2, environmentReady=True, baseReady=True, inputBudgetReady=True,
                     resourceProbeReady=False, resourceAccepted=True, stage6Ready=False)
        config = {'repository': str(cli.REPO), 'storageRoot': r'G:\PrivateRoot', 'python': sys.executable}
        with patch.object(cli, 'storage_root', return_value=Path(r'G:\PrivateRoot')), \
             patch.object(cli, 'read_json', side_effect=[{'winEnvironment': {}}, {'dependencyLockSha256': 'hash'}]), \
             patch.object(cli, 'digest', return_value='d7d4d162ec777c99018545eb37793d284e60e405c89436f82d28014bf9331d69'), \
             patch.object(cli, 'collect_observations', return_value=observations), \
             patch.object(cli, 'verify_input_budget', return_value={}), \
             patch.object(cli, 'audit_acceptance', return_value={'postProbeDependencyAcceptance': 'deferred'}), \
             patch.object(cli, 'evaluate_gates', return_value=gates):
            report = cli.run_health(config, gate='environment')
        self.assertEqual(report['exitCode'], 2)

    def test_environment_gate_success_does_not_claim_stage6(self):
        config = {'repository': str(cli.REPO), 'storageRoot': r'G:\PrivateRoot', 'python': sys.executable}
        gates = dict(exitCode=2, environmentReady=True, baseReady=True, inputBudgetReady=True,
                     resourceProbeReady=False, resourceAccepted=True, stage6Ready=False)
        with patch.object(cli, 'storage_root', return_value=Path(r'G:\PrivateRoot')), \
             patch.object(cli, 'read_json', side_effect=[{'winEnvironment': {}}, {'dependencyLockSha256': 'hash'}]), \
             patch.object(cli, 'digest', return_value='d7d4d162ec777c99018545eb37793d284e60e405c89436f82d28014bf9331d69'), \
             patch.object(cli, 'collect_observations', return_value={'checks': []}), \
             patch.object(cli, 'verify_input_budget', return_value={}), \
             patch.object(cli, 'audit_acceptance', return_value={'postProbeDependencyAcceptance': 'passed'}), \
             patch.object(cli, 'evaluate_gates', return_value=gates):
            report = cli.run_health(config, gate='environment')
        self.assertEqual(report['exitCode'], 0)
        self.assertFalse(report['stage6Ready'])
        self.assertFalse(report['resourceProbeReady'])


class BudgetEvidenceTests(unittest.TestCase):
    def test_missing_budget_file_is_a_failed_gate(self):
        config = {'repository': str(cli.REPO), 'storageRoot': r'G:\PrivateRoot'}
        with patch.object(cli, 'storage_root', return_value=Path(r'G:\PrivateRoot')), \
             patch.object(cli, 'read_json', side_effect=FileNotFoundError('missing-budget')):
            evidence = cli.verify_input_budget(config)
        self.assertFalse(evidence['inputBudgetReady'])
        self.assertFalse(evidence['verified'])
        self.assertEqual(evidence['reasonCodes'], ['input-evidence-mismatch'])

    def test_wrong_report_hash_cannot_be_ready(self):
        config = {'repository': str(cli.REPO), 'storageRoot': r'G:\PrivateRoot'}
        with patch.object(cli, 'storage_root', return_value=Path(r'G:\PrivateRoot')), \
             patch.object(cli, 'read_json', return_value={'budgetRun': 'run', 'budgetReportSha256': 'expected'}), \
             patch.object(cli, 'digest', return_value='different'):
            evidence = cli.verify_input_budget(config)
        self.assertFalse(evidence['inputBudgetReady'])
        self.assertFalse(evidence['verified'])
        self.assertEqual(evidence['status'], 'failed')


if __name__ == '__main__':
    unittest.main()
