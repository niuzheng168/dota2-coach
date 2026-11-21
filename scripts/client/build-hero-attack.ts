import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { parseKeyValues, kvObject, numbers } from '../lib/valve-kv.ts';
import type { KV } from '../lib/valve-kv.ts';

// What the game machine's creep HP display needs to tell when one attack of the player's own hero kills a creep: every hero's
// base damage, attributes and talents, and the items' plain stat bonuses, read from the installed client's scripts/npc with the
// pinned Source2Viewer CLI. An item value counts only when the game's own tooltip lists it as a stat line ("+$damage", "+$agi" ...),
// so conditional and proc damage (Rapier's toggle, Ballista, Misericorde ...) stays out and the estimate is a lower bound.
//   node scripts/client/build-hero-attack.ts [install] [Source2Viewer-CLI.exe] [--check]
const install = path.resolve(process.argv[2] && !process.argv[2].startsWith('--') ? process.argv[2] : 'C:/Steam/steamapps/common/dota 2 beta');
const cli = path.resolve(process.argv[3] && !process.argv[3].startsWith('--') ? process.argv[3] : '.tmp/client-audit/vrf-21.1/Source2Viewer-CLI.exe');
const output = 'data/dota2-hero-attack.json';
const hash = (bytes: Buffer | string) => createHash('sha256').update(bytes).digest('hex');
const pin = JSON.parse(fs.readFileSync('data/dota2-client-tool-pin.json', 'utf8'));
if (hash(fs.readFileSync(cli)) !== pin.executableSha256) throw new Error('Source2Viewer CLI executable pin mismatch');
const game = path.join(install, 'game/dota');
const build = Number(fs.readFileSync(path.join(game, 'steam.inf'), 'utf8').match(/^ServerVersion=(\d+)/m)?.[1]);
if (!(build >= 6983)) throw new Error(`Client build ${build} predates 7.42 (6983)`);
const pak = path.join(game, 'pak01_dir.vpk'), pakSha256 = hash(fs.readFileSync(pak));
const raw = path.resolve('.tmp/hero-attack', `${build}-${pakSha256.slice(0, 12)}`);
if (!fs.existsSync(path.join(raw, 'resource/localization/dota_english.txt')))
  execFileSync(cli, ['-i', pak, '-f', 'scripts/npc/,resource/localization/abilities_english.txt,resource/localization/dota_english.txt', '-o', raw, '-q'],
    { encoding: 'utf8', windowsHide: true, timeout: 300_000, maxBuffer: 64 << 20 });
const read = (file: string) => fs.readFileSync(path.join(raw, file), 'utf8');
const kv = (file: string) => parseKeyValues(read(file));
const tokens = (file: string) => Object.fromEntries(Object.entries(kvObject(kvObject(kv(file).lang).Tokens))
  .map(([key, value]) => [key.toLowerCase(), String(value)]));

const english = tokens('resource/localization/dota_english.txt');
const tooltips = tokens('resource/localization/abilities_english.txt');
const universal = Number(english.dota_loadout_primaryattribute_universal_tooltip?.match(/increases main attack damage by ([\d.]+)/)?.[1]);
if (!(universal > 0 && universal < 1)) throw new Error('Universal damage per attribute not found in dota_english.txt');
const abilities = kvObject(kv('scripts/npc/npc_abilities.txt').DOTAAbilities);
const attributeBonus = numbers(kvObject(kvObject(abilities.special_bonus_attributes).AbilityValues).value);
if (attributeBonus.length !== 7) throw new Error('Unexpected attribute bonus levels');

