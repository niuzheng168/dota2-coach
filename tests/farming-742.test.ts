import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

// The oracles below are from the two captured D2PT catalogs, OpenDota player slots,
// and Valve replay frames / combat logs, not from the study generator or its summary.
// Only the compact published JSON is needed at test time.
type Hero = 'luna' | 'troll_warlord';
type Rank = [id: number, mmr: number, radiant: 0 | 1, account: number];
type Point = { t: number; lane: number; neutral: number; lh: number };
type Hit = [time: number, origin: string, actor: 'body' | 'illusion' | '?', x: number | null, y: number | null, tick: number];
type Trace = [time: number, x: number, y: number, alive: 0 | 1, breakBefore: 0 | 1];
type Match = {
  id: number; hero: Hero; team: 'radiant' | 'dire'; accountId: number; mmr: number; won: boolean;
  start: number; duration: number; build: number; finalLH: number; url: string; replay: string;
  demSha256: string; metadataSha256: string; points: Point[]; firstHeld: Record<string, number>;
  events: Hit[]; trace: Trace[];
};
const root = new URL('../', import.meta.url);
const file = 'data/farming-742-routes.json';
const study = JSON.parse(readFileSync(new URL(file, root), 'utf8')) as {
  scope: string; captureDateUTC: string;
  patch: { version: string; from: number; until: number | null; listSha256: string; notesSha256: string };
  builds: { build: number; commit: string; asOf: string; sha256: string; text: string; url: string }[];
  sources: { heroId: number; feedRows: number; patchRankedCarryRows: number; ranking: Rank[] }[];
  map: { camps: { id: string; x: number; y: number; initialTier: number; box: number[] }[];
    towers: { id: string; team: number }[] }; matches: Match[];
};
const byId = new Map(study.matches.map(m => [m.id, m]));
function match(id: number) { const m = byId.get(id); assert.ok(m, `missing match ${id}`); return m; }
const heroes: Hero[] = ['luna', 'troll_warlord'];
const bounds = [300, 600, 900, 1200];
const sha = (text: string) => createHash('sha256').update(text).digest('hex');
const median = (xs: number[]) => {
  const sorted = xs.toSorted((a, b) => a - b);
  return (sorted[Math.floor((xs.length - 1) / 2)] + sorted[Math.floor(xs.length / 2)]) / 2;
};
function near(actual: number, expected: number, tolerance: number) {
  assert.ok(Math.abs(actual - expected) <= tolerance, `${actual} differs from ${expected} by more than ${tolerance}`);
}

// OD's terminal LH/duration and radiant_win are independent of the sampled economy.
// Last two columns: 20-min minus 10-min player m_iCreepKillGold / m_iNeutralKillGold.
// Not exclusively lane bounties: in 9097037811 a spiderling added 9 creep gold (tick 35986);
// a summoned neutral skeleton added 5 neutral gold (29208) without a matching camp dot.
type Ref = [id: number, playerSlot: number, radiantWin: boolean, build: number, odSeconds: number,
  finalLH: number, creepDelta: number, neutralDelta: number];
const refs: Record<Hero, Ref[]> = {
  luna: [
    [9100049738, 2, true, 6989, 2038, 498, 2757, 3039],
    [9097587313, 2, false, 6986, 2247, 423, 1946, 2556],
    [9097535245, 3, true, 6986, 2793, 622, 1832, 4042],
    [9097801447, 4, false, 6986, 1600, 209, 1188, 2175],
    [9097037811, 131, false, 6983, 1725, 467, 2019, 3954],
    [9096845332, 132, false, 6983, 1913, 445, 1658, 2595],
    [9096797937, 130, false, 6983, 2744, 596, 2397, 2769],
    [9098255705, 128, false, 6986, 2116, 455, 2832, 3092],
  ],
  troll_warlord: [
    [9098402606, 3, true, 6986, 1973, 358, 2534, 2028],
    [9098472218, 1, false, 6986, 1515, 185, 2549, 1287],
    [9098360852, 1, true, 6986, 1732, 310, 2515, 2795],
    [9097330939, 4, true, 6985, 3633, 522, 1773, 2153],
    [9098789898, 129, true, 6988, 1758, 257, 3039, 1621],
    [9099209663, 130, false, 6989, 2038, 396, 2636, 2239],
    [9099283649, 129, false, 6989, 2248, 453, 2606, 2845],
    [9097559249, 128, false, 6986, 1487, 187, 2741, 669],
  ],
};

