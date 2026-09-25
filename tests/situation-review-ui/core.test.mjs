import test from 'node:test';
import assert from 'node:assert/strict';
import { ReviewState, newDraft, filterSamples, neighbor, shortcut, inspectDraft, canSave, draftBlocking, evaluationKeys, parseStrictJson } from '../../apps/cli/SituationReviewUi/core.mjs';
import { point, headingEnd } from '../../apps/cli/SituationReviewUi/radar.mjs';

const narrative = { schemaVersion: 'situation-narrative-v1', highlights: [{ textZh: '双方分散。', evidenceIds: ['e1'] }, { textZh: '接触风险较低。', evidenceIds: ['e2'] }], summaryZh: '双方尚未接触。', uncertainties: [] };
const detail = (id = 'sample-a') => ({ candidate: { sampleId: id, candidate: structuredClone(narrative), input: { allowedEvidenceIds: ['e1', 'e2'] } }, decision: null, sampleRevision: 0, workspaceRevision: 0, blockingIssue: false });
function ready() { const state = new ReviewState(); state.acceptLoad(state.beginLoad('sample-a'), detail()); state.draft.evaluations = { factsCorrect: true, focusReasonable: true, summaryAccurateUseful: true, hallucination: false }; state.validation = { ...inspectDraft(state.draft.narrativeText, ['e1', 'e2']), valid: true }; return state; }
test('new evaluations require four explicit answers; server saved booleans restore exactly', () => {
  assert.deepEqual(Object.values(newDraft(detail()).evaluations), [null, null, null, null]);
  const state = ready(); assert.equal(canSave(state, 'approved'), true);
  for (const key of evaluationKeys) { const old = state.draft.evaluations[key]; state.draft.evaluations[key] = null; assert.equal(canSave(state, 'approved'), false); state.draft.evaluations[key] = old; }
  const saved = detail(); saved.decision = { evaluations: { factsCorrect: false, focusReasonable: true, summaryAccurateUseful: false, hallucination: true } };
  assert.deepEqual(newDraft(saved).evaluations, saved.decision.evaluations);
});
test('filtering retains source, ordinal order, decision and categories; boundaries never wrap', () => {
  const items = [{ sampleId: 'b', reviewOrdinal: 2, split: 'test', categories: ['plant'], decision: 'rejected' }, { sampleId: 'a', reviewOrdinal: 1, split: 'train', categories: ['plant'], decision: 'unreviewed' }];
  assert.deepEqual(filterSamples(items).map(s => s.sampleId), ['a', 'b']);
  assert.equal(items[0].sampleId, 'b'); assert.equal(filterSamples(items, { unreviewed: true }).length, 1);
  assert.equal(filterSamples(items, { split: 'test', category: 'plant', decision: 'rejected' }).length, 1);
  assert.equal(filterSamples(items, { category: 'missing' }).length, 0);
  const list = filterSamples(items); assert.equal(neighbor(list, 'a', -1), null); assert.equal(neighbor(list, 'b', 1), null); assert.equal(neighbor(list, 'a', 1), 'b'); assert.equal(neighbor([], 'a', 1), null); assert.equal(neighbor(list, 'outside', 1), 'a');
});
test('late loads and validations never render another sample or older edit', () => {
  const state = new ReviewState(), a = state.beginLoad('sample-a'), b = state.beginLoad('sample-b');
  assert.equal(state.acceptLoad(a, detail()), false); assert.equal(state.acceptLoad(b, detail('sample-b')), true);
  const old = state.beginValidation(); state.draft.narrativeText += ' '; const current = state.beginValidation();
  assert.equal(state.acceptValidation(old, { valid: true }), false); assert.equal(state.acceptValidation(current, { valid: true }), true);
  const late = state.beginValidation(); state.acceptLoad(state.beginLoad('sample-c'), detail('sample-c'));
  assert.equal(state.acceptValidation(late, { valid: true }), false);
});
test('dirty drafts survive conflict; loads reset only after accepted server response', () => {
  const state = ready(); assert.equal(state.dirty, true); state.baseline = JSON.stringify(state.draft); assert.equal(state.dirty, false);
  state.draft.note = '人工备注'; state.conflict = true; assert.equal(state.dirty, true); assert.equal(canSave(state, 'approved'), false);
  const before = state.draft; state.beginLoad('sample-b'); assert.equal(state.draft, before);
});
test('idempotent retry keeps requestId, changed payload creates new operation, save locks navigation', () => {
  const state = ready(); let n = 0; const uuid = () => `id-${++n}`;
  const first = state.operation('decision', { sampleId: 'sample-a', decision: 'approved' }, uuid);
  assert.deepEqual(state.operation('decision', { sampleId: 'sample-a', decision: 'approved' }, uuid), first);
  const second = state.operation('decision', { sampleId: 'sample-a', decision: 'rejected' }, uuid); assert.notEqual(first.requestId, second.requestId);
  state.busy = true; assert.equal(state.beginLoad('sample-b'), null); assert.equal(canSave(state, 'approved'), false);
  const response = { sampleId: 'sample-a', progress: { blockingIssue: true } };
  assert.equal(state.acceptSave(first, response), false); assert.equal(state.acceptSave(second, { ...response, sampleId: 'sample-b' }), false);
  assert.equal(state.acceptSave(second, response), true); assert.equal(state.blocking, true); assert.equal(state.pending, null);
});
test('unknown evidence and malformed narrative block approval but permit explicit rejection report', () => {
  const state = ready(), edited = structuredClone(narrative); edited.highlights[0].evidenceIds = ['not-current'];
  const result = inspectDraft(JSON.stringify(edited), ['e1', 'e2']); assert.deepEqual(result.unknownIds, ['not-current']); assert.ok(result.errors.length);
  state.validation = { ...result, valid: false }; assert.equal(canSave(state, 'modified'), false);
  state.draft.rejectReasonCode = 'unknown-evidence'; state.draft.note = '发现未知引用'; state.draft.issueCodes = ['unknown-evidence'];
  assert.equal(draftBlocking(state.draft), true); assert.equal(canSave(state, 'rejected'), true);
  assert.ok(inspectDraft('{', []).errors.length); assert.ok(inspectDraft('null', []).errors.length);
  const extra = { ...narrative, arbitrary: '不接受' }; assert.ok(inspectDraft(JSON.stringify(extra), ['e1', 'e2']).errors.length);
});
test('modified must differ, original approval cannot silently discard an edit, blocking is sticky', () => {
  const state = ready(); assert.equal(canSave(state, 'modified'), false);
  state.validation.narrative.summaryZh = '仍未观察到明显接触。'; assert.equal(canSave(state, 'approved'), false); assert.equal(canSave(state, 'modified'), true);
  state.draft.evaluations.factsCorrect = false; assert.equal(canSave(state, 'modified'), false);
  state.draft.evaluations.factsCorrect = true; state.blocking = true; assert.equal(canSave(state, 'modified'), false);
});
test('keyboard ignores repeats, input, composition and modifier chords', () => {
  assert.equal(shortcut({ key: 'a' }), 'approved'); assert.equal(shortcut({ key: 'ArrowRight' }), 'next');
  for (const variation of [{ repeat: true }, { isComposing: true }, { ctrlKey: true }, { target: { tagName: 'TEXTAREA' } }, { target: { tagName: 'INPUT' } }, { target: { tagName: 'BUTTON' } }, { target: { isContentEditable: true } }]) assert.equal(shortcut({ key: 'a', ...variation }), null);
});
test('radar uses normalized coordinates and screen heading with no secondary Y flip', () => {
  assert.deepEqual(point({ x: .25, y: .75 }), { x: 256, y: 768 }); assert.equal(point(null), null);
  assert.deepEqual(headingEnd({ x: .25, y: .75 }, { x: 0, y: -1 }), { x: 256, y: 738 });
  assert.equal(headingEnd({ x: .2, y: .2 }, null), null);
});
test('duplicate JSON keys including escaped equivalents are rejected before serialization', () => {
  assert.throws(() => parseStrictJson('{"x":1,"x":2}'), /duplicate/);
  assert.throws(() => parseStrictJson('{"x":1,"\\u0078":2}'), /duplicate/);
  assert.throws(() => parseStrictJson('{"array":[{"x":1,"x":2}]}'), /duplicate/);
  assert.deepEqual(parseStrictJson('{"x":[null,true,1e2,"a,b:{}"],"y":{"x":2}}'), { x: [null, true, 100, 'a,b:{}'], y: { x: 2 } });
});
test('property ordering is not a meaningful edit; reject reason independently triggers blocking', () => {
  const state = ready(); state.validation.narrative = { uncertainties: [], summaryZh: narrative.summaryZh, highlights: narrative.highlights, schemaVersion: narrative.schemaVersion };
  assert.equal(canSave(state, 'approved'), true); assert.equal(canSave(state, 'modified'), false);
  state.draft.rejectReasonCode = 'future-information'; assert.equal(draftBlocking(state.draft), true); assert.equal(canSave(state, 'approved'), false);
});
