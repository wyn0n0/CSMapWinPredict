"""Step 4 CPU-only prompt, plan and diagnostic selection reference.

No generation, label selection, model loading or test partition access.
Semantic Narrative support remains a separate human evaluation requirement.
"""
from collections import Counter
from copy import deepcopy
from functools import lru_cache
import hashlib
import json

from jsonschema import Draft202012Validator

from situation_core_context import (load_config, system_prompt, render_core,
                                    project_scene, observation_map, require)
from situation_environment import REPO, read_json
from situation_lossless_table import canonical
from situation_stage6_corpus import object_sha256

PROMPTS = 'situation-implementation/situation-commentary-prompt-v1.json'
RULES = 'situation-implementation/situation-commentary-rules-v1.json'
PROTOCOL = 'situation-implementation/situation-commentary-protocol-v1.json'
PLAN_SCHEMA = 'schemas/situation/situation-commentary-plan-v1.schema.json'
GROUPS = ('A', 'B', 'C', 'D')


@lru_cache(maxsize=1)
def configs():
    prompt, rules, protocol = (read_json(REPO / p) for p in (PROMPTS, RULES, PROTOCOL))
    require(prompt['version'] == 'commentary-prompts-v1'
            and rules['version'] == 'commentary-rules-v1', 'commentary-config-version')
    require(set(prompt['groups']) == set(GROUPS), 'commentary-groups')
    require(protocol['input']['maxSequenceLength'] == 5120
            and protocol['input']['generationReserveTokens'] == 512
            and protocol['input']['truncation'] is False, 'commentary-budget-policy')
    require(rules['planner']['maxClaims'] == rules['planner']['maxLimitations'] == 2,
            'planner-bounds')
    return prompt, rules, protocol


def build_plan(facts, core):
    """Finite deterministic claims grounded in current Facts; no hidden geometry."""
    ids = {e['id'] for e in facts['evidence']}
    claims, limitations = [], []
    def claim(text, evidence, rules):
        require(set(evidence) <= ids, 'planner-evidence-missing')
        return dict(textZh=text, evidenceIds=evidence, ruleIds=rules)
    t, ct = facts['alive']['t'], facts['alive']['ct']
    known = type(t) is int and type(ct) is int
    advantage = 'T' if known and t > ct else 'CT' if known and ct > t else None
    conflict = advantage is not None and facts['spatialAdvantage'] in ('T', 'CT') and advantage != facts['spatialAdvantage']
    planted = facts['bomb']['state'] in ('planted', 'defusing')
    focus = 'uncertain'
    if conflict:
        focus = 'factor-conflict'
        claims.append(claim('人数与空间条件偏向不同阵营，不能直接合成胜负判断。',
                            ['alive.t', 'alive.ct', 'spatial-advantage'], ['g1', 'g4']))
    if planted:
        if not claims: focus = 'post-plant'
        claims.append(claim('C4已下包，当前攻防围绕拆包机会。', ['bomb.state'], ['g2']))
    if not claims and facts['contactRisk'] in ('medium', 'high'):
        focus = 'contact'
        claims.append(claim('当前存在接触风险，但不证明已经交火。', ['contact-risk'], ['g3']))
    if len(claims) < 2 and known:
        if not claims: focus = 'numbers'
        claims.append(claim(f'T方{t}人，CT方{ct}人；人数不能单独决定局势。', ['alive.t', 'alive.ct'], ['g1']))
    if not claims:
        # Do not fabricate support just to produce a second claim.
        focus = 'uncertain'
    if planted and core['bomb']['secondsToExplosion'] is None:
        limitations.append(dict(textZh='爆炸剩余时间未知，不能判断是否来得及。',
                                sceneRefs=['scene:/bomb/secondsToExplosion'], ruleIds=['g2', 'g6']))
    if facts['confidence'] == 'low':
        limitations.append(dict(textZh='规则置信度低，局势解释需要保留。', evidenceIds=['confidence'], ruleIds=['g6']))
    elif any(q['severity'] in ('warning', 'error') for q in facts['dataQuality']):
        limitations.append(dict(textZh='输入存在质量限制，缺失信息不可当作已知。', evidenceIds=['input-quality'], ruleIds=['g6']))
    elif any(p['floor'] == 'unknown' for p in core['players']):
        index = next(i for i, p in enumerate(core['players']) if p['floor'] == 'unknown')
        limitations.append(dict(textZh='存在未知楼层，不能据此补充楼层关系。',
                                sceneRefs=[f'scene:/players/{index}/floor'], ruleIds=['g6']))
    plan = dict(version='commentary-planner-v1', focus=focus, supportedClaims=claims[:2], limitations=limitations[:2])
    validate_plan_structure(plan, facts, core)
    return plan