test('captured ranked pos-1 pool: best match per account within each hero AND side, then top four', () => {
  assert.equal(study.matches.length, 16); assert.equal(byId.size, 16);
  assert.deepEqual(study.sources.map(s => s.heroId).sort((a, b) => a - b), [48, 95]);
  for (const s of study.sources) {
    const luna = s.heroId === 48, hero = luna ? 'luna' : 'troll_warlord';
    assert.deepEqual([s.feedRows, s.patchRankedCarryRows, s.ranking.length], luna ? [108, 81, 29] : [45, 44, 13]);
    // Hash of just the ranked eligible tuples from the raw catalog, including skipped repeats.
    assert.equal(sha(JSON.stringify(s.ranking)), luna ? '3a93b1eabe9d5905053b1d4ed9dcc4da6b892e5ae0e8c41decdae0d81fd2b7c1'
      : '84345aad3e31b52963ff939670250c7ec29d08f3518dbeeda1c7da2ee902540f');
    const rank = (a: Rank, b: Rank) => b[1] - a[1] || b[0] - a[0];
    assert.deepEqual(s.ranking, s.ranking.toSorted(rank));
    assert.equal(new Set(s.ranking.map(r => r[0])).size, s.ranking.length);
    assert.ok(s.ranking.every(([, mmr, side, account]) => mmr >= 7500 && [0, 1].includes(side) && account > 0));
    const group = study.matches.filter(m => m.hero === hero);
    assert.equal(new Set(group.map(m => m.accountId)).size, luna ? 6 : 7); // cross-side repeats are legitimate
    for (const side of [0, 1]) {
      const pool = s.ranking.filter(r => r[2] === side);
      const best = [...new Set(pool.map(r => r[3]))].map(id => pool.filter(r => r[3] === id).toSorted(rank)[0]);
      const actual = group.filter(m => m.team === (side ? 'radiant' : 'dire'));
      assert.deepEqual(actual.map(m => [m.id, m.mmr, side, m.accountId]), best.sort(rank).slice(0, 4));
    }
  }
});

test('7.42 patch and binary provenance; faction, result and terminal LH per OpenDota slot', () => {
  assert.equal(study.scope, 'omniscient-offline-only'); assert.equal(study.patch.version, '7.42');
  assert.equal(study.captureDateUTC, '2026-11-22');
  assert.equal(study.patch.from, Date.UTC(2026, 10, 18, 8) / 1000);
  assert.equal(study.patch.listSha256, '82500517a50bef152dcc01bc42b6d517f19b6bf8f7e0d6c562a1a82ad8342939');
  assert.equal(study.patch.notesSha256, '5996779d30fe1a6c8178543de327831bb9d8be7c1a9ff65500682b6d853209a8');
  const end = study.patch.until ?? Date.UTC(2026, 10, 23) / 1000;
  for (const b of study.builds) {
    assert.equal(sha(b.text), b.sha256); assert.ok(b.url.includes(`/${b.commit}/`));
    assert.equal(Number(/^ServerVersion=(\d+)$/m.exec(b.text)?.[1]), b.build);
  }
  // ID-bound pairs of hashes; independently hashed all 16 actual DEMs and OpenDota responses.
  const artifacts = study.matches.toSorted((a, b) => a.id - b.id).map(m => [m.id, m.demSha256, m.metadataSha256]);
  assert.equal(sha(JSON.stringify(artifacts)), '831a84950a5c7e614e90a462ebe0c76685eeee386da2115d4a88cef1f90ec62d');
  for (const hero of heroes) for (const [id, slot, radiantWin, build, seconds, final] of refs[hero]) {
    const m = match(id), radiant = slot < 128;
    assert.deepEqual([m.hero, m.team, m.won, m.build, m.finalLH], [hero, radiant ? 'radiant' : 'dire', radiant === radiantWin, build, final], `${id}`);
    const b = study.builds.find(b => b.build === build); assert.ok(b);
    const asOf = Date.parse(b.asOf) / 1000;
    assert.ok(study.patch.from <= asOf && asOf <= m.start && m.start < end);
    near(m.duration, seconds, 1); assert.ok(m.duration > m.points[3].t && final > m.points[3].lh);
    assert.equal(m.url, `https://dota2protracker.com/matches/${id}`);
    assert.match(new URL(m.replay).pathname, new RegExp(`^/570/${id}_\\d+\\.dem\\.bz2$`));
  }
});

