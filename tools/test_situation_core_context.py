import copy
import json
import unittest

from jsonschema.exceptions import ValidationError

from situation_core_context import (load_config, render_core, pack, unpack, project_scene,
                                    observation_map, verify_observations, validators, system_prompt)
from situation_stage6_corpus import bind_record_hashes, PURPOSES, object_sha256
from test_situation_stage6_budget import record as facts_record


def record(split='train'):
    row = facts_record(split)
    counts = dict(flash=1, smoke=None, he=0, molotov=0, incendiary=0, decoy=0)
    weapons = {k: 0 for k in ('pistol', 'smg', 'rifle', 'sniper', 'shotgun', 'machinegun',
                              'grenade', 'knife', 'bomb', 'other', 'unknown')}
    scene = dict(map='de_mirage', sceneBuilderVersion='situation-scene-builder-v1.3',
        geometryVersion='mirage-geometry-v1', tick=42, dataQuality=[dict(code='floor-unknown',
            fieldPaths=['/players/0/floor'], severity='warning')],
        players=[dict(slot='CT1', side='CT', region=None, regionSource='unknown', floor='unknown',
            position=dict(x=0.123456789, y=-0.0, z=0.3333333333333333), health=75, armor=None,
            weaponCategory='rifle', heading=dict(x=1.0, y=0.0), utilityCounts=counts,
            velocity={'excluded': True}, trajectory={'excluded': True})],
        round=dict(phase='live', remainingSeconds=None, elapsedSeconds=3.141592653589793,
                   clockSource=None, score=dict(t=0, ct=0), ref='DO NOT RENDER'),
        bomb=dict(state='unknown', carrierSlot=None, defuserSlot=None, site=None, position=None,
                  region=None, regionSource='unknown', secondsToExplosion=None, secondsToDefuse=None),
        teams={s: dict(defuserCount=None, equipmentValue=3000, totalMoney=1000, helmetCount=1,
                       weaponCounts=copy.deepcopy(weapons), utilityCounts=copy.deepcopy(counts), alive=1) for s in ('t', 'ct')},
        effects=['DO NOT RENDER'], utilities=['DO NOT RENDER'], geometry={'excluded': True})
    row['input']['scene'] = scene
    row['metadata'].update(tick=42, matchRef=split + '-match', sourceSceneSha256='b'*64)
    row['purpose'] = PURPOSES[split][0]
    row['labelSource'] = 'template-prelabel'
    return bind_record_hashes(row)


