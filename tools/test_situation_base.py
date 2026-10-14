"""Exercise base integrity rejection without downloading or loading a model."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from prepare_situation_base import direct_download, validate_files


class BaseIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        files = {
            'LICENSE': 'Apache License\nVersion 2.0, January 2004\n',
            'config.json': json.dumps({'model_type': 'qwen3', 'architectures': ['Qwen3ForCausalLM']}),
            'model.safetensors.index.json': json.dumps({'weight_map': {'weight': 'model-00001-of-00001.safetensors'}}),
            'tokenizer_config.json': json.dumps({'chat_template': '{{ messages }}'}),
            'model-00001-of-00001.safetensors': 'synthetic hash fixture, not model weights',
        }
        self.ref = {'files': []}
        for name, value in files.items():
            data = value.encode('utf-8')
            (self.base / name).write_bytes(data)
            self.ref['files'].append(dict(path=name, bytes=len(data),
                                         sha256=hashlib.sha256(data).hexdigest() if name.endswith('.safetensors') else None,
                                         gitBlobSha1=hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()))

    def test_accepts_intact_inventory(self):
        entries, template = validate_files(self.base, self.ref)
        self.assertEqual(len(entries), 5)
        self.assertEqual(template, hashlib.sha256(b'{{ messages }}').hexdigest())

    def test_rejects_weight_and_git_blob_tampering_even_at_same_size(self):
        for name in ('model-00001-of-00001.safetensors', 'config.json'):
            path = self.base / name
            original = path.read_bytes()
            path.write_bytes(b'X' + original[1:])
            with self.assertRaisesRegex(ValueError, 'mismatch'):
                validate_files(self.base, self.ref)
            path.write_bytes(original)

    def test_rejects_unlisted_or_missing_files(self):
        (self.base / 'unlisted.py').write_text('fixture')
        with self.assertRaisesRegex(ValueError, 'base-file-set-mismatch'):
            validate_files(self.base, self.ref)
        (self.base / 'unlisted.py').unlink()
        (self.base / 'LICENSE').unlink()
        with self.assertRaises(FileNotFoundError):
            validate_files(self.base, self.ref)

    def test_rejects_duplicate_or_unsafe_inventory(self):
        self.ref['files'].append(dict(self.ref['files'][0]))
        with self.assertRaisesRegex(ValueError, 'duplicate-reference-file'):
            validate_files(self.base, self.ref)
        self.ref['files'].pop()
        self.ref['files'][0]['path'] = '../LICENSE'
        with self.assertRaisesRegex(ValueError, 'unexpected-file-type'):
            validate_files(self.base, self.ref)


    def test_http_resume_preserves_verified_offset(self):
        import requests
        first = MagicMock(status_code=206, headers={'Content-Range': 'bytes 0-4/5'})
        second = MagicMock(status_code=206, headers={'Content-Range': 'bytes 2-4/5'})
        first.__enter__.return_value = first
        second.__enter__.return_value = second
        def interrupted(chunk_size):
            yield b'ab'
            raise requests.ConnectionError('fixture interruption')
        first.iter_content.side_effect = interrupted
        second.iter_content.return_value = [b'cde']
        path = self.base / 'download-part'
        with patch('requests.get', side_effect=[first, second]) as get, patch('prepare_situation_base.time.sleep'):
            direct_download({'modelId': 'Qwen/Qwen3-1.7B', 'revision': 'a' * 40},
                            {'path': 'model.safetensors', 'bytes': 5}, path)
        self.assertEqual(path.read_bytes(), b'abcde')
        self.assertEqual(get.call_args_list[1].kwargs['headers']['Range'], 'bytes=2-')

    def test_http_resume_rejects_server_ignoring_range(self):
        import requests
        first = MagicMock(status_code=206, headers={'Content-Range': 'bytes 0-4/5'})
        second = MagicMock(status_code=200, headers={})
        first.__enter__.return_value = first
        second.__enter__.return_value = second
        def interrupted(chunk_size):
            yield b'ab'
            raise requests.ConnectionError('fixture interruption')
        first.iter_content.side_effect = interrupted
        path = self.base / 'download-part'
        with patch('requests.get', side_effect=[first, second]), patch('prepare_situation_base.time.sleep'):
            with self.assertRaisesRegex(ValueError, 'invalid-resume-status'):
                direct_download({'modelId': 'Qwen/Qwen3-1.7B', 'revision': 'a' * 40},
                                {'path': 'model.safetensors', 'bytes': 5}, path)
        self.assertEqual(path.read_bytes(), b'ab')


if __name__ == '__main__':
    unittest.main()
