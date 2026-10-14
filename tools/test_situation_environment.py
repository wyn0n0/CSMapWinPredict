"""Small storage-boundary tests. No GPU, model download or production data."""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest

from tools.situation_environment import (child_environment, copy_verified,
    dataset_inventory, digest, safe_child, storage_root)


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)
        self.source=self.root/'source'
        self.source.mkdir()
        self.payload=b'{"split":"train"}\n'
        (self.source/'train.jsonl').write_bytes(self.payload)
        self.manifest={'status':'complete','files':[{'path':'train.jsonl','bytes':len(self.payload),
            'sha256':hashlib.sha256(self.payload).hexdigest()}]}
        self.save_manifest()

    def save_manifest(self):
        (self.source/'manifest.json').write_text(json.dumps(self.manifest),encoding='utf-8')

    def test_storage_rejects_repo_child_ancestor_and_drive_root(self):
        repo=self.root/'repo'
        for value in (repo,repo/'models',self.root,Path(self.root.anchor)):
            with self.assertRaises(ValueError): storage_root(value,repo)
        self.assertEqual(storage_root(self.root/'external',repo),self.root/'external')

    def test_traversal_and_alternate_stream_rejected(self):
        for value in ('../outside','x/../../outside','train.jsonl:secret',str(self.root/'outside')):
            with self.assertRaises(ValueError): safe_child(self.source,value)

    def test_manifest_integrity_and_unlisted_file_rejected(self):
        original=digest(self.source/'manifest.json')
        self.assertEqual(len(dataset_inventory(self.source,original)),2)
        with self.assertRaisesRegex(ValueError,'manifest-drift'): dataset_inventory(self.source,'0'*64)
        (self.source/'train.jsonl').write_bytes(b'x'*len(self.payload))
        with self.assertRaisesRegex(ValueError,'integrity'): dataset_inventory(self.source)
        (self.source/'train.jsonl').write_bytes(self.payload)
        (self.source/'extra').write_text('x')
        with self.assertRaisesRegex(ValueError,'file-set'): dataset_inventory(self.source)

    def test_incomplete_and_duplicate_manifest_rejected(self):
        self.manifest['status']='incomplete'
        self.save_manifest()
        with self.assertRaisesRegex(ValueError,'not-complete'): dataset_inventory(self.source)
        self.manifest['status']='complete'
        self.manifest['files']*=2
        self.save_manifest()
        with self.assertRaisesRegex(ValueError,'duplicate'): dataset_inventory(self.source)

    def test_copy_reuse_tamper_no_overwrite_and_source_preserved(self):
        inventory=dataset_inventory(self.source)
        target=self.root/'target'
        self.assertFalse(copy_verified(self.source,target,inventory)['reused'])
        self.assertTrue(copy_verified(self.source,target,inventory)['reused'])
        (target/'train.jsonl').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError,'integrity'): copy_verified(self.source,target,inventory)
        self.assertEqual((target/'train.jsonl').read_bytes(),b'changed')
        self.assertEqual((self.source/'train.jsonl').read_bytes(),self.payload)

    def test_child_environment_does_not_mutate_parent(self):
        parent={'HF_HOME':'old','PYTHONPATH':'foreign','PYTHONHOME':'foreign','KEEP':'yes'}
        snapshot=parent.copy()
        env=child_environment(self.root/'external',parent)
        self.assertEqual(parent,snapshot)
        self.assertNotIn('PYTHONPATH',env)
        self.assertNotIn('PYTHONHOME',env)
        self.assertEqual(env['KEEP'],'yes')
        for key in ('HF_HOME','HF_HUB_CACHE','TORCH_HOME','PIP_CACHE_DIR','TEMP'):
            self.assertTrue(Path(env[key]).is_relative_to(self.root/'external'))


if __name__=='__main__':
    unittest.main()
