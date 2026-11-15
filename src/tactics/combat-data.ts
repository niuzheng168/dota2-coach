import fs from 'node:fs';
import type { CombatData, Composition, MapData } from './types.ts';

export const COMBAT = JSON.parse(fs.readFileSync(new URL('../../data/dota2-combat.json', import.meta.url), 'utf8')) as CombatData;
export const MAP = JSON.parse(fs.readFileSync(new URL('../../data/dota2-map.json', import.meta.url), 'utf8')) as MapData;
const compositions = JSON.parse(fs.readFileSync(new URL('../../data/dota2-camp-compositions.json', import.meta.url), 'utf8'));
export const COMPOSITIONS: Composition[] = compositions.camps.map((c: Composition) => ({
  ...c, units: Object.fromEntries(Object.entries(c.units).map(([key, n]) => [`npc_dota_neutral_${key}`, n])),
}));
if (COMBAT.patch !== MAP.patch || compositions.patch !== MAP.patch) {
  throw new Error('Jungle combat, map and composition snapshots must use the same patch');
}
