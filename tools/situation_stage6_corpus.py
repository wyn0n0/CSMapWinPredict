"""Stage-six paired corpus. All-split preparation runs in an isolated data process.

Consumers use load_corpus_partition/load_review_partition and are provisioned only
with their split directory. Raw test records/errors must stay in private storage.
Object hashes use stage6-json-v1 (not the legacy .NET canonical JSON hash domain).
"""
from collections import Counter, defaultdict
from copy import deepcopy
import hashlib
from itertools import zip_longest
import json
import math
from pathlib import Path
import re

PURPOSES = {'train': ('sft',), 'dev': ('checkpoint-selection', 'prompt-selection', 'evaluation'),
            'test': ('final-evaluation',)}
RULE = 'situation-analysis-rules-v8-close-exposure-1'
PAIRED_SCHEMA = 'situation-stage6-paired-record-v1'
REPOSITORY = Path(__file__).resolve().parents[1]
PAIRED_SCHEMA_PATH = REPOSITORY / 'situation-implementation/situation-stage6-paired-record-v1.schema.json'


def object_sha256(value):
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True,
                                    separators=(',', ':'), allow_nan=False).encode('utf-8')).hexdigest()


def bind_record_hashes(record):
    """Bind reconstituted reviewed records without pretending to emit .NET hashes.

    sourceSceneSha256 remains the immutable historical full source-scene hash;
    the other three payload hashes explicitly use stage6-json-v1.
    """
    record = deepcopy(record)
    record['metadata']['payloadHashDomain'] = 'stage6-json-v1'
    record['metadata']['modelInputSha256'] = object_sha256(record['input']['scene'])
    record['metadata']['factsSha256'] = object_sha256(record['input']['facts'])
    record['metadata']['prelabelSha256'] = object_sha256(record['output'])
    return record


def _saved_contract(contract):
    return {k: v for k, v in contract.items() if k != 'bindingResolver'}


def _verify_bindings(contract):
    resolver = contract.get('bindingResolver')
    if not callable(resolver):
        raise ValueError('source-binding-resolver-required')
    for binding in contract['sourceBindings']:
        value = resolver(binding['logicalPath'])
        actual = file_sha256(value) if isinstance(value, Path) else (hashlib.sha256(value).hexdigest() if isinstance(value, bytes) else None)
        if actual != binding['sha256']:
            raise ValueError('source-binding-hash-drift')


def file_sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def _write(path, value):
    with Path(path).open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(value, stream, ensure_ascii=False, sort_keys=True, allow_nan=False)
        stream.write('\n')


def _read(path):
    with Path(path).open(encoding='utf-8-sig') as stream:
        return json.load(stream)


def _purpose(split, purpose):
    if split not in PURPOSES or purpose not in PURPOSES[split]:
        raise ValueError('purpose-forbidden')


def _contract(contract):
    if contract.get('complete') is not True or contract.get('frozen') is not True:
        raise ValueError('contract-not-complete-frozen')
    if contract.get('analysisRuleVersion') != RULE or not contract.get('reviewVersion'):
        raise ValueError('contract-version')
    members = contract['splitMembers']
    expected = {'train': 71, 'dev': 8, 'test': 8}
    if set(members) != set(expected) or any(len(members[s]) != expected[s] for s in expected):
        raise ValueError('split-membership-count')
    flattened = [m for s in expected for m in members[s]]
    if len(set(flattened)) != 87:
        raise ValueError('split-membership-overlap')
    if contract.get('expectedReviewCounts') != {'train': 36, 'dev': 7, 'test': 7}:
        raise ValueError('review-scope')
    logical = contract.get('outputLogicalRoot', '')
    if not logical.startswith('storage:') or not logical[8:] or any(p in ('', '.', '..') for p in logical[8:].split('/')) or '\\' in logical:
        raise ValueError('output-logical-root-required')
    bindings = contract.get('sourceBindings', [])
    if not bindings or any(not re.fullmatch('[0-9a-f]{64}', b.get('sha256', '')) or
                           not b.get('logicalPath', '').startswith(('repo:', 'storage:')) for b in bindings):
        raise ValueError('source-bindings-required')
    _verify_bindings(contract)


