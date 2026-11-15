import fs from 'node:fs';
import type { CombatData, Composition, MapData } from './types.ts';

export const COMBAT = JSON.parse(fs.readFileSync(new URL('../../data/dota2-combat.json', import.meta.url), 'utf8')) as CombatData;
export const MAP = JSON.parse(fs.readFileSync(new URL('../../data/dota2-map.json', import.meta.url), 'utf8')) as MapData;
const flooded = JSON.parse(fs.readFileSync(new URL('../../data/dota2-flooded-camps.json', import.meta.url), 'utf8'));
if (flooded.patch !== MAP.patch) throw new Error('Flooded camp patch mismatch');
for (const [id, evolution] of Object.entries(flooded.camps)) {
  const camp = MAP.camps.find(c => c.id === id);
  if (!camp) throw new Error(`Unknown flooded camp ${id}`);
  camp.flooded = evolution as NonNullable<typeof camp.flooded>;
  camp.label = camp.label.replace(/中野|小野/, '水生营地');
}
const compositions = JSON.parse(fs.readFileSync(new URL('../../data/dota2-camp-compositions.json', import.meta.url), 'utf8'));
export const COMPOSITIONS: Composition[] = compositions.camps.map((c: Composition) => ({
  ...c, units: Object.fromEntries(Object.entries(c.units).map(([key, n]) => [`npc_dota_neutral_${key}`, n])),
}));
if (COMBAT.patch !== MAP.patch || compositions.patch !== MAP.patch) {
  throw new Error('Jungle combat, map and composition snapshots must use the same patch');
}
