import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import { mock } from 'node:test';
import { createSandboxServer } from '../src/sandbox/server.ts';

const archiveFile = new URL('../data/farming-742-routes.json', import.meta.url);
const expandedFile = new URL('../data/farming-742-expanded.json', import.meta.url);
const tempoFile = new URL('../data/farming-742-tempo.json', import.meta.url);
const archive = JSON.parse(await fs.readFile(archiveFile, 'utf8'));
const optional = file => fs.readFile(file, 'utf8').then(JSON.parse, e => {
  if (e.code !== 'ENOENT') throw e;
  return null;
});
const [expanded, tempo] = await Promise.all([optional(expandedFile), optional(tempoFile)]);
if (process.argv.includes('--require-expanded')) assert.ok(expanded, 'published expanded snapshot required for this run');
if (process.argv.includes('--require-tempo')) assert.ok(tempo, 'published minute roster required for this run');
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { server, token } = createSandboxServer();
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const origin = `http://127.0.0.1:${server.address().port}`, api = '/api/farming-742';
const headers = { Authorization: `Bearer ${token}` };
const get = (path, options = {}) => fetch(origin + path, { headers, ...options });
const state = () => Promise.all(['/api/state', '/api/sim/state'].map(async path => (await get(path)).json()));
const windows = [[5, 10], [10, 15], [15, 20], [10, 20]];
const requests = [], visits = [], errors = [];
let browser;

async function trackedPage(options) {
  const page = await browser.newPage(options);
  let visit;
  page.on('pageerror', e => errors.push(e.message));
  await page.route('**/*', route => {
    const req = route.request(), url = new URL(req.url());
    const path = url.pathname + url.search;
    requests.push([req.method(), url.origin, path]);
    if (req.isNavigationRequest()) { visit = { path, snapshots: [] }; visits.push(visit); }
    if (path.startsWith(api)) visit.snapshots.push(path);
    return url.origin === origin ? route.continue() : route.abort();
  });
  return page;
}

// Only replace this file read inside the test process. The actual HTTP handler, token,
// archive and static assets remain in use; never create, move or truncate a shared file.
async function withSnapshotFile(file, data, run) {
  const read = fs.readFile;
  const stub = mock.method(fs, 'readFile', async (path, ...args) => {
    if (path?.href !== file.href) return read(path, ...args);
    if (data === null) throw Object.assign(new Error('missing fixture'), { code: 'ENOENT' });
    return JSON.stringify(data);
  });
  try { await run(); } finally { stub.mock.restore(); }
}
const withExpandedFile = (data, run) => withSnapshotFile(expandedFile, data, run);
const coordRows = corpus => (corpus?.matches ?? []).filter(m => m.trace.some(p =>
  p[3] && p[0] >= 300 && p[0] < 1200 && p[0] <= m.duration && Number.isFinite(p[1]) && Number.isFinite(p[2])));

const stamp = t => {
  const ms = Math.round(t * 1000);
  return `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')}.${String(ms % 1000).padStart(3, '0')}`;
};

async function inspectCamp(page, camp, action) {
  const short = camp.id.replace('good_', 'G').replace('evil_', 'E');
  const label = `${short} · ${camp.id} · X ${camp.x} / Y ${camp.y}`;
  await page.getByRole('button', { name: label, exact: true }).locator('circle')[action]();
  assert.equal(await page.locator('#camp-info').textContent(), label);
}

