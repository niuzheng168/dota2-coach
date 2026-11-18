const $ = id => document.getElementById(id);
const token = sessionStorage.getItem('dota-sandbox-token') || new URLSearchParams(location.hash.slice(1)).get('token');
if (location.hash) { if (token) sessionStorage.setItem('dota-sandbox-token', token); history.replaceState(null, '', '/hero-scan'); }
const canvas = $('map'), ctx = canvas.getContext('2d');
const TRAIL_SECONDS = 20, STALE_STATUS = 3;
let context, overview = null, header = null, snaps = [], offset = 0, session = null, playing = false, loading = null;
const portraits = new Map();

async function api(path) {
  const r = await fetch(path, { headers: { Authorization: `Bearer ${token || ''}` } });
  if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || `HTTP ${r.status}`);
  return r;
}
const clock = t => t == null ? '--:--' : `${t < 0 ? '-' : ''}${Math.floor(Math.abs(t) / 60)}:${String(Math.floor(Math.abs(t) % 60)).padStart(2, '0')}`;
const short = hero => hero.replace('npc_dota_hero_', '');
const heroName = hero => header?.names.heroes[hero] || short(hero);
// Every recipe shows the same scroll, so the recognizer reports item_recipe without saying which.
const itemName = item => item === '?' ? '未识别' : item === 'item_recipe' ? '图纸' : header?.names.items[item] || item.replace('item_', '');
// Items drawn with the same icon (observer/sentry in the grey backpack): list the other names, if any differ.
function slotName(it, index) {
  const name = itemName(it.slots[index]), others = (it.alt?.[header.slots[index]] || []).map(itemName).filter(n => n !== name);
  return others.length ? `${name}（或${[...new Set(others)].join('/')}）` : name;
}
function status(text, error = false) { $('status').textContent = text; $('status').classList.toggle('error', error); }
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }

function portrait(hero) {
  if (!portraits.has(hero)) {
    const img = new Image(); img.dataset.tried = 'jpg'; img.src = `/heroes/${short(hero)}.jpg`;
    img.onerror = () => { if (img.dataset.tried === 'jpg') { img.dataset.tried = 'png'; img.src = `/heroes/${short(hero)}.png`; } };
    img.onload = () => draw();
    portraits.set(hero, img);
  }
  return portraits.get(hero);
}
function point(p) {
  const b = context.map.bounds, s = canvas.clientWidth;
  return { x: (p.x - b.min.x) / (b.max.x - b.min.x) * s, y: (b.max.y - p.y) / (b.max.y - b.min.y) * s };
}
const current = () => snaps[Number($('time').value)];