def _evidence_refs(value):
    if isinstance(value, dict):
        for key, child in value.items():
            if key in ('evidenceIds', 'evidenceRefs'):
                if not isinstance(child, list) or any(not isinstance(v, str) for v in child):
                    raise ValueError('invalid-evidence-reference')
                yield from child
            else:
                yield from _evidence_refs(child)
    elif isinstance(value, list):
        for child in value:
            yield from _evidence_refs(child)


def validate_record(record, contract):
    if record.get('schemaVersion') != 'situation-training-record-v1':
        raise ValueError('record-schema')
    meta, inp = record['metadata'], record['input']
    split = meta['split']
    if split not in PURPOSES or meta['matchRef'] not in contract['splitMembers'][split]:
        raise ValueError('record-split-membership')
    if not record.get('sampleId') or not meta.get('roundRef') or type(meta.get('tick')) is not int:
        raise ValueError('record-identity')
    if record.get('labelSource') not in ('template-prelabel', 'human-approved', 'human-modified'):
        raise ValueError('record-label-source')
    if inp['facts']['analysisRuleVersion'] != contract['analysisRuleVersion']:
        raise ValueError('mixed-analysis-rule')
    if not isinstance(inp.get('scene'), dict) or inp['scene'].get('tick') != meta['tick']:
        raise ValueError('full-scene-required')
    evidence = [e['id'] for e in inp['facts']['evidence']]
    if len(evidence) != len(set(evidence)) or len(inp['allowedEvidenceIds']) != len(set(inp['allowedEvidenceIds'])) or set(evidence) != set(inp['allowedEvidenceIds']):
        raise ValueError('evidence-set-mismatch')
    if not set(_evidence_refs(record['output'])) <= set(evidence):
        raise ValueError('unknown-label-evidence')
    for name in ('sourceSceneSha256', 'modelInputSha256', 'factsSha256', 'prelabelSha256'):
        if not re.fullmatch('[0-9a-f]{64}', meta.get(name, '')):
            raise ValueError('missing-source-hash')
    if meta.get('payloadHashDomain') == 'stage6-json-v1':
        for name, value in [('modelInputSha256', inp['scene']), ('factsSha256', inp['facts']), ('prelabelSha256', record['output'])]:
            if meta[name] != object_sha256(value):
                raise ValueError('record-payload-hash-drift')
    return split


def validate_pair(pair, contract):
    record = pair['record']
    split = validate_record(record, contract)
    if pair.get('sampleId') != record['sampleId'] or pair.get('split') != split:
        raise ValueError('pair-identity-mismatch')
    if pair.get('reviewVersion') != contract['reviewVersion'] or pair.get('analysisRuleVersion') != RULE:
        raise ValueError('pair-version-mismatch')
    allowed = pair.get('allowedUse')
    allowed = [allowed] if isinstance(allowed, str) else allowed
    if not allowed or not set(allowed) <= set(PURPOSES[split]) or PURPOSES[split][0] not in allowed:
        raise ValueError('pair-purpose-mismatch')
    hashes = pair.get('sourceHashes', {})
    expected = {'recordSha256': object_sha256(record), 'inputSha256': object_sha256(record['input']),
                'labelSha256': object_sha256(pair['label'])}
    if any(hashes.get(k) != v for k, v in expected.items()):
        raise ValueError('pair-source-hash-mismatch')
    if not set(_evidence_refs(pair['label'])) <= set(record['input']['allowedEvidenceIds']):
        raise ValueError('unknown-reviewed-evidence')
    if pair.get('reviewStatus', 'approved') not in ('approved', 'modified'):
        raise ValueError('pair-not-approved')
    return split


def _new_root(output_dir):
    root = Path(output_dir)
    root.mkdir(parents=True, exist_ok=False)
    _write(root / 'incomplete.json', {'complete': False, 'frozen': False})
    return root