// Generic talents that add plain damage, attack speed or attributes; hero-specific ones only through LEVEL_DAMAGE below.
const TALENT = /^special_bonus_(attack_damage|attack_base_damage|attack_speed|strength|agility|intelligence|all_stats|agility_and_intelligence|strength_and_agility|strength_and_intelligence)_\d+$/;
const talentEffect = (name: string) => {
  const kind = name.match(TALENT)?.[1];
  if (!kind) return {};
  const value = numbers(kvObject(kvObject(abilities[name]).AbilityValues).value)[0];
  if (!Number.isFinite(value)) throw new Error(`Talent without a value: ${name}`);
  if (kind === 'attack_speed') return { attackSpeed: value };
  if (kind.startsWith('attack_')) return { damage: value };
  const parts = kind === 'all_stats' ? ['str', 'agi', 'int'] : kind.split('_and_').map(k => k.slice(0, 3)); // strength -> str ...
  return Object.fromEntries(parts.map(k => [k, value]));
};
// Passive self damage that grows with the hero's level, checked against the HUD (level-1 Luna shows "53 +2").
const LEVEL_DAMAGE: Record<string, [string, string]> = { npc_dota_hero_luna: ['luna_lunar_blessing', 'self_bonus_damage'] };
// Heroes whose plain attack does less than the HUD number.
const UNSUPPORTED: Record<string, string> = { npc_dota_hero_jakiro: '杰奇洛的普攻分两下、每下伤害打折（双重麻烦），暂不标红' };
// Heroes that switch between melee and ranged (Quelling Blade then counts with its smaller, ranged bonus).
const SWITCHES = new Set(['npc_dota_hero_troll_warlord', 'npc_dota_hero_terrorblade', 'npc_dota_hero_dragon_knight', 'npc_dota_hero_lone_druid']);

const heroBase = kvObject(kvObject(kv('scripts/npc/heroes/npc_dota_hero_base.txt').DOTAHeroes).npc_dota_hero_base);
const heroes: Record<string, unknown> = {};
for (const file of fs.readdirSync(path.join(raw, 'scripts/npc/heroes')).sort()) {
  const key = file.replace(/\.txt$/, '');
  if (!key.startsWith('npc_dota_hero_') || key === 'npc_dota_hero_base' || key === 'npc_dota_hero_target_dummy') continue;
  const own = kvObject(kvObject(kv(`scripts/npc/heroes/${file}`).DOTAHeroes)[key]);
  const h: KV = { ...heroBase, ...own };
  const n = (name: string) => { const v = Number(h[name]); if (!Number.isFinite(v)) throw new Error(`${key}: ${name}`); return v; };
  const talents = Object.entries(own).filter(([k, v]) => /^Ability\d+$/.test(k) && typeof v === 'string' && v.startsWith('special_bonus_'))
    .sort(([a], [b]) => Number(a.slice(7)) - Number(b.slice(7))).map(([, v]) => v as string);
  if (talents.length !== 8) throw new Error(`${key}: ${talents.length} talents`);
  const primary = ({ DOTA_ATTRIBUTE_STRENGTH: 'str', DOTA_ATTRIBUTE_AGILITY: 'agi', DOTA_ATTRIBUTE_INTELLECT: 'int', DOTA_ATTRIBUTE_ALL: 'all' } as Record<string, string>)[String(h.AttributePrimary)];
  const attack = SWITCHES.has(key) ? 'both' : h.AttackCapabilities === 'DOTA_UNIT_CAP_MELEE_ATTACK' ? 'melee'
    : h.AttackCapabilities === 'DOTA_UNIT_CAP_RANGED_ATTACK' ? 'ranged' : undefined;
  if (!primary || !attack) throw new Error(`${key}: primary attribute or attack type`);
  const hero: Record<string, unknown> = {
    primary, attack, damage: [n('AttackDamageMin'), n('AttackDamageMax')],
    attributes: ['Strength', 'Agility', 'Intelligence'].map(a => n(`AttributeBase${a}`)),
    gains: ['Strength', 'Agility', 'Intelligence'].map(a => n(`Attribute${a}Gain`)),
    // Base attack time, base attack speed, attack point at 100 attack speed; projectile speed and range for ranged attacks.
    attackRate: n('AttackRate'), attackSpeed: n('BaseAttackSpeed'), attackPoint: n('AttackAnimationPoint'),
    ...(attack === 'melee' ? {} : { projectileSpeed: n('ProjectileSpeed'), range: n('AttackRange') }),
    talents: talents.map(talentEffect),
  };
  if (attack !== 'melee' && !((hero.projectileSpeed as number) > 0)) throw new Error(`${key}: ranged without projectile speed`);
  if (LEVEL_DAMAGE[key]) {
    const [ability, valueKey] = LEVEL_DAMAGE[key];
    const value = kvObject(kvObject(kvObject(kvObject(own.AbilityDefinitions)[ability]).AbilityValues)[valueKey]);
    const talentKeys = Object.keys(value).filter(k => k.startsWith('special_bonus_'));
    const perLevel = Number(value.hero_levelup), talent = talents.indexOf(talentKeys[0]) + 1;
    const talentBonus = Number(String(value[talentKeys[0]]).replace('+', ''));
    if (!(perLevel > 0) || (value.value ?? '0') !== '0' || talentKeys.length !== 1 || talent < 1 || !(talentBonus > 0))
      throw new Error(`${key}: ${ability}.${valueKey} changed`);
    hero.levelDamage = { source: `${ability}.${valueKey}`, perLevel, talent, talentBonus };
  }
  if (UNSUPPORTED[key]) hero.unsupported = UNSUPPORTED[key];
  heroes[key] = hero;
}