function draw() {
  const snap = current(), w = canvas.clientWidth, d = devicePixelRatio || 1;
  canvas.width = Math.round(w * d); canvas.height = Math.round(w * d);
  ctx.setTransform(d, 0, 0, d, 0, 0); ctx.fillStyle = '#101a1e'; ctx.fillRect(0, 0, w, w);
  if (!context) return;
  if (overview) {
    const o = context.map.overview, tl = point({ x: o.posX, y: o.posY }), br = point({ x: o.posX + o.coordinateSpan, y: o.posY - o.coordinateSpan });
    ctx.globalAlpha = 0.7; ctx.drawImage(overview, tl.x, tl.y, br.x - tl.x, br.y - tl.y); ctx.globalAlpha = 1;
  }
  if ($('lanes').checked) for (const l of context.map.lanePaths) {
    ctx.beginPath(); ctx.strokeStyle = l.team === 'radiant' ? '#9ace7e66' : '#e09c9e66'; ctx.lineWidth = 1.2;
    l.points.forEach((q, i) => { const v = point({ x: q[0], y: q[1] }); i ? ctx.lineTo(v.x, v.y) : ctx.moveTo(v.x, v.y); }); ctx.stroke();
  }
  if (!snap) return;
  const size = Math.max(20, w / 34);
  if ($('trails').checked) drawTrails(snap, size);
  const entries = Object.entries(snap.heroes);
  if ($('truth').checked) for (const [, h] of entries) if (h.truth?.x != null) drawTruth(h, size);
  // Not-visible heroes first so the current sightings stay on top.
  entries.sort(([, a], [, b]) => (a.seen?.visible ? 1 : 0) - (b.seen?.visible ? 1 : 0));
  for (const [hero, h] of entries) if (h.seen) drawHero(hero, h, size);
}
function drawTrails(snap, size) {
  const index = Number($('time').value);
  for (let i = index; i >= 0 && snap.t - snaps[i].t <= TRAIL_SECONDS; i--) {
    const fade = 1 - (snap.t - snaps[i].t) / TRAIL_SECONDS;
    for (const h of Object.values(snaps[i].heroes)) {
      if (!h.seen?.visible) continue;
      const p = point(h.seen);
      ctx.fillStyle = h.team === 2 ? `rgba(125,220,146,${0.5 * fade})` : `rgba(242,128,141,${0.5 * fade})`;
      ctx.beginPath(); ctx.arc(p.x, p.y, size * 0.08 + 1, 0, Math.PI * 2); ctx.fill();
    }
  }
}
function drawTruth(h, size) {
  const t = point(h.truth), r = size * 0.42;
  ctx.strokeStyle = '#ffffffcc'; ctx.lineWidth = 1.2; ctx.strokeRect(t.x - r, t.y - r, r * 2, r * 2);
  if (h.seen && Math.hypot(h.seen.x - h.truth.x, h.seen.y - h.truth.y) > 400) {
    const s = point(h.seen); ctx.setLineDash([3, 3]); ctx.beginPath(); ctx.moveTo(s.x, s.y); ctx.lineTo(t.x, t.y); ctx.stroke(); ctx.setLineDash([]);
  }
}
function drawHero(hero, h, size) {
  const p = point(h.seen), r = size / 2, st = h.status, dead = st && !st.alive, img = portrait(hero);
  ctx.save();
  ctx.globalAlpha = h.seen.visible ? 1 : 0.45;
  // Other minimap entries with this hero's name (illusions, clones): the minimap does not say which is real.
  for (const [x, y] of h.seen.others || []) {
    const o = point({ x, y });
    ctx.lineWidth = 2; ctx.strokeStyle = h.team === 2 ? '#7ddc92aa' : '#f2808daa';
    ctx.beginPath(); ctx.arc(o.x, o.y, r * 0.45, 0, Math.PI * 2); ctx.stroke();
  }
  ctx.beginPath(); ctx.arc(p.x, p.y, r, 0, Math.PI * 2); ctx.closePath();
  ctx.save(); ctx.clip();
  if (dead) ctx.filter = 'grayscale(1)';
  if (img.complete && img.naturalWidth) {
    const side = Math.min(img.naturalWidth, img.naturalHeight);
    ctx.drawImage(img, (img.naturalWidth - side) / 2, 0, side, side, p.x - r, p.y - r, size, size);
  } else { ctx.fillStyle = '#2a383f'; ctx.fill(); }
  ctx.restore();
  ctx.lineWidth = 2.5; ctx.strokeStyle = h.team === 2 ? '#7ddc92' : '#f2808d';
  if (!h.seen.visible) ctx.setLineDash([4, 3]);
  ctx.beginPath(); ctx.arc(p.x, p.y, r, 0, Math.PI * 2); ctx.stroke(); ctx.setLineDash([]);
  if (dead) {
    ctx.strokeStyle = '#ff9aa3'; ctx.lineWidth = 2; ctx.beginPath();
    ctx.moveTo(p.x - r * .7, p.y - r * .7); ctx.lineTo(p.x + r * .7, p.y + r * .7);
    ctx.moveTo(p.x + r * .7, p.y - r * .7); ctx.lineTo(p.x - r * .7, p.y + r * .7); ctx.stroke();
  }
  if (st) {
    ctx.globalAlpha *= st.age > STALE_STATUS ? 0.5 : 1;
    bar(p.x - r, p.y + r + 3, size, st.health, '#5fd27b');
    bar(p.x - r, p.y + r + 8, size, st.mana, '#5aa7f0');
  }
  if (!h.seen.visible) {
    ctx.globalAlpha = 0.9; ctx.font = `${Math.round(size * 0.42)}px system-ui`; ctx.textAlign = 'center'; ctx.fillStyle = '#e8f0ed';
    ctx.fillText(`${Math.round(h.seen.age)}s前`, p.x, p.y - r - 4);
  }
  ctx.restore();
}
function bar(x, y, width, v, color) {
  ctx.fillStyle = '#0b1215cc'; ctx.fillRect(x, y, width, 4);
  if (v && v[1] > 0) { ctx.fillStyle = color; ctx.fillRect(x, y, width * Math.min(1, v[0] / v[1]), 4); }
}

