"""Predecessor identity and completion boundaries for the real CLI audit."""
from datetime import datetime, timezone
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import psutil
import verify_situation_stage6_admission as admission
from situation_environment import write_new_json
from validate_situation_stage6_preparation import EvidenceError, EvidenceStore


class AdmissionWaitTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.repo, self.storage = root / 'repo', root / 'storage'
        self.repo.mkdir(); self.storage.mkdir()
        (self.repo / 'consumer.py').write_text('frozen', encoding='utf-8')
        self.run = self.storage / 'runs' / 'run'
        self.run.mkdir(parents=True)
        self.out = self.storage / 'audit'; self.out.mkdir()
        self.store = EvidenceStore(self.repo, self.storage)
        self.args = SimpleNamespace(source_run_id='run')
        write_new_json(self.run / 'finish-launch.json', dict(pid=123,
            startedUtc=datetime.fromtimestamp(1000, timezone.utc).isoformat()))
        self.process = Mock(pid=123)
        self.process.cmdline.return_value = ['python', 'prepare.py', 'finish', '--run-id', 'run']
        self.process.create_time.return_value = 1000
        for name, value in [('REPO', self.repo), ('SOURCES', ['consumer.py'])]:
            p = patch.object(admission, name, value); p.start(); self.addCleanup(p.stop)

    def test_wait_timeout_does_not_restart_or_advance_without_completion(self):
        self.process.wait.side_effect = [psutil.TimeoutExpired(30), 0]
        with patch.object(psutil, 'Process', return_value=self.process) as factory:
            with self.assertRaisesRegex(EvidenceError, 'finish-completion-missing'):
                admission.wait_for_finish(self.args, self.store, self.out)
        self.assertEqual(factory.call_count, 1)
        self.assertEqual(self.process.wait.call_count, 2)
        self.process.terminate.assert_not_called()

    def test_reused_pid_fails_before_wait(self):
        self.process.create_time.return_value = 9999
        with patch.object(psutil, 'Process', return_value=self.process):
            with self.assertRaisesRegex(EvidenceError, 'finish-process-identity-mismatch'):
                admission.wait_for_finish(self.args, self.store, self.out)
        self.process.wait.assert_not_called()

    def test_completed_predecessor_requires_unchanged_consumer_sources(self):
        write_new_json(self.run / 'finish-complete.json', dict(status='passed'))
        def finish(timeout):
            (self.repo / 'consumer.py').write_text('drift', encoding='utf-8')
            return 0
        self.process.wait.side_effect = finish
        with patch.object(psutil, 'Process', return_value=self.process):
            with self.assertRaisesRegex(EvidenceError, 'binding-hash-mismatch'):
                admission.wait_for_finish(self.args, self.store, self.out)


if __name__ == '__main__': unittest.main()
