"""Deterministic resource-probe coverage and assistant-only labels, without truncation."""


def choose_rows(rows, count=16):
    ordered = sorted(rows, key=lambda r: (-r['totalTokens'], r['sampleId']))
    if len(ordered) < count:
        raise ValueError('insufficient-probe-rows')
    selected = []
    def add(row):
        if row['sampleId'] not in {r['sampleId'] for r in selected}:
            selected.append(row)
    for metric in ('totalTokens', 'inputTokens', 'completionTokens'):
        add(min(rows, key=lambda r: (-r[metric], r['sampleId'])))
    def features(row):
        return {'phase:' + row['phase'], *('tag:' + t for t in row['selectionTags'])}
    needed = set.union(*(features(row) for row in rows))
    for row in selected:
        needed -= features(row)
    while needed and len(selected) < count:
        best = min(ordered, key=lambda r: (-len(features(r) & needed), -r['totalTokens'], r['sampleId']))
        add(best)
        needed -= features(best)
    if needed:
        raise ValueError('probe-coverage-exceeds-micro-batch-budget')
    for row in ordered:
        if len(selected) == count:
            break
        add(row)
    return selected


def assistant_labels(ids, prompt_ids, eos_id, max_length):
    if len(ids) > max_length:
        raise ValueError('probe-input-exceeds-budget')
    if ids[:len(prompt_ids)] != prompt_ids or not 0 < len(prompt_ids) < len(ids):
        raise ValueError('invalid-assistant-boundary')
    if eos_id not in ids[len(prompt_ids):]:
        raise ValueError('assistant-eos-missing')
    return [-100] * len(prompt_ids) + ids[len(prompt_ids):]
