/** Offline only. Rebuild the D2PT Luna/Troll study from its captured catalogs and Valve replays. */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { replayArea } from '../../src/replays/storage.ts';
import { gameTimeAt, recordedTrace, type TracePoint } from '../../src/replays/farming-routes.ts';

const root = replayArea('farm742');
const output = 'data/farming-742-routes.json';
const read = (file: string, directory = root) => {
  const raw = fs.readFileSync(path.join(directory, file), 'utf8');
  try { return JSON.parse(raw); }
  catch { throw new Error(`Invalid or incomplete ${file}; finish acquisition/extraction before building`); }
};
const sha = (data: Buffer | string) => createHash('sha256').update(data).digest('hex');
const hashFile = (file: string, directory = root) => sha(fs.readFileSync(path.join(directory, file)));
const ms = (n: number) => Math.round(n * 1000) / 1000;
const shortId = (id: string) => id.replace(/^neutralcamp_/, '');
export const heroes = { 48: 'luna', 95: 'troll_warlord' } as const;
type Hero = keyof typeof heroes;
export type Hero742 = typeof heroes[Hero];
export type Team742 = 'radiant' | 'dire';
export type Point742 = { t: number; lane: number; neutral: number; lh: number };
export type Hit742 = [time: number, origin: string, actor: 'body' | 'illusion' | '?',
  deathX: number | null, deathY: number | null, tick: number];
export type Row742 = { match_id: number; hero_id: Hero; name: string; account_id: number; is_radiant: number;
  won: number; mmr: number; last_hits: number; duration: number; activate_time: number; position: string; patch: string; lobby_type: number };
type Build742 = { build: number; asOf: string; text: string; sha256: string; commit: string; url: string };
const mainItems = new Set(['power_treads', 'phase_boots', 'mask_of_madness', 'yasha', 'manta', 'bfury',
  'maelstrom', 'mjollnir', 'sange_and_yasha', 'black_king_bar']);
export const endpoints = [300, 600, 900, 1200] as const;

function catalog(hero: Hero) {
  const file = `catalog-${hero}.json`;
  const feed: any[] = read(file);
  const pubs = feed.map(row => typeof row.data === 'string' ? JSON.parse(row.data) : row.data)
    .filter(r => r.patch === '7.42' && r.lobby_type === 7 && r.position === 'pos 1' && r.hero_id === hero);
  assert.equal(new Set(pubs.map(r => r.match_id)).size, pubs.length);
  const pool = pubs.filter(r => Number.isInteger(r.mmr) && r.mmr >= 7500 && r.account_id > 0)
    .sort((a, b) => b.mmr - a.mmr || b.match_id - a.match_id);
  const picks = [1, 0].flatMap(side => {
    const seen = new Set<number>();
    return pool.filter(r => {
      if (r.is_radiant !== side || seen.has(r.account_id)) return false;
      seen.add(r.account_id); return true;
    }).slice(0, 4);
  });
  assert.equal(picks.length, 8, 'Need four distinct accounts per hero/side');
  return { picks, source: { heroId: hero,
    url: `https://dota2protracker.com/api/hero-matches?hero_id=${hero}&position=pos+1&sort_by=mmr`,
    sha256: hashFile(file), feedRows: feed.length, patchRankedCarryRows: pubs.length,
    // Complete ranking pool (including skipped repeats), for auditing the cut without the live feed.
    ranking: pool.map(r => [r.match_id, r.mmr, r.is_radiant, r.account_id]) } };
}

function geometry(f: any) {
  const byIndex = new Map<number, any>(f.spawners.map((s: any) => [s.index, s]));
  assert.equal(f.camps.length, 28); assert.equal(byIndex.size, 28);
  // Learn the handle encoding against *this* replay: a whole-map bijection,
  // with the right initial tiers and each spawner inside its own named spawn box.
  const divisors = [1, 2].filter(div => f.camps.every((c: any) => {
    const s = byIndex.get(c.spawnerIndex / div), p = s?.position;
    return p && s.types[0]?.type === c.type && c.min[0] <= p.x && p.x <= c.max[0] &&
      c.min[1] <= p.y && p.y <= c.max[1];
  }));
  assert.equal(divisors.length, 1);
  const ids = new Map<number, string>();
  const camps = f.camps.map((c: any) => {
    const index = c.spawnerIndex / divisors[0], s = byIndex.get(index), id = shortId(c.id);
    ids.set(index, id);
    return { id, initialTier: c.type, x: Math.round(s.position.x), y: Math.round(s.position.y),
      box: [c.min[0], c.min[1], c.max[0], c.max[1]].map(Math.round) };
  }).sort((a: any, b: any) => a.id.localeCompare(b.id, 'en'));
  assert.equal(ids.size, 28); assert.equal(new Set(ids.values()).size, 28);
  const towers = f.towers.map((t: any) => ({ id: t.name, team: t.team,
    x: Math.round(t.position.x), y: Math.round(t.position.y) })).sort((a: any, b: any) => a.id.localeCompare(b.id, 'en'));
  assert.equal(new Set(towers.map((t: any) => t.id)).size, 22);
  return { ids, landmarks: { camps, towers } };
}

