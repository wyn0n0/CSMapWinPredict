"""Readable, reversible table candidate. Never rounds, drops, or hashes away input values."""
import json


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'), allow_nan=False)


def encode(value):
    if isinstance(value, dict):
        keys = sorted(value)
        return ['o', keys, [encode(value[k]) for k in keys]]
    if isinstance(value, list):
        if len(value) > 1 and all(canonical(v) == canonical(value[0]) for v in value[1:]):
            return ['c', encode(value[0]), len(value)]
        if len(value) > 2 and all(type(v) is int for v in value):
            step = value[1] - value[0]
            if all(v == value[0] + i * step for i, v in enumerate(value)):
                return ['r', value[0], step, len(value)]
        if value and all(isinstance(v, dict) and set(v) == set(value[0]) for v in value):
            keys = sorted(value[0])
            return ['t', keys, [encode([v[k] for v in value]) for k in keys], len(value)]
        return ['a', [encode(v) for v in value]]
    if value is None or type(value) in (str, int, float, bool):
        return value
    raise ValueError('unsupported-value-type')


def decode(value):
    if not isinstance(value, list):
        return value
    tag = value[0]
    if tag == 'o' and len(value) == 3:
        _, keys, values = value
        if len(keys) != len(values) or len(set(keys)) != len(keys):
            raise ValueError('invalid-object-columns')
        return dict(zip(keys, map(decode, values)))
    if tag == 'a' and len(value) == 2:
        return [decode(v) for v in value[1]]
    if tag == 'c' and len(value) == 3 and type(value[2]) is int and value[2] > 0:
        return [decode(value[1]) for _ in range(value[2])]
    if tag == 'r' and len(value) == 4 and all(type(v) is int for v in value[1:]) and value[3] > 0:
        return [value[1] + i * value[2] for i in range(value[3])]
    if tag == 't' and len(value) == 4:
        _, keys, columns, count = value
        cols = [decode(v) for v in columns]
        if type(count) is not int or count < 1 or len(keys) != len(cols) or len(set(keys)) != len(keys) or any(not isinstance(c, list) or len(c) != count for c in cols):
            raise ValueError('invalid-table-columns')
        return [dict(zip(keys, (c[i] for c in cols))) for i in range(count)]
    raise ValueError('invalid-codec-tag')