@lru_cache(maxsize=1)
def plan_validator():
    schema = read_json(REPO / PLAN_SCHEMA)
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def validate_plan_structure(plan, facts, core):
    plan_validator().validate(plan)
    ids = {e['id'] for e in facts['evidence']}
    rules = {r['id'] for r in configs()[1]['rules']}
    refs = observation_map(core)
    for item in plan['supportedClaims'] + plan['limitations']:
        require(set(item.get('evidenceIds', [])) <= ids, 'planner-unknown-evidence')
        require(set(item.get('sceneRefs', [])) <= set(refs), 'planner-unknown-scene-ref')
        require(set(item['ruleIds']) <= rules, 'planner-unknown-rule')


def verify_plan(plan, facts, core):
    validate_plan_structure(plan, facts, core)
    require(plan == build_plan(facts, core), 'planner-content-drift')


def render_groups(record, witness=None):
    require(record.get('metadata', {}).get('split') in ('train', 'dev'), 'commentary-test-forbidden')
    user, provenance = render_core(record, witness)
    return render_payload(user, record['input']['facts'], project_scene(record['input']['scene'])), provenance


def render_payload(user, facts, core):
    prompt, rules, _ = configs()
    common = system_prompt(load_config()) + '\n' + prompt['commonSuffix']
    common += '\n<general_rules>' + canonical(dict(version=rules['version'], rules=rules['rules'])) + '</general_rules>'
    result = {}
    for group in GROUPS:
        entry = prompt['groups'][group]
        system = common
        if entry.get('inherit'):
            system += '\n' + prompt['groups'][entry['inherit']]['suffix']
        if entry.get('suffix'): system += '\n' + entry['suffix']
        if group == 'C': system += '\n<teaching_example>' + canonical(entry['example']) + '</teaching_example>'
        actual_user, plan = user, None
        if group == 'D':
            system += '\n' + entry['planSuffix']
            plan = build_plan(facts, core)
            actual_user += '\n<content_plan>' + canonical(plan) + '</content_plan>'
        result[group] = dict(system=system, user=actual_user, plan=plan,
                             payloadSha256=object_sha256(json.loads(user)))
    return result


def repair_prompt(raw, adapter):
    repair = configs()[0]['repair']
    require(isinstance(raw, str) and bool(raw.strip()), 'repair-empty')
    count = len(adapter.tokenizer.encode(raw, add_special_tokens=False, truncation=False))
    require(count <= repair['maxSourceOutputTokens'], 'repair-source-over-budget')
    system, user = repair['system'], repair['prefix'] + raw + repair['suffix']
    measured = adapter.count_prompt(system, user)
    require(measured['inferencePromptTokens'] + 512 <= 5120, 'repair-input-over-budget')
    return dict(system=system, user=user, **measured)