const items: Record<string, Record<string, number[]>> = {};
const STATS: Record<string, string> = { '+$damage': 'damage', '+$str': 'str', '+$agi': 'agi', '+$int': 'int', '+$all': 'all',
  '+$primary_attribute': 'primary', '+$selected_attrib': 'selected', '+$attack': 'attackSpeed' };
const itemDefinitions = kvObject(kv('scripts/npc/items.txt').DOTAAbilities);
for (const [name, value] of Object.entries(itemDefinitions)) {
  if (!name.startsWith('item_') || name.startsWith('item_recipe_')) continue;
  const values = kvObject(kvObject(value).AbilityValues);
  const effect: Record<string, number[]> = {};
  const add = (kind: string, v: number[]) => { if (v.some(x => x !== 0)) effect[kind] = v; };
  for (const [key, raw] of Object.entries(values)) {
    const tip = tooltips[`dota_tooltip_ability_${name}_${key}`.toLowerCase()];
    const v = numbers(raw);
    if (tip === undefined || !v.length) continue;
    if (STATS[tip]) {
      add(STATS[tip], v);
      if (STATS[tip] === 'primary' && values[`${key}_universal`] !== undefined) add('universal', numbers(values[`${key}_universal`]));
    } else if (/^-<font[^>]*>Attack Damage<\/font>$/.test(tip)) {
      if (v.some(x => x > 0)) throw new Error(`${name}: positive value for a damage penalty`);
      add('damage', v);
    } else if (/^%-<font[^>]*>(Strength|Agility|Intelligence)<\/font>$/.test(tip)) {
      if (v.some(x => x > 0)) throw new Error(`${name}: positive value for an attribute penalty`);
      add(tip.includes('Strength') ? 'strPct' : tip.includes('Agility') ? 'agiPct' : 'intPct', v);
    }
  }
  if (name === 'item_vambrace') { // switches like Power Treads; its values have no stat tooltip lines
    const main = numbers(values.bonus_primary_stat), other = numbers(values.bonus_secondary_stat);
    if (main.length !== 1 || other.length !== 1) throw new Error('Vambrace changed');
    add('all', other); add('selected', [main[0] - other[0]]);
  }
  const quell = name === 'item_quelling_blade' ? ['damage_bonus', 'damage_bonus_ranged'] : name === 'item_bfury' ? ['quelling_bonus', 'quelling_bonus_ranged'] : null;
  if (quell) {
    const [melee, ranged] = quell.map(k => numbers(values[k]));
    if (melee.length !== 1 || ranged.length !== 1) throw new Error(`${name}: Quell changed`);
    add('quell', [melee[0], ranged[0]]);
  }
  // item_dagon_3 is item_dagon at level 3; GSI's item_level may still say 1.
  const baseLevel = Number(kvObject(value).ItemBaseLevel ?? 1);
  if (Object.keys(effect).length && baseLevel > 1) add('baseLevel', [baseLevel]);
  if (Object.keys(effect).length) items[name] = Object.fromEntries(Object.entries(effect).sort(([a], [b]) => a.localeCompare(b)));
}
for (const [name, kinds] of [['item_branches', ['all']], ['item_power_treads', ['selected']], ['item_quelling_blade', ['quell']],
  ['item_rapier', ['damage']], ['item_apex', ['primary', 'universal']]] as const)
  if (!kinds.every(k => items[name]?.[k])) throw new Error(`${name}: expected ${kinds.join(', ')}`);
