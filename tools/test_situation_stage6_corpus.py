import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from situation_stage6_corpus import (RULE, object_sha256, file_sha256, prepare_paired_corpus,
    partition_review_pairs, load_review_partition, load_corpus_partition, validate_pair, paired_record_validator, validate_paired_record, PAIRED_SCHEMA)


def identity(prefix, value):
    return prefix + '-' + hashlib.sha256(value.encode()).hexdigest()


def sample(value):
    return identity('sample', value)


def synthetic_scene(tick):
    team = dict(alive=0, totalHealth=0, totalArmor=0, totalMoney=0, equipmentValue=0, helmetCount=0, defuserCount=0,
                utilityCounts={k: 0 for k in ('flash','smoke','he','molotov','incendiary','decoy')},
                weaponCounts={k: 0 for k in ('pistol','smg','rifle','sniper','shotgun','machinegun','grenade','knife','bomb','other','unknown')})
    return dict(schemaVersion='minimap-scene-v1', sceneBuilderVersion='situation-scene-builder-v1.3', geometryVersion='mirage-geometry-v1',
                map='de_mirage', tick=tick, tickRate=64, players=[], teams=dict(t=team, ct=copy.deepcopy(team)),
                round=dict(ref=None, segmentId=0, number=1, phase='live', elapsedSeconds=10, remainingSeconds=100,
                           score=dict(t=0,ct=0), clockSource='synthetic'),
                bomb=dict(state='unknown', carrierSlot=None, defuserSlot=None, site=None, position=None, region=None,
                          regionSource='unknown', secondsToExplosion=None, secondsToDefuse=None),
                utilities=[], effects=[], geometry=dict(regionOccupancy=[],playerDistances=[]), dataQuality=[])


def narrative(text):
    return dict(schemaVersion='situation-narrative-v1',summaryZh=text,
                highlights=[dict(textZh='合成证据一',evidenceIds=['e1']),dict(textZh='合成证据二',evidenceIds=['e1'])],uncertainties=[])


def fixture():
    members = {s: [identity('match',f'{s}-{i}') for i in range(n)] for s, n in [('train',71),('dev',8),('test',8)]}
    contract = dict(complete=True, frozen=True, analysisRuleVersion=RULE, reviewVersion='synthetic-reviewed-v8',
        splitMembers=members, expectedReviewCounts={'train':36,'dev':7,'test':7},
        sourceBindings=[{'logicalPath':'repo:synthetic.json','sha256':hashlib.sha256(b'synthetic').hexdigest()}],
        bindingResolver=lambda _: b'synthetic', outputLogicalRoot='storage:datasets/synthetic')
    records, pairs = [], []
    for split, matches in members.items():
        for i, match in enumerate(matches):
            facts = dict(schemaVersion='situation-facts-v1', analysisRuleVersion=RULE,
                alive=dict(t=0,ct=0),totalHealth=dict(t=0,ct=0),bomb=dict(state='unknown',site=None),
                formation=dict(t='unknown',ct='unknown'),pressure=dict(side=None,site=None),contestedRegions=[],
                contactRisk='low',isolatedSide='unknown',spatialAdvantage='uncertain',confidence='low',
                evidence=[dict(id='e1',sourcePaths=['/alive'],ruleId='synthetic',textZh='合成证据')],dataQuality=[])
            record = dict(schemaVersion='situation-training-record-v1', sampleId=sample(f'{split}-{i}'),
                metadata=dict(split=split,matchRef=match,roundRef=identity('round',match),tick=i,
                    phase='live',selectionTags=['synthetic'],sampleWeight=1,weightNumerator=1,weightDenominator=1,
                    sourceSceneSha256='a'*64,modelInputSha256='b'*64,factsSha256='c'*64,prelabelSha256='d'*64),
                input=dict(scene=synthetic_scene(i), facts=facts,allowedEvidenceIds=['e1']),
                output=narrative('合成模板'),labelSource='template-prelabel',reviewStatus='unreviewed')
            records.append(record)
            if i < contract['expectedReviewCounts'][split]:
                reviewed=copy.deepcopy(record)
                reviewed['input']['facts']['contactRisk']='high'
                label=narrative('人工批准合成样本')
                pair=dict(sampleId=record['sampleId'],split=split,reviewVersion=contract['reviewVersion'],
                    analysisRuleVersion=RULE,record=reviewed,label=label,allowedUse={'train':'sft','dev':'checkpoint-selection','test':'final-evaluation'}[split],
                    reviewStatus='approved')
                pair['sourceHashes']=dict(recordSha256=object_sha256(reviewed),inputSha256=object_sha256(reviewed['input']),labelSha256=object_sha256(label))
                pairs.append(pair)
    return records,pairs,contract


