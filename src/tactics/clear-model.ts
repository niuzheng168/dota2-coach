import { COMBAT, COMPOSITIONS } from './combat-data.ts';
import { burstAbility } from './skills.ts';
import { clamp, levelValue, physicalMultiplier, RULES } from './rules.ts';
import type { CampKind, ClearEstimate, CombatData, Composition, DamageType, NeutralData, PlayerSnapshot, Resources, SelfStats } from './types.ts';

const DT = 0.1;
interface Creep { data: NeutralData; hp: number; maxHp: number; armor: number; attack: number; nextAttack: number; stacks: number }

export function simulateClear(
  player: PlayerSnapshot, stats: SelfStats, composition: Composition, start: number,
  resources: Resources, calibration = 1, data: CombatData = COMBAT,
): ClearEstimate {
  const upgrade = data.neutralUpgrade;
  const tier = Math.min(upgrade.max_level || 30, Math.max(0, Math.floor(start / (upgrade.increase_time || 450))));
  const creeps: Creep[] = [];
  let gold = 0, xp = 0, health = resources.health, mana = resources.mana, taken = 0, time = 0;
  const readyAt = { ...resources.readyAt }, casts: Record<string, number> = {};
  const tax = clamp(calibration, 0.5, 2);
  const add = (name: string, count: number, at: number) => {
    const unit = data.neutrals[name];
    if (!unit) throw new Error(`Unknown neutral: ${name}`);
    for (let i = 0; i < count; i++) {
      const upgraded = unit.abilities.includes('neutral_upgrade') ? tier : 0;
      const hp = unit.health + upgraded * (upgrade.increase_health || 0);
      creeps.push({
        data: unit, hp, maxHp: hp, stacks: 0, nextAttack: at + 0.6,
        armor: unit.armor + upgraded * (upgrade.increase_armor || 0),
        attack: unit.damage + upgraded * (upgrade.increase_damage || 0),
      });
      gold += unit.gold + upgraded * (upgrade.increase_gold || 0);
      xp += unit.xp + upgraded * (upgrade.increase_xp || 0);
    }
  };
  for (const [name, count] of Object.entries(composition.units)) add(name, count, 0);
  const damage = (creep: Creep, raw: number, type: DamageType) => {
    if (creep.hp <= 0) return 0;
    const dealt = Math.min(creep.hp, raw * (type === 'physical' ? physicalMultiplier(creep.armor)
      : type === 'magical' ? 1 - creep.data.magicResistance : 1));
    creep.hp -= dealt;
    if (creep.hp <= 0 && creep.data.name === 'npc_dota_neutral_mud_golem') {
      add('npc_dota_neutral_mud_golem_split', 2, time);
    }
    return dealt;
  };
  const h = data.heroes[player.hero];
  const learned = player.abilities;
  const spells = learned.map(a => ({ state: a, model: burstAbility(a, h?.abilities[a.name]) }))
    .filter(a => a.model !== null);
  const swipes = learned.find(a => a.name === 'ursa_fury_swipes');
  const swipeDamage = swipes ? levelValue(h?.abilities[swipes.name]?.values.damage_per_stack, swipes.level) : 0;
  const glaive = learned.find(a => a.name === 'luna_moon_glaive');
  const glaiveValues = glaive && h?.abilities[glaive.name]?.values;
  const bounces = glaiveValues ? levelValue(glaiveValues.bounces, glaive!.level) : 0;
  const retention = glaiveValues ? 1 - levelValue(glaiveValues.damage_reduction_percent, glaive!.level) / 100 : 0;
  const helix = learned.find(a => a.name === 'axe_counter_helix');
  const helixValues = helix && h?.abilities[helix.name]?.values;
  const helixAttacks = helixValues ? levelValue(helixValues.trigger_attacks, helix!.level) : 0;
  const helixDamage = helixValues ? levelValue(helixValues.damage, helix!.level) : 0;
  let hitsTaken = 0, nextHelix = 0, nextAttack = tax / stats.attacksPerSecond, busyUntil = 0;
  for (; time < RULES.maxClearSeconds; time += DT) {
    let alive = creeps.filter(c => c.hp > 0);
    if (!alive.length || health <= 0) break;
    const clock = start + time;
    if (time + 1e-9 >= busyUntil) {
      for (const spell of spells) {
        const model = spell.model!;
        // Spend at most 85% of maximum mana; reserve some for escape/laning.
        if ((readyAt[spell.state.name] ?? start) > clock || mana - model.mana < player.maxMana * 0.15) continue;
        // Do not spend a full spell for a nearly dead last creep.
        if (alive.reduce((sum, c) => sum + c.hp, 0) < model.damage * 0.4) continue;
        mana -= model.mana;
        readyAt[spell.state.name] = clock + model.cooldown;
        casts[spell.state.name] = (casts[spell.state.name] || 0) + 1;
        for (const creep of alive.slice(0, model.targets)) damage(creep, model.damage, model.type);
        busyUntil = time + model.castPoint * tax;
        nextAttack = Math.max(nextAttack, busyUntil);
        break;
      }
    }
    alive = creeps.filter(c => c.hp > 0);
    if (alive.length && time + 1e-9 >= busyUntil && time + 1e-9 >= nextAttack) {
      // Focus the weakest unit to reduce incoming DPS. Cleave hits distinct other units.
      alive.sort((a, b) => a.hp - b.hp);
      const target = alive[0];
      target.stacks++;
      damage(target, stats.damage + stats.creepDamage + target.stacks * swipeDamage, 'physical');
      for (const creep of alive.slice(1)) damage(creep, stats.damage * stats.cleave, 'physical');
      // Moon Glaives are attack-derived physical damage, NOT the default damage
      // type of a passive's datafeed entry. Bounce from the raw attack value so
      // each target's own armor applies exactly once, without extra attack modifiers.
      const struck = new Set([target]);
      let previous = target;
      for (let bounce = 1; bounce <= bounces; bounce++) {
        const targets = creeps.filter(c => c.hp > 0 && c !== previous);
        if (!targets.length) break;
        const next = targets.find(c => !struck.has(c)) ?? targets[0];
        damage(next, stats.damage * retention ** bounce, 'physical');
        struck.add(next); previous = next;
      }
      nextAttack += tax / stats.attacksPerSecond;
    }
    alive = creeps.filter(c => c.hp > 0);
    for (const creep of alive) {
      if (creep.hp <= 0) continue;
      if (time + 1e-9 >= creep.nextAttack) {
        // Extra 15% is an explicit uncertainty margin, NOT a simulation of neutral spells.
        const hit = creep.attack * physicalMultiplier(stats.armor) * 1.15;
        taken += hit; health -= hit; hitsTaken++;
        const bonusSpeed = creep.data.abilities.includes('neutral_upgrade') ? tier * (upgrade.increase_aspd || 0) : 0;
        creep.nextAttack += creep.data.attackTime * 100 / (creep.data.attackSpeed + bonusSpeed);
        if (helixAttacks && hitsTaken >= helixAttacks && time >= nextHelix) {
          for (const enemy of creeps.filter(c => c.hp > 0)) damage(enemy, helixDamage, 'pure');
          hitsTaken = 0; nextHelix = time + 0.3;
        }
        if (health <= 0) break;
      }
      if (creep.hp > 0) creep.hp = Math.min(creep.maxHp, creep.hp + creep.data.healthRegen * DT);
    }
    if (health <= 0) break;
    health = Math.min(player.maxHealth, health + stats.healthRegen * DT);
    mana = Math.min(player.maxMana, mana + stats.manaRegen * DT);
  }
  return {
    seconds: Math.min(RULES.maxClearSeconds, Math.max(DT, time)),
    healthAfter: Math.max(0, health), manaAfter: Math.max(0, mana), damageTaken: taken,
    gold, xp, readyAt, casts, feasible: health > 0 && creeps.every(c => c.hp <= 0),
  };
}

