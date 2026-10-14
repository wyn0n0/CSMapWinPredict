"""Shared token counts and train-only measurement summaries; no CLI side effects."""
from collections import Counter
import hashlib
import math
try:
    from .situation_evidence import require
except ImportError:
    from situation_evidence import require

def sha_text(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()


def train_membership(manifest, split, rows):
    groups = [{m['matchId'] for m in split[k]} for k in ('train', 'dev', 'test')]
    require([len(g) for g in groups] == [71, 8, 8] and len(set.union(*groups)) == 87, 'split-not-disjoint-71-8-8')
    refs = {'match-' + sha_text('situation-training-match-ref-v1\ndata=' + manifest['versions']['data'] + '\nmatch=' + m) for m in groups[0]}
    require(len(rows) == 500 and len({r['sampleId'] for r in rows}) == 500, 'pilot-must-have-500-unique-rows')
    require(all(r['metadata']['split'] == 'train' and r['metadata']['matchRef'] in refs for r in rows), 'non-train-member-rejected')
    require(len({r['metadata']['matchRef'] for r in rows}) == 71, 'pilot-train-match-coverage-mismatch')


def distribution(values):
    values = sorted(values)
    require(bool(values), 'empty-distribution')
    return dict(min=values[0], p50=values[math.ceil(len(values)*.50)-1],
                p95=values[math.ceil(len(values)*.95)-1], p99=values[math.ceil(len(values)*.99)-1], max=values[-1])


def count_sequence(tokenizer, system, user, target):
    messages = [dict(role='system', content=system), dict(role='user', content=user)]
    prompt = tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True, enable_thinking=False)
    complete = tokenizer.apply_chat_template(messages + [dict(role='assistant', content=target)],
                                             tokenize=False, add_generation_prompt=False, enable_thinking=False)
    require(complete.startswith(prompt + target), 'chat-template-training-prefix-mismatch')
    tokens = tokenizer(complete, add_special_tokens=False, truncation=False, return_offsets_mapping=True)
    ids, offsets = tokens['input_ids'], tokens['offset_mapping']
    prompt_ids = tokenizer.encode(prompt, add_special_tokens=False, truncation=False)
    # Full-sequence offsets define an exact partition even if a BPE token crosses a boundary.
    input_count = sum(end <= len(prompt) for start, end in offsets)
    target_end = len(prompt) + len(target)
    target_count = sum(end > len(prompt) and start < target_end for start, end in offsets)
    completion = len(ids) - input_count
    special_ids = set(tokenizer.all_special_ids)
    require(tokenizer.eos_token_id in ids[input_count:], 'assistant-eos-missing')
    require(tokenizer.decode(ids, skip_special_tokens=False, clean_up_tokenization_spaces=False) == complete, 'tokenizer-round-trip-failed')
    return dict(inputTokens=input_count, inferencePromptTokens=len(prompt_ids), targetTokens=target_count,
                targetStandaloneTokens=len(tokenizer.encode(target, add_special_tokens=False, truncation=False)),
                completionTokens=completion, assistantSuffixTokens=completion-target_count,
                systemStandaloneTokens=len(tokenizer.encode(system, add_special_tokens=False)),
                userStandaloneTokens=len(tokenizer.encode(user, add_special_tokens=False)),
                specialTokens=sum(token in special_ids for token in ids),
                specialTokenCounts=dict(Counter(tokenizer.convert_ids_to_tokens(i) for i in ids if i in special_ids)),
                totalTokens=len(ids), promptTokenPrefixExact=ids[:len(prompt_ids)] == prompt_ids,
                boundaryCrossingTokens=sum(start < len(prompt) < end for start, end in offsets),
                trainingTextSha256=sha_text(complete), promptSha256=sha_text(prompt), targetSha256=sha_text(target),
                fullUtf8Bytes=len(complete.encode('utf-8')))


def summary(rows, budgets):
    fields = ('inputTokens', 'inferencePromptTokens', 'targetTokens', 'targetStandaloneTokens',
              'completionTokens', 'assistantSuffixTokens', 'systemStandaloneTokens', 'userStandaloneTokens', 'specialTokens', 'totalTokens')
    return dict(samples=len(rows), distributions={f: distribution([r[f] for r in rows]) for f in fields},
                overBudget={str(b): dict(total=sum(r['totalTokens'] > b for r in rows),
                                         input=sum(r['inputTokens'] > b for r in rows),
                                         targetAlone=sum(r['targetStandaloneTokens'] > b for r in rows)) for b in budgets},
                prefixExactCount=sum(r['promptTokenPrefixExact'] for r in rows))
