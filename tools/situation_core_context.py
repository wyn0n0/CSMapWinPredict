"""Lossless, same-record core Scene projection. No model or dataset access.

Scene references name decoded leaves, never Facts evidence IDs. The support
checker proves typed observations only; prose/derived claims need the later
evaluation protocol and cannot pass merely because a reference exists.
"""
from pathlib import Path
from copy import deepcopy
from functools import lru_cache
import json

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

from situation_environment import REPO, digest, read_json
from situation_facts_prompt import render_facts
from situation_lossless_table import canonical
from situation_stage6_corpus import object_sha256, PURPOSES, RULE

REPRESENTATION = 'facts-core-scene-table-v1'
CONFIG_PATH = REPO / 'situation-implementation/situation-core-context-v1.json'
SCHEMA_PATH = REPO / 'schemas/situation/situation-core-context-v1.schema.json'
COMMON = ('map', 'sceneBuilderVersion', 'geometryVersion', 'dataQuality')
PLAYER = ('slot', 'side', 'region', 'regionSource', 'floor', 'position', 'health',
          'armor', 'weaponCategory', 'heading', 'utilityCounts')
ROUND = ('phase', 'remainingSeconds', 'elapsedSeconds', 'clockSource', 'score')
TEAM = ('defuserCount', 'equipmentValue', 'totalMoney', 'helmetCount', 'weaponCounts', 'utilityCounts')
BOMB = ('state', 'carrierSlot', 'defuserSlot', 'site', 'position', 'region',
        'regionSource', 'secondsToExplosion', 'secondsToDefuse')
SUPPORT_VERSION = 'core-scene-observations-v1'


def require(ok, code):
    if not ok:
        raise ValueError(code)


def pack(value):
    if isinstance(value, list):
        if value and all(isinstance(v, dict) and set(v) == set(value[0]) for v in value):
            columns = sorted(value[0])
            return dict(columns=columns, rows=[[pack(v[k]) for k in columns] for v in value])
        return [pack(v) for v in value]
    if isinstance(value, dict):
        require(set(value) != {'columns', 'rows'}, 'reserved-table-object')
        return {k: pack(value[k]) for k in sorted(value)}
    require(value is None or type(value) in (str, int, float, bool), 'unsupported-json-value')
    return value


def unpack(value):
    if isinstance(value, dict):
        if set(value) == {'columns', 'rows'}:
            columns, rows = value['columns'], value['rows']
            require(isinstance(columns, list) and bool(columns)
                    and all(isinstance(k, str) for k in columns)
                    and columns == sorted(set(columns)), 'invalid-table-columns')
            require(isinstance(rows, list) and bool(rows)
                    and all(isinstance(r, list) and len(r) == len(columns) for r in rows), 'invalid-table-rows')
            return [dict(zip(columns, (unpack(v) for v in row))) for row in rows]
        return {k: unpack(v) for k, v in value.items()}
    if isinstance(value, list):
        return [unpack(v) for v in value]
    return value


def project_scene(scene):
    def take(obj, fields):
        return {key: deepcopy(obj[key]) for key in fields}
    require(set(scene['bomb']) == set(BOMB), 'bomb-fields-changed')
    return dict(take(scene, COMMON), players=[take(p, PLAYER) for p in scene['players']],
                round=take(scene['round'], ROUND), bomb=take(scene['bomb'], BOMB),
                teams={side: take(scene['teams'][side], TEAM) for side in ('t', 'ct')})


@lru_cache(maxsize=1)
def validators():
    paths = [SCHEMA_PATH] + [REPO / 'schemas/situation' / name for name in (
        'minimap-scene-v1.schema.json', 'situation-facts-v1.schema.json', 'situation-narrative-v1.schema.json')]
    schemas = [read_json(p) for p in paths]
    for schema in schemas:
        Draft202012Validator.check_schema(schema)
    registry = Registry().with_resources((s['$id'], Resource.from_contents(s)) for s in schemas)
    return (Draft202012Validator(schemas[0], registry=registry),
            Draft202012Validator({'$ref': schemas[0]['$id'] + '#/$defs/decodedCore'}, registry=registry),
            Draft202012Validator(schemas[-1], registry=registry))


def load_config(path=CONFIG_PATH):
    config = read_json(path)
    require(config['schemaVersion'] == 'situation-core-context-config-v1'
            and config['representationVersion'] == REPRESENTATION, 'core-config-version')
    require(config['maxSequenceLength'] == 5120 and config['generationReserveTokens'] == 512
            and config['maxInferenceInputTokens'] == 4608 and config['maxCompletionTokens'] == 512,
            'core-budget-policy')
    require(config['enableThinking'] is False and config['truncation'] is False,
            'core-tokenizer-policy')
    require(config['fields'] == dict(common=list(COMMON), players=list(PLAYER), round=list(ROUND),
                                    bomb=list(BOMB), teams=list(TEAM)), 'core-field-policy')
    require(config['supportMapping']['version'] == SUPPORT_VERSION
            and config['supportMapping']['namespace'] == 'scene:', 'core-support-policy')
    for item in config['schemaBindings']:
        require(digest(REPO / item['path']) == item['sha256'], 'core-schema-drift')
    validators()
    return config