def _write_partitions(root, rows, contract, kind):
    """One self-contained manifest per split: train/dev need no test file or hash read."""
    bindings = []
    for split in PURPOSES:
        directory = root / split
        directory.mkdir()
        payload = directory / (kind + '.jsonl')
        with payload.open('x', encoding='utf-8', newline='\n') as stream:
            for row in rows[split]:
                stream.write(json.dumps(row, ensure_ascii=False, separators=(',', ':'), allow_nan=False) + '\n')
        # Read every saved byte and compare objects before publication.
        read_count = 0
        sentinel = object()
        with payload.open(encoding='utf-8') as stream:
            for line, expected in zip_longest(stream, rows[split], fillvalue=sentinel):
                if line is sentinel or expected is sentinel or json.loads(line) != expected:
                    raise ValueError('partition-readback-mismatch')
                read_count += 1
        manifest = {'schemaVersion': 'situation-stage6-partition-v1', 'kind': kind,
                    'complete': True, 'frozen': True, 'split': split, 'allowedUse': list(PURPOSES[split]),
                    'analysisRuleVersion': RULE, 'reviewVersion': contract['reviewVersion'],
                    'contractSha256': object_sha256(_saved_contract(contract)), 'rowCount': read_count,
                    'file': payload.name, 'sha256': file_sha256(payload), 'hashDomain': 'stage6-json-v1'}
        if kind == 'records':
            manifest['recordSchemaVersion'] = PAIRED_SCHEMA
            manifest['recordSchemaSha256'] = file_sha256(PAIRED_SCHEMA_PATH)
        _write(directory / 'pending-manifest.json', manifest)
        bindings.append({'logicalPath': f'{contract["outputLogicalRoot"]}/{split}/manifest.json',
                         'sha256': file_sha256(directory / 'pending-manifest.json'), 'split': split,
                         'scope': 'isolated-test' if split == 'test' else split,
                         'purpose': PURPOSES[split][0], 'complete': True, 'frozen': True})
        bindings.append({'logicalPath': f'{contract["outputLogicalRoot"]}/{split}/{kind}.jsonl',
                         'sha256': manifest['sha256'], 'split': split, 'purpose': PURPOSES[split][0],
                         'scope': 'isolated-test' if split == 'test' else split, 'complete': True, 'frozen': True})
    return bindings


def _publish_partitions(root):
    for split in PURPOSES:
        (root / split / 'pending-manifest.json').rename(root / split / 'manifest.json')


def partition_review_pairs(pairs, contract, output_dir):
    """ISOLATED preparation only. Caller verifies frozen source inventory first."""
    _contract(contract)
    root = _new_root(output_dir)
    rows = {s: [] for s in PURPOSES}
    seen = set()
    for pair in pairs:
        split = validate_pair(pair, contract)
        if pair['sampleId'] in seen:
            raise ValueError('duplicate-review-pair')
        seen.add(pair['sampleId'])
        rows[split].append(pair)
    if {s: len(rows[s]) for s in rows} != contract['expectedReviewCounts']:
        raise ValueError('review-pair-count')
    bindings = _write_partitions(root, rows, contract, 'review-pairs')
    _verify_bindings(contract)
    _publish_partitions(root)
    _write(root / 'complete.json', {'complete': True, 'frozen': True, 'bindings': bindings})
    return bindings


def _load_partition(directory, split, purpose, manifest_sha256, kind):
    _purpose(split, purpose)  # before ANY file access
    root = Path(directory)
    if file_sha256(root / 'manifest.json') != manifest_sha256:
        raise ValueError('partition-manifest-hash')
    manifest = _read(root / 'manifest.json')
    if manifest.get('complete') is not True or manifest.get('frozen') is not True or manifest.get('kind') != kind:
        raise ValueError('partition-incomplete')
    if manifest['split'] != split or purpose not in manifest['allowedUse'] or manifest['analysisRuleVersion'] != RULE:
        raise ValueError('partition-purpose-version')
    if manifest['file'] != kind + '.jsonl':
        raise ValueError('partition-file-path')
    if kind == 'records' and (manifest.get('recordSchemaVersion') != PAIRED_SCHEMA or
                              manifest.get('recordSchemaSha256') != file_sha256(PAIRED_SCHEMA_PATH)):
        raise ValueError('partition-record-schema-drift')
    payload = root / manifest['file']
    if file_sha256(payload) != manifest['sha256']:
        raise ValueError('partition-payload-hash')
    with payload.open(encoding='utf-8') as stream:
        rows = [json.loads(line) for line in stream]
    if len(rows) != manifest['rowCount'] or any((r.get('split') if kind == 'review-pairs' else r['metadata']['split']) != split for r in rows):
        raise ValueError('partition-row-split')
    if kind == 'records' and any(row.get('schemaVersion') != PAIRED_SCHEMA for row in rows):
        raise ValueError('partition-record-schema-version')
    return rows