class CorpusTests(unittest.TestCase):
    def test_pairing_repeat_complete(self):
        records,pairs,c=fixture()
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)/'new'
            report=prepare_paired_corpus(records,pairs,c,root)
            self.assertEqual(report['details']['trainEpochEntries'],215)
            self.assertTrue(report['details']['weightsVerified'])
            self.assertEqual(report['details']['humanCounts'],{'train':36,'dev':7,'test':7})
            rows=load_corpus_partition(root/'train','train','sft',file_sha256(root/'train'/'manifest.json'))
            first=next(r for r in rows if r['sampleId']==sample('train-0'))
            self.assertEqual(first['input']['facts']['contactRisk'],'high')
            self.assertEqual(first['output']['summaryZh'],'人工批准合成样本')
            self.assertEqual(first['schemaVersion'],PAIRED_SCHEMA)
            self.assertEqual(sum(r['sampleId']==sample('train-0') for r in rows),1)
            entries=json.loads((root/'train'/'epoch-entries.json').read_text())
            self.assertEqual(sum(e['sampleId']==sample('train-0') for e in entries),5)
            self.assertAlmostEqual(sum(e['sampleWeight'] for e in entries if e['sampleId']==sample('train-0')),1)
            self.assertTrue(all(b['logicalPath'].startswith(('storage:datasets/synthetic/','repo:')) for b in report['evidenceBindings']))
            manifest=json.loads((root/'manifest.json').read_text())
            self.assertTrue({'split.json','provenance.json','statistics.json','train/records.jsonl','test/manifest.json'} <= {f['path'] for f in manifest['files']})
            self.assertTrue(all(file_sha256(root/f['path'])==f['sha256'] for f in manifest['files']))
            marker=json.loads((root/'complete.json').read_text())
            self.assertEqual(marker['manifestSha256'],file_sha256(root/'manifest.json'))
            with self.assertRaises(FileExistsError): prepare_paired_corpus(records,pairs,c,root)

    def test_missing_selected_review_preserved(self):
        records,pairs,c=fixture()
        records=[r for r in records if r['sampleId']!=sample('train-0')]
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)/'new'
            prepare_paired_corpus(records,pairs,c,root)
            overlay=json.loads((root/'overlays.json').read_text())
            self.assertEqual(next(o for o in overlay if o['sampleId']==sample('train-0'))['reason'],'reviewed-tick-not-selected-by-v8')

    def test_pair_negative_cases(self):
        _,pairs,c=fixture()
        for change in ('version','split','hash','evidence','purpose'):
            p=copy.deepcopy(pairs[0])
            if change=='version': p['analysisRuleVersion']='v1'
            elif change=='split': p['split']='test'
            elif change=='hash': p['sourceHashes']['inputSha256']='0'*64
            elif change=='evidence':
                p['label']['highlights'][0]['evidenceIds']=['bad']
                p['sourceHashes']['labelSha256']=object_sha256(p['label'])
            else: p['allowedUse']='final-evaluation'
            with self.subTest(change=change),self.assertRaises(ValueError): validate_pair(p,c)

    def test_mixed_source_duplicate_membership(self):
        for change in ('mixed','duplicate','membership','missing-pair','overlap','incomplete'):
            records,pairs,c=fixture()
            if change=='mixed': records[0]['input']['facts']['analysisRuleVersion']='v1'
            elif change=='duplicate': records.append(copy.deepcopy(records[0]))
            elif change=='membership': records[0]['metadata']['split']='test'
            elif change=='missing-pair': pairs=pairs[1:]
            elif change=='overlap': c['splitMembers']['test'][0]=c['splitMembers']['train'][0]
            else: c['complete']=False
            with tempfile.TemporaryDirectory() as td,self.subTest(change=change),self.assertRaises(ValueError):
                prepare_paired_corpus(records,pairs,c,Path(td)/'new')

    def test_interruption_incomplete(self):
        records,pairs,c=fixture()
        def interrupted():
            yield records[0]
            raise KeyboardInterrupt()
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)/'new'
            with self.assertRaises(KeyboardInterrupt): prepare_paired_corpus(interrupted(),pairs,c,root)
            self.assertTrue((root/'incomplete.json').exists())
            self.assertFalse((root/'complete.json').exists())

    def test_binding_drift_rejected_before_publication(self):
        records,pairs,c=fixture()
        calls=[]
        def resolver(_):
            calls.append(1)
            return b'synthetic' if len(calls)==1 else b'drift'
        c['bindingResolver']=resolver
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)/'new'
            with self.assertRaisesRegex(ValueError,'source-binding-hash-drift'):
                prepare_paired_corpus(records,pairs,c,root)
            self.assertFalse((root/'complete.json').exists())
            self.assertFalse((root/'train'/'manifest.json').exists())

    def test_strict_envelope_schema(self):
        rows,_,_=fixture()
        row=copy.deepcopy(rows[-1]);row['schemaVersion']=PAIRED_SCHEMA;row['purpose']='final-evaluation'
        validator,_=paired_record_validator()
        validate_paired_record(row,validator)
        for change in ('legacy','extra','missing-facts','bad-scene','bad-target','purpose','human-unbound'):
            bad=copy.deepcopy(row)
            if change=='legacy': bad['schemaVersion']='situation-training-record-v1'
            elif change=='extra': bad['extra']=True
            elif change=='missing-facts': del bad['input']['facts']['evidence']
            elif change=='bad-scene': del bad['input']['scene']['teams']
            elif change=='bad-target': bad['output']['highlights']=[]
            elif change=='purpose': bad['purpose']='sft'
            else: bad['labelSource']='human-approved';bad['reviewStatus']='approved'
            with self.subTest(change=change),self.assertRaisesRegex(ValueError,'paired-record-schema-invalid'):
                validate_paired_record(bad,validator)

    def test_path_binding_resolver(self):
        _,pairs,c=fixture()
        with tempfile.TemporaryDirectory() as td:
            source=Path(td)/'source';source.write_bytes(b'synthetic')
            c['bindingResolver']=lambda _: source
            partition_review_pairs(pairs,c,Path(td)/'new')

    def test_physical_partition_and_tamper(self):
        _,pairs,c=fixture()
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)/'new'
            partition_review_pairs(pairs,c,root)
            directory=root/'train'
            digest=file_sha256(directory/'manifest.json')
            original=Path.open
            opened=[]
            def restricted(path,*args,**kwargs):
                opened.append(path)
                self.assertEqual(path.parent,directory)
                return original(path,*args,**kwargs)
            with patch.object(Path,'open',restricted):
                self.assertEqual(len(load_review_partition(directory,'train','sft',digest)),36)
            self.assertTrue(opened)
            with patch.object(Path,'open',side_effect=AssertionError('must reject before access')):
                with self.assertRaises(ValueError): load_review_partition(directory,'test','sft',digest)
            with (directory/'review-pairs.jsonl').open('a') as f: f.write('{}\n')
            with self.assertRaises(ValueError): load_review_partition(directory,'train','sft',digest)
            with self.assertRaises(ValueError): load_review_partition(directory,'train','sft','0'*64)

if __name__=='__main__': unittest.main()
