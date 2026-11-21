import fs from 'node:fs/promises';
import path from 'node:path';

// Common 7.42 ward spots from public OpenDota parses: where each team placed observer and sentry wards.
// Used by the coach as a prior when an enemy's ward count drops out of sight; never a live position.
//   node scripts/build-ward-spots.ts --fetch 400   download up to 400 more matches into the cache (≈1 req/s)
//   node scripts/build-ward-spots.ts               rebuild data/dota2-ward-spots-742.json from the cache
//   node scripts/build-ward-spots.ts --exclude ID,ID --out FILE   without some matches (to evaluate on them)
const PATCH_ID = 61, PATCH = '7.42';
const cache = path.resolve('.tmp/ward-spots/matches');
const arg = (name: string) => { const i = process.argv.indexOf(name); return i >= 0 ? process.argv[i + 1] : undefined; };
const output = arg('--out') ?? 'data/dota2-ward-spots-742.json';
const excluded = new Set((arg('--exclude') ?? '').split(',').filter(Boolean).map(id => `${id}.json`));
const API = 'https://api.opendota.com/api';
// Normal 5v5 modes on the standard map (turbo and event modes excluded); parsed matches of at least 15 minutes.
const MODES = new Set([1, 2, 3, 4, 5, 16, 22]);
const MIN_DURATION = 900;

async function get(url: string): Promise<any> {
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(url, { signal: AbortSignal.timeout(30_000), redirect: 'error' });
    if (response.ok) return await response.json();
    if (response.status === 429 && attempt < 3) { await sleep(60_000); continue; } // free tier: 60 per minute
    throw new Error(`OpenDota HTTP ${response.status}`);
  }
}
const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

/** OpenDota logs positions in 128-unit cells with the world origin at cell 128 (the same as the replay entity cell). */
const world = (cell: number) => Math.round((cell - 128) * 128);

async function fetchMatches(limit: number) {
  await fs.mkdir(cache, { recursive: true });
  const have = new Set((await fs.readdir(cache)).map(f => f.replace(/\.json$/, '')));
  const skipped = new Set<string>(JSON.parse(await fs.readFile(path.join(cache, '..', 'skipped.json'), 'utf8').catch(() => '[]')));
  let added = 0, cursor: number | undefined;
  const pro: number[] = (await get(`${API}/proMatches`)).map((m: any) => m.match_id);
  await sleep(1100);
  while (added < limit) {
    const fromPro = pro.length > 0;
    const page: number[] = fromPro ? pro.splice(0, pro.length)
      : (await get(`${API}/parsedMatches${cursor ? `?less_than_match_id=${cursor}` : ''}`)).map((m: any) => m.match_id);
    if (!page.length) break;
    if (!fromPro) cursor = Math.min(...page);
    for (const id of page) {
      if (added >= limit) break;
      if (have.has(String(id)) || skipped.has(String(id))) continue;
      await sleep(1100);
      let m: any;
      try { m = await get(`${API}/matches/${id}`); } catch (e) { console.warn(id, (e as Error).message); continue; }
      const ok = m.patch === PATCH_ID && MODES.has(m.game_mode) && m.duration >= MIN_DURATION &&
        m.players?.length === 10 && m.players.some((p: any) => Array.isArray(p.obs_log));
      if (!ok) { skipped.add(String(id)); continue; }
      const slim = {
        match_id: m.match_id, start_time: m.start_time, duration: m.duration, game_mode: m.game_mode, lobby_type: m.lobby_type,
        league: m.leagueid || 0, rank: m.average_rank ?? null,
        players: m.players.map((p: any) => ({ slot: p.player_slot, hero: p.hero_id,
          obs: (p.obs_log || []).map((w: any) => [w.time, w.x, w.y]), sen: (p.sen_log || []).map((w: any) => [w.time, w.x, w.y]) })),
      };
      await fs.writeFile(path.join(cache, `${id}.json`), JSON.stringify(slim));
      have.add(String(id)); added++;
      if (added % 25 === 0) console.log(`fetched ${added}/${limit} (cache ${have.size})`);
    }
    await fs.writeFile(path.join(cache, '..', 'skipped.json'), JSON.stringify([...skipped]));
  }
  console.log(`fetched ${added}; cache holds ${have.size} matches`);
}

