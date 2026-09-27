import { ReviewState, evaluationKeys, blockingCodes, filterSamples, neighbor, shortcut, inspectDraft, canSave, sortedSet } from './core.mjs';
import { drawRadar } from './radar.mjs';

const $ = id => document.getElementById(id), state = new ReviewState();
let session, samples = [], loading = false, image = null, imageMissing = false, validationTimer;
const filters = () => ({ split: $('split').value, category: $('category').value, decision: $('decisionFilter').value, unreviewed: $('unreviewed').checked });
const visible = () => filterSamples(samples, filters());
const make = (tag, text, className) => { const element = document.createElement(tag); if (text !== undefined) element.textContent = text; if (className) element.className = className; return element; };
const value = v => v === null || v === undefined ? 'unknown' : typeof v === 'object' ? JSON.stringify(v, null, 2) : String(v);
function message(text, error = false) { $('status').textContent = text; $('status').classList.toggle('invalid', error); }
function showPairs(target, pairs) { target.replaceChildren(); for (const [key, item] of pairs) target.append(make('dt', key), make('dd', value(item))); }
async function api(path, body) {
  const options = { cache: 'no-store', credentials: 'same-origin' };
  if (body !== undefined) Object.assign(options, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Review-Token': session.token }, body: JSON.stringify(body) });
  let response;
  try { response = await fetch(path, options); } catch { throw Object.assign(new Error('network-unavailable'), { status: 0 }); }
  let data;
  try { data = await response.json(); } catch { throw Object.assign(new Error('invalid-server-response'), { status: response.status }); }
  if (!response.ok) throw Object.assign(new Error(typeof data.code === 'string' ? data.code : 'request-failed'), { status: response.status });
  return data;
}
function renderProgress(progress) {
  session.progress = progress; state.blocking = progress.blockingIssue; $('blocking').hidden = !progress.blockingIssue;
  $('progress').replaceChildren(...[
    `有效 ${progress.completed}/${progress.total}`, `未复核 ${progress.unreviewed}`, `拒绝 ${progress.rejected}`, `操作 ${progress.operations}`, `workspace revision ${progress.workspaceRevision}`,
    ...Object.entries(progress.splits).map(([split, p]) => `${split} 有效 ${p.completed}/${p.total} · 未复核 ${p.unreviewed} · 拒绝 ${p.rejected}`)
  ].map(text => make('span', text)));
}
function renderNavigation() {
  const list = visible(), id = state.detail?.candidate.sampleId;
  $('selection').textContent = `筛选匹配 ${list.length}/${samples.length} 条${id ? ` · 当前 #${state.detail.candidate.reviewOrdinal} · ${state.detail.candidate.split} · ${id} · revision ${state.detail.sampleRevision}${list.some(s => s.sampleId === id) ? '' : '（当前样本不在筛选中）'}` : ''}`;
  for (const [button, delta] of [['previous', -1], ['next', 1]]) $(button).disabled = state.busy || loading || !neighbor(list, id, delta);
  for (const id of ['split', 'category', 'decisionFilter', 'unreviewed', 'go', 'jump', 'refresh']) $(id).disabled = state.busy || loading;
}
function renderControls() {
  for (const [id, kind] of [['approve', 'approved'], ['modify', 'modified'], ['reject', 'rejected']]) $(id).disabled = loading || !canSave(state, kind);
  for (const id of ['editor', 'evaluations', 'issues', 'issueFields', 'note', 'rejectReason', 'resetNarrative']) $(id).disabled = state.busy || loading;
  $('conflict').hidden = !state.conflict;
  $('replace').hidden = state.detail?.decision?.decision !== 'rejected';
  $('replace').disabled = state.busy || loading || state.dirty || state.conflict || state.blocking || state.detail?.blockingIssue;
  renderNavigation();
}
function highlightEvidence(ids) {
  let first;
  for (const element of $('evidence').children) { const selected = ids.includes(element.dataset.evidenceId); element.classList.toggle('selected', selected); if (selected && !first) first = element; }
  first?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}
function renderNarrative(target, narrative) {
  target.replaceChildren(); if (!narrative) { target.append(make('p', 'JSON 无效，暂无预览。', 'invalid')); return; }
  const allowed = new Set(state.detail.candidate.input.allowedEvidenceIds);
  for (const highlight of Array.isArray(narrative.highlights) ? narrative.highlights : []) {
    const ids = Array.isArray(highlight?.evidenceIds) ? highlight.evidenceIds : [], unknown = ids.some(id => !allowed.has(id));
    const button = make('button', value(highlight?.textZh), `highlight${unknown ? ' invalid' : ''}`);
    button.append(make('small', `\n${ids.join(', ')}${unknown ? ' · 未知引用' : ''}`)); button.addEventListener('click', () => highlightEvidence(ids)); target.append(button);
  }
  target.append(make('p', value(narrative.summaryZh)));
  const list = make('ul'); for (const item of Array.isArray(narrative.uncertainties) ? narrative.uncertainties : []) list.append(make('li', item)); target.append(list);
}
function renderValidation(result, pending = false) {
  const box = $('validation'); box.replaceChildren(); box.classList.toggle('invalid', !result.valid && !pending);
  box.append(make('p', pending ? '正在使用服务端校验器验证…' : result.valid ? '服务端校验通过。' : '当前编辑稿不能批准。'));
  for (const error of result.errors ?? []) box.append(make('p', error));
  if (result.unknownIds?.length) box.append(make('p', `无效引用：${result.unknownIds.join(', ')}。可修正编辑，或勾选“未知 evidence”并保存拒绝报告。`, 'invalid'));
  $('editor').setAttribute('aria-invalid', String(!result.valid && !pending)); renderControls();
}
async function validateNow() {
  if (!state.detail || loading) return;
  const ticket = state.beginValidation(), local = inspectDraft(ticket.text, state.detail.candidate.input.allowedEvidenceIds);
  renderNarrative($('preview'), local.narrative);
  if (local.errors.length) { state.acceptValidation(ticket, { ...local, valid: false }); renderValidation(state.validation); return; }
  renderValidation({ ...local, valid: false }, true);
  try {
    const server = await api('/review/validate', { sampleId: ticket.id, narrative: local.narrative });
    if (state.acceptValidation(ticket, { ...local, valid: server.valid, errors: server.errors })) renderValidation(state.validation);
  } catch (error) { if (state.acceptValidation(ticket, { ...local, valid: false, errors: [`校验失败：${error.message}。请重试编辑或重读服务端。`] })) renderValidation(state.validation); }
}
function changed(narrative = false) {
  if (!state.draft) return;
  if (narrative) { state.draft.narrativeText = $('editor').value; state.validation.valid = false; ++state.validationSequence; clearTimeout(validationTimer); validationTimer = setTimeout(validateNow, 250); }
  state.draft.issueFields = $('issueFields').value; state.draft.note = $('note').value; state.draft.rejectReasonCode = $('rejectReason').value;
  renderControls();
}
function renderDetail() {
  const { candidate, decision } = state.detail, { scene, facts } = candidate.input;
  $('workspace').hidden = false;
  $('ruleVersion').hidden = false;
    $('ruleVersion').textContent = `当前候选规则：${facts.analysisRuleVersion} · 几何：${scene.geometryVersion}。` +
      (candidate.schemaVersion === 'situation-review-candidate-v8-close-exposure'
        ? '已启用 600 单位局部探测、连续斜坡与 1 秒预测。已验证水平暴露距离不超过 150 单位时为高风险，超过时为中风险；按 200 单位/秒估算时间。保留成功即返回，距离不保证最短，高风险不代表当前已交火。'
        : candidate.schemaVersion === 'situation-review-candidate-v7-verified-exposure'
        ? '已启用 600 单位局部探测、连续斜坡检测与 1 秒预测。局部探测成功时展示已验证暴露距离，按水平速度 200 单位/秒估算时间；保留成功即返回，不搜索最短路径。详见接触风险证据。'
        : candidate.schemaVersion === 'situation-review-candidate-v5-continuous-slope'
        ? '已启用连续斜坡贴地检测与 1 秒恒速预测：移动沿坡面调整高度，通过身体和地面检查后最多判为中风险。暂不支持台阶、断崖或跳跃；45° 坡度上限为当前保守配置。未读取未来帧，历史地图匹配尚未认证。'
        : candidate.schemaVersion === 'situation-review-candidate-v4-position-prediction'
        ? '已启用 1 秒短时位置预测（0.25/0.5/0.75/1 秒）与局部移动探测：保持当前速度的假设经移动路径和射线检查后最多判为中风险。未读取未来帧；不模拟转向、急停或跳跃。筛选类别沿用 v1，历史地图匹配尚未认证。'
        : candidate.schemaVersion === 'situation-review-candidate-v3-local-peek'
        ? '已启用局部移动探测：短距离单方移动后可能暴露时最多判为中风险，不代表已交火。筛选类别沿用 v1；不预测移动意图，历史地图匹配尚未认证。'
        : candidate.schemaVersion === 'situation-review-candidate-v2-raycast'
        ? '事实、证据与摘要已用三维射线重算；筛选类别沿用原 v1 抽样。静态地图、近似身体高度，历史地图匹配尚未认证。'
        : '复核页展示该工件原始事实，不会自动套用其他版本的规则。');
  $('clock').replaceChildren(); const clock = make('dl'); showPairs(clock, [['target tick', scene.tick], ['tickRate', scene.tickRate], ['phase', scene.round.phase], ['elapsed / remaining 秒', `${value(scene.round.elapsedSeconds)} / ${value(scene.round.remainingSeconds)}`], ['clockSource', scene.round.clockSource], ['C4', scene.bomb], ['teams', scene.teams]]); $('clock').append(clock);
  drawRadar($('radar'), scene, image); $('radarNote').textContent = `${imageMissing ? '底图缺失；当前显示归一化坐标网格。 ' : ''}T 金色 · CT 蓝色 · 历史轨迹 · 方向箭头 · HP · C4 红色；缺失位置不绘制，详见下方明细。`;
  $('sceneDetails').textContent = JSON.stringify({ players: scene.players, utilities: scene.utilities, effects: scene.effects, geometry: scene.geometry }, null, 2);
  const labels = { alive: '人数', totalHealth: '生命', bomb: 'C4', formation: '队形', pressure: '压力', contestedRegions: '争夺区域', contactRisk: '接触风险', isolatedSide: '孤立方', spatialAdvantage: '空间优势（不是胜率）', confidence: '置信度' };
  showPairs($('facts'), Object.entries(labels).map(([key, label]) => [label, facts[key]]));
  $('quality').textContent = JSON.stringify({ scene: scene.dataQuality, facts: facts.dataQuality }, null, 2);
  $('evidence').replaceChildren();
  for (const evidence of facts.evidence) {
    const item = make('article', undefined, 'evidence'); item.dataset.evidenceId = evidence.id;
    item.append(make('strong', evidence.id), make('p', evidence.textZh), make('small', `ruleId: ${evidence.ruleId}`), make('small', `sourcePaths: ${evidence.sourcePaths.join(', ')}`)); $('evidence').append(item);
  }
  renderNarrative($('candidate'), candidate.candidate); $('editor').value = state.draft.narrativeText;
  for (const key of evaluationKeys) for (const radio of document.querySelectorAll(`input[name="${key}"]`)) radio.checked = state.draft.evaluations[key] === (radio.value === 'true');
  for (const input of $('issues').querySelectorAll('input')) input.checked = state.draft.issueCodes.includes(input.value);
  $('issueFields').value = state.draft.issueFields; $('note').value = state.draft.note; $('rejectReason').value = state.draft.rejectReasonCode;
  $('saved').textContent = decision ? JSON.stringify(decision, null, 2) : '尚无人工决定。'; renderControls();
}
async function loadSample(id, force = false) {
  if (!id || state.busy || loading) return false;
  if (!force && (state.dirty || state.pending) && !window.confirm('当前样本有未保存草稿或未确认操作。放弃当前页面状态并切换？')) return false;
  const ticket = state.beginLoad(id); loading = true; clearTimeout(validationTimer); renderControls(); message(`正在读取 ${id}…`);
  try {
    const detail = await api(`/review/samples/${encodeURIComponent(id)}`);
    if (!state.acceptLoad(ticket, detail)) return false;
    history.replaceState(null, '', `#${encodeURIComponent(id)}`); renderDetail(); message(`已读取 #${detail.candidate.reviewOrdinal}，请逐项判断。`); return true;
  } catch (error) { message(`读取失败：${error.message}。当前草稿已保留。`, true); return false; }
  finally { loading = false; renderControls(); if (state.detail) void validateNow(); }
}
async function reload() {
  if (state.busy || loading || ((state.dirty || state.pending) && !window.confirm('重读将丢弃当前草稿并查询权威记录。已复制需要保留的内容并继续？'))) return;
  loading = true; renderControls();
  try { const next = await api('/review/session'); session = next; renderProgress(next.progress); samples = await api('/review/samples'); const id = state.detail?.candidate.sampleId; loading = false; await loadSample(samples.some(s => s.sampleId === id) ? id : visible()[0]?.sampleId, true); renderNavigation(); }
  catch (error) { message(`重读失败：${error.message}`, true); }
  finally { loading = false; renderControls(); }
}
async function save(kind) {
  if (!canSave(state, kind) || loading) return;
  const draft = state.draft, id = state.detail.candidate.sampleId;
  const payload = state.operation('decision', { sampleId: id, expectedRevision: state.detail.sampleRevision, decision: kind, evaluations: draft.evaluations,
    issueFields: sortedSet(draft.issueFields.split(/\r?\n/).map(v => v.trim()).filter(Boolean)), issueCodes: sortedSet([...draft.issueCodes, ...(draft.evaluations.factsCorrect === false ? ['facts-incorrect'] : []), ...(kind === 'rejected' && blockingCodes.includes(draft.rejectReasonCode) ? [draft.rejectReasonCode] : [])]), note: draft.note.trim() || null,
    rejectReasonCode: kind === 'rejected' ? draft.rejectReasonCode : null, editedNarrative: kind === 'modified' ? state.validation.narrative : null }, () => crypto.randomUUID());
  state.busy = true; renderControls(); message(`正在保存 #${state.detail.candidate.reviewOrdinal} · ${kind}…`);
  let confirmed = false, refreshFailed = false;
  try {
    const result = await api('/review/decisions', payload);
    if (!state.acceptSave(payload, result)) throw new Error('response-sample-mismatch');
    renderProgress(result.progress); confirmed = true;
    // The backend response follows durable readback. Refresh the detail and list before another decision.
    samples = await api('/review/samples');
  } catch (error) { if (error.status === 409) state.conflict = true; if (confirmed) { refreshFailed = true; samples = []; } message(`${confirmed ? '决定已保存，但刷新失败' : '保存未确认'}：${error.message}${confirmed ? '；请重读服务端。' : '。草稿保留；相同载荷重试会沿用 requestId。'}`, true); }
  finally { state.busy = false; renderControls(); }
  if (confirmed) { const loaded = await loadSample(id, true); if (loaded && !refreshFailed) message(`已确认保存 ${kind} · ${id}。拒绝后需单独点击后备替换。`); else { state.conflict = true; renderControls(); message('决定已确认保存，列表或详情重读失败。请重读服务端后继续。', true); } }
}
async function replace() {
  if ($('replace').disabled || !window.confirm('此操作保留拒绝记录，并按冻结规则选取同 split 后备。新样本仍需人工复核。继续替换？')) return;
  const id = state.detail.candidate.sampleId;
  const request = state.operation('replacement', { sampleId: id, expectedWorkspaceRevision: session.progress.workspaceRevision }, () => crypto.randomUUID());
  state.busy = true; renderControls(); let nextId, refreshFailed = false;
  try { const response = await api('/review/replacements', request); if (!state.acceptSave(request, response) || !response.newSampleId) throw new Error('response-sample-mismatch'); nextId = response.newSampleId; renderProgress(response.progress); samples = await api('/review/samples'); }
  catch (error) { if (error.status === 409) state.conflict = true; if (nextId) { refreshFailed = true; samples = []; } message(`${nextId ? '替换已确认，但列表重读失败' : '替换未完成确认'}：${error.message}。请重试或重读服务端。`, true); }
  finally { state.busy = false; renderControls(); }
  if (nextId) { const loaded = await loadSample(nextId, true); if (!loaded || refreshFailed) { state.conflict = true; renderControls(); message('替换已确认，但列表或详情重读失败。请重读服务端后继续。', true); } }
}
function initializeForm() {
  const labels = ['直接事实正确', '重点选择合理', '摘要准确且有用', '存在幻觉（是表示存在问题）'];
  evaluationKeys.forEach((key, index) => { const row = make('div', undefined, 'evaluation'); row.append(make('span', labels[index])); const choices = make('span'); for (const [text, choice] of [['是', true], ['否', false]]) { const label = make('label'), input = make('input'); input.type = 'radio'; input.name = key; input.value = String(choice); input.addEventListener('change', () => { state.draft.evaluations[key] = choice; changed(); }); label.append(input, document.createTextNode(text)); choices.append(label); } row.append(choices); $('evaluations').append(row); });
  for (const [code, text] of [['facts-incorrect', '事实错误（阻断）'], ['identity-leak', '身份泄漏（阻断）'], ['future-information', '未来信息（阻断）'], ['unknown-evidence', '未知 evidence（阻断）']]) { const label = make('label'), input = make('input'); input.type = 'checkbox'; input.value = code; input.addEventListener('change', () => { state.draft.issueCodes = [...$('issues').querySelectorAll('input:checked')].map(item => item.value); changed(); }); label.append(input, document.createTextNode(text)); $('issues').append(label); }
  $('editor').addEventListener('input', () => changed(true)); for (const id of ['issueFields', 'note', 'rejectReason']) $(id).addEventListener('input', () => changed());
  $('resetNarrative').addEventListener('click', () => { if (window.confirm('将编辑区恢复为模板候选？')) { $('editor').value = JSON.stringify(state.detail.candidate.candidate, null, 2); changed(true); } });
  for (const id of ['split', 'category', 'decisionFilter', 'unreviewed']) $(id).addEventListener('change', renderNavigation);
  $('previous').addEventListener('click', () => loadSample(neighbor(visible(), state.detail?.candidate.sampleId, -1)));
  $('next').addEventListener('click', () => loadSample(neighbor(visible(), state.detail?.candidate.sampleId, 1)));
  $('go').addEventListener('click', () => { const query = $('jump').value.trim(), target = samples.find(s => s.sampleId === query || String(s.reviewOrdinal) === query); target ? loadSample(target.sampleId) : message('未找到当前 active 集中的 sampleId 或 ordinal。', true); });
  $('refresh').addEventListener('click', reload); $('approve').addEventListener('click', () => save('approved')); $('modify').addEventListener('click', () => save('modified')); $('reject').addEventListener('click', () => save('rejected')); $('replace').addEventListener('click', replace);
  document.addEventListener('keydown', event => { const action = shortcut(event); if (!action || state.busy || loading) return; event.preventDefault(); if (action === 'previous' || action === 'next') $(action).click(); else void save(action); });
  window.addEventListener('beforeunload', event => { if (state.dirty || state.busy || state.pending) { event.preventDefault(); event.returnValue = ''; } });
}
async function start() {
  initializeForm(); renderControls();
  try {
    session = await api('/review/session'); if (!session.token) throw new Error('missing-session-token');
    showPairs($('identity'), [['dataset SHA-256', session.datasetSha256], ['candidate manifest SHA-256', session.candidateManifestSha256], ['review draft', session.reviewDraft]]); renderProgress(session.progress);
    for (const category of session.categories) { const option = make('option', category); option.value = category; $('category').append(option); }
    samples = await api('/review/samples'); let remembered; try { remembered = decodeURIComponent(location.hash.slice(1)); } catch { remembered = ''; }
    const first = samples.find(s => s.sampleId === remembered) ?? samples.find(s => s.decision === 'unreviewed') ?? samples[0];
    if (first) await loadSample(first.sampleId, true); else { message('当前工作区没有可复核样本。'); renderNavigation(); }
  } catch (error) { message(`无法加载工作区：${error.message}。请确认本地服务仍运行并刷新。`, true); }
  const background = new Image(); background.onload = () => { image = background; if (state.detail) drawRadar($('radar'), state.detail.candidate.input.scene, image); }; background.onerror = () => { imageMissing = true; if (state.detail) { drawRadar($('radar'), state.detail.candidate.input.scene, null); $('radarNote').textContent = '底图缺失；显示归一化坐标网格。T 金色 · CT 蓝色 · C4 红色。'; } }; background.src = '/review-assets/mirage.webp';
}
void start();
