import { COMPOSITIONS } from './combat-data.ts';
import type { Camp, CampKind, Composition } from './types.ts';

const melee = ['tadpole', 'froglet', 'grown_frog', 'ancient_frog'];
const mage = ['tadpole', 'froglet_mage', 'grown_frog_mage', 'ancient_frog_mage'];

// Enumerate possible upgrade orders, not a fictitious ordinary medium pack.
// Camp state is still assumed unblocked; this is NOT a live spawn observation.
export function campCompositions(kind: CampKind, clock: number, camp?: Camp): Composition[] {
  if (!camp?.flooded) return COMPOSITIONS.filter(c => c.kind === kind);
  const { initialTier, maxTier } = camp.flooded;
  const upgrades = Math.min((maxTier - initialTier) * 3, Math.max(0, Math.floor(clock / 300)));
  const base = initialTier + Math.floor(upgrades / 3), remainder = upgrades % 3;
  const result = new Map<string, Composition>();
  for (let mask = 0; mask < 8; mask++) {
    if (mask.toString(2).replaceAll('0', '').length !== remainder) continue;
    const units: Record<string, number> = {};
    for (let i = 0; i < 3; i++) {
      const tier = Math.min(maxTier, base + ((mask >> i) & 1));
      const name = `npc_dota_neutral_${(i === 0 ? mage : melee)[tier]}`;
      units[name] = (units[name] || 0) + 1;
    }
    const key = JSON.stringify(Object.entries(units).sort());
    result.set(key, { id: `${camp.id}:evolution-${upgrades}:${mask}`, kind, units });
  }
  return [...result.values()];
}
