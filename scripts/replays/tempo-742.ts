/** Independent evidence tier: a D2PT minute series has no camping or position labels. */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { replayArea } from '../../src/replays/storage.ts';

const here = replayArea('farm742-more'), prior = replayArea('farm742'), out = 'data/farming-742-tempo.json';
const items: Record<number, string> = { 145: 'bfury', 147: 'manta', 166: 'maelstrom', 172: 'mask_of_madness' };
const heroes = { 48: 'luna', 95: 'troll_warlord' } as const;
const sha = (b: Buffer) => createHash('sha256').update(b).digest('hex');
const read = (p: string) => fs.readFileSync(p);

interface TempoMatch {
  id: number; hero: string; team: 'radiant' | 'dire'; accountId: number; player: string; mmr: number;
  won: boolean; start: number; duration: number; finalLH: number; minuteLH: number[];
  itemMinute: Record<string, number>;
}
function describe(values: number[]) {
  if (!values.length) return { n: 0, q25: null, median: null, q75: null };
  const s = [...values].sort((a, b) => a - b);
  const q = (part: number) => {
    const i = part * (s.length - 1);
    return s[Math.floor(i)] + (s[Math.ceil(i)] - s[Math.floor(i)]) * (i % 1);
  };
  return { n: s.length, q25: q(.25), median: q(.5), q75: q(.75) };
}
function group(rows: TempoMatch[]) {
  const covered = rows.filter(r => r.minuteLH.length > 20);
  const gain = (r: TempoMatch) => r.minuteLH[20] - r.minuteLH[10];
  const accounts = new Set(rows.map(r => r.accountId)), byPlayer = new Map<number, number[]>();
  for (const r of covered) {
    const a = byPlayer.get(r.accountId) ?? []; a.push(gain(r)); byPlayer.set(r.accountId, a);
  }
  return { n: rows.length, accounts: accounts.size, won: rows.filter(r => r.won).length,
    radiant: rows.filter(r => r.team === 'radiant').length, dire: rows.filter(r => r.team === 'dire').length,
    mmr: [Math.min(...rows.map(r => r.mmr)), Math.max(...rows.map(r => r.mmr))],
    lh: Object.fromEntries([5, 10, 15, 20].map(min => [min, describe(rows.filter(r => r.minuteLH.length > min).map(r => r.minuteLH[min]))])),
    gain10to20: describe(covered.map(gain)),
    // This is a sensitivity check, not an estimate for a random ladder player.
    gain10to20EachAccountOnce: describe([...byPlayer.values()].map(v => describe(v).median!)),
    items: Object.fromEntries(Object.values(items).map(item => [item, describe(rows.filter(r => Object.hasOwn(r.itemMinute, item)).map(r => r.itemMinute[item]))])) };
}
export function buildTempo() {
  const eligible = JSON.parse(read(`${here}/eligible.json`).toString());
  const rows: TempoMatch[] = [];
  for (const source of eligible.sources) {
    const id = source.heroId as keyof typeof heroes;
    const old = read(`${prior}/catalog-${id}.json`), fresh = source.freshFile ? read(`${here}/catalog-${id}.json`) : old;
    assert(sha(fresh) === source.sha256 && sha(old) === source.previousSha256);
    const records = new Map<number, any>();
    for (const buf of [old, fresh]) for (const raw of JSON.parse(buf.toString())) {
      const r = typeof raw.data === 'string' ? JSON.parse(raw.data) : raw.data; records.set(r.match_id, r);
    }
    for (const r of records.values()) {
      if (r.hero_id !== id || r.position !== 'pos 1' || r.patch !== '7.42' || r.lobby_type !== 7 ||
          !Number.isSafeInteger(r.account_id) || r.account_id <= 0 || !Number.isSafeInteger(r.mmr) || r.mmr < eligible.minimumMatchMmr) continue;
      const serial: number[] = r.time_series_last_hits;
      assert(Array.isArray(serial) && serial.length === Math.floor(r.duration / 60) + 2, `D2PT clock/index coverage ${r.match_id}`);
      const minuteLH = serial.slice(1); // calibrated against 64 actual game-clock boundaries below
      assert(minuteLH.every((v, i) => Number.isSafeInteger(v) && v >= 0 && v <= r.last_hits && (!i || v >= minuteLH[i - 1])));
      const itemMinute: Record<string, number> = {};
      for (const entry of r.items ?? []) {
        const key = items[entry.item_id];
        if (key && Number.isSafeInteger(entry.minute) && entry.minute >= 0)
          itemMinute[key] = Math.min(itemMinute[key] ?? Infinity, entry.minute);
      }
      rows.push({ id: r.match_id, hero: heroes[id], team: r.is_radiant ? 'radiant' : 'dire',
        accountId: r.account_id, player: r.name, mmr: r.mmr, won: !!r.won, start: r.activate_time,
        duration: r.duration, finalLH: r.last_hits, minuteLH, itemMinute });
    }
  }
  rows.sort((a, b) => a.id - b.id);
  assert.deepEqual(rows.map(r => r.id), eligible.rows.map((r: any) => r.match_id).sort((a: number, b: number) => a - b));
  assert.equal(new Set(rows.map(r => r.id)).size, rows.length);
  const pinned = JSON.parse(read('data/farming-742-routes.json').toString());
  const errors = [];
  for (const m of pinned.matches) {
    const row = rows.find(r => r.id === m.id);
    assert(row && row.hero === m.hero && row.team === m.team && row.accountId === m.accountId && row.finalLH === m.finalLH);
    for (const p of m.points) errors.push(Math.abs(row.minuteLH[Math.floor(p.t / 60)] - p.lh));
  }
  assert(errors.length === 64 && Math.max(...errors) <= 1, 'Minute indexing no longer agrees with anchor replays');
  const summary = Object.fromEntries(Object.values(heroes).map(hero => {
    const rs = rows.filter(r => r.hero === hero);
    return [hero, { all: group(rs), high7500: group(rs.filter(r => r.mmr >= 7500)),
      radiant: group(rs.filter(r => r.team === 'radiant')), dire: group(rs.filter(r => r.team === 'dire')) }];
  }));
  return { version: 1, patch: '7.42', scope: 'd2pt-directory-minute-telemetry-NOT-route-evidence', selection: eligible.selection,
    sources: eligible.sources, definition: { minuteLH: 'D2PT time_series_last_hits[minute+1] at each completed minute, includes controlled units; finalLH is separate.',
      itemMinute: 'Earliest item minute in the D2PT item list; not verified purchase, delivery or activation time.',
      summaries: 'Per-match quantiles with linear interpolation; incomplete windows are not extrapolated. No lane/neutral split, location or ingame safety label.' },
    replayCrossCheck: { matches: pinned.matches.length, boundaries: errors.length, exact: errors.filter(e => e === 0).length, maxLHError: Math.max(...errors) },
    summary, matches: rows };
}
if (process.argv[1] && path.resolve(process.argv[1]) === import.meta.filename) {
  const tempo = buildTempo(), text = JSON.stringify(tempo) + '\n';
  if (process.argv.includes('--check')) assert.equal(sha(read(out)), sha(Buffer.from(text)));
  else fs.writeFileSync(out, text);
  console.log(`D2PT tempo: ${tempo.matches.length} distinct ranked carries; ${tempo.replayCrossCheck.exact}/64 LH checkpoints exactly match replays`);
}
