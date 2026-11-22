'use strict';
const $ = id => document.getElementById(id);
const text = (id, s) => { $(id).textContent = s; };
const localBridge = location.protocol === 'http:' && location.hostname === '127.0.0.1' && !!location.port &&
  location.pathname === '/' && !location.search && !location.hash &&
  document.querySelector('meta[name="farm-shadow-bridge-origin"]')?.content === location.origin;
let key = '', revision = 0, pollTimer, ownTimer, visibleTimer, snapshot = null, deadline = 0, bindingId = null;
function invalidateRequests() {
  revision++;
  clearTimeout(pollTimer); clearTimeout(ownTimer); clearTimeout(visibleTimer);
  return revision;
}
function clearVisible() {
  text('visible', '—'); text('shadow', '无当前影子结果。');
}
function resetForms() {
  $('confirm').reset(); $('observe').reset();
}
function clearFrame(message) {
  snapshot = null; deadline = 0; bindingId = null;
  text('status', message); text('own', '—'); clearVisible(); resetForms();
  $('confirmFields').disabled = true; $('observeFields').disabled = true; text('meta', '等待有效本人帧。');
}
const side = v => v === 'radiant' ? '天辉' : '夜魇';
const clock = s => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
const siteState = { present: '看见目标', empty: '看见为空', unknown: '未知' };
const areaName = { own: '亲见己方局部', enemy: '对方区域', unknown: '区域未知' };
const dangerName = { seen: '看到危险', 'not-seen': '当前未看到危险', unknown: '危险未核对' };
const reasonName = { 'only-observed-empty-targets': '当前核对过的目标都为空', 'no-current-visible-own-target': '没有新鲜、亲见的己方局部目标',
  'insufficient-independent-history': '可比的独立历史账号不足', 'no-visible-target-with-history': '历史与此刻可见目标没有交集',
  'outside-study-window': '游戏时钟不在研究窗口内', 'self-or-threat-cancelled': '已见危险或本人状态不允许，已撤回' };
