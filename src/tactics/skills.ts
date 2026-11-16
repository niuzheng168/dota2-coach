import { levelValue } from './rules.ts';
import type { AbilityData, AbilityState, DamageType } from './types.ts';

// Reviewed mechanics only. A field containing "damage" is NOT enough to infer an effect.
const BURSTS: Record<string, { field?: string; targets: number }> = {
  luna_lucent_beam: { field: 'beam_damage', targets: 1 },
  sven_storm_bolt: { targets: 3 },
  lina_dragon_slave: { field: 'dragon_slave_damage', targets: 5 },
  lina_light_strike_array: { field: 'light_strike_array_damage', targets: 3 },
  crystal_maiden_crystal_nova: { field: 'nova_damage', targets: 5 },
  dragon_knight_breathe_fire: { field: 'damage', targets: 5 },
  centaur_hoof_stomp: { field: 'stomp_damage', targets: 4 },
  magnataur_shockwave: { field: 'shock_damage', targets: 5 },
  ursa_earthshock: { targets: 4 },
};
export function isModeledAbility(name: string): boolean {
  return Object.hasOwn(BURSTS, name) ||
    ['sven_great_cleave', 'luna_moon_glaive', 'luna_lunar_blessing', 'special_bonus_unique_luna_7',
      'ursa_fury_swipes', 'axe_counter_helix'].includes(name);
}
export function burstAbility(ability: AbilityState, data: AbilityData | undefined):
  { damage: number; type: DamageType; targets: number; mana: number; cooldown: number; castPoint: number } | null {
  const model = Object.hasOwn(BURSTS, ability.name) ? BURSTS[ability.name] : undefined;
  if (!model || !data) return null;
  const damage = levelValue(model.field ? data.values[model.field] : data.damage, ability.level);
  const cooldown = levelValue(data.cooldown.length ? data.cooldown : data.values.AbilityCooldown, ability.level);
  const mana = levelValue(data.mana.length ? data.mana : data.values.AbilityManaCost, ability.level);
  if (!(damage > 0) || !(cooldown > 0) || !(mana > 0)) return null;
  return { damage, type: data.damageType, targets: model.targets, mana, cooldown,
    castPoint: levelValue(data.castPoint.length ? data.castPoint : data.values.AbilityCastPoint, ability.level) || 0.3 };
}