test('first full 5/10/15/20-minute samples and raw 10–20 counter deltas (not kill-dot counts)', () => {
  for (const hero of heroes) for (const [id, , , , , , creep, neutral] of refs[hero]) {
    const { points } = match(id); assert.equal(points.length, 4);
    points.forEach((p, i) => {
      assert.ok(p.t >= bounds[i] && p.t < bounds[i] + 1.2, `${id}: boundary ${bounds[i]}`);
      for (const key of ['lane', 'neutral', 'lh'] as const)
        assert.ok(Number.isSafeInteger(p[key]) && p[key] >= (i ? points[i - 1][key] : 0), `${id}: ${key}`);
    });
    assert.deepEqual([points[3].lane - points[1].lane, points[3].neutral - points[1].neutral], [creep, neutral], `${id}`);
  }
  // OD's minute buckets are 82 and 189 respectively; these are the first replay frames AFTER the boundaries.
  assert.deepEqual(match(9097535245).points[1], { t: 600.1, lane: 2467, neutral: 700, lh: 83 });
  assert.deepEqual(match(9098402606).points[3], { t: 1200.133, lane: 5159, neutral: 2379, lh: 191 });
  assert.deepEqual(match(9097559249).points.map(p => p.t), [300.733, 600.733, 900.7, 1200.7]);
});

test('10–20: median of per-match rates (actual elapsed seconds) and shares, not pooled or total GPM', () => {
  // Run the public summary read-only; a regression cannot regenerate the JSON or fall back to the raw cache.
  const summary = JSON.parse(execFileSync(process.execPath, ['--permission', `--allow-fs-read=${file}`,
    '--allow-fs-read=scripts/replays/study-742.ts', '--allow-fs-read=src', '--allow-fs-read=package.json',
    'scripts/replays/study-742.ts', '--summary'], { cwd: root, encoding: 'utf8' })) as {
    hero: Hero; medianCumulativeLH: number[]; windows: { minutes: number[]; nonzeroBoth: number;
      medianFarmPerMinute: number; medianLaneShare: number; medianDistinctBodyCamps: number }[] }[];
  for (const hero of heroes) {
    const group = study.matches.filter(m => m.hero === hero);
    const rates = group.map(({ points: [, a, , b] }) => {
      const l = b.lane - a.lane, n = b.neutral - a.neutral, minutes = (b.t - a.t) / 60;
      assert.ok(l > 0 && n > 0);
      return [l / minutes, n / minutes, (l + n) / minutes, l / (l + n)];
    });
    // Full-precision raw times; the compact endpoints are rounded to milliseconds.
    const want = hero === 'luna' ? [198.25, 290.4, 548.1, 0.4110473982] : [257.75, 209.055634, 461.112673, 0.5480880406];
    const med = want.map((_, i) => median(rates.map(r => r[i])));
    med.forEach((value, i) => near(value, want[i], i === 3 ? 1e-9 : 0.001));
    const lh = bounds.map((_, i) => median(group.map(m => m.points[i].lh)));
    assert.deepEqual(lh, hero === 'luna' ? [25, 69.5, 142, 232.5] : [22.5, 54.5, 116.5, 185]);
    const row = summary.find(r => r.hero === hero); assert.ok(row); assert.deepEqual(row.medianCumulativeLH, lh);
    const w = row.windows.find(w => w.minutes[0] === 10 && w.minutes[1] === 20); assert.ok(w);
    assert.equal(w.nonzeroBoth, 8); near(w.medianFarmPerMinute, med[2], 1e-8); near(w.medianLaneShare, med[3], 1e-10);
    const campIds = new Set(study.map.camps.map(c => c.id));
    const camps = median(group.map(m => new Set(m.events.filter(([t, site, actor]) =>
      t >= 600 && t < 1200 && actor === 'body' && campIds.has(site)).map(e => e[1])).size));
    assert.equal(camps, hero === 'luna' ? 13.5 : 9.5); assert.equal(w.medianDistinctBodyCamps, camps);
  }
});

// All records at a tick, as a multiset: two indistinguishable same-tick deaths must NOT be deduplicated.
type AtTick = [number, string, Hit[2], number | null, number | null];
function atTick(id: number, tick: number, ...want: AtTick[]) {
  const got = match(id).events.filter(e => e[5] === tick).map(e => e.slice(0, 5));
  const bag = (rows: unknown[][]) => rows.map(r => JSON.stringify(r)).sort();
  assert.deepEqual(bag(got), bag(want), `${id} tick ${tick}`);
}

test('map landmarks and observed event domains: origin, actor, death coordinates and game-clock order', () => {
  const { camps, towers } = study.map, ids = new Set(camps.map(c => c.id));
  assert.equal(ids.size, 28); assert.equal(camps.length, 28);
  assert.equal(new Set(towers.map(t => t.id)).size, 22); assert.equal(towers.length, 22);
  assert.deepEqual(camps.filter(c => ['good_14', 'good_20', 'evil_11', 'evil_20'].includes(c.id))
    .map(c => [c.id, c.x, c.y, c.initialTier]).sort(), [
    ['evil_11', -4228, 8140, 0], ['evil_20', -6408, 7816, 1], ['good_14', 4520, -8336, 0], ['good_20', 6712, -7632, 1],
  ]);
  for (const { x, y, box: [x0, y0, x1, y1] } of camps) assert.ok(x0 <= x && x <= x1 && y0 <= y && y <= y1);
  for (const m of study.matches) {
    assert.ok(m.events.length > 0);
    m.events.forEach(([t, site, actor, x, y, tick], i) => {
      assert.ok(t >= 0 && t < 1200 && Number.isSafeInteger(tick) && tick > 0);
      if (i) assert.ok(t >= m.events[i - 1][0] && tick >= m.events[i - 1][5]);
      assert.ok(site === '?' || /^lane:(bot|mid|top|\?)$/.test(site) || ids.has(site));
      assert.ok(['body', 'illusion', '?'].includes(actor));
      assert.ok((x === null && y === null) || (Number.isInteger(x) && Number.isInteger(y)));
    });
  }
});