async function exercise(page, corpus, set) {
  const $ = id => page.locator('#' + id);
  await $('viewer').waitFor();
  assert.equal(await $(`set-${set}`).getAttribute('aria-current'), 'page');
  assert.ok((await $('viewer-status').textContent()).includes(`已载入 ${corpus.matches.length} 场回放`));
  assert.equal(await $('layer-picker').isVisible(), set !== '16');
  if (set !== '16') {
    assert.equal(await $('spatial-count').textContent(), `有坐标可看路线 ${coordRows(corpus).length} 场`);
    if (corpus.counts) assert.ok((await $('spatial-source').textContent()).includes(`原归档 ${corpus.counts.original16} 场，本轮新增 ${corpus.counts.newVerified} 场`));
  }
  assert.equal(await $('replay-window').inputValue(), '600,1200');
  assert.equal(await page.locator('#towers rect').count(), corpus.map.towers.length);
  assert.deepEqual(await page.locator('#camps g').evaluateAll(gs => gs.map(g => g.getAttribute('transform'))),
    corpus.map.camps.map(c => `translate(${c.x} ${-c.y})`));
  const bounds = new Map(corpus.matches.map(m => [m.id, new Map((m.points ?? []).filter(Boolean)
    .map(p => [Math.floor(p.t / 60), p]))]));
  const coverage = (rows, a, b) => rows.filter(m => bounds.get(m.id).has(a) && bounds.get(m.id).has(b)).length;
  const apiCount = () => requests.filter(r => r[2].startsWith(api)).length;
  const initial = apiCount();
  for (const hero of ['luna', 'troll_warlord', 'all']) {
    await $('hero-filter').selectOption(hero);
    for (const team of ['radiant', 'dire', 'all']) {
      await $('team-filter').selectOption(team);
      const rows = corpus.matches.filter(m => (hero === 'all' || hero === m.hero) && (team === 'all' || team === m.team));
      if (set === '16' && hero !== 'all' && team !== 'all') assert.equal(rows.length, 4);
      assert.deepEqual(await $('replay-match').locator('option').evaluateAll(os => os.map(o => o.value)), rows.map(m => String(m.id)));
      assert.equal(await $('match-count').textContent(), `${rows.length} / ${corpus.matches.length} 场`);
      assert.ok((await $('window-coverage').textContent()).includes(rows.length ? `${coverage(rows, 10, 20)} / ${rows.length} 场` : '没有符合筛选条件的对局'));
      assert.equal(await $('match-detail').isVisible(), !!rows.length);
      assert.equal(await $('replay-match').isDisabled(), !rows.length);
    }
  }
  for (const m of corpus.matches) {
    await $('replay-match').selectOption(String(m.id));
    const meta = await $('replay-meta').textContent();
    assert.ok(meta.includes(String(m.id)) && meta.includes(stamp(m.duration)));
    for (const [from, to] of windows) {
      await $('replay-window').selectOption(`${60 * from},${60 * to}`);
      const got = await page.evaluate(() => {
        const el = id => document.getElementById(id);
        return { gold: [el('replay-lane-gold').textContent, el('replay-neutral-gold').textContent],
          times: el('counter-times').textContent, coverage: el('window-coverage').textContent, extent: el('trace-coverage').textContent,
          path: el('body-trace').getAttribute('d'),
          max: Number(el('replay-time').max), disabled: el('replay-time').disabled, sample: el('sample-info').textContent,
          marker: el('sample-marker').getAttribute('visibility') };
      });
      const a = bounds.get(m.id).get(from), b = bounds.get(m.id).get(to);
      assert.deepEqual(got.gold, a && b ? [String(b.lane - a.lane), String(b.neutral - a.neutral)] : ['未覆盖', '未覆盖'], `${m.id}: ${from}–${to}`);
      assert.ok(got.times.includes(`${from} 分钟 ${a ? stamp(a.t) : '未覆盖'}`));
      assert.ok(got.times.includes(`${to} 分钟 ${b ? stamp(b.t) : '未覆盖'}`));
      assert.ok(got.coverage.includes(`${coverage(corpus.matches, from, to)} / ${corpus.matches.length} 场`));
      const trace = m.trace.filter(([t]) => from * 60 <= t && t < to * 60 && t <= m.duration);
      assert.equal(got.path, trace.flatMap((p, k) => p[3] ? [`${!p[4] && trace[k - 1]?.[3] ? 'L' : 'M'}${p[1]} ${-p[2]}`] : []).join(' '));
      assert.equal(got.max, Math.max(0, trace.length - 1));
      assert.equal(got.disabled, trace.length < 2);
      assert.equal(got.marker, trace[0]?.[3] ? 'visible' : 'hidden');
      assert.ok(got.sample.includes(trace.length ? stamp(trace[0][0]) : '未覆盖'));
      assert.ok(got.extent.includes(trace.length ? `${trace.length} 点（${stamp(trace[0][0])} 至 ${stamp(trace.at(-1)[0])}）` : '未覆盖'));
      assert.equal(got.extent.includes(`本局在 ${stamp(m.duration)} 结束`), m.duration < to * 60);
    }
  }
  assert.equal(apiCount(), initial, 'all match, filter, and window changes are local');
}

