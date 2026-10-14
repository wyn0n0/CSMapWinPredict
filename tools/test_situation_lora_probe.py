import copy
import contextlib
import io
import json
from pathlib import Path
import os
import sys
from types import SimpleNamespace
import unittest
import tempfile
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from situation_probe_inputs import assistant_labels, choose_rows
import probe_situation_lora as probe


class ProbeInputTests(unittest.TestCase):
    def test_labels_only_supervise_complete_assistant(self):
        self.assertEqual(assistant_labels([1, 2, 3, 4, 9], [1, 2], 9, 5), [-100, -100, 3, 4, 9])
        for ids, prefix, limit in [([1, 2, 9], [1], 2), ([1, 2, 9], [2], 3),
                                   ([1, 2], [1], 3), ([1, 2, 9], [], 3), ([1, 2, 9], [1, 2, 9], 3)]:
            with self.assertRaises(ValueError):
                assistant_labels(ids, prefix, 9, limit)

    def test_selection_covers_longest_inputs_targets_and_categories(self):
        rows = [dict(sampleId=str(i), totalTokens=100+i, inputTokens=80+i, completionTokens=20,
                     phase='live', selectionTags=['common']) for i in range(20)]
        rows[0].update(completionTokens=50, phase='post-plant', selectionTags=['rare'])
        rows[1]['inputTokens'] = 1000
        before = copy.deepcopy(rows)
        chosen = choose_rows(rows)
        self.assertEqual(len(chosen), 16)
        self.assertTrue({'0', '1', '19'} <= {r['sampleId'] for r in chosen})
        self.assertEqual(chosen, choose_rows(list(reversed(rows))))
        self.assertEqual(rows, before)

    def test_uncovered_categories_do_not_silently_disappear(self):
        rows = [dict(sampleId=str(i), totalTokens=100+i, inputTokens=80+i, completionTokens=20,
                     phase='live', selectionTags=[str(i)]) for i in range(20)]
        with self.assertRaisesRegex(ValueError, 'coverage'):
            choose_rows(rows)

    def test_failed_probe_cannot_claim_ready_or_overwrite(self):
        with tempfile.TemporaryDirectory() as temporary:
            config = {'storageRoot': temporary}
            with patch.object(sys, 'argv', ['probe_situation_lora.py', '--run-id', 'failure-case']), \
                 patch.object(probe, 'read_json', return_value=config), \
                 patch.object(probe, 'probe', side_effect=RuntimeError('synthetic failure')), \
                 patch.object(probe, 'smi', return_value={'exitCode': 0}), \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(probe.main(), 1)
                path = Path(temporary) / 'reports/failure-case/memory-probe.json'
                original = path.read_bytes()
                report = json.loads(original)
                self.assertFalse(report['resourceReady'])
                self.assertFalse(report['trainingReady'])
                self.assertEqual(report['status'], 'failed')
                with self.assertRaises(FileExistsError):
                    probe.main()
                self.assertEqual(path.read_bytes(), original)

    def test_nf4_requires_the_approved_conditional_profile(self):
        with tempfile.TemporaryDirectory() as temporary:
            local = dict(python=sys.executable, storageRoot=temporary)
            cfg = dict(optimizerSteps=2, microBatchSize=1, gradientAccumulationSteps=16)
            with patch.object(probe, 'read_json', return_value=local), \
                 patch.dict(os.environ, SITUATION_STORAGE_ROOT=str(Path(temporary).resolve())), \
                 patch.object(probe, 'verify_installed_profile', return_value={'profile': 'requirements-situation-win-cu126'}):
                with self.assertRaisesRegex(ValueError, 'nf4-requires-approved-qlora-profile'):
                    probe.probe(Path(temporary), {}, cfg, SimpleNamespace(precision='nf4'))

    def test_bf16_accepts_both_exact_approved_profiles_without_installing(self):
        with tempfile.TemporaryDirectory() as temporary:
            local = dict(python=sys.executable, storageRoot=temporary)
            cfg = dict(optimizerSteps=2, microBatchSize=1, gradientAccumulationSteps=16)
            for name in ('requirements-situation-win-cu126', 'requirements-situation-win-cu126-qlora'):
                profile = dict(profile=name, lockSha256='test-hash', installedPackages={})
                report = {}
                with patch.object(probe, 'read_json', return_value=local), \
                     patch.dict(os.environ, SITUATION_STORAGE_ROOT=str(Path(temporary).resolve())), \
                     patch.object(probe, 'verify_installed_profile', return_value=profile), \
                     patch.object(probe, 'prepare_inputs', side_effect=RuntimeError('stop-before-model-load')):
                    with self.assertRaisesRegex(RuntimeError, 'stop-before-model-load'):
                        probe.probe(Path(temporary), report, cfg, SimpleNamespace(precision='bf16'))
                self.assertEqual(report['dependencyProfile'], profile)


if __name__ == '__main__':
    unittest.main()
