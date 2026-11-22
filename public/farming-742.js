const $ = id => document.getElementById(id);
const heroes = { luna: '露娜', troll_warlord: '巨魔战将' };
const teams = { radiant: '天辉', dire: '夜魇' };
const sets = { expanded: '本轮坐标包', tempo: 'D2PT 分钟名册', '16': '原 16 场归档' };
const uncovered = '未覆盖';
const clock = t => `${Math.floor(t / 60)}:${(t % 60).toFixed(3).padStart(6, '0')}`;
const matchSelect = $('replay-match'), windowSelect = $('replay-window'), slider = $('replay-time');
let corpus, filtered = [], samples = [];
let roster = [], drawable = new Map();
const tempoSelect = $('tempo-match');
const matchOption = m => new Option(`${m.id} · ${m.player} · ${heroes[m.hero]} / ${teams[m.team]}`, m.id);

function svg(tag, attrs) {
  const el = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [name, value] of Object.entries(attrs)) el.setAttribute(name, value);
  return el;
}

function drawLandmarks({ map, matches }) {
  // A single world-coordinate scale for all matches. SVG Y points down; replay Y points north.
  let extent = 0;
  for (const { x, y, box } of map.camps) extent = Math.max(extent, Math.abs(x), Math.abs(y), ...(box ?? []).map(Math.abs));
  for (const { x, y } of map.towers) extent = Math.max(extent, Math.abs(x), Math.abs(y));
  for (const m of matches) for (const [, x, y] of m.trace ?? []) extent = Math.max(extent, Math.abs(x), Math.abs(y));
  const edge = extent + 900;
  $('route-map').setAttribute('viewBox', `${-edge} ${-edge} ${2 * edge} ${2 * edge}`);
  for (const { x, y } of map.towers)
    $('towers').append(svg('rect', { x: x - 85, y: -y - 85, width: 170, height: 170 }));
  for (const { id, x, y } of map.camps) {
    const short = id.replace('good_', 'G').replace('evil_', 'E');
    const detail = `${short} · ${id} · X ${x} / Y ${y}`;
    const g = svg('g', { transform: `translate(${x} ${-y})`, tabindex: 0, role: 'button', 'aria-label': detail });
    const label = svg('text', { x: 140, y: -90 });
    label.textContent = short;
    g.append(svg('circle', { r: 90 }), label);
    const inspect = () => { $('camp-info').textContent = detail; };
    g.onpointerenter = g.onfocus = g.onclick = inspect;
    g.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); inspect(); } };
    $('camps').append(g);
  }
}

function showSample() {
  const p = samples[slider.valueAsNumber], marker = $('sample-marker');
  marker.setAttribute('visibility', p?.[3] ? 'visible' : 'hidden');
  let detail = '窗口内没有本体采样（未覆盖）';
  if (p) {
    const [t, x, y, alive] = p;
    detail = `${clock(t)} · ${alive ? `X ${x} / Y ${y}` : '死亡（不绘制位置）'}`;
    if (alive) { marker.setAttribute('cx', x); marker.setAttribute('cy', -y); }
  }
  $('sample-info').textContent = detail;
  slider.setAttribute('aria-valuetext', detail);
}

// The compact replay format stores the first frame less than 1.2 s after each boundary.
// A later boundary (or a terminal snapshot) is not a substitute for a missing one.
function counterAt(m, boundary) {
  return m.points?.find(p => p?.t >= boundary && p.t < boundary + 1.2 && p.t <= (m.duration ?? Infinity)
    && Number.isSafeInteger(p.lane) && Number.isSafeInteger(p.neutral));
}