async function unavailable(page) {
  await page.locator('#viewer-status').getByText(/本轮坐标包不可用（HTTP 503）/).waitFor();
  assert.ok(await page.locator('#viewer').isHidden());
  assert.ok(await page.locator('#set-16').isVisible());
}

async function exerciseTempo(page, roster, spatial) {
  const $ = id => page.locator('#' + id);
  await $('pick-tempo').click();
  await $('tempo-view').waitFor();
  assert.equal(new URL(page.url()).search, '?set=tempo');
  assert.equal(await $('tempo-count').textContent(), `${roster.matches.length} 场分钟样本`);
  const full20 = roster.matches.filter(m => m.duration >= 1200 && Number.isSafeInteger(m.minuteLH[20]) && m.minuteLH[20] >= 0).length;
  assert.ok((await $('tempo-status').textContent()).includes(`Dota2ProTracker（D2PT）· 20 分钟补刀覆盖 ${full20} / ${roster.matches.length} 场`));
  assert.deepEqual(await $('tempo-match').locator('option').evaluateAll(os => os.map(o => o.value)), roster.matches.map(m => String(m.id)));
  const rows = coordRows(spatial);
  let linked;
  for (const m of roster.matches) {
    await $('tempo-match').selectOption(String(m.id));
    assert.ok((await $('tempo-meta').textContent()).includes(String(m.id)));
    const actual = await page.locator('.minute-lh dd').allTextContents();
    assert.deepEqual(actual, [5, 10, 15, 20].map(k => m.duration >= 60 * k && Number.isSafeInteger(m.minuteLH[k]) && m.minuteLH[k] >= 0
      ? String(m.minuteLH[k]) : '未覆盖'), `D2PT actual minute index in ${m.id}`);
    const same = rows.some(r => r.id === m.id && r.hero === m.hero && r.team === m.team && r.accountId === m.accountId);
    assert.equal(await $('tempo-open-route').isVisible(), same);
    assert.ok(await $('route-map').isHidden(), 'a minute record never renders a map');
    if (same) linked = m;
    else assert.match(await $('tempo-route-info').textContent(), /不绘制路线|不提供路线/);
  }
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  if (linked) {
    await $('tempo-match').selectOption(String(linked.id));
    await $('tempo-open-route').click();
    assert.ok(await $('route-map').isVisible());
    assert.equal(await $('replay-match').inputValue(), String(linked.id));
  } else await $('pick-spatial').click();
  assert.equal(new URL(page.url()).search, '');
}