function meter(cls, v) { const b = el('span', `bar ${cls}`), i = el('i'); i.style.width = `${v && v[1] ? Math.min(100, v[0] / v[1] * 100) : 0}%`; b.append(i); return b; }
const near = (a, b) => a && b && b[1] && Math.abs(a[0] - b[0]) <= 0.05 * b[1] && a[1] === b[1];
function renderHeroes(snap) {
  const rows = Object.entries(snap.heroes).sort(([a, x], [b, y]) => (x.team ?? 9) - (y.team ?? 9) || heroName(a).localeCompare(heroName(b), 'zh'));
  $('heroes').replaceChildren(...rows.map(([hero, h]) => {
    const st = h.status, it = h.items, seen = h.seen, t = h.truth;
    const row = el('div', `hero ${h.team === 2 ? 'radiant' : h.team === 3 ? 'dire' : ''} ${st && !st.alive ? 'dead' : ''}`);
    const img = el('img'); img.alt = ''; img.src = portrait(hero).src; img.onerror = () => { img.src = `/heroes/${short(hero)}.png`; };
    const title = el('div'); title.append(el('span', 'name', heroName(hero)),
      el('span', 'side', `${h.team === 2 ? '天辉' : h.team === 3 ? '夜魇' : ''}${h.ally == null ? '' : h.ally ? ' · 友方' : ' · 敌方'}`));
    row.append(img, title);
    const state = el('div', `line${st && st.age > STALE_STATUS ? ' stale' : ''}`);
    if (st) {
      state.append(el('span', `vals${t && t.level !== st.level ? ' bad' : ''}`, `Lv${st.level ?? '?'} `),
        meter('hp', st.health), el('span', `vals${t && !near(st.health, t.health) ? ' bad' : ''}`, `${st.health ? st.health.join('/') : '?'} `),
        meter('mp', st.mana), el('span', `vals${t && !near(st.mana, t.mana) ? ' bad' : ''}`, `${st.mana ? st.mana.join('/') : '?'}`),
        el('span', null, ` · ${st.alive ? '' : '死亡 · '}${st.age.toFixed(1)}s前读到`));
    } else state.textContent = `还没读到它的界面${snap.mode === 'team-view' ? '（敌方需在视野内才能选中）' : ''}`;
    const items = el('div', `line${it && it.age > 10 ? ' stale' : ''}`);
    if (it) {
      const named = range => range.filter(i => it.slots[i]).map(i => slotName(it, i));
      const names = named([0, 1, 2, 3, 4, 5]), extra = named([6, 7, 8]);
      const wrong = t && it.slots.slice(0, 6).some((name, i) => (name || 'empty') !== (t.items[`slot${i}`] || 'empty'));
      items.append(el('span', `vals${wrong ? ' bad' : ''}`, names.join('、') || '无装备'),
        el('span', null, `${extra.length ? ` · 背包 ${extra.join('、')}` : ''}${it.slots[9] ? ` · 中立 ${slotName(it, 9)}` : ''}${it.slots[10] ? ' · 有TP' : ''} · ${Math.round(it.age)}s前`));
    } else items.textContent = '装备未读';
    const copies = seen?.others ? ` · ${seen.visible ? '另有' : '当时另有'} ${seen.others.length} 个同名单位（幻象/分身，分不出真身）` : '';
    const where = el('div', 'line', !seen ? '未出现在小地图' : (seen.visible ? `可见 (${seen.x}, ${seen.y})` :
      `最后出现 ${clock(seen.clock)}（${Math.round(seen.age)}s前）于 (${seen.x}, ${seen.y})`) + copies);
    row.append(state, items, where);
    if (t) row.append(el('div', 'line', `真值 Lv${t.level} HP ${t.health.join('/')} MP ${t.mana.join('/')}${t.alive ? '' : ' 死亡'}`));
    return row;
  }));
}
function render() {
  const snap = current();
  if (!snap) { draw(); return; }
  $('clock').textContent = clock(snap.clock);
  $('mode-note').textContent = snap.mode === 'team-view'
    ? 'Plus 实时观战 · 队伍视角：敌方只在队友视野内能被选中扫描；不可见时保留最后读到的状态和最后出现位置（推测，不是实时位置）。'
    : snap.mode === 'full-spectator'
      ? '完整观战/本地回放（双方全图）：只用于离线验收，不能当作实战中可见的信息。'
      : `数据模式：${snap.mode}`;
  const tracked = Object.values(snap.heroes).filter(h => h.status).length;
  $('meta').textContent = `记录第 ${snap.t.toFixed(1)} 秒 · 游戏时间 ${clock(snap.clock)} · 已读 ${tracked}/${Object.keys(snap.heroes).length} 名英雄 · ` +
    `每名英雄约 ${snap.cycle ?? '?'} 秒刷新一次 · GSI 延迟 ${snap.gsiAge ?? '?'} 秒 · 对局 ${snap.matchId ?? '未知'}` +
    (snap.selecting === false ? ' · 暂停切换：观战 Dota 不在前台，只记录画面上自己出现的英雄' : '');
  renderHeroes(snap); draw();
}
function acceptance() {
  if (!header?.truth) { $('accept-section').hidden = true; return; }
  const c = { level: [0, 0], health: [0, 0], mana: [0, 0], alive: [0, 0], items: [0, 0], position: [0, 0], copies: [0, 0] };
  const add = (k, ok) => { c[k][0] += ok ? 1 : 0; c[k][1]++; };
  const close = (x, y, t) => Math.hypot(x - t.x, y - t.y) <= 200;
  for (const s of snaps) for (const h of Object.values(s.heroes)) {
    const t = h.truth; if (!t) continue;
    if (h.status && h.status.age <= 1) {
      add('level', h.status.level === t.level); add('alive', h.status.alive === t.alive);
      add('health', near(h.status.health, t.health)); add('mana', near(h.status.mana, t.mana));
    }
    if (h.items && h.items.age <= 5) for (let i = 0; i < 6; i++) add('items', (h.items.slots[i] || 'empty') === (t.items[`slot${i}`] || 'empty'));
    if (!h.seen?.visible || t.x == null) continue;
    if (h.seen.others) add('copies', [[h.seen.x, h.seen.y], ...h.seen.others].some(([x, y]) => close(x, y, t)));
    else add('position', close(h.seen.x, h.seen.y, t));
  }
  const labels = { level: '等级', health: '血量（±5%）', mana: '魔法（±5%）', alive: '存活', items: '主装备格',
    position: '小地图位置（≤200，单个同名单位）', copies: '有幻象/分身时真身在候选中（≤200）' };
  $('accept').replaceChildren(...Object.entries(c).filter(([k, [, n]]) => n || k !== 'copies').map(([k, [ok, n]]) => {
    const share = n ? ok / n : 0, li = el('li', share >= 0.98 ? 'pass' : 'pending');
    li.textContent = `${labels[k]}：${n ? (share * 100).toFixed(1) : '-'}%（${ok}/${n}）`; return li;
  }));
  $('accept-section').hidden = false;
}