export function estimateCamp(
  player: PlayerSnapshot, stats: SelfStats, kind: CampKind, start: number,
  resources: Resources, calibration = 1,
): ClearEstimate & { clearRange: [number, number] } {
  const results = COMPOSITIONS.filter(c => c.kind === kind)
    .map(c => simulateClear(player, stats, c, start, resources, calibration));
  const readyAt = { ...resources.readyAt }, casts: Record<string, number> = {};
  for (const result of results) {
    for (const [name, at] of Object.entries(result.readyAt)) readyAt[name] = Math.max(readyAt[name] || 0, at);
    for (const [name, count] of Object.entries(result.casts)) casts[name] = Math.max(casts[name] || 0, count);
  }
  // Planning uses the conservative envelope, not a fictitious "average creep".
  return {
    seconds: Math.max(...results.map(r => r.seconds)),
    clearRange: [Math.min(...results.map(r => r.seconds)), Math.max(...results.map(r => r.seconds))],
    healthAfter: Math.min(...results.map(r => r.healthAfter)), manaAfter: Math.min(...results.map(r => r.manaAfter)),
    damageTaken: Math.max(...results.map(r => r.damageTaken)),
    gold: results.reduce((sum, r) => sum + r.gold, 0) / results.length,
    xp: results.reduce((sum, r) => sum + r.xp, 0) / results.length,
    readyAt, casts, feasible: results.length > 0 && results.every(r => r.feasible),
  };
}
