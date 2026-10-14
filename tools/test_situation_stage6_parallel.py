import json
from pathlib import Path
import tempfile
import unittest

from situation_environment import digest, read_json, write_new_json
from situation_stage6_parallel import assign_shards, match_ref, merge_partition, ALGORITHM
from situation_evidence import binding
from validate_situation_stage6_preparation import EvidenceError, EvidenceStore


class ParallelExportTests(unittest.TestCase):
    def test_assignment_balances_bytes_deterministically_without_changing_members(self):
        members = [dict(matchId=k) for k in 'abcdef']
        sizes = dict(zip('abcdef', (8, 7, 6, 5, 4, 3)))
        result = assign_shards(members, sizes)
        self.assertEqual(result, dict(zip('abcdef', (0, 1, 2, 2, 1, 0))))
        self.assertEqual(result, assign_shards(list(reversed(members)), sizes))
        self.assertEqual(set(result), set(sizes))
        with self.assertRaises(EvidenceError): assign_shards(members + members, sizes)

    def fixture(self, root):
        repository, storage = root / 'repo', root / 'storage'
        repository.mkdir(); storage.mkdir()
        run = storage / 'run'; run.mkdir()
        store = EvidenceStore(repository, storage, isolated=True)
        members = [dict(matchId=k * 64) for k in 'abc']
        assignments = {m['matchId']: i for i, m in enumerate(members)}
        for i, member in enumerate(members):
            folder = run / f'shard-{i}' / 'train'
            folder.mkdir(parents=True)
            ref = match_ref(member['matchId'])
            row = dict(sampleId='sample-' + str(i), metadata=dict(split='train', matchRef=ref, roundRef='round-' + str(i)))
            (folder / 'train.jsonl').write_text(json.dumps(row) + '\n', encoding='utf-8')
            manifest = dict(schemaVersion='situation-stage6-source-v1', split='train', purpose='sft',
                analysisRuleVersion='v8', splitSha256='1' * 64, analysisRulesSha256='2' * 64,
                selectionSha256='3' * 64, baseManifestSha256='4' * 64, baseProvenanceSha256='5' * 64,
                meshSha256='6' * 64, identityPolicy='frozen', selectionPolicy='frozen', sourceFiles=[],
                status='complete', complete=True, frozen=True,
                sharding=dict(index=i, count=3, algorithm=ALGORITHM, matchIds=[member['matchId']]),
                matches=[ref], counts=dict(samples=1, rounds=1, matches=1),
                files=[dict(path='train.jsonl', sha256=digest(folder / 'train.jsonl'), rows=1)])
            write_new_json(folder / 'manifest.json', manifest)
        return store, run, members, assignments

    def test_merge_preserves_bytes_and_binds_all_shards(self):
        with tempfile.TemporaryDirectory() as temp:
            store, run, members, assignments = self.fixture(Path(temp))
            expected = b''.join((run / f'shard-{i}/train/train.jsonl').read_bytes() for i in range(3))
            result = merge_partition(store, run, 'train', 'sft', members, assignments, binding)
            self.assertEqual(result['counts'], dict(samples=3, rounds=3, matches=3))
            self.assertEqual(len(result['shardBindings']), 6)
            self.assertEqual((run / 'source-train/train.jsonl').read_bytes(), expected)
            with self.assertRaises(FileExistsError):
                merge_partition(store, run, 'train', 'sft', members, assignments, binding)

    def test_incomplete_wrong_assignment_purpose_and_hash_drift_never_complete(self):
        for mutation in ('incomplete', 'assignment', 'purpose', 'hash', 'count', 'duplicate'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp:
                store, run, members, assignments = self.fixture(Path(temp))
                folder = run / 'shard-1/train'
                manifest = read_json(folder / 'manifest.json')
                if mutation == 'incomplete': manifest['complete'] = False
                elif mutation == 'assignment': manifest['sharding']['index'] = 0
                elif mutation == 'purpose': manifest['purpose'] = 'final-evaluation'
                elif mutation == 'hash': (folder / 'train.jsonl').write_bytes(b'changed')
                elif mutation == 'count': manifest['counts']['samples'] = 2
                else:
                    record = json.loads((folder / 'train.jsonl').read_text())
                    record['sampleId'] = 'sample-0'
                    (folder / 'train.jsonl').write_text(json.dumps(record) + '\n', encoding='utf-8')
                    manifest['files'][0]['sha256'] = digest(folder / 'train.jsonl')
                (folder / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
                with self.assertRaises(EvidenceError):
                    merge_partition(store, run, 'train', 'sft', members, assignments, binding)
                self.assertFalse((run / 'source-train/manifest.json').exists())


if __name__ == '__main__': unittest.main()