// Towers by tier, for the tower last-hit plan; every lane and both teams must agree.
const units = kvObject(kv('scripts/npc/npc_units.txt').DOTAUnits);
const towers: Record<string, unknown> = {};
for (const tier of [1, 2, 3, 4]) {
  const names = tier === 4 ? ['npc_dota_goodguys_tower4', 'npc_dota_badguys_tower4']
    : ['goodguys', 'badguys'].flatMap(team => ['top', 'mid', 'bot'].map(lane => `npc_dota_${team}_tower${tier}_${lane}`));
  const stats = names.map(name => {
    const u = kvObject(units[name]);
    const n = (k: string) => { const v = Number(u[k]); if (!Number.isFinite(v)) throw new Error(`${name}: ${k}`); return v; };
    return { damage: [n('AttackDamageMin'), n('AttackDamageMax')], attackRate: n('AttackRate'), attackSpeed: n('BaseAttackSpeed'), range: n('AttackRange') };
  });
  if (stats.some(s => JSON.stringify(s) !== JSON.stringify(stats[0]))) throw new Error(`Tier ${tier} towers differ`);
  towers[tier] = stats[0];
}

// Rapier's toggled 250 and Enchanted Quiver's next-attack 250 are not stats; Rapier's always-on base damage is.
if (items.item_enchanted_quiver || items.item_rapier.damage[0] !== numbers(kvObject(kvObject(itemDefinitions.item_rapier).AbilityValues).bonus_damage_base)[0])
  throw new Error('A conditional damage value was taken for a stat');

const result = {
  version: 'hero-attack-1', patch: '7.42',
  sources: {
    client: { build, steamInf: 'game/dota/steam.inf' },
    pak: { file: 'game/dota/pak01_dir.vpk', sha256: pakSha256 },
    files: Object.fromEntries(['scripts/npc/items.txt', 'scripts/npc/npc_abilities.txt', 'scripts/npc/npc_units.txt', 'resource/localization/abilities_english.txt',
      'resource/localization/dota_english.txt'].map(f => [f, hash(fs.readFileSync(path.join(raw, f)))])),
    tool: { name: pin.name, version: pin.version, executableSha256: pin.executableSha256 },
  },
  note: 'Attack damage before armor of the installed client (build ' + build + '). Hero damage [min, max] excludes attributes; talents are in the ' +
    'order GSI reports talent_1..talent_8. Item values are per item level; only values the game tooltips as plain stats. Attack interval = ' +
    'attackRate x 100 / attack speed; towers by tier.',
  universalDamagePerAttribute: universal, attributeBonus, heroes, items, towers,
};
const text = JSON.stringify(result, null, 1) + '\n';
if (process.argv.includes('--check')) {
  const old = JSON.parse(fs.readFileSync(output, 'utf8'));
  if (JSON.stringify({ ...old, sources: undefined }) !== JSON.stringify({ ...result, sources: undefined }))
    throw new Error(`Installed client attack data differs from ${output}`);
  console.log('Hero attack data matches the installed client.');
} else {
  fs.writeFileSync(output, text);
  console.log(`Hero attack ${result.patch} (build ${build}): ${Object.keys(heroes).length} heroes, ${Object.keys(items).length} items with stat bonuses.`);
}