try {
  assert.equal(archive.patch.version, '7.42');
  assert.equal(new Set(archive.matches.map(m => m.id)).size, 16);
  assert.deepEqual([archive.map.camps.length, archive.map.towers.length], [28, 22]);
  if (expanded) assert.ok(expanded.matches.length > 0 && expanded.matches.length <= 101);
  assert.match(await (await get('/farming-replays')).text(), /7\.41f 归档：.*href="\/farming-742".*href="\/farming-742\?set=16"/);
  const before = await state();
  for (const suffix of ['', '?set=16', '?set=expanded', '?set=tempo']) {
    const path = api + suffix;
    assert.equal((await get(path, { headers: {} })).status, 401);
    assert.equal((await get(path, { headers: { Authorization: `Bearer ${'x'.repeat(43)}` } })).status, 401);
    assert.equal((await get(path, { headers: { ...headers, Origin: 'http://example.invalid' } })).status, 403);
    for (const method of ['HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS']) {
      const denied = await get(path, { method });
      assert.equal(denied.status, 405);
      assert.equal(denied.headers.get('allow'), 'GET');
    }
    const data = suffix === '?set=tempo' ? tempo : suffix === '?set=expanded' ? expanded : archive, res = await get(path);
    assert.equal(res.headers.get('cache-control'), 'no-store');
    assert.equal(res.status, data ? 200 : 503);
    if (data) assert.deepEqual(await res.json(), data);
  }
  for (const path of ['/data/farming-742-routes.json', '/data/farming-742-expanded.json', '/data/farming-742-tempo.json', api + '?set=unknown', api + '/1'])
    assert.equal((await get(path)).status, 404);

  browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH });
  const guest = await trackedPage();
  for (const path of ['/farming-742', '/farming-742?set=16', '/farming-742?set=tempo']) {
    await guest.goto(origin + path);
    await guest.locator(path.endsWith('=tempo') ? '#tempo-status' : '#viewer-status').getByText(/需要本次沙盒会话/).waitFor();
    assert.ok(await guest.locator('#viewer').isHidden());
    assert.ok(await guest.locator('#tempo-view').isHidden());
  }
  await guest.close();
  const page = await trackedPage({ viewport: { width: 1280, height: 900 }, hasTouch: true });
  await page.goto(`${origin}/farming-742#token=${token}`);
  if (expanded) await exercise(page, expanded, 'expanded'); else await unavailable(page);
  if (tempo) await exerciseTempo(page, tempo, expanded);
  assert.equal(new URL(page.url()).hash, '');
  assert.equal(await page.evaluate(() => sessionStorage.getItem('dota-sandbox-token')), token);
  await page.locator('#set-16').click();
  await exercise(page, archive, '16');
  assert.equal(new URL(page.url()).search, '?set=16');
  await inspectCamp(page, archive.map.camps[0], 'hover');
  const m = archive.matches.find(m => m.trace.some(([t,,, alive]) => t >= 600 && !alive));
  await page.locator('#replay-match').selectOption(String(m.id));
  const trace = m.trace.filter(([t]) => t >= 600 && t < 1200);
  for (const alive of [0, 1]) {
    const i = trace.findIndex(p => p[3] === alive);
    await page.locator('#replay-time').evaluate((el, i) => { el.value = i; el.dispatchEvent(new Event('input')); }, i);
    assert.equal(await page.locator('#sample-marker').isVisible(), !!alive);
    if (alive) {
      assert.equal(Number(await page.locator('#sample-marker').getAttribute('cx')), trace[i][1]);
      assert.equal(Number(await page.locator('#sample-marker').getAttribute('cy')), -trace[i][2]);
      assert.ok((await page.locator('#sample-info').textContent()).includes(`X ${trace[i][1]} / Y ${trace[i][2]}`));
    } else assert.match(await page.locator('#sample-info').textContent(), /死亡/);
  }
  await page.reload();
  await page.locator('#viewer').waitFor();
  assert.equal(await page.locator('#set-16').getAttribute('aria-current'), 'page');

  const mobile = await trackedPage({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true });
  await mobile.goto(`${origin}/farming-742#token=${token}`);
  if (expanded) await exercise(mobile, expanded, 'expanded'); else await unavailable(mobile);
  if (tempo) await exerciseTempo(mobile, tempo, expanded);
  if (!expanded) { await mobile.locator('#set-16').tap(); await mobile.locator('#viewer').waitFor(); }
  await inspectCamp(mobile, (expanded ?? archive).map.camps.at(-1), 'tap');
  assert.ok(await mobile.evaluate(() => document.documentElement.scrollWidth <= innerWidth && innerWidth === 390));

  if ((expanded?.matches.length ?? 0) < 101) {
    const capacity = { ...archive, matches: Array.from({ length: 101 }, (_, i) => ({ ...archive.matches[i % 16], id: i + 1 })) };
    await withExpandedFile(capacity, async () => {
      await mobile.goto(origin + '/farming-742');
      await exercise(mobile, capacity, 'expanded');
    });
  }
  const base = archive.matches[0];
  const fixture = { ...archive, minimumMatchMmr: 7000, matches: [
    { ...base, id: 1, duration: 925, points: base.points.slice(0, 3), trace: base.trace.filter(([t]) => t <= 925),
      endPoint: { t: 925, lane: 5000, neutral: 2000, lh: 150 } }, // partial trace, no 20-minute counter
    { ...base, id: 2, points: [base.points[0], base.points[1], base.points[3]], trace: [base.trace[0]] }, // missing interior endpoint; single/empty trace
    { ...base, id: 3, duration: 89, points: [], trace: [] },
    { ...base, id: 4, duration: 610, points: [base.points[0], { ...base.points[0], t: base.points[1].t }, null], trace: [] }, // recorded zero delta, not a missing value
  ] };
  const minuteLH = Array.from({ length: 21 }, (_, i) => i * 2);
  const minuteFixture = { patch: '7.42', matches: [
    { ...base, id: 1, duration: 925, minuteLH }, // even a padded minute 20 must not be shown
    { ...base, id: 2, accountId: base.accountId + 1, minuteLH }, // same ID, different player: no route link
    { ...base, id: 3, duration: 89, minuteLH: [0] },
    { ...base, id: 5, minuteLH: minuteLH.map((v, i) => i === 5 ? 0 : i === 10 ? null : v) },
  ] };
  await withExpandedFile(fixture, () => withSnapshotFile(tempoFile, minuteFixture, async () => {
    await mobile.goto(origin + '/farming-742');
    await exercise(mobile, fixture, 'expanded');
    assert.match(await mobile.locator('#viewer-status').textContent(), /入选门槛：对局平均 MMR ≥ 7000/);
    await mobile.locator('#replay-match').selectOption('1');
    await mobile.locator('#replay-window').selectOption('900,1200');
    await mobile.locator('#replay-time').press('End');
    assert.ok((await mobile.locator('#sample-info').textContent()).includes(stamp(fixture.matches[0].trace.at(-1)[0])));
    await exerciseTempo(mobile, minuteFixture, fixture);
  }));
  await withExpandedFile({ ...fixture, matches: [] }, async () => {
    await mobile.reload();
    await mobile.getByText(/本轮坐标包尚未收录可显示的对局/).waitFor();
    assert.ok(await mobile.locator('#viewer').isHidden());
  });
  await withExpandedFile(null, async () => {
    assert.equal((await get(api + '?set=expanded')).status, 503);
    await mobile.reload();
    await unavailable(mobile);
    if (tempo) await exerciseTempo(mobile, tempo, null);
    await mobile.locator('#set-16').tap();
    await mobile.locator('#viewer').waitFor();
    assert.equal(await mobile.locator('#match-count').textContent(), '16 / 16 场');
    await mobile.locator('#set-expanded').tap();
    await unavailable(mobile);
  });
  await withSnapshotFile(tempoFile, null, async () => {
    await mobile.goto(origin + '/farming-742?set=tempo');
    await mobile.locator('#tempo-status').getByText(/分钟名册不可用（HTTP 503）/).waitFor();
    assert.ok(await mobile.locator('#tempo-view').isHidden());
    await mobile.locator('#pick-spatial').tap();
    if (expanded) await mobile.locator('#viewer').waitFor(); else await unavailable(mobile);
  });

  const assets = new Set(['/farming-742', '/farming-742?set=tempo', '/farming-742?set=16', '/farming-742.js', '/farming-742.css',
    '/farming-replays.css', '/simulator.css', api + '?set=16', api + '?set=expanded', api + '?set=tempo']);
  for (const [method, host, path] of requests) assert.ok(method === 'GET' && host === origin && assets.has(path), `unexpected ${method} ${path}`);
  for (const { path, snapshots } of visits) assert.deepEqual(snapshots.sort(), path.endsWith('=16') ?
    [api + '?set=16'] : [api + '?set=expanded', api + '?set=tempo'], 'one authenticated GET per source per entry, no silent fallback');
  assert.deepEqual(errors, []);
  assert.deepEqual(await state(), before);
  console.log(`PASS 7.42 viewer: archive 16 × 4 windows; ${expanded ? `current Valve ${expanded.matches.length} (${coordRows(expanded).length} with coordinates) × 4` : 'no current Valve snapshot (503); capacity fixture only'}; ${tempo ? `real D2PT ${tempo.matches.length} × 4 minute indices` : 'no published D2PT roster'}. Desktop and mobile, short/missing/empty cases, independent auth/GET-only and unchanged sandbox state passed.`);
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
