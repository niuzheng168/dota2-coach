/** Offline, physical right-click scenarios only. No imports from the 7.41f simulator or route planner. */
import raw from '../../data/farming-742-camp-units.json' with { type: 'json' };
import catalog from '../../data/farming-742-camp-catalog.json' with { type: 'json' };

export type Range742 = [number, number];
export type Coverage742 = 'all' | 'none' | number[][]; // row i: indices reachable from / behind target i
export interface Attacker742 {
  hero: 'luna' | 'troll_warlord' | 'generic';
  /** Current total white + green attack, BEFORE enemy armor and Quell. Includes ordinary item/attribute bonuses. */
  damage: Range742;
  /** 100-based total IAS with fixed items and AGI, BEFORE Fervor and active Madness. Not attacks/sec. */
  attackSpeed: number;
  mode?: 'melee' | 'ranged'; bat?: number; // generic needs both; Troll needs mode, uses its stance KV
  weapon: 'none' | 'quelling_blade' | 'bfury'; // ordinary BF only; passive +50 already in damage
  glaives?: number; glaiveTalent?: boolean;
  fervor?: number; fervorTalent?: boolean; rage?: number; // fresh target, zero carried stacks
  madnessRemaining?: number; // actual remaining seconds at first impact, not inferred from inventory
}
export interface CampInput742 {
  patch: '7.42'; clock: number; // game seconds AT FIRST LANDED DAMAGE
  scope: 'full-unstacked' | 'listed-units'; // both refer to full-health ordinary units, never mixed/stacked payouts
  units: string[]; // exact members, in primary focus order; duplicates are distinct entities
  attacker?: Attacker742;
  policy?: 'uninterrupted-attacks'; // explicitly asserts no other offensive/defensive effects
  formation?: { glaive?: Coverage742; cleave?: Coverage742; hopSeconds?: number };
  engageToFirstHitSeconds?: number; // if not supplied, no contact→empty estimate
}
export interface CampEstimate742 {
  status: 'scenario' | 'bounty-only' | 'unsupported'; tier: number | null;
  bounty: { listed: Range742; wholeCamp: Range742 | null; basis: 'gross-roll-if-killed-in-this-tier' } | null;
  timing: { firstHitToLastDeathSeconds: Range742; engageToLastDeathSeconds: Range742 | null;
    basis: 'uninterrupted-attack-scenarios-not-a-prediction-interval';
    scope: CampInput742['scope']; scenarios: { roll: 'min' | 'max'; rangedMaim: 'none' | 'every'; seconds: number; attacks: number }[] } | null;
  reasons: string[];
}
export interface Neutral742 {
  hp: number; regen: number; armor: number; magicResistPct: number; gold: readonly number[]; xp: number;
  attack: readonly number[]; bat: number; baseAttackSpeed: number; ancient: boolean; abilities: readonly string[];
}
const names = new Set(catalog.compositions.flatMap(c => c.units));
const signature = (s: string[]) => [...s].sort().join('+');
const rosters = new Set(catalog.compositions.map(c => signature(c.units)));
const mechanics: Record<string, Record<string, unknown>> = raw.mechanics;
function val(source: string, key: string, level = 1, field = 'value') {
  const v = mechanics[source][key], text = typeof v === 'object' && v !== null ? (v as Record<string, unknown>)[field] : v;
  const a = typeof text === 'string' ? text.trim().split(/\s+/).map(Number) : [];
  if (!a.length || !a.every(Number.isFinite) || (a.length > 1 && (level > a.length || level < 1))) {
    throw new Error(`Invalid pinned special ${source}.${key}.${field}[${level}]`);
  }
  return a.length === 1 ? a[0] : a[level - 1];
}
const short = (k: string) => k.replace(/^npc_dota_neutral_/, '');
export function neutral742(name: string): Readonly<Neutral742> | null {
  const id = name.startsWith('npc_dota_') ? name : `npc_dota_neutral_${name}`;
  return Object.hasOwn(raw.units, id) ? raw.units[id as keyof typeof raw.units] : null;
}
export const physicalMultiplier742 = (armor: number) => 1 - .06 * armor / (1 + .06 * Math.abs(armor));
const finite = (n: unknown, min: number, max: number): n is number =>
  typeof n === 'number' && Number.isFinite(n) && n >= min && n <= max;
