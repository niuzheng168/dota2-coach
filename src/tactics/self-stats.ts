import { COMBAT } from './combat-data.ts';
import { clamp, levelValue, RULES } from './rules.ts';
import { isModeledAbility } from './skills.ts';
import type { CombatData, PlayerSnapshot, SelfStats } from './types.ts';

const object = (v: unknown): Record<string, any> => v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, any> : {};
const finite = (n: unknown): n is number => typeof n === 'number' && Number.isFinite(n);
// Fail closed: no spectator/coach payload, enemy data, or guessed position/team.
export function parsePlayerSnapshot(payload: Record<string, any>): PlayerSnapshot | null {
  const hero = object(payload.hero), player = object(payload.player), map = object(payload.map);
  if (['hero', 'player', 'abilities', 'items', 'draft'].some(key => {
    const block = object(payload[key]); return block.team2 || block.team3;
  })) return null;
  const team = player.team_name === 'radiant' || player.team_name === 'dire' ? player.team_name
    : player.team_name === 'DOTA_TEAM_GOODGUYS' || player.team === 2 ? 'radiant'
      : player.team_name === 'DOTA_TEAM_BADGUYS' || player.team === 3 ? 'dire' : null;
  if (!team || typeof hero.name !== 'string' || !/^npc_dota_hero_[a-z0-9_]{1,64}$/.test(hero.name) ||
      ![hero.xpos, hero.ypos, hero.level, hero.health, hero.max_health, hero.mana, hero.max_mana, map.clock_time].every(finite) ||
      hero.max_health <= 0 || hero.health < 0 || hero.health > hero.max_health ||
      hero.max_mana < 0 || hero.mana < 0 || hero.mana > hero.max_mana ||
      hero.level < 1 || hero.level > 30 || !Number.isInteger(hero.level) ||
      Math.abs(hero.xpos) > 12000 || Math.abs(hero.ypos) > 12000 ||
      typeof hero.alive !== 'boolean') return null;
  if (map.game_state && !['DOTA_GAMERULES_STATE_PRE_GAME', 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS'].includes(map.game_state)) return null;
  return {
    hero: hero.name, team, position: { x: hero.xpos, y: hero.ypos }, clock: map.clock_time,
    level: hero.level, health: hero.health, maxHealth: hero.max_health, mana: hero.mana, maxMana: hero.max_mana,
    alive: hero.alive, canAct: !hero.stunned && !hero.hexed && !hero.disarmed && !hero.silenced &&
      map.paused !== true && map.is_paused !== true,
    abilities: Object.values(object(payload.abilities)).filter(a => a && typeof a === 'object' &&
      typeof a.name === 'string' && finite(a.level) && Number.isInteger(a.level) && a.level > 0 && a.level <= 10 &&
      finite(a.cooldown) && a.cooldown >= 0).slice(0, 32)
      .map(a => ({ name: a.name, level: a.level, cooldown: a.cooldown })),
    items: Object.entries(object(payload.items)).filter(([slot, item]) =>
      /^(slot[0-5]|neutral0)$/.test(slot) && item && typeof item === 'object' &&
      typeof item.name === 'string' && item.name !== 'empty').map(([, item]) =>
      // GSI "passive" describes the item's cast behavior, not whether its stats are active.
      ({ name: item.name, passive: true })),
    lastHits: finite(player.last_hits) && player.last_hits >= 0 ? player.last_hits : null,
  };
}

export function calculateSelfStats(player: PlayerSnapshot, data: CombatData = COMBAT): SelfStats | null {
  const h = Object.hasOwn(data.heroes, player.hero) ? data.heroes[player.hero] : undefined;
  if (!h) return null;
  const warnings = ['未计入临时增益、专属天赋、命石/先天叠层、幻象和召唤物；请以游戏面板为准。'];
  const attrs = h.attributes.map((base, i) => base + h.gains[i] * (player.level - 1));
  let bonusDamage = 0, bonusArmor = 0, attackSpeed = 0, boots = 0, flatMove = 0, percentMove = 0;
  let healthRegen = 0, manaRegen = 0, magicFactor = 1, cleave = 0, creepDamage = 0;
  for (const item of player.items) {
    if (!item.passive) continue;
    const values = Object.hasOwn(data.items, item.name) ? data.items[item.name] : undefined;
    if (!values) { warnings.push(`未建模物品：${item.name}`); continue; }
    const v = (key: string) => levelValue(values[key]);
    attrs[0] += v('bonus_strength') + v('bonus_all_stats');
    attrs[1] += v('bonus_agility') + v('bonus_all_stats');
    attrs[2] += v('bonus_intellect') + v('bonus_intelligence') + v('bonus_all_stats');
    bonusDamage += v('bonus_damage'); bonusArmor += v('bonus_armor'); attackSpeed += v('bonus_attack_speed');
    healthRegen += v('bonus_health_regen'); manaRegen += v('bonus_mana_regen');
    const move = v('bonus_movement_speed') || v(h.melee ? 'bonus_movement_speed_melee' : 'bonus_movement_speed_ranged');
    if (/^item_(boots|phase_boots|power_treads|arcane_boots|tranquil_boots|travel_boots(_2)?|guardian_greaves|boots_of_bearing)$/.test(item.name)) {
      boots = Math.max(boots, move);
    } else flatMove += move;
    percentMove = Math.max(percentMove, v('movement_speed_percent_bonus'));
    magicFactor *= 1 - clamp(v('bonus_magical_armor') / 100, 0, 1);
    if (item.name === 'item_power_treads') warnings.push('动力鞋当前属性不可见：未加切换属性。');
    if (item.name === 'item_bfury' && h.melee) cleave += v('cleave_damage_percent_creep') / 100;
    if (['item_quelling_blade', 'item_bfury'].includes(item.name)) {
      creepDamage = Math.max(creepDamage, v(h.melee ? 'quelling_bonus' : 'quelling_bonus_ranged'));
    }
    warnings.push(`物品 ${item.name} 仅计白名单静态属性；主动、触发、光环及条件效果未计。`);
  }
  for (const ability of player.abilities) {
    if (ability.name === 'sven_great_cleave') {
      cleave += levelValue(h.abilities[ability.name]?.values.great_cleave_damage, ability.level) / 100;
    } else if (!isModeledAbility(ability.name) || !Object.hasOwn(h.abilities, ability.name)) {
      warnings.push(`未建模技能：${ability.name}`);
    }
  }
  if (!player.abilities.length) warnings.push('未收到已学技能及冷却：仅按普攻估算；请更新 GSI 配置。');
  const primaryDamage = h.primary === 'all' ? attrs.reduce((a, b) => a + b, 0) * RULES.universalDamage
    : attrs[h.primary === 'str' ? 0 : h.primary === 'agi' ? 1 : 2];
  return {
    damage: h.damage + primaryDamage + bonusDamage,
    armor: h.armor + attrs[1] / RULES.agilityPerArmor + bonusArmor,
    magicResistance: 1 - (1 - clamp(h.magicResistance + attrs[2] * 0.001, 0, 1)) * magicFactor,
    attacksPerSecond: clamp(h.attackSpeed + attrs[1] + attackSpeed, RULES.minAttackSpeed, RULES.maxAttackSpeed) / (100 * h.attackTime),
    moveSpeed: clamp((h.moveSpeed + boots + flatMove) * (1 + percentMove / 100), RULES.minMoveSpeed, RULES.maxMoveSpeed),
    healthRegen: h.healthRegen + attrs[0] * RULES.healthRegenPerStrength + healthRegen,
    manaRegen: h.manaRegen + attrs[2] * RULES.manaRegenPerInt + manaRegen,
    cleave, creepDamage, warnings: [...new Set(warnings)],
  };
}