function showWindow() {
  const m = filtered.find(m => String(m.id) === matchSelect.value);
  $('match-detail').hidden = !m;
  if (!m) {
    $('window-coverage').textContent = '没有符合筛选条件的对局，请更换英雄或阵营。';
    return;
  }
  const [from, to] = windowSelect.value.split(',').map(Number);
  const covered = filtered.filter(m => counterAt(m, from) && counterAt(m, to)).length;
  $('window-coverage').textContent = `当前筛选 · ${from / 60}–${to / 60} 分钟金币覆盖：${covered} / ${filtered.length} 场（两端均有计数）`;

  $('replay-player').textContent = `${m.player} · ${heroes[m.hero]}`;
  $('replay-result').textContent = m.won ? '胜利' : '失利';
  $('replay-result').dataset.won = m.won;
  const duration = Number.isFinite(m.duration) ? ` · 时长 ${clock(m.duration)}` : '';
  $('replay-meta').textContent = `${teams[m.team]} · 对局 ${m.id}${duration} · 平均 ${m.mmr} MMR · build ${m.build}`;
  const a = counterAt(m, from), b = counterAt(m, to);
  $('replay-lane-gold').textContent = a && b ? b.lane - a.lane : uncovered;
  $('replay-neutral-gold').textContent = a && b ? b.neutral - a.neutral : uncovered;
  $('counter-times').textContent = `计数采样：${from / 60} 分钟 ${a ? clock(a.t) : uncovered} → ${to / 60} 分钟 ${b ? clock(b.t) : uncovered}`;

  samples = (m.trace ?? []).filter(([t]) => t >= from && t < to && t <= (m.duration ?? Infinity));
  const span = samples.length ? `${samples.length} 点（${clock(samples[0][0])} 至 ${clock(samples.at(-1)[0])}）` : `${uncovered}（无本体采样）`;
  const ended = Number.isFinite(m.duration) && m.duration < to ? ` · 本局在 ${clock(m.duration)} 结束` : '';
  $('trace-coverage').textContent = `本窗轨迹：${span}${ended}`;
  // breakBefore comes from the raw replay, before downsampling. Never bridge it or a dead sample.
  const path = [];
  let penDown = false;
  for (const [, x, y, alive, breakBefore] of samples) {
    if (alive) path.push(`${penDown && !breakBefore ? 'L' : 'M'}${x} ${-y}`);
    penDown = !!alive;
  }
  $('body-trace').setAttribute('d', path.join(' '));
  slider.max = Math.max(0, samples.length - 1);
  slider.value = 0;
  slider.disabled = samples.length < 2;
  showSample();
}

function filterMatches() {
  const hero = $('hero-filter').value, team = $('team-filter').value, previous = matchSelect.value;
  filtered = corpus.matches.filter(m => (hero === 'all' || m.hero === hero) && (team === 'all' || m.team === team));
  matchSelect.replaceChildren(...filtered.map(matchOption));
  if (filtered.some(m => String(m.id) === previous)) matchSelect.value = previous;
  matchSelect.disabled = windowSelect.disabled = !filtered.length;
  $('match-count').textContent = `${filtered.length} / ${corpus.matches.length} 场`;
  showWindow();
}

async function snapshot(set, token) {
  const res = await fetch(`/api/farming-742?set=${set}`, { headers: token ? { Authorization: `Bearer ${token}` } : {} });
  if (res.status === 401) throw Error('需要本次沙盒会话，请先从带 #token 的本机入口进入。');
  if (!res.ok) throw Error(`${sets[set]}不可用（HTTP ${res.status}）。${set === 'expanded' ? '可切换至原 16 场归档。' : ''}`);
  const data = await res.json();
  if (!Array.isArray(data.matches) || new Set(data.matches.map(m => m.id)).size !== data.matches.length)
    throw Error(`${sets[set]}缺少有效的逐局名册。`);
  if (!data.matches.length) throw Error(`${sets[set]}尚未收录可显示的对局。`);
  return data;
}

function openRoutes(data, set) {
  if (data.patch?.version !== '7.42' || data.scope !== 'omniscient-offline-only' ||
      !Array.isArray(data.map?.camps) || !Array.isArray(data.map?.towers) ||
      (set === '16' && (data.version !== 1 || data.matches.length !== 16)))
    throw Error(`${sets[set]}不是预期的 7.42 离线回放资料。`);
  corpus = data;
  drawLandmarks(corpus);
  filterMatches();
  $('hero-filter').onchange = $('team-filter').onchange = filterMatches;
  matchSelect.onchange = windowSelect.onchange = showWindow;
  slider.oninput = showSample;
  const withCoords = corpus.matches.filter(m => m.trace?.some(([t, x, y, alive]) =>
    alive && t >= 300 && t < 1200 && t <= (m.duration ?? Infinity) && Number.isFinite(x) && Number.isFinite(y)));
  drawable = new Map(withCoords.map(m => [String(m.id), m]));
  $('spatial-count').textContent = `有坐标可看路线 ${drawable.size} 场`;
  const { original16, newVerified } = corpus.counts ?? {};
  if ([original16, newVerified].every(n => Number.isSafeInteger(n) && n >= 0) && original16 + newVerified === corpus.matches.length)
    $('spatial-source').textContent = `已核验回放：原归档 ${original16} 场，本轮新增 ${newVerified} 场。`;
  const floor = corpus.minimumMatchMmr;
  const threshold = Number.isFinite(floor) ? ` · 入选门槛：对局平均 MMR ≥ ${floor}` : '';
  $('viewer-status').textContent = `${sets[set]} · 已载入 ${corpus.matches.length} 场回放${threshold} · ${corpus.map.camps.length} 个营地 / ${corpus.map.towers.length} 个塔址`;
  $('viewer').hidden = false;
}

