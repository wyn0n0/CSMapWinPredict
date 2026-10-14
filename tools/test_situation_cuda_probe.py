"""Failure reporting and no-overwrite tests; GPU success is checked by the real probe."""
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import probe_situation_cuda as probe


class ProbeTests(unittest.TestCase):
    def test_wrong_interpreter_is_rejected_before_cuda(self):
        with self.assertRaisesRegex(probe.ProbeFailure, 'wrong-interpreter'):
            probe.probe({}, {'python': str(probe.REPO / 'not-the-interpreter.exe')})

    def test_runtime_failure_cannot_report_ready_or_overwrite_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = str(Path(temporary).resolve())
            with patch.object(sys, 'argv', ['probe_situation_cuda.py', '--run-id', 'failure-case']), \
                 patch.object(probe, 'read_json', return_value={'storageRoot': root}), \
                 patch.dict(probe.os.environ, {'SITUATION_STORAGE_ROOT': root}), \
                 patch.object(probe, 'probe', side_effect=RuntimeError('driver failure')) as operation, \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(probe.main(), 1)
                output = Path(root) / 'reports/failure-case/cuda-probe.json'
                original = output.read_bytes()
                report = json.loads(original)
                self.assertFalse(report['cudaBf16Ready'])
                self.assertEqual(report['status'], 'failed')
                self.assertEqual(report['error']['message'], 'driver failure')
                self.assertEqual(report['capabilities']['qlora'], 'not-tested')
                with self.assertRaises(FileExistsError):
                    probe.main()
                self.assertEqual(output.read_bytes(), original)
                operation.assert_called_once()

    def test_run_id_cannot_escape_report_directory(self):
        with patch.object(sys, 'argv', ['probe_situation_cuda.py', '--run-id', '../outside']):
            with self.assertRaisesRegex(probe.ProbeFailure, 'invalid-run-id'):
                probe.main()


if __name__ == '__main__':
    unittest.main()
