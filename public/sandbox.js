const $ = id => document.getElementById(id);
const fragment = new URLSearchParams(location.hash.slice(1));
const token = fragment.get('token') || sessionStorage.getItem('dota-sandbox-token');
if (fragment.has('token')) { sessionStorage.setItem('dota-sandbox-token', token); history.replaceState(null, '', '/sandbox'); }
let context, state, selectedCamp, selectedUnit, playing = false, busy = false, background;
let queuedWork = Promise.resolve(), pendingWork = 0;
const canvas = $('map'), ctx = canvas.getContext('2d');
const format = n => Number(n).toFixed(1);
const clock = n => `${Math.floor(n / 60)}:${(n % 60).toFixed(1).padStart(4, '0')}`;
function message(text, error = false) { $('status').textContent = text; $('status').classList.toggle('error', error); }
async function api(path, body) {
  const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: `Bearer ${token || ''}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const value = await response.json();
  if (!response.ok) throw new Error(value.error || `HTTP ${response.status}`);
  return value;
}
async function serial(work) {
  pendingWork++; busy = true;
  const current = queuedWork.then(work, work);
  queuedWork = current.catch(() => {});
  try { return await current; } finally { pendingWork--; busy = pendingWork > 0; }
}
function command(path, body) {
  return serial(async () => {
    try { state = await api(path, body); render(); message(state.done ? '本轮结束；重置后可用同一种子比较另一方案。' : '模拟状态已更新。'); }
    catch (error) { playing = false; message(error.message, true); }
    finally { $('play').textContent = playing ? '暂停' : '播放'; }
  });
}
function action(action) { return command('/api/action', { action }); }
function planRows() {
  return (state.plan?.steps || state.plan?.visits || []).map(s => {
    const camp = context.camps.find(c => c.id === s.campId);
    const label = !s.kind || s.kind === 'camp' ? camp.label : s.kind === 'lane' ? `${s.laneId} 收线 · ${s.attackMode}` :
      s.kind === 'pull' ? `引野 ${camp.label}` : s.kind === 'wait' ? `等待${s.untilClock ? '至 ' + clock(s.untilClock) : ' ' + s.duration + ' 秒'}` : '移动';
    return { ...s, label, destination: s.position || camp || null };
  });
}
function updateExitOptions() {
  const exits = context.campExits?.find(e => e.campId === selectedCamp)?.exits || [];
  const names = { west: '西侧', east: '东侧', south: '南侧', north: '北侧' };
  $('pull-exit').replaceChildren(...exits.map((e, i) => {
    const option = document.createElement('option'); option.value = i;
    option.textContent = `${names[e.direction]} (${e.position.x}, ${e.position.y})`; return option;
  }));
}
function updateLaneOptions() {
  const lane = context.lanes.find(l => l.id === $('lane').value);
  $('lane-anchor').replaceChildren(...lane.points.map((p, i) => {
    const option = document.createElement('option'); option.value = i;
    option.textContent = `位置 ${i + 1} (${p.x}, ${p.y})`; return option;
  }));
  $('lane-anchor').value = String(Math.min(3, lane.points.length - 2));
}
function point(p) {
  const g = context.grid;
  return { x: (p.x - g.origin.x) / (g.width * g.cellSize) * canvas.clientWidth,
    y: canvas.clientHeight - (p.y - g.origin.y) / (g.height * g.cellSize) * canvas.clientHeight };
}
function line(points, color, dashed = false, width = 1) {
  if (!points.length) return;
  ctx.beginPath(); ctx.strokeStyle = color; ctx.lineWidth = width; ctx.setLineDash(dashed ? [5, 5] : []);
  points.forEach((p, i) => { const v = point(p); i ? ctx.lineTo(v.x, v.y) : ctx.moveTo(v.x, v.y); });
  ctx.stroke(); ctx.setLineDash([]);
}
function draw() {
  if (!state || !context) return;
  const w = canvas.clientWidth, h = canvas.clientHeight, dpr = devicePixelRatio || 1;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr);
  }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, w, h);
  ctx.imageSmoothingEnabled = false; ctx.drawImage(background, 0, 0, w, h);
  for (const lane of context.lanes) line(lane.points, '#53665a', false, 2);
  // The old decorative diagonal was not verified river or territory geometry.
  // Do not draw it as if it establishes an own-side boundary.
  const rows = planRows();
  if (state.plan) line([context.initialPlayer.position, ...rows.flatMap(s => s.kind === 'pull'
    ? [context.camps.find(c => c.id === s.campId), s.destination] : s.destination ? [s.destination] : [])], '#c6a768', true, 1.5);
  line([state.player.position, ...state.path], '#79d9d0', false, 2);
  for (const camp of context.camps) {
    const p = point(camp), alive = state.units.some(u => u.campId === camp.id);
    ctx.beginPath(); ctx.arc(p.x, p.y, camp.id === selectedCamp ? 11 : 8, 0, Math.PI * 2);
    ctx.strokeStyle = camp.id === selectedCamp ? '#79d9d0' : alive ? '#a58f61' : '#52616b'; ctx.lineWidth = 1; ctx.stroke();
    ctx.fillStyle = alive ? '#dfc893' : '#85989d'; ctx.font = '10px system-ui'; ctx.textAlign = 'center';
    ctx.fillText(camp.id.replace('neutralcamp_', '').replace('good_', 'R').replace('evil_', 'D'), p.x, p.y - 13);
    if (camp.id === selectedCamp) {
      const a = point(camp.bounds.min), b = point(camp.bounds.max);
      ctx.strokeStyle = '#79d9d0'; ctx.strokeRect(a.x, b.y, b.x - a.x, a.y - b.y);
    }
  }
  for (const unit of state.units) {
    const p = point(unit);
    ctx.beginPath(); ctx.arc(p.x, p.y, unit.team === 'neutral' ? 2.7 : 2, 0, Math.PI * 2);
    ctx.fillStyle = unit.team === 'neutral' ? '#e6bf73' : unit.team === 'radiant' ? '#80b893' : '#ee929a'; ctx.fill();
    if (unit.stacked) { ctx.strokeStyle = '#f2d69b'; ctx.strokeRect(p.x - 4, p.y - 4, 8, 8); }
    if (unit.hp < unit.maxHp) {
      ctx.fillStyle = '#20313a'; ctx.fillRect(p.x - 5, p.y - 7, 10, 2);
      ctx.fillStyle = '#a0d9a3'; ctx.fillRect(p.x - 5, p.y - 7, 10 * unit.hp / unit.maxHp, 2);
    }
  }
  if (state.plan) rows.forEach((visit, i) => {
    if (!visit.destination || i < state.plan.index) return;
    const p = point(visit.destination);
    if (rows.slice(state.plan.index, i).some(s => s.destination && Math.hypot(s.destination.x - visit.destination.x, s.destination.y - visit.destination.y) < 100)) return;
    ctx.fillStyle = '#f2d69b'; ctx.font = 'bold 12px system-ui'; ctx.fillText(String(i + 1), p.x + 13, p.y + 15);
  });
  const hero = point(state.player.position); ctx.fillStyle = state.player.alive ? '#79f1e2' : '#ee929a';
  ctx.beginPath(); ctx.moveTo(hero.x, hero.y - 9); ctx.lineTo(hero.x + 7, hero.y + 7); ctx.lineTo(hero.x - 7, hero.y + 7); ctx.closePath(); ctx.fill();
  ctx.font = 'bold 12px system-ui'; ctx.textAlign = 'left'; ctx.fillText('LUNA', hero.x + 11, hero.y + 4);
}
function render() {
  $('clock').textContent = clock(state.clock); $('remaining').textContent = `剩余 ${format(180 - state.elapsedSeconds)} 秒`;
  $('neutral-gold').textContent = format(state.score.neutralGold); $('lane-gold').textContent = format(state.score.laneGold);
  $('gpm').textContent = format(state.score.totalGpmOver180Seconds);
  $('total-gold').textContent = `合计 ${format(state.score.totalGold)} 金`;
  $('resources').textContent = `HP ${Math.round(state.player.health)} · MP ${Math.round(state.player.mana)}`;
  $('mode').textContent = state.plan ? `${state.plan.steps ? '线野方案' : '纯野旧格式'} · ${state.plan.model}` : '手动机制测试 · 非实战建议';
  if (state.done) { playing = false; $('play').textContent = '播放'; }
  const units = state.units.filter(u => u.campId === selectedCamp);
  if (!units.some(u => u.id === selectedUnit)) selectedUnit = units[0]?.id;
  $('camp-detail').textContent = units.length ? `${units.length} 个单位 · ${format(units.reduce((s, u) => s + u.gold, 0))} 模拟金币` : '空营地；整分刷新时若有单位占框则不会刷新。';
  const spawn = state.spawnState?.find(c => c.campId === selectedCamp);
  $('spawn-detail').textContent = spawn ? `刷新倒计时 ${format(spawn.nextSpawnAt - state.clock)}s · ${spawn.blocked ? '当前占框' : '当前空框'} · 框外 ${spawn.outsideBox} 只 · 堆叠标记 ${spawn.stacked} 只` : '';
  const lane = state.laneSummary?.find(l => l.laneId === $('lane').value);
  $('lane-detail').textContent = lane ? `可见敌兵 ${lane.waves.reduce((s, w) => s + w.count, 0)} 个 / ${format(lane.enemyGold)} 金 · 下波出兵 ${format(lane.nextWaveAt - state.clock)}s` : '';
  $('units').replaceChildren(...units.map(u => {
    const button = document.createElement('button'); button.textContent = `${u.name.replace('npc_dota_neutral_', '')} · ${Math.ceil(u.hp)} HP`;
    button.classList.toggle('selected', u.id === selectedUnit); button.onclick = () => { selectedUnit = u.id; render(); }; return button;
  }));
  $('model-summary').textContent = state.recommendationScope?.reason || '己方可活动区域尚未核验，已暂停路线加载与付费规划。';
  $('plan').replaceChildren(...planRows().map((visit, i) => {
    const li = document.createElement('li'); li.textContent = visit.label;
    li.classList.toggle('active', i === state.plan.index); return li;
  }));
  const labels = { neutral_spawn: '野怪刷新', lane_spawn: '三路出兵', spawn_blocked: '刷新被占框阻止',
    stack_spawn: '堆野刷新', neutral_aggro: '野怪追逐', neutral_return: '野怪归位', lane_pull: '兵线被野怪吸引', pull_leave: '引野撤离',
    death: '单位死亡', hero_death: 'Luna 死亡', cast: '施法释放', cast_start: '开始施法', cast_cancelled: '施法取消', attack: '普攻', order: '行动' };
  $('events').replaceChildren(...state.events.slice(-14).reverse().map(e => {
    const li = document.createElement('li');
    li.textContent = `${clock(e.clock)} ${labels[e.type] || e.type} ${e.unitId || e.campId || ''}`; return li;
  }));
  draw();
}
$('reset').onclick = () => { playing = false; command('/api/reset', { seed: Number($('seed').value) }); };
$('play').onclick = () => { playing = !playing && !state?.done; $('play').textContent = playing ? '暂停' : '播放'; };
$('step').onclick = () => command('/api/step', { seconds: 10 });
$('camp').onchange = () => { selectedCamp = $('camp').value; updateExitOptions(); render(); };
$('lane').onchange = () => { updateLaneOptions(); render(); };
$('farm-lane').onclick = () => {
  const lane = context.lanes.find(l => l.id === $('lane').value);
  action({ type: 'farm_lane', laneId: lane.id, position: lane.points[Number($('lane-anchor').value)],
    attackMode: $('attack-mode').value, targetPriority: [] });
};
$('pull').onclick = () => {
  const exit = context.campExits.find(e => e.campId === selectedCamp)?.exits[Number($('pull-exit').value)];
  if (exit) action({ type: 'pull', campId: selectedCamp, position: exit.position });
};
$('farm').onclick = () => action({ type: 'farm', campId: selectedCamp, targetPriority: [] });
$('stop').onclick = () => action({ type: 'stop' });
$('attack').onclick = () => action({ type: 'attack', targetId: selectedUnit });
$('beam').onclick = () => action({ type: 'cast', name: 'luna_lucent_beam', targetId: selectedUnit || 'current' });
$('mask').onclick = () => action({ type: 'cast', name: 'item_mask_of_madness' });
canvas.onclick = e => {
  if (!context) return;
  const rect = canvas.getBoundingClientRect(), x = e.clientX - rect.left, y = e.clientY - rect.top;
  const nearest = context.camps.map(c => ({ id: c.id, d: Math.hypot(point(c).x - x, point(c).y - y) })).sort((a, b) => a.d - b.d)[0];
  if (nearest.d < 30) { selectedCamp = nearest.id; $('camp').value = selectedCamp; updateExitOptions(); render(); }
};
new ResizeObserver(draw).observe(canvas);
try {
  context = await api('/api/context'); state = await api('/api/state');
  $('seed').value = state.seed; selectedCamp = 'neutralcamp_good_5';
  for (const camp of context.camps) { const option = document.createElement('option'); option.value = camp.id; option.textContent = camp.label; $('camp').append(option); }
  $('camp').value = selectedCamp;
  updateExitOptions(); updateLaneOptions();
  $('assumptions').replaceChildren(...context.rules.assumptions.map(text => { const li = document.createElement('li'); li.textContent = text; return li; }));
  background = document.createElement('canvas'); background.width = context.grid.width; background.height = context.grid.height;
  const b = background.getContext('2d'), image = b.createImageData(background.width, background.height);
  context.grid.rows.forEach((row, y) => [...row].forEach((v, x) => {
    const index = ((background.height - 1 - y) * background.width + x) * 4;
    image.data.set(v === '1' ? [29, 47, 43, 255] : [17, 30, 33, 255], index);
  })); b.putImageData(image, 0, 0);
  render(); message('已连接。路线建议已撤下；仅可手动测试机制。');
  setInterval(async () => {
    if (busy) return;
    if (playing) await command('/api/step', { seconds: Number($('speed').value) / 2 });
    else await serial(async () => { try { state = await api('/api/state'); render(); } catch (e) { message(e.message, true); } });
  }, 500);
} catch (error) { message(`${error.message}。请使用启动日志中带会话 token 的本机地址。`, true); }
