"""Round-balanced expansion and assistant/EOS sample-mean reduction (no optimizer)."""
from collections import Counter
import math


def build_epoch_entries(train_records, repeat_policy):
    """Five TOTAL occurrences for approved human records; one for template records."""
    repeat = repeat_policy.get('human_train_total_occurrences_per_epoch')
    if type(repeat) is not int or repeat != 5:
        raise ValueError('human-repeat-must-total-five')
    records = list(train_records)
    if not records:
        raise ValueError('empty-train')
    ids, denominators, repeats = set(), Counter(), []
    for record in records:
        meta = record['metadata']
        if meta['split'] != 'train' or record.get('purpose', 'sft') != 'sft':
            raise ValueError('train-purpose-required')
        if record['sampleId'] in ids:
            raise ValueError('duplicate-source-sample')
        ids.add(record['sampleId'])
        human = record['labelSource'] in ('human-approved', 'human-modified')
        if human and record['reviewStatus'] not in ('approved', 'modified'):
            raise ValueError('human-review-not-approved')
        n = repeat if human else 1
        repeats.append(n)
        denominators[(meta['matchRef'], meta['roundRef'])] += n
    result = []
    for record, n in zip(records, repeats):
        meta = record['metadata']
        d = denominators[(meta['matchRef'], meta['roundRef'])]
        for occurrence in range(n):
            # Full immutable scene/Facts payload is shared; only entry metadata varies.
            item = dict(record)
            item['metadata'] = dict(meta)
            item['metadata'].update(sampleWeight=1 / d, weightNumerator=1, weightDenominator=d)
            item['epochOccurrence'] = occurrence
            item['epochTotalOccurrences'] = n
            result.append(item)
    return result


def weighted_sample_mean_loss(token_losses, assistant_mask, sample_weights, *, normalizer):
    """Torch [batch,tokens] loss AFTER causal alignment, mask includes assistant EOS.

    Return sum(w_i * mean(supervised token losses_i)) / fixed normalizer.
    normalizer is predetermined globally/window-wide, NEVER sum(batch weights).
    Epoch objective uses number of original rounds. A trainer using gradient
    accumulation must compensate any framework automatic division exactly once.
    """
    import torch
    if not math.isfinite(normalizer) or normalizer <= 0:
        raise ValueError('positive-fixed-normalizer-required')
    if token_losses.ndim != 2 or assistant_mask.shape != token_losses.shape or assistant_mask.dtype != torch.bool:
        raise ValueError('loss-mask-shape')
    if token_losses.shape[0] == 0 or not torch.isfinite(token_losses).all().item():
        raise ValueError('empty-or-nonfinite-loss')
    weights = torch.as_tensor(sample_weights, dtype=token_losses.dtype, device=token_losses.device)
    if weights.shape != (token_losses.shape[0],) or not torch.isfinite(weights).all().item() or (weights <= 0).any().item():
        raise ValueError('invalid-sample-weights')
    counts = assistant_mask.sum(dim=1)
    if (counts == 0).any().item():
        raise ValueError('empty-supervision')
    means = token_losses.masked_fill(~assistant_mask, 0).sum(dim=1) / counts
    return (means * weights).sum() / normalizer