def features(record):
    """Input-only tags, including explicit missing observations (not labels)."""
    require(record['metadata']['split'] in ('train', 'dev'), 'commentary-test-forbidden')
    facts, scene = record['input']['facts'], record['input']['scene']
    alive = facts['alive']; t, ct = alive['t'], alive['ct']
    known = type(t) is int and type(ct) is int
    tags = []
    if t == ct == 5: tags.append('five-v-five')
    if known and min(t, ct) == 1 and max(t, ct) >= 1: tags.append('clutch')
    if facts['bomb']['state'] in ('planted', 'defusing'): tags.append('post-plant')
    advantage = 'T' if known and t > ct else 'CT' if known and ct > t else None
    if advantage and facts['spatialAdvantage'] in ('T', 'CT') and advantage != facts['spatialAdvantage']:
        tags.append('factor-conflict')
    if facts['confidence'] == 'low' or any(q['severity'] in ('warning', 'error') for q in facts['dataQuality']):
        tags.append('low-quality')
    if any(o['value'] is None or o['value'] == 'unknown' for o in observation_map(project_scene(scene)).values()):
        tags.append('unknown')
    meta = record['metadata']
    return dict(sampleId=record['sampleId'], split=meta['split'], matchRef=meta['matchRef'],
                roundRef=meta['roundRef'], categories=tags,
                reviewed=record.get('labelSource') in ('human-approved', 'human-modified'),
                inputSha256=object_sha256(record['input']),
                sourceSceneSha256=meta['sourceSceneSha256'])


def stable_key(sample_id, namespace='select'):
    return hashlib.sha256(('42|' + namespace + '|' + sample_id).encode()).hexdigest()


def select_diagnostics(rows, policy=None):
    policy = configs()[2]['diagnostics'] if policy is None else policy
    require(all(r['split'] in ('train', 'dev') for r in rows), 'commentary-test-forbidden')
    require(len({r['sampleId'] for r in rows}) == len(rows), 'duplicate-sample-id')
    result = []
    for split, wanted in policy['counts'].items():
        candidates = sorted((deepcopy(r) for r in rows if r['split'] == split), key=lambda r: stable_key(r['sampleId']))
        selected = [r for r in candidates if split == 'dev' and r['reviewed']]
        require(split != 'dev' or len(selected) == policy['expectedReviewedDev'], 'reviewed-dev-count')
        def available():
            used_rounds = {(r['matchRef'], r['roundRef']) for r in selected}
            used_scenes = {r['sourceSceneSha256'] for r in selected}
            return [r for r in candidates if (r['matchRef'], r['roundRef']) not in used_rounds
                    and r['sourceSceneSha256'] not in used_scenes]
        # Reserve rare category rounds first so arbitrary match coverage cannot
        # consume the only eligible real scene. Overrides reflect input inventory.
        minimums = {c: policy.get('minimumPerCategoryOverrides', {}).get(split, {}).get(
            c, policy['minimumPerCategory'][split]) for c in policy['categories']}
        while True:
            counts = Counter(c for r in selected for c in r['categories'])
            deficient = [c for c in policy['categories'] if counts[c] < minimums[c]]
            if not deficient: break
            options = available()
            category = min(deficient, key=lambda c: (sum(c in r['categories'] for r in options), c))
            options = [r for r in options if category in r['categories']]
            require(bool(options) and len(selected) < wanted, 'diagnostic-category-coverage:' + category)
            options.sort(key=lambda r: (-sum(c in r['categories'] for c in deficient), stable_key(r['sampleId'])))
            selected.append(options[0])
        while len({r['matchRef'] for r in selected}) < policy['minimumMatches'][split]:
            known = {r['matchRef'] for r in selected}
            options = [r for r in available() if r['matchRef'] not in known]
            require(bool(options) and len(selected) < wanted, 'diagnostic-match-coverage')
            selected.append(options[0])
        while len(selected) < wanted:
            known = {r['matchRef'] for r in selected}
            options = available()
            require(bool(options), 'diagnostic-insufficient-rows')
            options.sort(key=lambda r: (r['matchRef'] in known, stable_key(r['sampleId'])))
            selected.append(options[0])
        result += sorted(selected, key=lambda r: stable_key(r['sampleId'], 'order'))
    return result