function render(s, elapsed) {
  clearTimeout(ownTimer); clearTimeout(visibleTimer);
  text('mode', (s.exercise === 'custom-lobby' ? '自定义局 · 人工声明' : '复盘 / 合成演练 · 非实战验收') +
    (s.archive ? ` · ${s.archive.name} / ${s.archive.matches} 场历史` : ''));
  $('stop').disabled = s.status.code === 'STOPPED'; $('disconnect').disabled = false;
  const ttl = s.timing ? s.timing.expiresAt - s.now - elapsed : 0; // subtract entire request RTT conservatively
  if (!s.own || ttl <= 0) { clearFrame(ttl <= 0 && s.own ? '帧已到期。' : `${s.status.code} · ${s.status.message}`); return; }
  if (s.binding.contextId !== bindingId) resetForms();
  bindingId = s.binding.contextId; snapshot = s; deadline = performance.now() + ttl;
  ownTimer = setTimeout(() => clearFrame('本机有效期已到，等待新帧。'), ttl);
  const p = s.own;
  text('status', `${s.status.code} · ${s.status.message}`);
  text('own', `局 ${s.binding.matchId} · ${side(p.team)} · ${p.hero} · 游戏时钟 ${clock(p.clock)} (${p.clock}s)\n` +
    `坐标 (${p.position.x}, ${p.position.y}) · Lv ${p.level} · HP ${p.health}/${p.maxHealth} · MP ${p.mana}/${p.maxMana} · 正补 ${p.lastHits ?? '未知'}\n` +
    `主栏：${s.reported.mainItems ? p.items.map(i => i.name).join('、') || '没有所见物品' : '未上报'}\n` +
    `已学技能（已知冷却）：${s.reported.abilities ? p.abilities.map(a => `${a.name} ${a.level}级/${a.cooldown}s`).join('、') || '没有可用读数' : '未上报'}\n` +
    `距来源时戳约 ${Math.max(0, s.now - s.timing.sourceAt)} ms（来源整秒，不是链路延迟）`);
  $('confirmFields').disabled = false; $('observeFields').disabled = !s.confirmation;
  const m = s.confirmation;
  text('meta', m ? `build ${m.build ?? '未知'} / 补丁 ${m.patch ?? '未对应'} / 开局 ${m.matchStartUnix ?? '未知'} / ` +
    (m.metadataSource === 'matched-archive' ? '同局归档对应' : '操作员本局核对') : '尚未确认本局。');
  clearVisible();
  if (s.visible) {
    const remaining = s.timing.factsExpireAt - s.now - elapsed;
    if (remaining <= 0) { text('status', '目击已到期；没有当前候选。'); return; }
    text('visible', `${clock(s.visible.clock)} 本人目击 · ${dangerName[s.visible.danger]}\n` +
      (s.visible.targets.map(t => `${t.targetId}：${siteState[t.state]} / ${areaName[t.area]}`).join('\n') || '没有当前目击目标'));
    if (s.shadow?.status === 'observed') {
      const n = s.shadow.evidence.accounts;
      const broad = s.shadow.broadCandidates.map(c => `${c.family === 'lane' ? '兵线类' : '己方营地类'} ${c.supportingAccounts}/${n}`);
      const exact = s.shadow.candidates.map(c => `${c.targetId} ${c.supportingAccounts}/${n}`);
      text('shadow', `只读历史重合（非路线）：宽类 ${broad.join('、') || '弃答'}；具体地点诊断 ${exact.join('、') || '无'}。分母为独立历史账号。`);
    } else if (s.shadow) text('shadow', `无候选：${reasonName[s.shadow.reason] ?? s.shadow.reason}`);
    visibleTimer = setTimeout(() => { clearVisible(); text('status', '目击已到期；没有当前候选。'); }, remaining);
  }
}
async function api(path, body) {
  if (!localBridge) throw new Error('不是独立桥页面，未发送 token 或请求。');
  const r = await fetch(path, { method: body === undefined ? 'GET' : 'POST', cache: 'no-store', credentials: 'omit',
    redirect: 'error', signal: AbortSignal.timeout(1800), headers: { Authorization: `Bearer ${key}`,
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: JSON.stringify(body) });
  const data = await r.json();
  if (!r.ok) throw new Error(`${r.status} ${data.code} · ${data.message}`);
  return data;
}
async function poll(id = revision) {
  if (id !== revision || !key || document.hidden) return;
  const started = performance.now();
  try {
    const s = await api('/api/state');
    if (id !== revision) return;
    if (s.schema !== 'farm-shadow-bridge/1') { disconnect('应答不属于本桥，已断开。'); return; }
    render(s, performance.now() - started);
  } catch (e) {
    if (id !== revision) return;
    clearFrame('无法确认当前输入，已清除。'); text('message', e.message);
    if (/^401 /.test(e.message)) { disconnect('Operator token 无效，已断开。'); return; }
  }
  if (id === revision) pollTimer = setTimeout(() => void poll(id), 450);
}
function disconnect(message) {
  invalidateRequests(); key = ''; $('token').value = ''; $('disconnect').disabled = true; $('stop').disabled = true;
  clearFrame('没有有效帧。'); text('mode', '未连接'); text('message', message);
}
$('login').addEventListener('submit', e => {
  e.preventDefault();
  if (!localBridge) return;
  const k = $('token').value.trim();
  disconnect('正在核对本机进程…'); key = k; $('disconnect').disabled = false;
  text('message', '只读轮询；切换局、断流或断开即清除显示。'); void poll();
});
$('disconnect').onclick = () => disconnect('页面已断开。若要终止接收，请在终端按 Ctrl+C。');
function anchor() {
  if (!snapshot || performance.now() >= deadline) throw new Error('没有新鲜本人帧，未发送。');
  return { ...snapshot.binding, capturedAt: Date.now(), clock: snapshot.own.clock };
}
async function write(path, body) {
  const id = invalidateRequests();
  clearVisible(); $('confirmFields').disabled = true; $('observeFields').disabled = true;
  // During a write no old result (or own frame) may survive a slow response.
  text('own', '—'); text('status', '等待操作结果；旧读数已撤回。');
  try { await api(path, body); if (id === revision) text('message', '本机已接收。'); }
  catch (e) { if (id === revision) { clearFrame('写入未获确认。'); text('message', e.message); } }
  if (id === revision) await poll(id);
}
function submit(fn) { return e => { e.preventDefault(); try { fn(); } catch (err) { text('message', err.message); } }; }
const nullable = id => $(id).value === '' ? null : $(id).valueAsNumber;
$('confirm').onsubmit = submit(() => {
  const a = anchor();
  void write('/api/confirm', { ...a, exercise: snapshot.exercise, ownPlayerView: $('ownView').checked,
    build: nullable('build'), matchStartUnix: nullable('start') });
});
$('observe').onsubmit = submit(() => {
  const targetId = $('target').value.trim(), a = anchor(), visibleNow = $('visibleNow').checked;
  $('visibleNow').checked = false;
  void write('/api/visible', { ...a, visibleNow, danger: $('danger').value,
    targets: targetId ? [{ targetId, state: $('presence').value, area: $('area').value }] : [] });
});
$('threat').onclick = submit(() => { const a = anchor(); $('observe').reset();
  void write('/api/visible', { ...a, visibleNow: true, danger: 'seen', targets: [] }); });
$('stop').onclick = () => { clearFrame('正在终止本次桥…'); void write('/api/stop', { stop: true }); };
document.addEventListener('visibilitychange', () => {
  invalidateRequests(); clearFrame('页面切换后重新核对有效帧。');
  if (!document.hidden) void poll();
});
window.addEventListener('pagehide', () => disconnect('已清除本页会话。'));
$('token').disabled = $('connect').disabled = !localBridge;
if (!localBridge) text('message', '不是独立桥页面：请从本次 CLI 打印的本机地址打开，不要在此输入 token。');
