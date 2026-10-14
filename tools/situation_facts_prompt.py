"""Shared deterministic Facts-only model input; independent of labels and Scene."""
import json

from situation_lossless_table import canonical


def render_facts(source):
    facts = source['facts']
    ids = [item['id'] for item in facts['evidence']]
    if len(ids) != len(set(ids)):
        raise ValueError('duplicate-evidence-id')
    allowed = source['allowedEvidenceIds']
    if len(allowed) != len(set(allowed)) or set(ids) != set(allowed):
        raise ValueError('facts-ids-differ-from-allowed-set')
    result = canonical(facts)
    if json.loads(result) != facts:
        raise ValueError('facts-content-changed')
    return result