// Which lane a creep spawned for, NOT a guess from the hero's position. Keep
// pulled creeps attached to their spawn lane, and missing/ambiguous names unknown.
function spawnLane(unit: any, t3: any[]) {
  const p = unit.spawnPosition;
  if (!p) return null;
  const ranked = t3.filter(t => t.team === unit.team)
    .map(t => ({ lane: t.id.split('_').at(-1), d: Math.hypot(t.x - p.x, t.y - p.y) })).sort((a, b) => a.d - b.d);
  return ranked.length === 3 && ranked[0].d < 1000 && ranked[1].d - ranked[0].d > 500 ? ranked[0].lane : null;
}

// Both studies use this path, including the original same-tick attribution and trace breaks.
// Short games may omit *unreached* endpoints, never a missing sample inside a covered window.
export function compact(r: Row742, patch: { from: number; until: number | null }, builds: Build742[],
  { directory = root, allowShort = false } = {}) {
  const id = r.match_id, hero = heroes[r.hero_id], npc = `npc_dota_hero_${hero}`;
  const api = read(`${id}.opendota.json`, directory), acq = read(`${id}.acquisition.json`, directory);
  const f = read(`${id}.facts.json`, directory), team = r.is_radiant ? 2 : 3;
  const player = api.players.find((p: any) => p.hero_id === r.hero_id);
  const playerId = (slot: number) => slot < 128 ? slot : slot - 128 + 5;
  assert(f.complete === true && f.schemaVersion === 4 && f.postGameTick > 0 && f.matchId === id && api.match_id === id && acq.matchId === id, 'Replay completeness/schema/ID mismatch');
  assert(f.parser?.name === 'gem-dota' && f.parser.version === '0.14.0', 'Unexpected facts parser');
  assert(api.patch === 61 && api.lobby_type === 7 && api.start_time >= patch.from && (!patch.until || api.start_time < patch.until), 'OpenDota patch/lobby/start outside 7.42');
  assert(api.start_time === r.activate_time && Math.abs(api.duration - r.duration) < 3, 'Catalog start/duration mismatch');
  assert(builds.some(b => b.build === f.gameBuild && Date.parse(b.asOf) / 1000 <= api.start_time) &&
    Math.abs(f.durationSeconds - api.duration) < 3, 'Replay build/duration mismatch');
  assert.equal(acq.metadataSha256, hashFile(`${id}.opendota.json`, directory)); assert.equal(acq.demSha256, f.demSha256);
  assert.match(f.demSha256, /^[a-f0-9]{64}$/); assert.equal(api.replay_url, acq.replayUrl);
  assert(player && (player.player_slot < 128) === !!r.is_radiant && player.last_hits === r.last_hits);
  if (player.account_id != null) assert.equal(player.account_id, r.account_id);
  assert.equal(!!r.won, (team === 2) === api.radiant_win);
  assert(f.heroPath.length && f.heroPath.every((h: any) => h.team === team && h.name === npc && h.playerIdField === playerId(player.player_slot)));
  const final = f.frames.at(-1);
  assert.equal(final.tick, f.postGameTick); assert.equal(final.gameTime, f.durationSeconds);
  assert.deepEqual(api.players.map((p: any) => p.player_slot).sort((a: number, b: number) => a-b), [0,1,2,3,4,128,129,130,131,132]);
  for (const p of api.players) {
    assert(Number.isSafeInteger(p.last_hits) && p.last_hits >= 0);
    assert.equal(final.heroes.find((h: any) => h.playerIdField === playerId(p.player_slot))?.economy?.m_iLastHitCount, p.last_hits, `Terminal LH mismatch in slot ${p.player_slot}`);
  }
  const { ids, landmarks } = geometry(f);
  const t3 = landmarks.towers.filter((t: any) => /_tower3_(bot|mid|top)$/.test(t.id));
  const deaths = new Map<string, any[]>();
  for (const u of f.creatures) {
    if (u.diedTick == null || !u.unitName) continue;
    const key = `${u.diedTick}:${u.unitName}`;
    if (!deaths.has(key)) deaths.set(key, []);
    deaths.get(key)!.push(u);
  }
  const teamName = team === 2 ? 'badguys' : 'goodguys'; // enemy creeps only; no denies
  const laneRe = new RegExp(`^npc_dota_(?:creep_${teamName}_|${teamName}_siege)`);
  const logs = hero === 'luna' ? f.lunaCombatLog : f.trollCombatLog;
  const events: Hit742[] = [];
  for (const e of logs) {
    if (e.log_type !== 'DEATH' || e.attacker_name !== npc || e.attacker_team !== team) continue;
    const neutral = e.target_name?.startsWith('npc_dota_neutral_') && e.target_team === 4;
    if (!neutral && (e.target_team !== (team === 2 ? 3 : 2) || !laneRe.test(e.target_name))) continue;
    const time = gameTimeAt(f.clock, e.tick);
    if (time < 0 || ms(time) >= 1200 || e.tick > f.postGameTick) continue;
    const matches = deaths.get(`${e.tick}:${e.target_name}`) ?? [];
    const locations = matches.map(u => neutral ? ids.get(u.spawnerHandle & 0x3fff) : spawnLane(u, t3));
    const location = locations.length && locations[0] && locations.every(k => k === locations[0]) ? locations[0] : '?';
    const pos = matches.length === 1 ? matches[0].deathPosition : null;
    events.push([ms(time), neutral ? location : `lane:${location}`,
      !e.attacker_is_illusion_present ? '?' : e.attacker_is_illusion ? 'illusion' : 'body',
      pos ? Math.round(pos.x) : null, pos ? Math.round(pos.y) : null, e.tick]);
  }
  events.sort((a, b) => a[5] - b[5]);
  const firstHeld: Record<string, number> = {}, points: Point742[] = [];
  let startPoint: Point742 | undefined, endPoint: Point742 | undefined;
  const point = (frame: any, e: any): Point742 => {
    const counters = [e.m_iCreepKillGold, e.m_iNeutralKillGold, e.m_iLastHitCount];
    assert(counters.every(n => Number.isSafeInteger(n) && n >= 0), 'Missing replay economy counters');
    return { t: ms(frame.gameTime), lane: counters[0], neutral: counters[1], lh: counters[2] };
  };
  for (const frame of f.frames) {
    if (frame.gameTime < 0) continue;
    const h = frame.heroes.find((h: any) => h.name === npc && h.playerIdField === playerId(player.player_slot));
    assert(h);
    if (!startPoint) {
      assert(frame.gameTime < 1.2, 'Missing start sample');
      startPoint = point(frame, h.economy);
    }
    if (frame === final) endPoint = point(frame, h.economy);
    for (const item of h.items) {
      const key = item.name?.replace(/^item_/, '');
      if (item.slot <= 5 && mainItems.has(key) && firstHeld[key] == null) firstHeld[key] = ms(frame.gameTime);
    }
    const boundary = endpoints[points.length];
    if (boundary != null && frame.gameTime >= boundary) {
      assert(frame.gameTime - boundary < 1.2, 'Missing untruncated boundary');
      points.push(point(frame, h.economy));
    }
  }
  assert(startPoint && endPoint);
  assert.equal(points.length, allowShort ? endpoints.filter(t => t <= f.durationSeconds).length : endpoints.length);
  const allPoints = [startPoint, ...points, endPoint];
  for (const [i, p] of allPoints.entries()) if (i) {
    const prev = allPoints[i-1];
    assert(p.t >= prev.t && p.lane >= prev.lane && p.neutral >= prev.neutral && p.lh >= prev.lh, 'Nonmonotonic counters/clock');
  }
  const trace = recordedTrace(f.heroPath, 2).filter(([t]) => t >= 300 && t < 1200)
    .map(([t, x, y, alive, br], i): TracePoint => [t, Math.round(x), Math.round(y), alive, i === 0 ? 1 : br]);
  const side: Team742 = team === 2 ? 'radiant' : 'dire';
  return { landmarks, startPoint, endPoint, extractionScope: String(f.extractionScope ?? 'full'), postGameTick: Number(f.postGameTick),
    match: { id, hero, team: side, player: r.name, accountId: r.account_id,
    mmr: r.mmr, won: !!r.won, start: api.start_time, duration: ms(f.durationSeconds), build: f.gameBuild, finalLH: player.last_hits,
    url: `https://dota2protracker.com/matches/${id}`, replay: acq.replayUrl, demSha256: f.demSha256, metadataSha256: acq.metadataSha256,
    points, firstHeld, events, trace } };
}