def load_review_partition(directory, split, purpose, expected_manifest_sha256):
    return _load_partition(directory, split, purpose, expected_manifest_sha256, 'review-pairs')


def load_corpus_partition(directory, split, purpose, expected_manifest_sha256):
    return _load_partition(directory, split, purpose, expected_manifest_sha256, 'records')



def paired_record_validator():
    """Offline references only; every external schema resource is locally pinned."""
    from jsonschema import Draft202012Validator
    from referencing import Registry, Resource
    paths = [PAIRED_SCHEMA_PATH] + [REPOSITORY / 'schemas/situation' / name for name in (
        'situation-training-record-v1.schema.json', 'minimap-scene-v1.schema.json',
        'situation-facts-v1.schema.json', 'situation-narrative-v1.schema.json')]
    resources, bindings = [], []
    for path in paths:
        schema = _read(path)
        Draft202012Validator.check_schema(schema)
        resources.append((schema['$id'], Resource.from_contents(schema)))
        bindings.append({'logicalPath': 'repo:' + path.relative_to(REPOSITORY).as_posix(),
                         'sha256': file_sha256(path), 'kind': 'schema'})
    registry = Registry().with_resources(resources)
    return Draft202012Validator(_read(PAIRED_SCHEMA_PATH), registry=registry), bindings


def validate_paired_record(record, validator):
    # Do not include jsonschema's potentially complete test payload diagnostic.
    from jsonschema.exceptions import ValidationError
    try:
        validator.validate(record)
    except ValidationError:
        raise ValueError('paired-record-schema-invalid') from None


