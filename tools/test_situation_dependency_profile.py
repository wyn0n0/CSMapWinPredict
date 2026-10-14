import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import situation_dependency_profile as profile


class DependencyProfileTests(unittest.TestCase):
    def test_only_exact_known_profiles_are_accepted(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for stem, packages in [('requirements-situation-win-cu126', ['torch']),
                                   ('requirements-situation-win-cu126-qlora', ['torch', 'bitsandbytes'])]:
                lock = root / (stem + '.lock.txt')
                lock.write_text('locked', encoding='utf-8')
                (root / (stem + '.sources.json')).write_text(json.dumps(dict(lockSha256=profile.digest(lock),
                    packages=[dict(name=p, version='1') for p in packages])), encoding='utf-8')
            def installed(names):
                return [SimpleNamespace(metadata={'Name': name}, version='1') for name in names]
            with patch.object(profile, 'REPO', root):
                for names in (['torch'], ['torch', 'bitsandbytes']):
                    with patch.object(profile.importlib.metadata, 'distributions', return_value=installed(names)):
                        self.assertEqual(len(profile.verify_installed_profile()['installedPackages']), len(names))
                with patch.object(profile.importlib.metadata, 'distributions', return_value=installed(['torch', 'unexpected'])):
                    with self.assertRaisesRegex(ValueError, 'known-locks'):
                        profile.verify_installed_profile()
                (root / 'requirements-situation-win-cu126.lock.txt').write_text('tampered', encoding='utf-8')
                with patch.object(profile.importlib.metadata, 'distributions', return_value=installed(['torch'])):
                    with self.assertRaisesRegex(ValueError, 'lock-drift'):
                        profile.verify_installed_profile()


if __name__ == '__main__':
    unittest.main()