async function pull() {
  if (loading) return loading;
  loading = (async () => {
    while (true) {
      const r = await (await api(`/api/hero-scan/${encodeURIComponent(session)}?offset=${offset}`)).json();
      if (r.restarted) { offset = 0; snaps = []; header = null; continue; }
      header ||= r.header; snaps.push(...r.snapshots); offset = r.next;
      if (r.next >= r.size || !r.snapshots.length) break;
    }
  })().finally(() => { loading = null; });
  return loading;
}
async function open(id) {
  session = id; offset = 0; snaps = []; header = null; playing = false; $('play').textContent = '▶ 播放';
  status('正在读取记录…');
  await pull();
  $('time').max = Math.max(0, snaps.length - 1); $('time').value = $('follow').checked ? $('time').max : '0';
  acceptance(); render();
  status(snaps.length ? `已载入 ${snaps.length} 个快照（每 ${header?.every ?? '?'} 秒一个）。` : '这段记录还没有快照。');
}
$('session').onchange = () => open($('session').value).catch(e => status(e.message, true));
$('time').oninput = render;
for (const id of ['trails', 'truth', 'lanes']) $(id).onchange = draw;
$('play').onclick = () => { playing = !playing; $('play').textContent = playing ? '❚❚ 暂停' : '▶ 播放'; };
new ResizeObserver(draw).observe(canvas);
let last = performance.now();
setInterval(() => {
  const now = performance.now(), dt = (now - last) / 1000; last = now;
  if (!playing || !snaps.length) return;
  let i = Number($('time').value);
  const target = snaps[i].t + dt * Number($('speed').value);
  while (i < snaps.length - 1 && snaps[i + 1].t <= target) i++;
  if (i >= snaps.length - 1) { playing = false; $('play').textContent = '▶ 播放'; }
  $('time').value = i; render();
}, 100);
setInterval(async () => {
  if (!$('follow').checked || !session) return;
  try {
    const atEnd = Number($('time').value) >= snaps.length - 1;
    await pull(); $('time').max = Math.max(0, snaps.length - 1);
    if (atEnd) $('time').value = $('time').max;
    acceptance(); render();
  } catch (e) { status(e.message, true); }
}, 1000);

try {
  context = await (await api('/api/sim/context')).json();
  try { overview = await createImageBitmap(await (await api('/api/map-overview')).blob()); } catch { overview = null; }
  const { sessions } = await (await api('/api/hero-scan')).json();
  if (!sessions.length) throw new Error('还没有扫描记录：先运行 scripts/vision/hero_scan.py scan');
  for (const s of sessions) {
    const o = el('option', null, `${s.id}${s.truth ? ' · 含真值（完整观战）' : ''}${s.finished ? '' : ' · 未结束（进行中或中断）'}`); o.value = s.id; $('session').append(o);
  }
  if (!sessions[0].finished) $('follow').checked = true;
  await open(sessions[0].id);
} catch (e) { status(`${e.message}。请从本机沙盒（npm run sandbox）打开 /hero-scan 并带上会话 token。`, true); }