def prepare_paired_corpus(records, reviewed_pairs, contract, output_dir):
    """ISOLATED all-split preparation; return status/bindings, never raw examples."""
    from situation_stage6_weights import build_epoch_entries
    _contract(contract)
    root = _new_root(output_dir)
    validator, schema_bindings = paired_record_validator()
    by_id, origins, seen_locations = {}, {}, set()
    for row in records:
        validate_record(row, contract)
        key = row['sampleId']
        location = tuple(row['metadata'][k] for k in ('matchRef', 'roundRef', 'tick'))
        if key in by_id or location in seen_locations:
            raise ValueError('duplicate-source-record')
        seen_locations.add(location)
        by_id[key] = deepcopy(row)
        origins[key] = object_sha256(row)
    pair_counts, paired_ids, overlays = Counter(), set(), []
    for pair in reviewed_pairs:
        split = validate_pair(pair, contract)
        key, reviewed = pair['sampleId'], pair['record']
        if key in paired_ids:
            raise ValueError('duplicate-reviewed-target')
        paired_ids.add(key)
        pair_counts[split] += 1
        old = by_id.get(key)
        if old is not None and any(old['metadata'][k] != reviewed['metadata'][k] for k in ('split', 'matchRef', 'roundRef', 'tick')):
            raise ValueError('review-lineage-conflict')
        if old is not None and (object_sha256(old['input']['scene']) != object_sha256(reviewed['input']['scene']) or
                                old['metadata']['sourceSceneSha256'] != reviewed['metadata']['sourceSceneSha256']):
            raise ValueError('review-source-scene-conflict')
        location = tuple(reviewed['metadata'][k] for k in ('matchRef', 'roundRef', 'tick'))
        if old is None and location in seen_locations:
            raise ValueError('conflicting-template-duplicate')
        updated = deepcopy(reviewed)  # reviewed input AND label, never label-only join
        updated['output'] = deepcopy(pair['label'])
        updated['labelSource'] = 'human-modified' if pair.get('reviewStatus') == 'modified' else 'human-approved'
        updated['reviewStatus'] = pair.get('reviewStatus', 'approved')
        updated['reviewVersion'] = pair['reviewVersion']
        updated['purpose'] = PURPOSES[split][0]
        updated['pairingHashes'] = deepcopy(pair['sourceHashes'])
        updated = bind_record_hashes(updated)
        by_id[key] = updated
        seen_locations.add(location)
        overlays.append({'sampleId': key, 'split': split, 'reviewInputSha256': object_sha256(updated['input']),
                         'approvedLabelSha256': object_sha256(updated['output']),
                         'replacedTemplateSha256': origins.get(key),
                         'reason': 'replaced-template' if old is not None else 'reviewed-tick-not-selected-by-v8'})
    if dict(pair_counts) != contract['expectedReviewCounts']:
        raise ValueError('review-overwrite-count')
    rows = {s: [] for s in PURPOSES}
    round_sizes = Counter((r['metadata']['split'], r['metadata']['matchRef'], r['metadata']['roundRef']) for r in by_id.values())
    for record in by_id.values():
        split = record['metadata']['split']
        record['purpose'] = PURPOSES[split][0]
        n = round_sizes[(split, record['metadata']['matchRef'], record['metadata']['roundRef'])]
        record['metadata'].update(sampleWeight=1/n, weightNumerator=1, weightDenominator=n)
        record['schemaVersion'] = PAIRED_SCHEMA
        validate_paired_record(record, validator)
        rows[split].append(record)
    for split in rows:
        rows[split].sort(key=lambda r: (r['metadata']['matchRef'], r['metadata']['roundRef'], r['metadata']['tick'], r['sampleId']))
        if {r['metadata']['matchRef'] for r in rows[split]} != set(contract['splitMembers'][split]):
            raise ValueError('missing-split-match')
    # Independent matches may have identical deterministic Facts. Record that fact;
    # do not mistake semantic equality for same-source leakage or redraw test.
    def semantic_facts(value):
        if isinstance(value, dict):
            return {k: semantic_facts(v) for k, v in value.items() if k not in ('sampleId', 'matchRef', 'roundRef', 'demoRef', 'tick', 'sourcePaths')}
        if isinstance(value, list):
            return [semantic_facts(v) for v in value]
        return value
    similar = defaultdict(list)
    for record in by_id.values():
        similar[object_sha256(semantic_facts(record['input']['facts']))].append(record)
    duplicate_audit = [{'factsSha256': h, 'count': len(group),
                        'splits': sorted({r['metadata']['split'] for r in group}),
                        'distinctSourceLocations': len({tuple(r['metadata'][k] for k in ('matchRef', 'roundRef', 'tick')) for r in group}),
                        'decision': 'retain-independent-deterministic-states'}
                       for h, group in similar.items() if len(group) > 1]
    epoch = build_epoch_entries(rows['train'], {'human_train_total_occurrences_per_epoch': 5})
    totals = defaultdict(float)
    for record in epoch:
        totals[(record['metadata']['matchRef'], record['metadata']['roundRef'])] += record['metadata']['sampleWeight']
    if any(not math.isclose(total, 1.0, rel_tol=0, abs_tol=1e-12) for total in totals.values()):
        raise ValueError('round-weight-total')
    bindings = _write_partitions(root, rows, contract, 'records')
    # Epoch manifest is a compact index, never duplicates full scene/Facts payloads on disk.
    entries = [{'sampleId': r['sampleId'], 'occurrence': r['epochOccurrence'], 'totalOccurrences': r['epochTotalOccurrences'],
                'weightNumerator': 1, 'weightDenominator': r['metadata']['weightDenominator'],
                'sampleWeight': r['metadata']['sampleWeight']} for r in epoch]
    _write(root / 'train' / 'epoch-entries.json', entries)
    _write(root / 'overlays.json', overlays)
    _write(root / 'contract.json', _saved_contract(contract))
    _write(root / 'duplicate-audit-private.json', duplicate_audit)
    for name in ('train/epoch-entries.json', 'overlays.json', 'contract.json', 'duplicate-audit-private.json'):
        _read(root / name)
        bindings.append({'logicalPath': f'{contract["outputLogicalRoot"]}/{name}', 'sha256': file_sha256(root / name),
                         'scope': 'isolated-test' if name in ('overlays.json', 'duplicate-audit-private.json') else 'preparation'})
    details = {'verified': True, 'complete': True, 'frozen': True, 'counts': {s: len(rows[s]) for s in rows},
               'versions': {'analysisRuleVersion': RULE, 'reviewVersion': contract['reviewVersion'], 'recordSchemaVersion': PAIRED_SCHEMA},
               'purposeIsolationVerified': True, 'pairingVerified': True, 'weightsVerified': True, 'splitVerified': True,
               'humanCounts': dict(pair_counts), 'trainEpochEntries': len(entries), 'trainOriginalRounds': len(totals),
               'humanTotalOccurrences': 5, 'gradientIntegration': 'deferred-steps-6-7',
               'exactInputDuplicateGroups': sum(1 for v in Counter(object_sha256(r['input']) for r in by_id.values()).values() if v > 1),
               'nearDuplicatePolicy': 'private identity-stripped Facts hash audit; independent deterministic equality retained; same-source location duplicates rejected',
               'testHistory': 'previously observed during rule acceptance and human review; not externally blind'}
    _write(root / 'split.json', {'schemaVersion': 'situation-stage6-split-reference-v1',
                               'members': contract['splitMembers'], 'purposes': PURPOSES})
    _write(root / 'provenance.json', {'schemaVersion': 'situation-stage6-provenance-v1',
        'versions': details['versions'], 'sourceBindings': contract['sourceBindings'] + schema_bindings,
        'identityPolicy': 'original sample/match/round IDs preserve source lineage',
        'payloadPolicy': 'v8 source rebuild; approved reviewed input and label paired together',
        'hashDomains': {'source': 'original .NET canonical', 'reviewedPayload': 'stage6-json-v1'}})
    statistics = {split: {'samples': len(records),
        'rounds': len({(r['metadata']['matchRef'], r['metadata']['roundRef']) for r in records}),
        'matches': len({r['metadata']['matchRef'] for r in records}),
        'phase': dict(Counter(r['metadata']['phase'] for r in records)),
        'selectionTags': dict(Counter(t for r in records for t in r['metadata']['selectionTags'])),
        'labelSources': dict(Counter(r['labelSource'] for r in records))} for split, records in rows.items()}
    _write(root / 'statistics.json', statistics)
    for name in ('split.json', 'provenance.json', 'statistics.json'):
        _read(root / name)
        bindings.append({'logicalPath': f'{contract["outputLogicalRoot"]}/{name}', 'sha256': file_sha256(root / name),
                         'scope': 'isolated-test' if name == 'statistics.json' else 'preparation'})
    _verify_bindings(contract)
    for b in schema_bindings:
        if file_sha256(REPOSITORY / b['logicalPath'][5:]) != b['sha256']:
            raise ValueError('paired-schema-drift')
    _publish_partitions(root)
    inventory = [{'path': p.relative_to(root).as_posix(), 'sha256': file_sha256(p), 'bytes': p.stat().st_size}
                 for p in sorted(root.rglob('*')) if p.is_file()]
    _write(root / 'manifest.json', {'schemaVersion': 'situation-stage6-corpus-manifest-v1',
        'status': 'complete', 'complete': True, 'frozen': True, 'versions': details['versions'],
        'files': inventory, 'inventoryScope': 'all payloads; report and completion separately sealed',
        'sourceBindings': contract['sourceBindings'] + schema_bindings})
    bindings.append({'logicalPath': f'{contract["outputLogicalRoot"]}/manifest.json', 'sha256': file_sha256(root / 'manifest.json')})
    for item in inventory:
        if file_sha256(root / item['path']) != item['sha256']:
            raise ValueError('final-inventory-drift')
    report = {'schemaVersion': 'situation-stage6-evidence-v1', 'checkId': 'corpus-pairing', 'status': 'passed',
              'reasonCodes': [], 'evidenceBindings': contract['sourceBindings'] + schema_bindings + bindings, 'details': details}
    _write(root / 'report.json', report)
    _write(root / 'complete.json', {'complete': True, 'frozen': True, 'reportSha256': file_sha256(root / 'report.json'),
                                   'manifestSha256': file_sha256(root / 'manifest.json')})
    return report
