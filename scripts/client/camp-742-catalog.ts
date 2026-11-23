/** Observed spawn batches ≠ spawn probabilities. Gross entity bounty fields ≠ recipient credit. */
import assert from 'node:assert/strict';
import { replayArea } from '../../src/replays/storage.ts';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import { gameTimeAt } from '../../src/replays/farming-routes.ts';
import { details, facts, packs, read, roster, rounded, routes, sameBuild, save } from './camp-742-facts.ts';

const data = read('data/farming-742-camp-units.json'), upgradeStep = Number(data.mechanics.neutral_upgrade.increase_time);
const comps = new Map<string, { units: string[]; observations: number; matches: Set<number>; spawnerTypes: Set<number>; witness: any }>();
const bonus = new Map<number, { offsets: Set<string>; names: Set<string>; ids: Set<string>; builds: Set<number>;
  firstClock: number; lastClock: number; snapshots: number; witness: any }>();
const samples = [];
const noteBytes = fs.readFileSync(`${replayArea('farm742')}/patch.json`), notes = JSON.parse(noteBytes.toString('utf8'));
const notesSha256 = createHash('sha256').update(noteBytes).digest('hex');
assert(notes.success && notes.patch_number === '7.42' && notes.patch_timestamp === routes.patch.from &&
  routes.patch.version === '7.42' && routes.patch.notesSha256 === notesSha256);
assert.deepEqual(routes.matches.filter((m: any) => m.build === 6989).map((m: any) => m.id).sort(), sameBuild);
for (const m of [...routes.matches].sort((a, b) => a.id - b.id)) {
  const f = facts(m), spawned = packs(f);
  const pin = data.builds.find((b: any) => b.build === m.build), sourcePin = routes.builds.find((b: any) => b.build === m.build);
  assert(pin && sourcePin && pin.commit === sourcePin.commit && pin.steamInfSha256 === sourcePin.sha256 &&
    m.start >= notes.patch_timestamp && (!routes.patch.until || m.start < routes.patch.until));
  samples.push({ matchId: m.id, build: m.build, demSha256: m.demSha256, spawnBatches: spawned.length });
  for (const p of spawned) {
    const names = roster(p.units), key = names.join('+');
    assert(names.every(s => data.units[`npc_dota_neutral_${s}`]));
    if (!comps.has(key)) comps.set(key, { units: names, observations: 0, matches: new Set(), spawnerTypes: new Set(),
      witness: { matchId: m.id, camp: p.camp, tick: p.spawnTick, seconds: rounded(p.spawnSecond),
        spawnerHandle: p.handle, ids: [...p.units].sort((a, b) => a.unitName.localeCompare(b.unitName, 'en')).map(u => u.id) } });
    const c = comps.get(key)!; c.observations++; c.matches.add(m.id); c.spawnerTypes.add(p.spawnerType);
  }
  const d = details(f), entities = new Map<string, any>(f.creatures.map((u: any) => [u.id, u]));
  for (const [tick, id, name, handle, hp, low, high] of d.census) {
    const u = entities.get(id), def = data.units[name];
    assert(u?.unitName === name && u.spawnerHandle === handle && u.spawnedTick <= tick &&
      (u.diedTick == null || tick <= u.diedTick));
    assert(def?.abilities.includes('neutral_upgrade') && Number.isInteger(low) && Number.isInteger(high));
    const t = gameTimeAt(f.clock, tick), tier = Math.floor(t / upgradeStep);
    // Sample well clear of clock edges, not the moment an upgrade is being applied.
    assert(t % 30 >= 4 && t % 30 < 7 && t > 60);
    const offset = `${low - def.gold[0]},${high - def.gold[1]}`;
    if (!bonus.has(tier)) bonus.set(tier, { offsets: new Set(), names: new Set(), ids: new Set(), builds: new Set(), snapshots: 0, firstClock: t, lastClock: t,
      witness: { matchId: m.id, id, name, tick, seconds: rounded(t), hp, rolled: [low, high] } });
    const b = bonus.get(tier)!; b.offsets.add(offset); b.names.add(name.slice(17)); b.ids.add(`${m.id}:${id}`); b.builds.add(m.build);
    b.firstClock = Math.min(b.firstClock, t); b.lastClock = Math.max(b.lastClock, t); b.snapshots++;
  }
}
const compositions = [...comps.entries()].sort(([a], [b]) => a.localeCompare(b, 'en')).map(([, c]) => ({
  units: c.units, observations: c.observations, matchCount: c.matches.size, spawnerTypes: [...c.spawnerTypes].sort(), witness: c.witness }));
const bountyTiers = [...bonus].sort(([a], [b]) => a - b).map(([tier, b]) => {
  assert.equal(b.offsets.size, 1, `Conflicting gross gold fields for tier ${tier}: ${[...b.offsets]}`);
  return { tier, offset: [...b.offsets][0].split(',').map(Number), censusRows: b.snapshots, distinctEntities: b.ids.size,
    firstCensusClock: rounded(b.firstClock), lastCensusClock: rounded(b.lastClock),
    checkedNames: [...b.names].sort(), checkedBuilds: [...b.builds].sort(), witness: b.witness };
});
const batches = compositions.reduce((n, c) => n + c.observations, 0);
save('catalog', { patch: '7.42', patchEvidence: { valvePage: routes.patch.notes, capturedNotesSha256: notesSha256, from: notes.patch_timestamp },
  rosterWindow: 'Natural spawner batches at minute waves 1..20 inclusive; not clear counts or odds.',
  goldBasis: 'Entity m_iGoldBountyMin/Max; every 30s at :05/:35, all live naturally spawned neutrals on team 4, all sixteen full DEMs. No stack split or payment implied.',
  samples, batches, compositions, bountyTiers });
console.log(`${samples.length} matches / ${batches} spawn batches / ${compositions.length} compositions.`,
  'Empirical bounty offsets:', bountyTiers.map(b => [b.tier, b.offset, b.distinctEntities]));