const level = (n: unknown): n is number => Number.isInteger(n) && finite(n, 0, 4);
const keys = (x: unknown, valid: string[]) => x != null && typeof x === 'object' && !Array.isArray(x) &&
  Object.keys(x).every(k => valid.includes(k));
const r3 = (s: number) => Math.round(1000 * s) / 1000;

/** Static pairwise geometry, not pathfinding or a promise that targets remain there. */
export function glaiveLinks742(points: { x: number; y: number }[]): number[][] {
  if (!Array.isArray(points) || !points.every(p => Number.isFinite(p?.x) && Number.isFinite(p?.y))) {
    throw new TypeError('Need finite unit positions');
  }
  const radius = val('luna_moon_glaive', 'range');
  return points.map((p, i) => points.flatMap((q, j) => i !== j && Math.hypot(p.x - q.x, p.y - q.y) <= radius ? [j] : []));
}
function links(n: number, spec: Coverage742 | undefined, required: boolean): number[][] | null {
  if (spec === undefined && required && n > 1) return null;
  if (spec === 'all') return Array.from({ length: n }, (_, i) => Array.from({ length: n }, (_, j) => j).filter(j => j !== i));
  if (spec === undefined || spec === 'none') return Array.from({ length: n }, () => []);
  if (!Array.isArray(spec) || spec.length !== n || spec.some((row, i) => !Array.isArray(row) || new Set(row).size !== row.length ||
    row.some(j => !Number.isInteger(j) || j < 0 || j >= n || j === i))) return null;
  return spec;
}
const unsupportedAura = new Set(['granite_golem_hp_aura', 'black_dragon_dragonhide_aura',
  'enraged_wildkin_toughness_aura', 'satyr_hellcaller_unholy_aura', 'spawnlord_aura']);
const changesRoster = new Set(['mud_golem_rock_destroy', 'dark_troll_warlord_raise_dead']);
const step = val('neutral_upgrade', 'increase_time');
const maxTrial = 120; // computational horizon, never returned as a predicted duration
const baseKeys = ['hero', 'damage', 'attackSpeed', 'mode', 'bat', 'weapon', 'madnessRemaining'];
type State = { hp: number; max: number; armor: number; regen: number };

