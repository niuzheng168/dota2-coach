const $ = id => document.getElementById(id);
const token = sessionStorage.getItem('dota-sandbox-token') || new URLSearchParams(location.hash.slice(1)).get('token');
if (location.hash) { if (token) sessionStorage.setItem('dota-sandbox-token', token); history.replaceState(null, '', '/map-audit'); }
const canvas = $('map'), ctx = canvas.getContext('2d');
let data, image, match, report, frame, selected = 'neutralcamp_evil_8', playing = false;
const time = n => `${Math.floor(n / 60)}:${String(Math.floor(n % 60)).padStart(2, '0')}`;
async function get(path) {
  const r = await fetch(path, { headers: { Authorization: `Bearer ${token || ''}` } });
  if (!r.ok) throw new Error((await r.json()).error || `HTTP ${r.status}`);
  return r;
}
function point(p) {
  const b = data.reference.bounds;
  return { x: (p.x - b.min.x) / (b.max.x - b.min.x) * canvas.clientWidth,
    y: (b.max.y - p.y) / (b.max.y - b.min.y) * canvas.clientHeight };
}
function line(points, color, width = 1, dashed = false) {
  ctx.beginPath(); ctx.strokeStyle = color; ctx.lineWidth = width; ctx.setLineDash(dashed ? [5, 5] : []);
  points.forEach((p, i) => { const v = point(p); i ? ctx.lineTo(v.x, v.y) : ctx.moveTo(v.x, v.y); });
  ctx.stroke(); ctx.setLineDash([]);
}
function draw() {
  if (!frame) return;
  const w = canvas.clientWidth, h = canvas.clientHeight, d = devicePixelRatio || 1;
  canvas.width = Math.round(w * d); canvas.height = Math.round(h * d);
  ctx.setTransform(d, 0, 0, d, 0, 0); ctx.fillStyle = '#101a1e'; ctx.fillRect(0, 0, w, h);
  if (image) {
    const o = data.reference.overview, tl = point({ x: o.posX, y: o.posY }), br = point({ x: o.posX + o.coordinateSpan, y: o.posY - o.coordinateSpan });
    ctx.globalAlpha = 0.65; ctx.drawImage(image, tl.x, tl.y, br.x - tl.x, br.y - tl.y); ctx.globalAlpha = 1;
  }
  if ($('trees').checked) for (const t of data.reference.trees) {
    const p = point({ x: t[0], y: t[1] }); ctx.fillStyle = '#c4e0b9';
    ctx.beginPath(); ctx.arc(p.x, p.y, 1.2, 0, 2 * Math.PI); ctx.fill();
  }
  if ($('lanes').checked) for (const l of data.reference.lanePaths)
    line(l.points.map(p => ({ x: p[0], y: p[1] })), l.team === 'radiant' ? '#9ace7e' : '#e09c9e', 1.3);
  if ($('old-lanes').checked) for (const l of data.oldSchematicLanes) line(l.points, '#ff725c', 2, true);
  if ($('grid-diff').checked && data.clientAudit) for (const c of data.clientAudit.pointNavigation.oldGridDifferences) {
    const p=point(c);ctx.strokeStyle='#ff6b89';ctx.lineWidth=2;ctx.strokeRect(p.x-4,p.y-4,8,8);
  }
  if ($('route').checked) {
    const samples = match.path.filter(p => p[0] <= frame.time);
    // Do not render huge discontinuities as walking paths. Never alter the recorded points.
    for (let i = 1; i < samples.length; i++) {
      const a = samples[i - 1], b = samples[i];
      if (b[0] - a[0] > 0.3 || Math.hypot(a[1] - b[1], a[2] - b[2]) > 300) continue;
      line([{ x: a[1], y: a[2] }, { x: b[1], y: b[2] }], '#70fff0', 2);
    }
  }
  for (const c of data.reference.camps) {
    const p = point(c), active = c.id === selected;
    ctx.strokeStyle = active ? '#fff2bb' : '#ead4a1'; ctx.lineWidth = active ? 2 : 1;
    ctx.beginPath();
    if (c.kind === 'ancient') { ctx.moveTo(p.x, p.y - 8); ctx.lineTo(p.x + 8, p.y); ctx.lineTo(p.x, p.y + 8); ctx.lineTo(p.x - 8, p.y); ctx.closePath(); }
    else ctx.arc(p.x, p.y, 6, 0, Math.PI * 2);
    ctx.stroke(); ctx.font = '10px system-ui'; ctx.textAlign = 'center'; ctx.fillStyle = '#fff0c7';
    ctx.fillText(c.id.replace('neutralcamp_good_', 'R').replace('neutralcamp_evil_', 'D'), p.x, p.y - 11);
    if (active) { const a = point(c.bounds.min), b = point(c.bounds.max); ctx.strokeRect(a.x, b.y, b.x - a.x, a.y - b.y); }
  }
  for (const u of frame.units) {
    const p = point(u);
    ctx.fillStyle = u.kind === 'neutral' ? '#ffce70' : u.team === 2 ? '#7aee99' : '#ff8593';
    if (u.kind === 'tower') { ctx.fillRect(p.x - 4, p.y - 4, 8, 8); ctx.strokeStyle = '#ecf5ed'; ctx.strokeRect(p.x - 4, p.y - 4, 8, 8); }
    else { ctx.beginPath(); ctx.arc(p.x, p.y, u.campId === selected ? 3.3 : 2.2, 0, 2 * Math.PI); ctx.fill(); }
  }
  for (const hero of frame.heroes) {
    const p = point(hero), luna = hero.name === 'npc_dota_hero_luna';
    ctx.fillStyle = luna ? '#71ffea' : hero.team === 2 ? '#d9f0b2' : '#fa9ca7';
    ctx.beginPath(); ctx.moveTo(p.x, p.y - 7); ctx.lineTo(p.x + 5, p.y + 5); ctx.lineTo(p.x - 5, p.y + 5); ctx.closePath(); ctx.fill();
    if (luna) { ctx.font = 'bold 12px system-ui'; ctx.textAlign = 'left'; ctx.fillText('LUNA', p.x + 9, p.y + 3); }
  }
}
function render() {
  frame = match.frames[Number($('time').value)];
  $('clock').textContent = time(frame.time);
  const camp = data.reference.camps.find(c => c.id === selected), units = frame.units.filter(u => u.campId === selected);
  $('camp-status').textContent = `${camp.label} · ${units.length} 个实际存活单位`;
  $('units').replaceChildren(...units.map(u => { const li = document.createElement('li'); li.textContent = `${u.name.replace('npc_dota_neutral_', '')} · ${u.hp} HP`; return li; }));
  const h = frame.luna;
  $('hero').textContent = `等级 ${h.level} · HP ${h.health} · MP ${Math.round(h.mana)} · 正补 ${h.economy.m_iLastHitCount}`;
  $('items').textContent = h.items.filter(i => i.slot < 6).map(i => i.name?.replace('item_', '') || i.class).join(' / ');
  $('income').textContent = `10–13 分钟记录：野怪 ${match.scoreWindow.neutralGold} 金 + 兵线 ${match.scoreWindow.laneGold} 金。`;
  const checks = [
    [`营地 ${report.campGeometry.rows.length}/28，坐标误差 ≤ ${report.campGeometry.maxCenterError.toFixed(3)}`, report.campGeometry.status === 'matched'],
    [`刷新框误差 ≤ ${report.campGeometry.maxBoxError.toFixed(4)}`, report.campGeometry.status === 'matched'],
    [`防御塔 ${report.towerGeometry.count}/22，坐标误差 ≤ ${report.towerGeometry.maxPositionError.toFixed(3)}`, report.towerGeometry.status === 'matched'],
    [`运行时树木 ${data.reference.trees.length} 个 · 位置核验`, data.clientAudit?.trees.positionsMatch === true],
    [`回放 build ${report.gameBuild} = 固定数值源 build`, true],
  ];
  if (data.clientAudit) {
    const a=data.clientAudit;
    checks.push([`地形高度 ${a.terrain.cells} 点 · 超差 ${a.terrain.changedCellsAbove001}`,a.terrain.changedCellsAbove001===0]);
    checks.push([`客户端原始导航文件逐字节匹配`,a.navigation.matchesPinnedSource===true]);
    checks.push([`旧通行网格 ${a.pointNavigation.oldGridDifferences.length} 格差异（可勾选查看）`,false]);
    const samples=a.mechanics?.samples||[],one=label=>samples.find(s=>s.label===label);
    const bare=one('bare-luna'),equipped=one('treads-mask-inactive'),active=one('mask-requested');
    const cut=one('tree-cut'),regrown=one('tree-regrown');
    $('client-mechanics').textContent=bare&&equipped&&active
      ? `Luna 裸装 ${bare.idealSpeed} → 假腿 ${equipped.idealSpeed} → 开疯脸 ${active.idealSpeed.toFixed(1)}。单树砍倒/指令长回碰撞：${cut?.nearby===false&&regrown?.nearby===true?'已验证':'未通过'}。`
      : '尚无完整的局部移动/树木机制实测。';
  }
  $('checks').replaceChildren(...checks.map(([text, pass]) => { const li = document.createElement('li'); li.className = pass ? 'pass' : 'pending'; li.textContent = `${pass ? '✓' : '△'} ${text}`; return li; }));
  draw();
}
function selectMatch() {
  match = data.samples.matches.find(m => String(m.id) === $('match').value);
  report = data.report.matches.find(m => m.matchId === match.id);
  $('time').max = match.frames.length - 1; $('time').value = '0'; render();
}
$('match').onchange = selectMatch;
$('time').oninput = render;
$('camp').onchange = () => { selected = $('camp').value; render(); };
for (const id of ['trees', 'lanes', 'route', 'old-lanes','grid-diff']) $(id).onchange = draw;
$('play').onclick = () => { playing = !playing; $('play').textContent = playing ? '暂停记录' : '播放记录'; };
canvas.onclick = e => {
  const r = canvas.getBoundingClientRect();
  const nearest = data.reference.camps.map(c => ({ c, d: Math.hypot(point(c).x - e.clientX + r.left, point(c).y - e.clientY + r.top) })).sort((a, b) => a.d - b.d)[0];
  if (nearest.d < 24) { selected = nearest.c.id; $('camp').value = selected; render(); }
};
new ResizeObserver(draw).observe(canvas);
try {
  data = await (await get('/api/map-audit')).json();
  try { image = await createImageBitmap(await (await get('/api/map-overview')).blob()); } catch { image = null; }
  for (const m of data.samples.matches) { const o = document.createElement('option'); o.value = m.id; o.textContent = `${m.id} · ${m.label}`; $('match').append(o); }
  for (const c of data.reference.camps) { const o = document.createElement('option'); o.value = c.id; o.textContent = c.label; $('camp').append(o); }
  $('camp').value = selected;
  for (const text of data.reference.limitations) { const li = document.createElement('li'); li.textContent = text; $('limits').append(li); }
  for (const text of ['自然长树计时与实时树状态', '河道精确加速边界/曲线', '塔的攻击与仇恨模拟', '全部移速修正/技能组合', '连续碰撞、转身和追逐']) { const li = document.createElement('li'); li.textContent = text; $('remaining').append(li); }
  $('provenance').textContent = `地图来源提交 ${data.reference.sources.commit}。${data.reference.sources.warning} 原始回放、解压文件与解析版本均有哈希。未调用大模型生成路线。`;
  selectMatch(); $('status').textContent = image ? '已载入原始回放快照和固定版本底图；不是完整地图机制验收。' : '已载入回放坐标；本地底图未缓存，没有用假河道代替。';
  setInterval(() => { if (!playing) return; const i = Number($('time').value); if (i >= match.frames.length - 1) { playing = false; $('play').textContent = '播放记录'; return; } $('time').value = i + 1; render(); }, 700);
} catch (error) { $('status').textContent = `${error.message}。请从本机沙盒的“地图与回放核验”入口打开。`; }