def system_prompt(config):
    # All field semantics and reference metadata seen by the model are counted.
    return config['system'] + '\n' + canonical(config['supportMapping'])


def render_core(record, source_witness=None):
    require(record.get('schemaVersion') == 'situation-stage6-paired-record-v1', 'paired-record-required')
    meta, source = record['metadata'], record['input']
    domain = meta.get('payloadHashDomain')
    require(domain in (None, 'stage6-json-v1'), 'payload-hash-domain')
    if source_witness is not None:
        require(source_witness['recordSha256'] == object_sha256(record)
                and source_witness['split'] == meta['split'], 'source-record-witness-mismatch')
    if domain == 'stage6-json-v1':
        for field, value in [('modelInputSha256', source['scene']), ('factsSha256', source['facts'])]:
            require(meta[field] == object_sha256(value), 'paired-input-hash-drift')
    else:
        # Legacy metadata uses the frozen .NET canonicalizer (Unicode escaping
        # and six-decimal rules). Never compare those hashes to Python JSON or
        # rewrite them. The scoped loader witnesses the unchanged record in a
        # hash-verified frozen partition, whose upstream pairing is audited.
        require(source_witness is not None, 'legacy-record-needs-verified-partition')
        require(all(isinstance(source_witness.get(k), str) and len(source_witness[k]) == 64
                    for k in ('manifestSha256', 'partitionSha256', 'rawLineSha256')),
                'source-partition-witness-invalid')
    require(source['scene']['tick'] == meta['tick'], 'paired-tick-mismatch')
    require(source['facts']['analysisRuleVersion'] == RULE, 'analysis-rule-mismatch')
    require(record['purpose'] in PURPOSES[meta['split']], 'record-purpose-mismatch')
    facts = json.loads(render_facts(source))
    core = project_scene(source['scene'])
    payload_validator, core_validator, _ = validators()
    core_validator.validate(core)
    slots = [p['slot'] for p in core['players']]
    require(len(slots) == len(set(slots)) and all(p['slot'].startswith(p['side']) for p in core['players']),
            'scene-slot-mismatch')
    payload = dict(facts=facts, sceneContext=pack(core))
    payload_validator.validate(payload)
    require(canonical(unpack(payload['sceneContext'])) == canonical(core), 'core-round-trip-failed')
    user = canonical(payload)
    require(canonical(json.loads(user)['facts']) == canonical(source['facts']), 'facts-changed')
    provenance = dict(representationVersion=REPRESENTATION, supportVersion=SUPPORT_VERSION,
                      sampleId=record['sampleId'], sourceSceneSha256=object_sha256(source['scene']),
                      historicalSceneSha256=meta['sourceSceneSha256'], factsSha256=object_sha256(facts),
                      sourceHashDomain=domain or 'frozen-dotnet-canonical-json',
                      historicalModelInputSha256=meta['modelInputSha256'], historicalFactsSha256=meta['factsSha256'],
                      selectedCoreSha256=object_sha256(core), payloadSha256=object_sha256(payload))
    if source_witness is not None:
        provenance['sourceWitness'] = deepcopy(source_witness)
    return user, provenance


def observation_map(core):
    """scene:<JSON pointer> maps a decoded scalar/empty-container to its source.

    Selected field names and array order match input.scene exactly, so pointers
    map identically to source paths. Non-selected fields cannot be referenced.
    """
    result = {}
    def walk(value, path):
        if isinstance(value, dict) and value:
            for key in sorted(value):
                walk(value[key], path + '/' + key.replace('~', '~0').replace('/', '~1'))
        elif isinstance(value, list) and value:
            for index, item in enumerate(value):
                walk(item, path + '/' + str(index))
        else:
            result['scene:' + path] = dict(sourcePath='/input/scene' + path, value=deepcopy(value))
    walk(core, '')
    return result


def verify_observations(record, provenance, assertions, source_witness=None):
    """Verify exact typed field assertions; never accepts a free-text conclusion."""
    _, actual = render_core(record, source_witness)
    require(actual == provenance, 'support-source-mismatch')
    require(isinstance(assertions, list) and bool(assertions), 'empty-observations')
    refs = observation_map(project_scene(record['input']['scene']))
    verified = []
    for claim in assertions:
        require(set(claim) == {'sceneRef', 'operator', 'value'} and claim['operator'] == 'equals',
                'only-direct-observations-supported')
        require(claim['sceneRef'] in refs, 'unknown-core-reference')
        observed = refs[claim['sceneRef']]
        require(canonical(claim['value']) == canonical(observed['value']), 'observation-value-mismatch')
        verified.append(dict(claim, sourcePath=observed['sourcePath'],
                             known=claim['value'] is not None and claim['value'] != 'unknown'))
    return dict(status='passed', observations=verified, semanticNarrativeVerified=False,
                supportVersion=SUPPORT_VERSION, provenance=actual)