export function estimateCamp742(input: CampInput742): CampEstimate742 {
  const out: CampEstimate742 = { status: 'unsupported', tier: null, bounty: null, timing: null, reasons: [] };
  const stop = (why: string) => { out.reasons.push(why); return out; };
  if (!keys(input, ['patch', 'clock', 'scope', 'units', 'attacker', 'policy', 'formation', 'engageToFirstHitSeconds']) ||
      input.patch !== '7.42' || !finite(input.clock, 60, Number.MAX_SAFE_INTEGER) ||
      !['full-unstacked', 'listed-units'].includes(input.scope) || !Array.isArray(input.units) ||
      input.units.length === 0 || input.units.length > 32 || !input.units.every(n => typeof n === 'string')) return stop('invalid-or-unknown-camp-context');
  const ns = input.units.map(short);
  if (!ns.every(n => names.has(n))) return stop('not-an-observed-ordinary-spawn-unit');
  if (input.scope === 'full-unstacked' && !rosters.has(signature(ns))) return stop('full-roster-not-in-observed-catalog');
  const tier = Math.floor(input.clock / step), empirical = catalog.bountyTiers.find(r => r.tier === tier);
  // The entity ability does not necessarily update on the exact clock tick. In the DEMs an old tier
  // is still visible at 900.27. Leave a one-second guard on either side, not false precision.
  if (!empirical || input.clock > catalog.bountyTiers.at(-1)!.lastCensusClock ||
      Math.abs(input.clock - Math.round(input.clock / step) * step) <= 1) return stop('unverified-tier-or-upgrade-boundary');
  out.tier = tier;
  const us = ns.map(n => neutral742(n)!), dynamic = us.some(u => u.abilities.some(a => changesRoster.has(a)));
  if (tier === 0 || ns.every(n => empirical.checkedNames.includes(n))) {
    const listed: Range742 = [0, 0];
    for (const u of us) for (const i of [0, 1]) listed[i] += u.gold[i] + empirical.offset[i];
    out.bounty = { listed, wholeCamp: input.scope === 'full-unstacked' && !dynamic ? [...listed] : null,
      basis: 'gross-roll-if-killed-in-this-tier' };
    out.status = 'bounty-only';
  } else out.reasons.push('species-not-checked-in-this-bounty-tier');
  if (dynamic) return stop('split-or-summon-not-in-initial-roster');
  if (!input.attacker) return stop('no-attacker-snapshot');
  if (input.clock >= 1200) return stop('outside-first-20min-timing-study');
  if (input.policy !== 'uninterrupted-attacks') return stop('requires-uninterrupted-physical-attacks-policy');
  if (us.some(u => u.abilities.some(a => unsupportedAura.has(a)))) return stop('unmodeled-defensive-aura');
  if (input.engageToFirstHitSeconds !== undefined && !finite(input.engageToFirstHitSeconds, 0, Number.MAX_SAFE_INTEGER)) return stop('invalid-opening-delay');
  const a = input.attacker, isLuna = a?.hero === 'luna', isTroll = a?.hero === 'troll_warlord';
  if (!keys(a, [...baseKeys, ...(isLuna ? ['glaives', 'glaiveTalent'] : isTroll ? ['fervor', 'fervorTalent', 'rage'] : [])]) ||
    (!isLuna && !isTroll && a.hero !== 'generic') || !Array.isArray(a.damage) || a.damage.length !== 2 ||
    !a.damage.every(n => finite(n, .01, 100000)) || a.damage[1] < a.damage[0] ||
    !finite(a.attackSpeed, 20, 700) || !['none', 'bfury', 'quelling_blade'].includes(a.weapon) ||
    (isLuna ? (a.mode != null && a.mode !== 'ranged') : !['ranged', 'melee'].includes(a.mode!)) ||
    (isLuna && (!level(a.glaives) || (a.glaiveTalent !== undefined && typeof a.glaiveTalent !== 'boolean'))) ||
    (isTroll && (!level(a.fervor) || !level(a.rage) || (a.fervorTalent !== undefined && typeof a.fervorTalent !== 'boolean'))) ||
    ((isLuna || isTroll) ? a.bat !== undefined : !finite(a.bat, .01, 10))) return stop('invalid-attack-stats-or-unmodeled-effect');
  const melee = !isLuna && a.mode === 'melee', bf = a.weapon === 'bfury' && melee;
  const bat = isLuna ? raw.heroes.luna.bat : isTroll ? (melee ? val('troll_warlord_switch_stance', 'base_attack_time') :
    raw.heroes.troll_warlord.bat) : a.bat!;
  const mask = a.madnessRemaining === undefined ? 0 : a.madnessRemaining;
  if (!finite(mask, 0, val('item_mask_of_madness', 'berserk_duration'))) return stop('invalid-madness-window');
  if (input.formation !== undefined && !keys(input.formation, ['glaive', 'cleave', 'hopSeconds'])) return stop('invalid-formation');
  const glaive = isLuna && a.glaives! > 0 ? val('luna_moon_glaive', 'bounces', a.glaives) : 0;
  const bounce = links(us.length, input.formation?.glaive, glaive > 0), cleave = links(us.length, input.formation?.cleave, bf);
  const hop = input.formation?.hopSeconds ?? 0;
  if (!bounce || !cleave || !finite(hop, 0, 5)) return stop('needs-explicit-valid-splash-coverage');
  if (glaive && a.weapon !== 'none' && bounce.some(row => row.length)) return stop('unverified-quell-on-moon-glaive');
  const retention = glaive ? 1 - (val('luna_moon_glaive', 'damage_reduction_percent', a.glaives) +
    (a.glaiveTalent ? val('luna_moon_glaive', 'damage_reduction_percent', 1, 'special_bonus_unique_luna_7') : 0)) / 100 : 0;
  const cleaveFraction = bf ? val('item_bfury', 'cleave_damage_percent_creep') / 100 : 0;
  const quell = a.weapon === 'none' ? 0 : a.weapon === 'bfury' ? val('item_bfury', melee ? 'quelling_bonus' : 'quelling_bonus_ranged') :
    val('item_quelling_blade', melee ? 'damage_bonus' : 'damage_bonus_ranged');
  const fervor = isTroll && a.fervor! > 0 ? val('troll_warlord_fervor', 'attack_speed', a.fervor) +
    (a.fervorTalent ? val('troll_warlord_fervor', 'attack_speed', 1, 'special_bonus_unique_troll_warlord_5') : 0) : 0;
  const maim = isTroll && !melee && a.rage! > 0 ? val('troll_warlord_berserkers_rage', 'maim_damage', a.rage) : 0;
  const maxStacks = val('troll_warlord_fervor', 'max_stacks'), maskSpeed = val('item_mask_of_madness', 'berserk_bonus_attack_speed');
  const initial = us.map(u => ({ max: u.hp + tier * val('neutral_upgrade', 'increase_health'),
    armor: u.armor + tier * val('neutral_upgrade', 'increase_armor'), regen: u.regen }));
  type Hop = { at: number; from: number; number: number; visited: Set<number> };
  function trial(roll: number, proc: boolean) {
    const creeps: State[] = initial.map(u => ({ ...u, hp: u.max }));
    let t = 0, primaryAt = 0, stacks = 0, previous = -1, attacks = 0;
    const hops: Hop[] = [];
    const hit = (i: number, x: number) => { creeps[i].hp = Math.max(0, creeps[i].hp - x * physicalMultiplier742(creeps[i].armor)); };
    const alive = (i: number) => creeps[i].hp > 0;
    const focus = () => creeps.findIndex(c => c.hp > 0); // fixed input order, not omniscient optimization
    while (focus() !== -1) {
      hops.sort((x, y) => x.at - y.at);
      const next = Math.min(primaryAt, hops[0]?.at ?? Infinity);
      if (!Number.isFinite(next) || next > maxTrial) return null;
      for (const c of creeps) if (c.hp > 0) c.hp = Math.min(c.max, c.hp + c.regen * (next - t));
      t = next;
      if (hops.length && hops[0].at <= primaryAt) {
        const h = hops.shift()!, targets = bounce![h.from].filter(alive);
        const dest = targets.find(i => !h.visited.has(i)) ?? targets[0]; // deterministic unvisited-first approximation
        if (dest !== undefined) {
          hit(dest, roll * retention ** h.number); h.visited.add(dest);
          if (h.number < glaive) hops.push({ at: t + hop, from: dest, number: h.number + 1, visited: h.visited });
        }
        continue;
      }
      const i = focus();
      if (i !== previous) stacks = 0;
      hit(i, roll + quell + (proc ? maim : 0)); attacks++;
      if (bf) for (const j of cleave![i]) if (alive(j)) hit(j, (roll + quell) * cleaveFraction);
      if (glaive) hops.push({ at: t + hop, from: i, number: 1, visited: new Set([i]) });
      stacks = Math.min(stacks + 1, maxStacks); previous = i;
      const speed = a.attackSpeed + (alive(i) ? stacks * fervor : 0);
      if (focus() === -1) break;
      if (speed + (t < mask ? maskSpeed : 0) > 700) return 'outside-ias-domain' as const;
      // One BAT of work at IAS/100; integrate a known active-mask expiry inside this interval.
      const boosted = t < mask ? Math.min(mask - t, bat * 100 / (speed + maskSpeed)) : 0;
      const remaining = Math.max(0, bat - boosted * (speed + maskSpeed) / 100);
      primaryAt = t + boosted + remaining * 100 / speed;
    }
    return { seconds: r3(t), attacks };
  }
  const scenarios: NonNullable<CampEstimate742['timing']>['scenarios'] = [];
  for (const [i, roll] of (['min', 'max'] as const).entries()) for (const proc of maim ? [false, true] : [false]) {
    const result = trial(a.damage[i], proc);
    if (!result) return stop('did-not-finish-within-120s-scenario-horizon');
    if (result === 'outside-ias-domain') return stop('effective-ias-outside-20-to-700-model-domain');
    scenarios.push({ roll, rangedMaim: proc ? 'every' : 'none', ...result });
  }
  const ts = scenarios.map(s => s.seconds), range: Range742 = [Math.min(...ts), Math.max(...ts)];
  if (input.clock + range[1] >= (tier + 1) * step - 1) {
    out.bounty = null; out.status = 'unsupported'; return stop('scenario-crosses-neutral-upgrade');
  }
  if (input.clock + range[1] >= 1200) return stop('outside-first-20min-timing-study');
  out.status = 'scenario';
  out.timing = { scope: input.scope, basis: 'uninterrupted-attack-scenarios-not-a-prediction-interval',
    firstHitToLastDeathSeconds: range, engageToLastDeathSeconds:
    input.engageToFirstHitSeconds === undefined ? null : [r3(input.engageToFirstHitSeconds + range[0]), r3(input.engageToFirstHitSeconds + range[1])], scenarios };
  return out;
}
