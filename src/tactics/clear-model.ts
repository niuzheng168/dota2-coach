import { COMBAT, COMPOSITIONS } from './combat-data.ts';
import { campCompositions } from './camp-compositions.ts';
import { burstAbility } from './skills.ts';
import { clamp, levelValue, physicalMultiplier, RULES } from './rules.ts';
import type { CampKind, ClearEstimate, CombatData, CombatOptions, Composition, DamageType, KillEvent, NeutralData, PlayerSnapshot, Resources, SelfStats } from './types.ts';

const DT = 0.1;
interface Creep { data: NeutralData; hp: number; maxHp: number; armor: number; attack: number; nextAttack: number; stacks: number; gold: number; xp: number }

export function initialResources(player: PlayerSnapshot): Resources {
  return { health: player.health, mana: player.mana, activeUntil: {}, readyAt: Object.fromEntries([
    ...player.abilities.map(a => [a.name, player.clock + a.cooldown]),
    ...player.items.filter(i => i.cooldown !== undefined).map(i => [i.name, player.clock + i.cooldown!]),
  ]) };
}

export function simulateClear(
  player: PlayerSnapshot, stats: SelfStats, composition: Composition, start: number,
  resources: Resources, calibration = 1, data: CombatData = COMBAT, options: CombatOptions = {},
): ClearEstimate {
  const upgrade = data.neutralUpgrade;
  const tier = Math.min(upgrade.max_level || 30, Math.max(0, Math.floor(start / (upgrade.increase_time || 450))));
  const creeps: Creep[] = [];
  let gold = 0, xp = 0, health = resources.health, mana = resources.mana, taken = 0, time = 0;
  const readyAt = { ...resources.readyAt }, activeUntil = { ...resources.activeUntil }, casts: Record<string, number> = {};
  const kills: KillEvent[] = [];
  const limit = Math.max(0, Math.min(RULES.maxClearSeconds, options.limitSeconds ?? RULES.maxClearSeconds));
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
        gold: unit.gold + upgraded * (upgrade.increase_gold || 0),
        xp: unit.xp + upgraded * (upgrade.increase_xp || 0),
      });
    }
  };
  for (const [name, count] of Object.entries(composition.units)) add(name, count, 0);
  const damage = (creep: Creep, raw: number, type: DamageType) => {
    if (creep.hp <= 0) return 0;
    const dealt = Math.min(creep.hp, raw * (type === 'physical' ? physicalMultiplier(creep.armor)
      : type === 'magical' ? 1 - creep.data.magicResistance : 1));
    creep.hp -= dealt;
    if (creep.hp <= 0) {
      gold += creep.gold; xp += creep.xp;
      kills.push({ seconds: time, unit: creep.data.name, gold: creep.gold, xp: creep.xp });
      if (creep.data.name === 'npc_dota_neutral_mud_golem') add('npc_dota_neutral_mud_golem_split', 2, time);
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
  const talent = player.hero === 'npc_dota_hero_luna' && learned.some(a => a.name === 'special_bonus_unique_luna_7' && a.level > 0) ? 5 : 0;
  const retention = glaiveValues ? 1 - clamp(levelValue(glaiveValues.damage_reduction_percent, glaive!.level) - talent, 0, 100) / 100 : 0;
  const mask = player.items.some(i => i.name === 'item_mask_of_madness' && i.passive) ? data.items.item_mask_of_madness : undefined;
  const maskValue = (key: string) => levelValue(mask?.[key]);
  const maskActive = (clock: number) => (activeUntil.item_mask_of_madness ?? -Infinity) > clock;
  const rate = (clock: number) => maskActive(clock) && stats.attackTime && stats.attackSpeed
    ? clamp(stats.attackSpeed + maskValue('berserk_bonus_attack_speed'), RULES.minAttackSpeed, RULES.maxAttackSpeed) / (100 * stats.attackTime)
    : stats.attacksPerSecond;
  const helix = learned.find(a => a.name === 'axe_counter_helix');
  const helixValues = helix && h?.abilities[helix.name]?.values;
  const helixAttacks = helixValues ? levelValue(helixValues.trigger_attacks, helix!.level) : 0;
  const helixDamage = helixValues ? levelValue(helixValues.damage, helix!.level) : 0;
  let hitsTaken = 0, nextHelix = 0, nextAttack = tax / stats.attacksPerSecond, busyUntil = 0;
  let pendingSpell: { at: number; name: string; model: NonNullable<ReturnType<typeof burstAbility>> } | null = null;
  const scheduled = options.scheduledCasts?.map(c => ({ ...c, attempted: false }));
  for (let tick = 0; (time = tick * DT) <= limit + 1e-9; tick++) {
    let alive = creeps.filter(c => c.hp > 0);
    if (!alive.length || health <= 0) break;
    const clock = start + time;
    // A cast started just before the horizon must not award a kill until its
    // cast point actually completes. Mana/cooldown are committed at release.
    if (pendingSpell && time + 1e-9 >= pendingSpell.at) {
      const { name, model } = pendingSpell;
      mana -= model.mana;
      readyAt[name] = clock + model.cooldown;
      casts[name] = (casts[name] || 0) + 1;
      for (const creep of alive.slice(0, model.targets)) damage(creep, model.damage, model.type);
      pendingSpell = null;
      alive = creeps.filter(c => c.hp > 0);
    }
    // Model-authored schedules bypass the old automatic spell/item policy. Failed
    // casts stay failed; the verifier never invents a more favorable cast time.
    if (alive.length && scheduled) for (const cast of scheduled) {
      if (cast.attempted || time + 1e-9 < cast.seconds) continue;
      cast.attempted = true;
      if (player.silenced || maskActive(clock) || time + 1e-9 < busyUntil) continue;
      if (cast.name === 'item_mask_of_madness') {
        if (!mask || !stats.attackTime || !stats.attackSpeed || readyAt[cast.name] === undefined ||
            readyAt[cast.name] > clock || mana < maskValue('AbilityManaCost')) continue;
        mana -= maskValue('AbilityManaCost');
        readyAt[cast.name] = clock + maskValue('AbilityCooldown');
        activeUntil[cast.name] = clock + maskValue('berserk_duration');
        casts[cast.name] = (casts[cast.name] || 0) + 1;
        nextAttack = Math.min(nextAttack, time + tax / rate(clock));
      } else {
        const spell = spells.find(s => s.state.name === cast.name);
        if (!spell || (readyAt[cast.name] ?? start) > clock || mana < spell.model!.mana) continue;
        busyUntil = time + spell.model!.castPoint * tax;
        pendingSpell = { at: busyUntil, name: cast.name, model: spell.model! };
        nextAttack = Math.max(nextAttack, busyUntil);
      }
    }
    if (!scheduled && alive.length && options.policy !== 'attacks-only' && !player.silenced && !maskActive(clock) && time + 1e-9 >= busyUntil) {
      for (const spell of spells) {
        const model = spell.model!;
        // Spend at most 85% of maximum mana; reserve some for escape/laning.
        if ((readyAt[spell.state.name] ?? start) > clock || mana - model.mana < player.maxMana * 0.15) continue;
        // Do not spend a full spell for a nearly dead last creep.
        if (alive.reduce((sum, c) => sum + c.hp, 0) < model.damage * 0.4) continue;
        busyUntil = time + model.castPoint * tax;
        pendingSpell = { at: busyUntil, name: spell.state.name, model };
        nextAttack = Math.max(nextAttack, busyUntil);
        break;
      }
      // Never infer readiness from a missing item cooldown. Do not reset it between camps.
      if (mask && stats.attackTime && stats.attackSpeed && readyAt.item_mask_of_madness !== undefined &&
          readyAt.item_mask_of_madness <= clock && time + 1e-9 >= busyUntil &&
          mana - maskValue('AbilityManaCost') >= player.maxMana * 0.15) {
        mana -= maskValue('AbilityManaCost');
        readyAt.item_mask_of_madness = clock + maskValue('AbilityCooldown');
        activeUntil.item_mask_of_madness = clock + maskValue('berserk_duration');
        casts.item_mask_of_madness = (casts.item_mask_of_madness || 0) + 1;
        nextAttack = Math.min(nextAttack, time + tax / rate(clock));
      }
    }
    alive = creeps.filter(c => c.hp > 0);
    if (alive.length && time + 1e-9 >= busyUntil && time + 1e-9 >= nextAttack) {
      // Focus the weakest unit to reduce incoming DPS. Cleave hits distinct other units.
      if (options.targetPriority) {
        const rank = (creep: Creep) => {
          const i = options.targetPriority!.indexOf(creep.data.name);
          return i < 0 ? Infinity : i;
        };
        alive.sort((a, b) => rank(a) - rank(b));
      } else alive.sort((a, b) => a.hp - b.hp);
      const target = alive[0];
      target.stacks++;
      const attackDamage = damage(target, stats.damage + stats.creepDamage + target.stacks * swipeDamage, 'physical');
      health = Math.min(player.maxHealth, health + attackDamage * (stats.lifesteal || 0));
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
      nextAttack += tax / rate(clock);
    }
    alive = creeps.filter(c => c.hp > 0);
    for (const creep of alive) {
      if (creep.hp <= 0) continue;
      if (time + 1e-9 >= creep.nextAttack) {
        // Extra 15% is an explicit uncertainty margin, NOT a simulation of neutral spells.
        const armor = stats.armor - (maskActive(clock) ? maskValue('berserk_armor_reduction') : 0);
        const hit = creep.attack * physicalMultiplier(armor) * 1.15;
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
    const remaining = Math.max(0, Math.min(DT, limit - time));
    health = Math.min(player.maxHealth, health + stats.healthRegen * remaining);
    mana = Math.min(player.maxMana, mana + stats.manaRegen * remaining);
  }
  const complete = creeps.every(c => c.hp <= 0);
  return {
    seconds: Math.min(limit, Math.max(DT, time)),
    healthAfter: Math.max(0, health), manaAfter: Math.max(0, mana), damageTaken: taken,
    gold, xp, readyAt, activeUntil, kills, casts, complete,
    feasible: health > 0 && (complete || (options.limitSeconds !== undefined && options.limitSeconds <= RULES.maxClearSeconds)),
  };
}

export function estimateCamp(
  player: PlayerSnapshot, stats: SelfStats, kind: CampKind, start: number,
  resources: Resources, calibration = 1, options: CombatOptions = {},
): ClearEstimate & { clearRange: [number, number] } {
  const results = (options.camp ? campCompositions(kind, options.spawnAt ?? start, options.camp) : COMPOSITIONS.filter(c => c.kind === kind))
    .map(c => simulateClear(player, stats, c, start, resources, calibration, COMBAT, options));
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
    // An active buff is carried only when every possible pack leaves it active.
    activeUntil: Object.fromEntries(Object.keys(resources.activeUntil || {}).concat('item_mask_of_madness')
      .map(name => [name, Math.min(...results.map(r => r.activeUntil?.[name] ?? 0))])),
    kills: results.flatMap(r => (r.kills || []).map(k => ({ ...k, weight: 1 / results.length }))),
    complete: results.length > 0 && results.every(r => r.complete),
    readyAt, casts, feasible: results.length > 0 && results.every(r => r.feasible),
  };
}
