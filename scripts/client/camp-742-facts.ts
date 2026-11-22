/** Offline audit plumbing shared only by the 7.42 camp research scripts. */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { gameTimeAt } from '../../src/replays/farming-routes.ts';

export const read = (f: string) => JSON.parse(fs.readFileSync(f, 'utf8'));
export const routes = read('data/farming-742-routes.json');
export const sameBuild = [9099209663, 9099283649, 9100049738]; // ALL installed-build matches in the original sixteen
export const rounded = (v: number) => Math.round(v * 1000) / 1000;
export function group<K, V>(rows: V[], key: (v: V) => K): Map<K, V[]> {
  const m = new Map<K, V[]>();
  for (const v of rows) { const k = key(v); if (!m.has(k)) m.set(k, []); m.get(k)!.push(v); }
  return m;
}
export function save(name: string, data: unknown) {
  const f = `data/farming-742-camp-${name}.json`, text = JSON.stringify(data, null, 2) + '\n';
  if (process.argv.includes('--check')) assert.equal(fs.readFileSync(f, 'utf8'), text, f);
  else fs.writeFileSync(f, text);
}
export function facts(m: any) {
  const f = read(`.tmp/farm742/${m.id}.facts.json`);
  assert(f.schemaVersion === 4 && f.complete && f.matchId === m.id && f.gameBuild === m.build &&
    f.demSha256 === m.demSha256 && f.postGameTick === f.frames.at(-1).tick && f.frames.at(-1).gameTime > 1200);
  return f;
}
export function details(f: any) {
  const d = read(`.tmp/farm742-camps/${f.matchId}.damage.json`);
  assert(d.matchId === f.matchId && d.build === f.gameBuild && d.demSha256 === f.demSha256 &&
    d.endTick === f.postGameTick && d.bodyId === f.heroPath[0].id && d.hero === f.heroPath[0].unitName &&
    d.team === f.heroPath[0].team && gameTimeAt(f.clock, d.lastTick) > 1201);
  return d;
}
export interface Creature {
  id: string; class: string; unitName: string; team: number; spawnerHandle: number;
  spawnedTick: number | null; spawnedGameTime: number; diedTick: number | null; diedGameTime: number;
}
export interface Pack { camp: string; handle: number; spawnTick: number; spawnSecond: number; spawnerType: number; units: Creature[] }
export const roster = (us: Creature[]) => us.map(u => u.unitName.replace(/^npc_dota_neutral_/, '')).sort();
export function packs(f: any): Pack[] {
  const map = new Map<number, any>();
  for (const s of f.spawners) {
    // Independent box+index bijection; never choose the camp nearest the hero or the kill.
    const box = f.camps.filter((c: any) => c.min[0] <= s.position.x && s.position.x <= c.max[0] &&
      c.min[1] <= s.position.y && s.position.y <= c.max[1]);
    assert.equal(box.length, 1); assert([s.index, 2 * s.index].includes(box[0].spawnerIndex));
    map.set(s.index, { ...s, camp: box[0].id.replace(/^neutralcamp_/, '') });
  }
  assert.equal(map.size, 28); assert.equal(new Set([...map.values()].map(s => s.camp)).size, 28);
  const us = (f.creatures as Creature[]).filter(u => u.class === 'CDOTA_BaseNPC_Creep_Neutral' && u.team === 4 &&
    u.spawnedTick != null && u.spawnedGameTime > 0 && u.spawnedGameTime < 1201 &&
    Number.isInteger(u.spawnerHandle) && u.spawnerHandle !== 0xffffff);
  const groups = group(us, u => `${u.spawnerHandle}:${Math.round(u.spawnedGameTime / 60)}`);
  return [...groups.values()].map(units => {
    const { spawnerHandle: handle, spawnedTick: tick, spawnedGameTime: t } = units[0], s = map.get(handle % 16384);
    assert(s && tick != null && Math.abs(t - Math.round(t / 60) * 60) < 1, 'Unexpected/non-wave natural spawn');
    assert(units.every(u => u.spawnedTick === tick && u.unitName.startsWith('npc_dota_neutral_')), 'Unresolved spawn batch');
    return { camp: s.camp, handle, spawnTick: tick, spawnSecond: t,
      spawnerType: s.types.findLast((row: any) => row.tick <= tick).type, units };
  });
}