class CoreContextTests(unittest.TestCase):
    def test_schema_config_and_lossless_exact_values(self):
        config = load_config()
        row = record()
        original = copy.deepcopy(row)
        user, provenance = render_core(row)
        payload = json.loads(user)
        self.assertEqual(payload['facts'], row['input']['facts'])
        self.assertEqual(unpack(payload['sceneContext']), project_scene(row['input']['scene']))
        self.assertIn('0.123456789', user)
        self.assertIn('-0.0', user)
        self.assertNotIn('DO NOT RENDER', user)
        self.assertNotIn('trajectory', user)
        self.assertNotIn('velocity', user)
        self.assertEqual(row, original)
        self.assertEqual(render_core(row), (user, provenance))
        self.assertIn('core-scene-observations-v1', system_prompt(config))

    def test_empty_and_multiple_players_quality_roundtrip(self):
        for count in (0, 1, 2):
            row = record()
            row['input']['scene']['players'] *= count
            if count == 2:
                row['input']['scene']['players'][1] = copy.deepcopy(row['input']['scene']['players'][1])
                row['input']['scene']['players'][1]['slot'] = 'CT2'
            row['input']['scene']['dataQuality'] *= count
            user, _ = render_core(bind_record_hashes(row))
            self.assertEqual(len(unpack(json.loads(user)['sceneContext'])['players']), count)

    def test_input_and_label_provenance_never_in_prompt(self):
        row = record()
        user, _ = render_core(row)
        for secret in ('sampleId', 'sourceSceneSha256', 'matchRef', 'prelabelSha256', 'tick'):
            self.assertNotIn('"' + secret + '"', user)

    def test_tampered_scene_and_facts_rejected(self):
        for field in ('scene', 'facts'):
            row = record()
            row['input'][field]['dataQuality'] = []
            with self.assertRaisesRegex(ValueError, 'paired-input-hash-drift'):
                render_core(row)

    def test_source_tick_mismatch_rejected(self):
        row = record(); row['metadata']['tick'] += 1
        with self.assertRaisesRegex(ValueError, 'paired-tick-mismatch'):
            render_core(row)

    def test_legacy_domain_requires_unchanged_frozen_partition_witness(self):
        row = record(); del row['metadata']['payloadHashDomain']
        row['metadata']['modelInputSha256'] = 'd'*64  # .NET hash is a different domain.
        with self.assertRaisesRegex(ValueError, 'legacy-record-needs-verified-partition'):
            render_core(row)
        witness = dict(recordSha256=object_sha256(row), split='train', manifestSha256='e'*64,
                       partitionSha256='f'*64, rawLineSha256='a'*64)
        _, provenance = render_core(row, witness)
        self.assertEqual(provenance['historicalModelInputSha256'], 'd'*64)
        self.assertEqual(provenance['sourceSceneSha256'], object_sha256(row['input']['scene']))
        row['input']['scene']['players'][0]['health'] = 74
        with self.assertRaisesRegex(ValueError, 'source-record-witness-mismatch'):
            render_core(row, witness)

    def test_missing_field_cannot_be_defaulted(self):
        row = record(); del row['input']['scene']['players'][0]['armor']
        with self.assertRaises(KeyError):
            render_core(bind_record_hashes(row))

    def test_no_new_bomb_fields_silently_dropped(self):
        row = record(); row['input']['scene']['bomb']['future'] = True
        with self.assertRaisesRegex(ValueError, 'bomb-fields-changed'):
            render_core(bind_record_hashes(row))

    def test_evidence_set_preserved_and_mismatch_fails(self):
        row = record(); row['input']['allowedEvidenceIds'] = ['invented']
        with self.assertRaisesRegex(ValueError, 'facts-ids-differ'):
            render_core(row)

    def test_table_ragged_duplicate_or_reordered_columns_rejected(self):
        for value in (dict(columns=['a'],rows=[[]]), dict(columns=['a','a'],rows=[[1,2]]),
                      dict(columns=['b','a'],rows=[[1,2]]), dict(columns=['a'],rows=[])):
            with self.assertRaises(ValueError): unpack(value)

    def test_schema_rejects_wrong_column_type_extra_core_and_fact_fields(self):
        user, _ = render_core(record())
        for mutation in ('column', 'extra', 'facts'):
            payload = json.loads(user)
            if mutation == 'column': payload['sceneContext']['players']['rows'][0][0] = 'wrong'
            if mutation == 'extra': payload['sceneContext']['velocity'] = 123
            if mutation == 'facts': payload['facts']['private'] = 'extra'
            with self.assertRaises(ValidationError): validators()[0].validate(payload)

    def test_support_source_and_type_and_value(self):
        row = record(); _, provenance = render_core(row)
        claims = [dict(sceneRef='scene:/players/0/health', operator='equals', value=75)]
        result = verify_observations(row, provenance, claims)
        self.assertFalse(result['semanticNarrativeVerified'])
        self.assertEqual(result['observations'][0]['sourcePath'], '/input/scene/players/0/health')
        for wrong in (74, 75.0, '75', True):
            with self.assertRaisesRegex(ValueError, 'observation-value-mismatch'):
                verify_observations(row, provenance, [dict(claims[0], value=wrong)])
        changed = copy.deepcopy(provenance); changed['sampleId'] = 'different'
        with self.assertRaisesRegex(ValueError, 'support-source-mismatch'):
            verify_observations(row, changed, claims)

    def test_null_unknown_do_not_become_known_observations(self):
        row = record(); _, provenance = render_core(row)
        claims = [dict(sceneRef='scene:/bomb/secondsToDefuse', operator='equals', value=None),
                  dict(sceneRef='scene:/players/0/floor', operator='equals', value='unknown')]
        self.assertTrue(all(not o['known'] for o in verify_observations(row, provenance, claims)['observations']))

    def test_excluded_or_invented_references_and_prose_rejected(self):
        row = record(); _, provenance = render_core(row)
        for ref in ('alive', 'scene:/players/0/velocity/x', 'scene:/players/1/health'):
            with self.assertRaisesRegex(ValueError, 'unknown-core-reference'):
                verify_observations(row, provenance, [dict(sceneRef=ref, operator='equals', value=75)])
        with self.assertRaisesRegex(ValueError, 'only-direct-observations-supported'):
            verify_observations(row, provenance, [dict(sceneRef='scene:/players/0/health', operator='implies', value='will win')])

    def test_every_observation_points_to_original_source(self):
        row = record()
        for item in observation_map(project_scene(row['input']['scene'])).values():
            value = row
            for part in item['sourcePath'].split('/')[1:]:
                value = value[int(part)] if isinstance(value, list) else value[part]
            self.assertEqual(value, item['value'])


if __name__ == '__main__': unittest.main()