export function patchEvidence742(directory = root, buildsDirectory = directory) {
  const patches: { patch_number: string; patch_timestamp: number }[] = read('patch-list.json', directory).patches;
  const from = patches.find(p => p.patch_number === '7.42')?.patch_timestamp, notes = read('patch.json', directory);
  assert(from && notes.patch_number === '7.42' && notes.patch_timestamp === from, 'Patch list/notes mismatch');
  const patch = { version: '7.42', from, until: patches.map(p => p.patch_timestamp).filter(t => t > from).sort((a,b) => a-b)[0] ?? null,
    listSha256: hashFile('patch-list.json', directory), notesSha256: hashFile('patch.json', directory), notes: 'https://www.dota2.com/patches/7.42' };
  const builds: Build742[] = read('builds.json', buildsDirectory).filter((b: Build742) => Date.parse(b.asOf) / 1000 >= from);
  for (const b of builds) assert(sha(b.text) === b.sha256 && Number(b.text.match(/^ServerVersion=(\d+)/m)?.[1]) === b.build);
  return { patch, builds };
}

function build() {
  const { patch, builds } = patchEvidence742();
  const catalogs = [catalog(48), catalog(95)];
  let map; const matches = [];
  for (const r of catalogs.flatMap(c => c.picks)) {
    const packed = compact(r, patch, builds);
    if (map) assert.deepEqual(packed.landmarks, map, `Map geometry changed in ${r.match_id}`);
    else map = packed.landmarks;
    matches.push(packed.match);
  }
  const result = { version: 1, captureDateUTC: '2026-11-22', scope: 'omniscient-offline-only', patch, builds,
    selection: 'Within each hero and faction: top four distinct accounts by match-average MMR (floor 7500), match ID descending for ties. No win, duration or item filter.',
    sources: catalogs.map(c => c.source), map,
    format: { ranking: ['matchId', 'matchMMR', 'isRadiant', 'accountId'],
      points: 'First samples >= 5/10/15/20 minutes. lane=m_iCreepKillGold (also some enemy summons), neutral=m_iNeutralKillGold (also some neutral summons), lh=last hits. Counters include owned units, not just body kills.',
      events: ['seconds (0–20min)', 'spawn camp ID / lane of origin / ? unresolved', 'body / illusion / ?',
        'victim death X or null', 'victim death Y or null', 'replay tick'],
      trace: ['seconds (5–20min)', 'body X', 'body Y', 'alive', 'break before'],
      firstHeld: 'First second-sampled observation in main slots 0–5, not purchase or delivery time.' }, matches };
  return JSON.stringify(result) + '\n';
}