function pickLayer(tempo, updateUrl = true) {
  $('spatial-panel').hidden = tempo;
  $('tempo-panel').hidden = !tempo;
  $('pick-spatial').setAttribute('aria-pressed', String(!tempo));
  $('pick-tempo').setAttribute('aria-pressed', String(tempo));
  if (updateUrl) history.replaceState(null, '', tempo ? '/farming-742?set=tempo' : '/farming-742');
}

function minuteHits(m, minute) {
  const hits = m.minuteLH?.[minute]; // index 0 is game minute 0, not minute 1 or a Valve frame.
  return m.duration >= 60 * minute && Number.isSafeInteger(hits) && hits >= 0 ? hits : null;
}

function showTempoMatch() {
  const m = roster.find(m => String(m.id) === tempoSelect.value);
  if (!m) return;
  $('tempo-meta').textContent = `${m.player} · ${heroes[m.hero]} / ${teams[m.team]} · 对局 ${m.id} · 时长 ${clock(m.duration)} · 平均 ${m.mmr} MMR`;
  for (const minute of [5, 10, 15, 20]) $(`lh-${minute}`).textContent = minuteHits(m, minute) ?? uncovered;
  const route = drawable.get(String(m.id));
  const same = route && route.hero === m.hero && route.team === m.team && route.accountId === m.accountId;
  $('tempo-open-route').hidden = !same;
  $('tempo-route-info').textContent = same ? '本局另有独立核验的 Valve 回放坐标。' :
    corpus ? '本局没有可查看的实录坐标，不绘制路线。' : '坐标包未载入；分钟名册不提供路线。';
}

function openRoster(data) {
  if (data.patch !== '7.42' || (data.scope != null && data.scope !== 'd2pt-directory-minute-telemetry-NOT-route-evidence') ||
      !data.matches.every(m => Array.isArray(m.minuteLH)))
    throw Error('不是预期的 7.42 D2PT 分钟名册。');
  roster = data.matches;
  tempoSelect.replaceChildren(...roster.map(matchOption));
  tempoSelect.onchange = showTempoMatch;
  $('tempo-open-route').onclick = () => {
    if ($('tempo-open-route').hidden) return;
    $('hero-filter').value = $('team-filter').value = 'all';
    filterMatches();
    matchSelect.value = tempoSelect.value;
    showWindow();
    pickLayer(false);
    matchSelect.focus();
  };
  $('tempo-count').textContent = `${roster.length} 场分钟样本`;
  const full20 = roster.filter(m => minuteHits(m, 20) !== null).length;
  $('tempo-status').textContent = `来源：Dota2ProTracker（D2PT）· 20 分钟补刀覆盖 ${full20} / ${roster.length} 场`;
  showTempoMatch();
  $('tempo-view').hidden = false;
}

try {
  const key = 'dota-sandbox-token', fragment = new URLSearchParams(location.hash.slice(1)).get('token');
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);
  if (fragment) sessionStorage.setItem(key, fragment);
  const token = fragment || sessionStorage.getItem(key);
  const set = new URLSearchParams(location.search).get('set') ?? 'expanded';
  if (!Object.hasOwn(sets, set)) throw Error('没有这个 7.42 样本集，请在上方重新选择。');
  const spatialSet = set === '16' ? '16' : 'expanded';
  $(`set-${spatialSet}`).setAttribute('aria-current', 'page');
  const routes = snapshot(spatialSet, token).then(data => openRoutes(data, spatialSet)).catch(error => {
    corpus = undefined;
    drawable.clear();
    $('viewer').hidden = true;
    $('spatial-count').textContent = '坐标数据未载入';
    $('viewer-status').textContent = error.message;
  });
  if (set !== '16') {
    $('layer-picker').hidden = false;
    $('pick-tempo').onclick = () => pickLayer(true);
    $('pick-spatial').onclick = () => pickLayer(false);
    pickLayer(set === 'tempo', false);
    const minutes = snapshot('tempo', token).then(openRoster).catch(error => {
      $('tempo-count').textContent = '分钟样本未载入';
      $('tempo-status').textContent = error.message;
    });
    await Promise.all([routes, minutes]);
    showTempoMatch(); // Whichever independently authenticated source finished last.
  } else await routes;
} catch (error) {
  $('viewer-status').textContent = error.message;
}