type Placement = { x: number; y: number; t: number };
/** Greedy density clusters: the densest point within RADIUS takes its unassigned neighbours, until clusters get small. */
function clusters(points: Placement[], matches: number) {
  const RADIUS = 320, MIN = Math.max(3, Math.round(matches * 0.02));
  // Neighbours through a grid of RADIUS cells; each point's count of unassigned neighbours is kept up to date.
  const cell = (p: Placement) => `${Math.floor(p.x / RADIUS)},${Math.floor(p.y / RADIUS)}`;
  const grid = new Map<string, number[]>();
  points.forEach((p, i) => { const k = cell(p); if (!grid.has(k)) grid.set(k, []); grid.get(k)!.push(i); });
  const left = new Set(points.map((_, i) => i));
  const near = (i: number) => {
    const cx = Math.floor(points[i].x / RADIUS), cy = Math.floor(points[i].y / RADIUS), out: number[] = [];
    for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++)
      for (const j of grid.get(`${cx + dx},${cy + dy}`) ?? [])
        if (left.has(j) && Math.hypot(points[i].x - points[j].x, points[i].y - points[j].y) <= RADIUS) out.push(j);
    return out;
  };
  const density = points.map((_, i) => near(i).length);
  const out = [];
  while (left.size) {
    let best = -1;
    for (const i of left) if (best < 0 || density[i] > density[best]) best = i;
    const members = near(best);
    if (members.length < MIN) break;
    const x = members.reduce((s, j) => s + points[j].x, 0) / members.length;
    const y = members.reduce((s, j) => s + points[j].y, 0) / members.length;
    const spread = Math.sqrt(members.reduce((s, j) => s + (points[j].x - x) ** 2 + (points[j].y - y) ** 2, 0) / members.length);
    const minutes = members.map(j => points[j].t / 60);
    out.push({ x: Math.round(x), y: Math.round(y), spread: Math.round(spread), count: members.length,
      perMatch: Number((members.length / matches).toFixed(3)),
      // Placements by game phase: before 10:00, 10:00–25:00, after 25:00.
      phases: [minutes.filter(m => m < 10).length, minutes.filter(m => m >= 10 && m < 25).length, minutes.filter(m => m >= 25).length] });
    for (const j of members) left.delete(j);
    for (const j of members) for (const k of near(j)) density[k]--;
  }
  return out;
}

async function build() {
  const files = (await fs.readdir(cache).catch(() => [])).filter(f => f.endsWith('.json') && !excluded.has(f)).sort();
  if (files.length < 50) throw new Error(`Only ${files.length} cached matches; fetch more first (--fetch N)`);
  const groups: Record<string, Placement[]> = { radiantObserver: [], radiantSentry: [], direObserver: [], direSentry: [] };
  let first = Infinity, last = 0, pro = 0;
  for (const file of files) {
    const m = JSON.parse(await fs.readFile(path.join(cache, file), 'utf8'));
    first = Math.min(first, m.start_time); last = Math.max(last, m.start_time); pro += m.league ? 1 : 0;
    for (const p of m.players) {
      const side = p.slot < 128 ? 'radiant' : 'dire';
      for (const [t, x, y] of p.obs) groups[side + 'Observer'].push({ t, x: world(x), y: world(y) });
      for (const [t, x, y] of p.sen) groups[side + 'Sentry'].push({ t, x: world(x), y: world(y) });
    }
  }
  const spots = Object.fromEntries(Object.entries(groups).map(([k, v]) => [k, clusters(v, files.length)]));
  const result = {
    version: 'ward-spots-742-1', patch: PATCH,
    source: { api: `${API}/matches/{id}`, field: 'players[].obs_log / sen_log', matches: files.length, proMatches: pro,
      from: new Date(first * 1000).toISOString(), to: new Date(last * 1000).toISOString(),
      filter: `patch ${PATCH_ID} (7.42), game modes ${[...MODES].join('/')}, at least ${MIN_DURATION / 60} minutes, parsed` },
    note: 'Clusters of ward placements by the placing team (world units; a cluster takes placements within 320 of its densest point). ' +
      'A prior for where an enemy probably warded, not an observation. perMatch = placements per match in the cluster; phases = before 10:00, 10–25:00, after 25:00.',
    placements: Object.fromEntries(Object.entries(groups).map(([k, v]) => [k, v.length])),
    spots,
  };
  await fs.writeFile(output, JSON.stringify(result, null, 1) + '\n');
  console.log(`${files.length} matches → ${Object.entries(spots).map(([k, v]) => `${k} ${v.length} spots / ${groups[k].length}`).join(', ')}`);
}

if (arg('--fetch') !== undefined) await fetchMatches(Number(arg('--fetch') || 200));
else await build();