function summarize(corpus: any) {
  const median = (values: number[]) => {
    const sorted = [...values].sort((a, b) => a - b), i = Math.floor(sorted.length / 2);
    return sorted.length ? (sorted[i] + sorted[(sorted.length - 1) >> 1]) / 2 : null;
  };
  return Object.values(heroes).map(hero => {
    const group = corpus.matches.filter((m: any) => m.hero === hero);
    const windows = [[0, 1], [1, 2], [2, 3], [1, 3]].map(([a, b]) => {
      const delta = group.map((m: any) => {
        const x = m.points[a], y = m.points[b];
        return { lane: y.lane - x.lane, neutral: y.neutral - x.neutral, secs: y.t - x.t };
      });
      return { minutes: [endpoints[a] / 60, endpoints[b] / 60],
        nonzeroBoth: delta.filter((d: any) => d.lane > 0 && d.neutral > 0).length,
        medianFarmPerMinute: median(delta.map((d: any) => 60 * (d.lane + d.neutral) / d.secs)),
        medianLaneShare: median(delta.map((d: any) => d.lane / (d.lane + d.neutral))),
        medianDistinctBodyCamps: median(group.map((m: any) => new Set(m.events
          .filter(([t, site, actor]: [number, string, string]) =>
            t >= endpoints[a] && t < endpoints[b] && actor === 'body' && /^(good|evil)_\d+$/.test(site))
          .map((e: any[]) => e[1])).size)) };
    });
    return { hero, matches: group.length, accounts: new Set(group.map((m: any) => m.accountId)).size,
      medianCumulativeLH: endpoints.map((_, i) => median(group.map((m: any) => m.points[i].lh))), windows };
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === import.meta.filename) {
  if (process.argv.includes('--summary')) {
    console.log(JSON.stringify(summarize(JSON.parse(fs.readFileSync(output, 'utf8'))), null, 2));
  } else {
    const text = build();
    if (process.argv.includes('--check')) assert(fs.readFileSync(output, 'utf8') === text, 'Study snapshot differs');
    else fs.writeFileSync(output, text);
    console.log(`Verified ${output} (${Buffer.byteLength(text)} bytes)`);
  }
}
