"""Complete Facts budget measurement. No model loading or training side effects."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path

try:
    from .situation_lossless_table import canonical
    from .situation_token_measurement import count_sequence, distribution
except ImportError:
    from situation_lossless_table import canonical
    from situation_token_measurement import count_sequence, distribution

ROOT = Path(__file__).resolve().parents[1]
REVISION = '70d244cc86ccca08cf5af4e1e306ecf908b1ad5e'
RULE = 'situation-analysis-rules-v8-close-exposure-1'
REPRESENTATION = 'facts-only-utf8-v1'

def sha(value):
    return hashlib.sha256(value.encode('utf-8')).hexdigest()

class QwenTokenizerAdapter:
    """Caller verifies local tokenizer files/base manifest before construction."""
    def __init__(self, tokenizer, *, revision, chat_template_sha256):
        if revision != REVISION or sha(tokenizer.chat_template) != chat_template_sha256:
            raise ValueError('tokenizer-binding-mismatch')
        self.tokenizer = tokenizer
        self.metadata = dict(modelId='Qwen/Qwen3-1.7B', tokenizerRevision=revision,
                             chatTemplateSha256=chat_template_sha256,
                             enableThinking=False, truncation=False)

    def count_sequence(self, system, user, target):
        return count_sequence(self.tokenizer, system, user, target)

    def count_prompt(self, system, user):
        text = self.tokenizer.apply_chat_template(
            [dict(role='system', content=system), dict(role='user', content=user)],
            tokenize=False, add_generation_prompt=True, enable_thinking=False)
        ids = self.tokenizer.encode(text, add_special_tokens=False, truncation=False)
        if self.tokenizer.decode(ids, skip_special_tokens=False, clean_up_tokenization_spaces=False) != text:
            raise ValueError('tokenizer-round-trip-failed')
        return dict(inferencePromptTokens=len(ids), promptSha256=sha(text))


def measure_paired_budget(record_iter, tokenizer_adapter, contract):
    """One complete traversal. Validation failures are retained, never dropped.

    Ordinary scope consumes train/dev only. isolated-test scope must run in a
    separate process and its detailed report must remain private. Main combines
    only isolatedTestStatus and the isolated report hash into the ordinary report.
    """
    reasons, rows, seen = [], [], set()
    counts = dict(train=0, dev=0, test=0)
    prompt_path = ROOT / 'situation-implementation/situation-facts-prompt-v1.json'
    prompt = json.loads(prompt_path.read_text(encoding='utf-8'))
    metadata = tokenizer_adapter.metadata
    def check(ok, code):
        if not ok and code not in reasons:
            reasons.append(code)
    check(bool(contract.get('evidenceBindings')), 'evidence-bindings-missing')
    check(metadata.get('tokenizerRevision') == REVISION and metadata.get('modelId') == 'Qwen/Qwen3-1.7B', 'tokenizer-revision-invalid')
    check(metadata.get('enableThinking') is False and metadata.get('truncation') is False, 'tokenizer-policy-invalid')
    check(metadata.get('chatTemplateSha256') == contract.get('chatTemplateSha256') and bool(contract.get('chatTemplateSha256')), 'chat-template-binding-mismatch')
    check(contract.get('maxSequenceLength', 2560) == 2560 and contract.get('generationReserveTokens', 512) == 512, 'budget-policy-invalid')
    check(contract.get('promptConfig', prompt) == prompt, 'prompt-config-drift')
    from jsonschema import Draft202012Validator
    for field in ('payloadSchema', 'targetSchema'):
        check(hashlib.sha256((ROOT / prompt[field]).read_bytes()).hexdigest() == prompt[field + 'Sha256'], 'prompt-schema-hash-drift')
    facts_validator = Draft202012Validator(json.loads((ROOT / prompt['payloadSchema']).read_text(encoding='utf-8')))
    target_validator = Draft202012Validator(json.loads((ROOT / prompt['targetSchema']).read_text(encoding='utf-8')))
    isolated = contract.get('scope') == 'isolated-test'
    allowed = {'test'} if isolated else {'train', 'dev'}
    for record in record_iter:
        sample_id = record.get('sampleId')
        split = record.get('metadata', {}).get('split')
        if split not in allowed:
            check(False, 'split-purpose-forbidden')
            continue
        counts[split] += 1
        row = dict(sampleId=sample_id, split=split, status='failed')
        rows.append(row)
        try:
            if not isinstance(sample_id, str) or sample_id in seen:
                raise ValueError('sample-id-invalid-or-duplicate')
            seen.add(sample_id)
            if record.get('schemaVersion') != 'situation-stage6-paired-record-v1':
                raise ValueError('record-schema-invalid')
            purpose = contract.get('purposeBySplit', {}).get(split)
            if purpose not in {'train': {'sft'}, 'dev': {'checkpoint-selection', 'prompt-selection', 'evaluation'}, 'test': {'final-evaluation'}}[split]:
                raise ValueError('split-purpose-forbidden')
            facts = record['input']['facts']
            facts_validator.validate(facts)
            if facts['analysisRuleVersion'] != RULE:
                raise ValueError('analysis-rule-mismatch')
            ids = [e['id'] for e in facts['evidence']]
            allowed_ids = record['input']['allowedEvidenceIds']
            if len(ids) != len(set(ids)) or len(allowed_ids) != len(set(allowed_ids)) or set(ids) != set(allowed_ids):
                raise ValueError('allowed-evidence-mismatch')
            user = canonical(facts)
            if json.loads(user) != facts:
                raise ValueError('facts-round-trip-failed')
            row.update(factsSha256=sha(user), evidenceCount=len(ids))
            if split == 'train':
                target_validator.validate(record['output'])
                if any(not set(h['evidenceIds']) <= set(ids) for h in record['output']['highlights']):
                    raise ValueError('target-evidence-invalid')
                measured = tokenizer_adapter.count_sequence(prompt['system'], user, canonical(record['output']))
                if measured.get('promptTokenPrefixExact') is not True or measured.get('boundaryCrossingTokens') != 0:
                    raise ValueError('token-boundary-mismatch')
                if measured.get('assistantSuffixTokens', 0) < 1:
                    raise ValueError('assistant-eos-missing')
            else:
                measured = tokenizer_adapter.count_prompt(prompt['system'], user)
            if type(measured.get('inferencePromptTokens')) is not int or measured['inferencePromptTokens'] <= 0:
                raise ValueError('token-count-invalid')
            if split == 'train' and (type(measured.get('totalTokens')) is not int or measured['totalTokens'] < measured['inferencePromptTokens']):
                raise ValueError('token-count-invalid')
            row.update(measured)
            row['generationTokensWithReserve'] = measured['inferencePromptTokens'] + 512
            row['overBudget'] = row['generationTokensWithReserve'] > 2560 or (split == 'train' and measured['totalTokens'] > 2560)
            if row['overBudget']:
                raise ValueError('sequence-over-budget')
            row['status'] = 'passed'
        except (ValueError, KeyError, TypeError) as exc:
            code = str(exc) if type(exc) is ValueError else 'record-invalid'
            row['reasonCode'] = code
            check(False, code)
        except Exception as exc:
            # JSON-schema diagnostics can contain entire payloads; do not copy them.
            row['reasonCode'] = 'schema-or-tokenizer-validation-failed'
            check(False, row['reasonCode'])
    expected = contract.get('expectedCounts', {})
    for split in allowed:
        check(type(expected.get(split)) is int and expected[split] > 0 and counts[split] == expected[split], 'full-scope-count-mismatch')
    over = sum(row.get('overBudget', False) for row in rows)
    status_by_split = {s: counts[s] > 0 and all(r['status'] == 'passed' for r in rows if r['split'] == s) for s in allowed}
    test_status = 'passed' if isolated and not reasons else contract.get('isolatedTestStatus', 'not-tested')
    if not isolated:
        check(test_status == 'passed', 'isolated-test-budget-not-passed')
    details = dict(verified=not reasons, maxSequenceLength=2560, generationReserveTokens=512,
                   recordSchemaVersion='situation-stage6-paired-record-v1',
                   representationVersion=REPRESENTATION, tokenizerRevision=REVISION,
                   analysisRuleVersion=RULE, trainValidated=status_by_split.get('train', False),
                   devValidated=status_by_split.get('dev', False), isolatedTestStatus=test_status,
                   overBudgetCount=over, counts=counts, rows=rows,
                   distributions={s: {k: distribution([r[k] for r in rows if r['split'] == s and k in r])
                                      for k in ('inferencePromptTokens', 'totalTokens', 'generationTokensWithReserve')
                                      if any(r['split'] == s and k in r for r in rows)} for s in allowed},
                   totalTrainTokens=sum(r.get('totalTokens', 0) for r in rows if r['split'] == 'train'))
    return dict(schemaVersion='situation-stage6-budget-report-v1', checkId='paired-budget',
                status='failed' if reasons else 'passed', reasonCodes=reasons,
                evidenceBindings=contract.get('evidenceBindings', []), details=details)