test('combat attribution: exclude denies, enemy copies and converted neutrals; keep allied illusions separate', () => {
  atTick(9100049738, 10246); atTick(9097037811, 8586); // picked Luna denied her own lane creep on each side
  atTick(9098402606, 9444); atTick(9099209663, 8196); // same check for Troll
  for (const tick of [23324, 29058, 29076, 29146]) atTick(9096797937, tick); // Radiant Luna illusions, picked Luna is Dire
  atTick(9097587313, 30532); // neutral-named mud golem split, target_team=3, not a camp last hit
  atTick(9100049738, 35984, [971.267, 'good_20', 'body', 6277, -7412]);
  atTick(9100049738, 35992, [971.533, 'lane:bot', 'illusion', 5845, -5140]);
  atTick(9097037811, 34658, [908.567, 'good_8', 'illusion', -4882, 284]);
  atTick(9099209663, 41488, [1163.1, 'lane:top', 'illusion', -5896, 5552]);
  assert.equal(match(9100049738).firstHeld.manta, 950.333); // main slot, not the cast or first illusion kill
  assert.equal(match(9098402606).firstHeld.bfury, 723.167); assert.equal(match(9097559249).firstHeld.bfury, undefined);
});

test('camp of origin is not nearest death point; partial and conflicting entity matches stay unresolved', () => {
  atTick(9097801447, 21234, [444.233, 'good_2', 'body', 3754, -4318]); // killed closer to good_1
  atTick(9097801447, 23112, [506.833, '?', 'body', null, null]); // same-name wolves from good_7 AND good_15
  atTick(9097037811, 24080, [555.967, '?', 'body', -6366, 7179]); // unique split golem, no spawner
  atTick(9096845332, 23868, [574.833, 'lane:?', 'body', null, null]); // same-name creeps from different lanes
  atTick(9097037811, 27260, [661.967, 'lane:?', 'body', null, null]); // enemy siege, no matched lane entity
  atTick(9099283649, 33288, [836.367, 'evil_15', 'body', null, null], [836.367, 'evil_15', 'body', -2562, 4025],
    [836.367, 'evil_15', 'body', null, null]); // common spawner, only one unambiguous individual position
});

test('body traces break on death/jump/frozen clock; game time excludes the pause rather than losing farm events', () => {
  for (const { trace } of study.matches) {
    assert.equal(trace[0]?.[4], 1);
    trace.forEach(([t, x, y, alive, br], i) => {
      assert.ok(t >= 300 && t < 1200 && Number.isInteger(x) && Number.isInteger(y));
      assert.ok([0, 1].includes(alive) && [0, 1].includes(br));
      if (i) {
        const prev = trace[i - 1]; assert.ok(t >= prev[0]);
        if (t === prev[0] || alive !== prev[3]) assert.equal(br, 1);
      }
    });
  }
  const tracePoint = (id: number, t: number) => match(id).trace.find(p => p[0] === t);
  assert.deepEqual(tracePoint(9098472218, 301.567), [301.567, 5245, -5662, 1, 1]); // jump from the fountain
  assert.deepEqual(tracePoint(9097587313, 408.1), [408.1, 6427, -3425, 0, 1]);
  // OD disconnect/reconnect at 829/830 belonged to Radiant slot 2, NOT the selected Dire Troll (slot 129).
  // Raw pause [32052,33088): 1036 replay ticks, zero elapsed game seconds. No fixed duplicate count required.
  assert.deepEqual(tracePoint(9099283649, 829.7), [829.7, -2959, 5592, 1, 1]);
  atTick(9099283649, 31912, [825.033, 'lane:top', 'body', -5068, 5672], [825.033, 'lane:top', 'body', -4671, 5771]);
  const clockAt = (tick: number) => match(9099283649).events.find(e => e[5] === tick)![0];
  near(clockAt(33288) - clockAt(31912), (33288 - 31912 - 1036) / 30, 0.001);
  atTick(9097559249, 26506, [670.533, 'lane:top', 'body', -5922, 5189]); // death exactly at another pause's first tick
});
