export const RULES = {
  agilityPerArmor: 6, universalDamage: 0.45,
  healthRegenPerStrength: 0.1, manaRegenPerInt: 0.05,
  minAttackSpeed: 20, maxAttackSpeed: 700, minMoveSpeed: 100, maxMoveSpeed: 550,
  horizon: 180, maxClearSeconds: 90, minHealthFraction: 0.3,
  // Bound search work per time bucket, not the number of camps in the output.
  routeBucketSeconds: 5, routeBeamWidth: 12,
  staleAfterMs: 8_000, replanMs: 10_000,
} as const;
export const clamp = (n: number, min: number, max: number) => Math.min(max, Math.max(min, n));
export function physicalMultiplier(armor: number): number {
  return 1 - 0.06 * armor / (1 + 0.06 * Math.abs(armor));
}
export function levelValue(values: number[] | undefined, level = 1): number {
  return values?.[Math.min(Math.max(level - 1, 0), values.length - 1)] ?? 0;
}
