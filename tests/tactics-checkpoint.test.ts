import assert from 'node:assert/strict';
import { test } from 'node:test';
import { COMBAT, COMPOSITIONS, MAP } from '../src/tactics/combat-data.ts';
import { campCompositions } from '../src/tactics/camp-compositions.ts';
import { calculateSelfStats, parsePlayerSnapshot } from '../src/tactics/self-stats.ts';
import { isModeledAbility } from '../src/tactics/skills.ts';
import type { PlayerSnapshot } from '../src/tactics/types.ts';

const luna: PlayerSnapshot = {
  hero: 'npc_dota_hero_luna', team: 'radiant', position: { x: -1454, y: -3356 },
  clock: 600, level: 10, health: 1018, maxHealth: 1018, mana: 556, maxMana: 556,
  alive: true, canAct: true, lastHits: null,
  abilities: [
    { name: 'luna_moon_glaive', level: 4, cooldown: 0 },
    { name: 'special_bonus_unique_luna_7', level: 1, cooldown: 0 },
    { name: 'luna_lunar_orbit', level: 1, cooldown: 0 },
  ],
  items: [
    { name: 'item_power_treads', passive: true },
    { name: 'item_mask_of_madness', passive: true, cooldown: 0 },
  ],
};

test('reviewed mask data and talent are modeled while orbit still reports its missing geometry', () => {
  const mask = COMBAT.items.item_mask_of_madness;
  assert.deepEqual(mask.AbilityCooldown, [16]);
  assert.deepEqual(mask.AbilityManaCost, [25]);
  const stats = calculateSelfStats(luna)!;
  assert.equal(stats.lifesteal, 0.22);
  assert.ok(stats.warnings.some(w => w.includes('主攻击吸血')));
  assert.ok(stats.warnings.some(w => w.includes('未建模技能：luna_lunar_orbit')));
  assert.ok(stats.warnings.some(w => w.includes('每跳衰减减少 5')));
  assert.equal(isModeledAbility('luna_lunar_orbit'), false);
  assert.equal(isModeledAbility('special_bonus_unique_luna_7'), true);
});

test('explicit treads attribute and Luna innate affect stats without guessing missing input', () => {
  const unknown = calculateSelfStats(luna)!;
  const confirmed = calculateSelfStats({ ...luna, items: luna.items.map(item =>
    item.name === 'item_power_treads' ? { ...item, attribute: 'agi' as const } : item) })!;
  assert.ok(Math.abs(unknown.damage - 123.6) < 1e-9);
  assert.ok(Math.abs(confirmed.damage - unknown.damage - 10) < 1e-9);
  assert.ok(Math.abs(confirmed.armor - unknown.armor - 10 / 6) < 1e-9);
  assert.ok(Math.abs(confirmed.attacksPerSecond - unknown.attacksPerSecond - 10 / 170) < 1e-9);
  assert.ok(unknown.warnings.some(w => w.includes('未加切换属性')));
});

test('GSI preserves item cooldown and passive talent while silence remains fail-closed', () => {
  const parsed = parsePlayerSnapshot({
    map: { clock_time: 600 },
    player: { team_name: 'radiant' },
    hero: { name: luna.hero, level: 10, xpos: -1454, ypos: -3356, health: 1018,
      max_health: 1018, mana: 556, max_mana: 556, alive: true, silenced: true },
    abilities: { ability0: { name: 'special_bonus_unique_luna_7', level: 1 },
      ability1: { name: 'luna_lunar_orbit', level: 1 } },
    items: { slot0: { name: 'item_mask_of_madness', passive: false, cooldown: 12 },
      slot1: { name: 'item_power_treads', passive: false } },
  })!;
  assert.ok(parsed);
  assert.equal(parsed.silenced, true);
  assert.equal(parsed.canAct, false);
  assert.deepEqual(parsed.abilities, [{ name: 'special_bonus_unique_luna_7', level: 1, cooldown: 0 }]);
  assert.equal(parsed.items[0].cooldown, 12);
  assert.equal(parsed.items[1].attribute, undefined);
});

test('provisional flooded packs enumerate evolution without changing ordinary compositions', () => {
  assert.equal(MAP.camps.filter(c => c.flooded).length, 6);
  const growing = MAP.camps.find(c => c.id === 'neutralcamp_good_4')!;
  const capped = MAP.camps.find(c => c.id === 'neutralcamp_good_11')!;
  const small = MAP.camps.find(c => c.id === 'neutralcamp_good_14')!;
  for (const camp of [growing, capped, small]) {
    for (const clock of [0, 299, 300, 600, 900, 1800, 3600]) {
      const packs = campCompositions(camp.kind, clock, camp);
      assert.ok(packs.length > 0);
      for (const pack of packs) {
        assert.equal(Object.values(pack.units).reduce((a, b) => a + b, 0), 3);
        assert.ok(Object.keys(pack.units).every(name => Object.hasOwn(COMBAT.neutrals, name)));
      }
    }
  }
  assert.deepEqual(campCompositions(small.kind, 0, small)[0].units, { npc_dota_neutral_tadpole: 3 });
  assert.ok(Object.keys(campCompositions(growing.kind, 1800, growing)[0].units)
    .every(name => name.includes('ancient_frog')));
  assert.ok(Object.keys(campCompositions(capped.kind, 1800, capped)[0].units)
    .every(name => name.includes('grown_frog')));
  assert.deepEqual(campCompositions('medium', 600), COMPOSITIONS.filter(c => c.kind === 'medium'));
});
