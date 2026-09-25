export const evaluationKeys = ['factsCorrect', 'focusReasonable', 'summaryAccurateUseful', 'hallucination'];
export const blockingCodes = ['facts-incorrect', 'identity-leak', 'future-information', 'unknown-evidence'];
export const clone = value => JSON.parse(JSON.stringify(value));
export const sortedSet = values => [...new Set(values)].sort();
export function canonical(value) {
  if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']';
  if (value && typeof value === 'object') return '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}';
  return JSON.stringify(value);
}
export function parseStrictJson(text) {
  const value = JSON.parse(text);
  // JSON.parse validates grammar; this second walk rejects duplicate object keys
  // before reserialization could silently erase them from the server request.
  const tokens = text.match(/"(?:\\.|[^"\\])*"|[{}\[\]:,]|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null/g);
  let index = 0;
  function walk() {
    const token = tokens[index++];
    if (token === '{') {
      const seen = new Set();
      while (tokens[index] !== '}') {
        const key = JSON.parse(tokens[index++]); if (seen.has(key)) throw new Error('duplicate-property'); seen.add(key);
        index++; walk(); if (tokens[index] === ',') index++;
      }
      index++;
    } else if (token === '[') { while (tokens[index] !== ']') { walk(); if (tokens[index] === ',') index++; } index++; }
  }
  walk(); return value;
}
export function filterSamples(samples, filters = {}) {
  return samples.filter(s => (!filters.split || s.split === filters.split) &&
    (!filters.category || s.categories.includes(filters.category)) &&
    (!filters.decision || s.decision === filters.decision) &&
    (!filters.unreviewed || s.decision === 'unreviewed')).sort((a, b) => a.reviewOrdinal - b.reviewOrdinal);
}
export function neighbor(samples, id, delta) {
  const index = samples.findIndex(s => s.sampleId === id);
  if (index < 0) return samples[delta < 0 ? samples.length - 1 : 0]?.sampleId ?? null;
  return samples[index + delta]?.sampleId ?? null;
}
export function shortcut(event) {
  const target = event.target;
  if (event.repeat || event.isComposing || event.altKey || event.ctrlKey || event.metaKey ||
      target?.isContentEditable || /^(INPUT|TEXTAREA|SELECT|BUTTON)$/.test(target?.tagName ?? '')) return null;
  return ({ ArrowLeft: 'previous', ArrowRight: 'next', a: 'approved', m: 'modified', r: 'rejected' })[event.key] ?? null;
}
export function newDraft(detail) {
  const saved = detail.decision;
  return {
    narrativeText: JSON.stringify(saved?.finalNarrative ?? detail.candidate.candidate, null, 2),
    evaluations: Object.fromEntries(evaluationKeys.map(k => [k, saved?.evaluations?.[k] ?? null])),
    issueFields: (saved?.issueFields ?? []).join('\n'), issueCodes: [...(saved?.issueCodes ?? [])],
    note: saved?.note ?? '', rejectReasonCode: saved?.rejectReasonCode ?? ''
  };
}
export function inspectDraft(text, allowedIds) {
  let narrative;
  try { narrative = parseStrictJson(text); } catch { return { narrative: null, unknownIds: [], errors: ['Narrative JSON 语法无效或含重复字段。'] }; }
  const errors = [], allowed = new Set(allowedIds), unknownIds = [];
  const object = narrative && typeof narrative === 'object' && !Array.isArray(narrative);
  if (!object) return { narrative: null, unknownIds, errors: ['Narrative 必须为对象。'] };
  const keys = Object.keys(narrative).sort().join(',');
  if (keys !== 'highlights,schemaVersion,summaryZh,uncertainties') errors.push('Narrative 字段与契约不符。');
  if (narrative.schemaVersion !== 'situation-narrative-v1') errors.push('Narrative schemaVersion 无效。');
  const chinese = value => typeof value === 'string' && value.trim() && /[\u3400-\u9fff]/u.test(value);
  if (!chinese(narrative.summaryZh) || narrative.summaryZh.length > 240) errors.push('摘要须为不超过 240 字的中文文本。');
  if (!Array.isArray(narrative.highlights) || narrative.highlights.length < 2 || narrative.highlights.length > 4) errors.push('须有 2–4 条 highlights。');
  for (const [i, highlight] of (Array.isArray(narrative.highlights) ? narrative.highlights : []).entries()) {
    if (!highlight || typeof highlight !== 'object' || Object.keys(highlight).sort().join(',') !== 'evidenceIds,textZh') { errors.push(`highlight ${i + 1} 形状无效。`); continue; }
    if (!chinese(highlight.textZh) || highlight.textZh.length > 240) errors.push(`highlight ${i + 1} 须为不超过 240 字的中文文本。`);
    if (!Array.isArray(highlight.evidenceIds) || !highlight.evidenceIds.length || new Set(highlight.evidenceIds).size !== highlight.evidenceIds.length) errors.push(`highlight ${i + 1} 证据引用缺失或重复。`);
    for (const id of Array.isArray(highlight.evidenceIds) ? highlight.evidenceIds : []) if (typeof id !== 'string' || !allowed.has(id)) unknownIds.push(String(id));
  }
  if (!Array.isArray(narrative.uncertainties) || !narrative.uncertainties.every(chinese) || new Set(narrative.uncertainties).size !== narrative.uncertainties.length) errors.push('uncertainties 须为不重复的中文文本数组。');
  if (unknownIds.length) errors.push('引用了不在当前 allowedEvidenceIds 中的证据。');
  return { narrative, unknownIds: sortedSet(unknownIds), errors };
}
export function draftBlocking(draft) { return draft.evaluations.factsCorrect === false || draft.issueCodes.some(c => blockingCodes.includes(c)) || blockingCodes.includes(draft.rejectReasonCode); }
export function canSave(state, decision) {
  if (!state.detail || state.busy || state.conflict || evaluationKeys.some(k => typeof state.draft.evaluations[k] !== 'boolean')) return false;
  if (decision === 'rejected') return /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/.test(state.draft.rejectReasonCode) && !!state.draft.note.trim();
  if (state.blocking || state.detail.blockingIssue || draftBlocking(state.draft) || !state.validation.valid) return false;
  const changed = canonical(state.validation.narrative) !== canonical(state.detail.candidate.candidate);
  return decision === 'modified' ? changed : !changed;
}
// Each load/validation belongs to one sample and one generation. Late responses are inert.
export class ReviewState {
  constructor() { this.detail = null; this.draft = null; this.busy = false; this.conflict = false; this.blocking = false; this.generation = 0; this.validationSequence = 0; this.pending = null; this.validation = { valid: false, errors: [] }; }
  beginLoad(id) { if (this.busy) return null; this.loadingId = id; return { id, generation: ++this.generation }; }
  acceptLoad(ticket, detail) {
    if (!ticket || ticket.generation !== this.generation || ticket.id !== this.loadingId || detail.candidate.sampleId !== ticket.id) return false;
    this.detail = detail; this.draft = newDraft(detail); this.baseline = JSON.stringify(this.draft); this.conflict = false; this.pending = null;
    this.validation = { valid: false, errors: [] }; ++this.validationSequence; return true;
  }
  get dirty() { return !!this.draft && JSON.stringify(this.draft) !== this.baseline; }
  beginValidation() { this.validation.valid = false; return { id: this.detail.candidate.sampleId, generation: this.generation, sequence: ++this.validationSequence, text: this.draft.narrativeText }; }
  acceptValidation(ticket, result) {
    if (ticket.generation !== this.generation || ticket.sequence !== this.validationSequence || ticket.id !== this.detail?.candidate.sampleId || ticket.text !== this.draft.narrativeText) return false;
    this.validation = result; return true;
  }
  operation(kind, payload, uuid) {
    const signature = JSON.stringify({ kind, payload });
    if (!this.pending || this.pending.signature !== signature) this.pending = { kind, signature, payload: { ...clone(payload), requestId: uuid() } };
    return clone(this.pending.payload);
  }
  acceptSave(request, response) {
    if (request.sampleId !== this.detail?.candidate.sampleId || response.sampleId !== request.sampleId || request.requestId !== this.pending?.payload.requestId) return false;
    this.blocking = response.progress.blockingIssue; this.baseline = JSON.stringify(this.draft); this.pending = null; return true;
  }
}
