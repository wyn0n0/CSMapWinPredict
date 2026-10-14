"""Three independent Demo exporters; immutable shards and verified byte merge.

Only the isolated data-preparation process may call this module. Assignment uses
Demo file sizes and frozen match IDs, never labels or held-out measurements.
"""
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
import hashlib
import json

from situation_environment import digest, read_json, safe_child, write_new_json
from validate_situation_stage6_preparation import require

ALGORITHM = 'demo-bytes-desc-greedy-v1'


def assign_shards(members, sizes, count=3):
    require(1 <= count <= 3 and len({m['matchId'] for m in members}) == len(members), 'shard-input-invalid')
    totals, assignments = [0] * count, {}
    for member in sorted(members, key=lambda m: (-sizes[m['matchId']], m['matchId'])):
        key = member['matchId']
        require(sizes[key] >= 0, 'shard-size-invalid')
        index = min(range(count), key=lambda i: (totals[i], i))
        assignments[key] = index
        totals[index] += sizes[key]
    return assignments


def match_ref(match_id):
    payload = 'situation-training-match-ref-v1\ndata=situation-training-data-v1\nmatch=' + match_id
    return 'match-' + hashlib.sha256(payload.encode('utf-8')).hexdigest()


def merge_partition(store, run, split, purpose, members, assignments, binding):
    output = run / ('source-' + split)
    output.mkdir(exist_ok=False)
    write_new_json(output / 'incomplete.json', dict(status='incomplete', complete=False))
    shards, bound, common = [], [], None
    fields = ('schemaVersion', 'split', 'purpose', 'analysisRuleVersion', 'splitSha256', 'analysisRulesSha256',
              'selectionSha256', 'baseManifestSha256', 'baseProvenanceSha256', 'meshSha256',
              'identityPolicy', 'selectionPolicy', 'sourceFiles')
    for index in range(3):
        folder = run / f'shard-{index}' / split
        path = folder / 'manifest.json'
        manifest = read_json(path)
        expected_ids = sorted(m['matchId'] for m in members if assignments[m['matchId']] == index)
        expected_refs = {match_ref(x) for x in expected_ids}
        require(manifest.get('status') == 'complete' and manifest.get('complete') is True
                and manifest.get('frozen') is True and manifest['split'] == split
                and manifest['purpose'] == purpose, 'shard-not-complete')
        require(manifest['sharding'] == dict(index=index, count=3, algorithm=ALGORITHM, matchIds=expected_ids), 'shard-assignment-mismatch')
        require(set(manifest['matches']) == expected_refs and len(manifest['matches']) == len(expected_refs)
                and manifest['counts']['matches'] == len(expected_refs), 'shard-members-mismatch')
        current = {k: manifest[k] for k in fields}
        if common is None: common = current
        require(current == common, 'shard-contract-mismatch')
        require(len(manifest['files']) == 1 and manifest['files'][0]['path'] == split + '.jsonl', 'shard-file-inventory')
        artifact = manifest['files'][0]
        record_path = safe_child(folder, artifact['path'])
        b = binding(store, record_path, split=split, purpose=purpose)
        require(b['sha256'] == artifact['sha256'], 'shard-file-drift')
        bound += [binding(store, path, split=split, purpose=purpose), b]
        for source in manifest['sourceFiles']: store.verify(source)
        shards.append((record_path, manifest, expected_refs))
    ids, rounds, matches, total = set(), set(), set(), 0
    target_path = output / (split + '.jsonl')
    with target_path.open('xb') as target:
        for path, manifest, expected_refs in shards:
            local_rounds, local_count = set(), 0
            with path.open('rb') as source:
                for line in source:
                    require(line.endswith(b'\n'), 'shard-incomplete-line')
                    row = json.loads(line)
                    meta = row['metadata']
                    require(meta['split'] == split and meta['matchRef'] in expected_refs
                            and row['sampleId'] not in ids, 'shard-row-identity')
                    ids.add(row['sampleId'])
                    local_rounds.add(meta['roundRef'])
                    matches.add(meta['matchRef'])
                    target.write(line)
                    local_count += 1
            require(local_count == manifest['counts']['samples'] == manifest['files'][0]['rows']
                    and len(local_rounds) == manifest['counts']['rounds'], 'shard-readback-count')
            require(not rounds.intersection(local_rounds), 'shard-round-overlap')
            rounds.update(local_rounds)
            total += local_count
    require(matches == {match_ref(m['matchId']) for m in members}, 'merged-members-incomplete')
    # Reopen the complete output and compare every byte against the validated
    # source streams. The .NET exporter already checked every typed v8 record.
    with target_path.open('rb') as merged:
        for path, _, _ in shards:
            with path.open('rb') as source:
                while chunk := source.read(1024 * 1024):
                    require(merged.read(len(chunk)) == chunk, 'merged-byte-readback')
        require(merged.read(1) == b'', 'merged-extra-bytes')
    for b in bound + common['sourceFiles']: store.verify(b)
    result = dict(deepcopy(common), status='complete', complete=True, frozen=True,
                  sharding=dict(count=3, algorithm=ALGORITHM, merged=True), shardBindings=bound,
                  matches=sorted(matches), counts=dict(samples=total, rounds=len(rounds), matches=len(matches)),
                  files=[dict(path=target_path.name, sha256=digest(target_path), rows=total)])
    write_new_json(output / 'manifest.json', result)
    return result


def export_parallel(store, run, baseline, cli_dll, base, command_evidence, binding, verify_inputs, repository, purposes):
    split_path = repository / baseline['split_file']
    split_doc = read_json(split_path)
    plan = dict(schemaVersion='situation-stage6-export-plan-v1', workers=3, algorithm=ALGORITHM, splits={})
    for split in purposes:
        members = split_doc[split]
        sizes = {m['matchId']: (repository / 'data/mirage' / m['fileName']).stat().st_size for m in members}
        plan['splits'][split] = dict(assignments=assign_shards(members, sizes), demoBytes=sizes)
    write_new_json(run / 'parallel-plan.json', plan)

    def worker(index):
        folder = run / f'shard-{index}'
        folder.mkdir(exist_ok=False)
        for split, purpose in purposes.items():
            command_evidence(['dotnet', str(cli_dll), '--export-situation-stage6-source-shard',
                str(repository / 'data/mirage'), str(split_path), str(base), str(folder / split), split, purpose,
                str(index), '3'], folder / ('export-' + split + '.json'), stream_progress=True)
        print(json.dumps(dict(worker=index, status='exported')), flush=True)

    verify_inputs()
    with ThreadPoolExecutor(max_workers=3) as pool:
        futures = [pool.submit(worker, i) for i in range(3)]
        for future in futures: future.result()
    verify_inputs()
    for split, purpose in purposes.items():
        merge_partition(store, run, split, purpose, split_doc[split], plan['splits'][split]['assignments'], binding)
    verify_inputs()
