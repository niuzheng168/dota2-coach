import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { COMBAT, MAP, COMPOSITIONS } from '../src/tactics/combat-data.ts';
import { physicalMultiplier } from '../src/tactics/rules.ts';
import { calculateSelfStats, parsePlayerSnapshot } from '../src/tactics/self-stats.ts';
import { burstAbility } from '../src/tactics/skills.ts';
import { estimateCamp, simulateClear } from '../src/tactics/clear-model.ts';
import { GridNavigation, insideCamp, NAVIGATION, nextSpawn } from '../src/tactics/map-data.ts';
import { CampTracker, planJungleRoute } from '../src/tactics/jungle.ts';
import { travelToCamp } from '../src/tactics/travel.ts';
import { RULES } from '../src/tactics/rules.ts';
import { TacticalEngine } from '../src/tactics/index.ts';
import { GsiCapture, sanitizeGsiCapture } from '../src/tactics/capture.ts';
import { parseKeyValues, numbers } from '../scripts/lib/valve-kv.ts';
import type { Camp, CombatData, MapData, PlayerSnapshot, SelfStats } from '../src/tactics/types.ts';

const camp = MAP.camps.find(c => c.id === 'neutralcamp_good_5')!;
function player(overrides: Partial<PlayerSnapshot> = {}): PlayerSnapshot {
  return {
    hero: 'npc_dota_hero_sven', team: 'radiant', position: { x: camp.x, y: camp.y },
    clock: 600, level: 12, health: 2500, maxHealth: 2500, mana: 600, maxMana: 600,
    alive: true, canAct: true, lastHits: 70,
    abilities: [{ name: 'sven_great_cleave', level: 4, cooldown: 0 }, { name: 'sven_storm_bolt', level: 4, cooldown: 0 }],
    items: [{ name: 'item_power_treads', passive: true }, { name: 'item_broadsword', passive: true }],
    ...overrides,
  };
}
function payload(overrides: Partial<PlayerSnapshot> = {}) {
  const p = player(overrides);
  return {
    map: { matchid: 'test-1', clock_time: p.clock, game_state: 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS' },
    hero: { name: p.hero, xpos: p.position.x, ypos: p.position.y, level: p.level,
      health: p.health, max_health: p.maxHealth, mana: p.mana, max_mana: p.maxMana, alive: p.alive },
    player: { team_name: p.team, last_hits: p.lastHits, steamid: 'private-id', name: 'private-name' },
    abilities: Object.fromEntries(p.abilities.map((a, i) => [`ability${i}`, a])),
    items: Object.fromEntries(p.items.map((item, i) => [`slot${i}`, item])),
  };
}
const unit = { ...COMBAT.neutrals.npc_dota_neutral_kobold, name: 'dummy', health: 1000, healthRegen: 0,
  armor: 0, magicResistance: 0, damage: 0, attackTime: 1, attackSpeed: 100, gold: 50, xp: 100, abilities: [] };
const dummyData: CombatData = { ...COMBAT, neutrals: { dummy: unit } };
const dummyStats: SelfStats = {
  damage: 100, armor: 0, magicResistance: 0, attacksPerSecond: 1, moveSpeed: 300,
  healthRegen: 0, manaRegen: 0, cleave: 0, creepDamage: 0, warnings: [],
};
const dummyComposition = { id: 'dummy', kind: 'small' as const, units: { dummy: 1 } };
const resources = { health: 1000, mana: 600, readyAt: {} };

test('KeyValues handles nested values, comments, directives and rejects truncated input', () => {
  const kv = parseKeyValues('#base "base.txt"\n"unit" { "value" "1 2" // comment\n "nested" { "value" "3" } }');
  assert.deepEqual(numbers(kv.unit), [1, 2]);
  assert.deepEqual(numbers({ value: '3.5 4' }), [3.5, 4]);
  assert.deepEqual(numbers('damage * strength'), []);
  assert.throws(() => parseKeyValues('"a" { "b" "c"'), /Unclosed/);
  assert.throws(() => parseKeyValues('}'), /closing/);
});

test('data snapshot has all heroes, version consistency, spawn boxes and referenced neutrals', () => {
  assert.equal(COMBAT.patch, MAP.patch);
  assert.equal(Object.keys(COMBAT.heroes).length, 127);
  assert.equal(MAP.camps.length, 28);
  assert.equal(new Set(MAP.camps.map(c => c.id)).size, MAP.camps.length);
  for (const c of MAP.camps) assert.ok(insideCamp(c, c));
  for (const c of COMPOSITIONS) for (const name of Object.keys(c.units)) assert.ok(COMBAT.neutrals[name]);
  for (const h of Object.values(COMBAT.heroes)) {
    assert.ok(h.attackTime > 0 && h.moveSpeed > 0 && h.attackSpeed > 0);
  }
});

test('armor has diminishing positive returns and increases damage at negative armor', () => {
  assert.equal(physicalMultiplier(0), 1);
  assert.equal(physicalMultiplier(10), 0.625);
  assert.equal(physicalMultiplier(-10), 1.375);
});

test('self stats use true base attack speed, level growth, primary stats and nonstacking boots', () => {
  const basic = player({ level: 1, items: [], abilities: [] });
  const s = calculateSelfStats(basic)!;
  assert.equal(s.damage, 62); assert.equal(s.armor, 3);
  assert.equal(s.attacksPerSecond, 128 / 190); assert.equal(s.moveSpeed, 325);
  const treads = calculateSelfStats({ ...basic, items: [{ name: 'item_power_treads', passive: true }] })!;
  const both = calculateSelfStats({ ...basic, items: [
    { name: 'item_power_treads', passive: true }, { name: 'item_boots', passive: true },
  ] })!;
  assert.equal(both.moveSpeed, treads.moveSpeed);
  assert.ok(calculateSelfStats(player())!.damage > s.damage);
  assert.ok(treads.warnings.some(w => w.includes('动力鞋')));
});

test('only complete self-player GSI is accepted, and only active slots contribute stats', () => {
  const raw: any = payload();
  raw.items.slot0.passive = false; // castable treads still give stats!
  raw.items.slot6 = { name: 'item_rapier' };
  raw.items.stash0 = { name: 'item_rapier' };
  raw.items.neutral0 = { name: 'item_unknown_test' };
  const parsed = parsePlayerSnapshot(raw)!;
  assert.equal(parsed.items.length, 3);
  assert.ok(parsed.items[0].passive);
  assert.ok(calculateSelfStats(parsed)!.warnings.some(w => w.includes('item_unknown_test')));
  for (const block of ['hero', 'player', 'items', 'abilities', 'draft']) {
    assert.equal(parsePlayerSnapshot({ ...payload(), [block]: { team2: {} } }), null);
  }
  for (const value of [undefined, NaN, Infinity, '600']) {
    const bad: any = payload(); bad.hero.xpos = value;
    assert.equal(parsePlayerSnapshot(bad), null);
  }
  assert.equal(parsePlayerSnapshot({ ...payload(), player: { team_name: 'unknown' } }), null);
  assert.equal(parsePlayerSnapshot({ ...payload(), map: { ...payload().map, game_state: 'DOTA_GAMERULES_STATE_POST_GAME' } }), null);
});

test('reviewed burst adapters resolve live snapshot damage/cooldown/mana, unknown mechanics are not guessed', () => {
  for (const [hero, name] of [
    ['sven', 'sven_storm_bolt'], ['lina', 'lina_dragon_slave'], ['lina', 'lina_light_strike_array'],
    ['crystal_maiden', 'crystal_maiden_crystal_nova'], ['dragon_knight', 'dragon_knight_breathe_fire'],
    ['centaur', 'centaur_hoof_stomp'], ['magnataur', 'magnataur_shockwave'], ['ursa', 'ursa_earthshock'],
  ]) {
    const spell = burstAbility({ name, level: 4, cooldown: 0 }, COMBAT.heroes[`npc_dota_hero_${hero}`].abilities[name]);
    assert.ok(spell && spell.damage > 0 && spell.cooldown > 0 && spell.mana > 0, name);
  }
  assert.equal(burstAbility({ name: 'unknown', level: 4, cooldown: 0 }, undefined), null);
});

test('per-unit combat respects armor, HP, attack speed, unit deaths and remaining mana/cooldown', () => {
  const p = player({ abilities: [] });
  const base = simulateClear(p, dummyStats, dummyComposition, 600, resources, 1, dummyData);
  assert.ok(base.feasible); assert.ok(Math.abs(base.seconds - 10.1) < 0.11);
  const armored = simulateClear(p, dummyStats, dummyComposition, 600, resources, 1,
    { ...dummyData, neutrals: { dummy: { ...unit, armor: 10 } } });
  assert.ok(armored.seconds > base.seconds * 1.5);
  const fast = simulateClear(p, { ...dummyStats, attacksPerSecond: 2 }, dummyComposition, 600, resources, 1, dummyData);
  assert.ok(fast.seconds < base.seconds * 0.6);
  const caster = player({ abilities: [{ name: 'sven_storm_bolt', level: 4, cooldown: 0 }] });
  const cast = simulateClear(caster, dummyStats, dummyComposition, 600, resources, 1, dummyData);
  const cooldown = simulateClear(caster, dummyStats, dummyComposition, 600,
    { ...resources, readyAt: { sven_storm_bolt: 700 } }, 1, dummyData);
  const noMana = simulateClear(caster, dummyStats, dummyComposition, 600, { ...resources, mana: 0 }, 1, dummyData);
  assert.ok(cast.seconds < base.seconds); assert.equal(cast.manaAfter, 490);
  assert.equal(cast.readyAt.sven_storm_bolt, 612);
  assert.deepEqual(cooldown.casts, {}); assert.deepEqual(noMana.casts, {});
});

test('creeps stop attacking on death; no healing can resurrect a hero or a helix-killed creep', () => {
  const deadly = { ...dummyData, neutrals: { dummy: { ...unit, damage: 10000 } } };
  const dead = simulateClear(player({ abilities: [] }), { ...dummyStats, healthRegen: 100000 },
    dummyComposition, 600, { ...resources, health: 100 }, 1, deadly);
  assert.equal(dead.feasible, false); assert.equal(dead.healthAfter, 0);
  const weak = { ...dummyData, neutrals: { dummy: { ...unit, health: 50, damage: 10, healthRegen: 10 } } };
  const clear = simulateClear(player({ hero: 'npc_dota_hero_axe',
    abilities: [{ name: 'axe_counter_helix', level: 4, cooldown: 0 }] }),
  { ...dummyStats, damage: 0 }, { ...dummyComposition, units: { dummy: 4 } }, 600, resources, 1, weak);
  assert.ok(clear.feasible); assert.ok(clear.seconds < 2);
});

test('cleave improves multiple-target clearing and mud golem children are counted once', () => {
  const p = player({ abilities: [] });
  const multi = { ...dummyComposition, units: { dummy: 3 } };
  const basic = simulateClear(p, dummyStats, multi, 600, resources, 1, dummyData);
  const cleave = simulateClear(p, { ...dummyStats, cleave: 0.8 }, multi, 600, resources, 1, dummyData);
  assert.ok(cleave.seconds < basic.seconds * 0.6);
  const golems = COMPOSITIONS.find(c => c.id === 'mud-golems')!;
  const result = simulateClear(p, { ...dummyStats, damage: 10000 }, golems, 100, { ...resources, health: 10000 });
  assert.ok(result.feasible);
  assert.equal(result.gold, COMBAT.neutrals.npc_dota_neutral_mud_golem.gold * 2 +
    COMBAT.neutrals.npc_dota_neutral_mud_golem_split.gold * 4);
});

test('Ursa fury swipes accumulate on the attacked target instead of being a constant DPS bonus', () => {
  const plain = player({ hero: 'npc_dota_hero_ursa', abilities: [] });
  const swipes = { ...plain, abilities: [{ name: 'ursa_fury_swipes', level: 4, cooldown: 0 }] };
  const base = simulateClear(plain, dummyStats, dummyComposition, 600, resources, 1, dummyData);
  const stacked = simulateClear(swipes, dummyStats, dummyComposition, 600, resources, 1, dummyData);
  assert.ok(stacked.feasible);
  assert.ok(Math.abs(stacked.seconds - 5.1) < 0.11);
  assert.ok(stacked.seconds < base.seconds * 0.6);
});

test('Luna moon glaives speed up packs, scale with level, and cannot bounce to a lone surviving target itself', () => {
  const plain = player({ hero: 'npc_dota_hero_luna', abilities: [] });
  const withGlaive = (level: number) => ({ ...plain, abilities: [{ name: 'luna_moon_glaive', level, cooldown: 0 }] });
  const pack = { ...dummyComposition, units: { dummy: 3 } };
  const base = simulateClear(plain, dummyStats, pack, 600, resources, 1, dummyData);
  const low = simulateClear(withGlaive(1), dummyStats, pack, 600, resources, 1, dummyData);
  const high = simulateClear(withGlaive(4), dummyStats, pack, 600, resources, 1, dummyData);
  assert.ok(high.seconds < low.seconds && low.seconds < base.seconds);
  const single = simulateClear(withGlaive(4), dummyStats, dummyComposition, 600, resources, 1, dummyData);
  assert.ok(Math.abs(single.seconds - 10.1) < 0.11);
  const stats = calculateSelfStats(withGlaive(4))!;
  assert.ok(!stats.warnings.some(w => w.includes('未建模技能：luna_moon_glaive')));
  assert.ok(stats.warnings.some(w => w.includes('弹射距离')));
});

test('moon glaive bounces apply each target armor, ignore magic resistance, and do not copy quelling damage', () => {
  const p = player({ hero: 'npc_dota_hero_luna', abilities: [{ name: 'luna_moon_glaive', level: 4, cooldown: 0 }] });
  const data = { ...dummyData, neutrals: {
    front: { ...unit, name: 'front', health: 60 },
    back: { ...unit, name: 'back', health: 40, armor: 10, magicResistance: 1 },
  } };
  const pack = { ...dummyComposition, units: { front: 1, back: 1 } };
  // Focus order is lowest HP first; make both initial HP equal so front is attacked.
  data.neutrals.front.health = 40;
  const result = simulateClear(p, dummyStats, pack, 600, resources, 1, data);
  assert.ok(Math.abs(result.seconds - 1.1) < 0.11); // 100 × 0.65 × 0.625 kills the second unit.
  data.neutrals.front.health = data.neutrals.back.health = 45;
  const quelling = simulateClear(p, { ...dummyStats, creepDamage: 100 }, pack, 600, resources, 1, data);
  assert.ok(Math.abs(quelling.seconds - 2.1) < 0.11, 'extra creep damage must not be added to the bounce');
});

test('moon glaives can return to a previously hit living unit after other eligible targets have been hit', () => {
  const luna = COMBAT.heroes.npc_dota_hero_luna;
  const data = { ...dummyData, heroes: { ...COMBAT.heroes, npc_dota_hero_luna: {
    ...luna, abilities: { ...luna.abilities, luna_moon_glaive: {
      ...luna.abilities.luna_moon_glaive, values: { bounces: [3], damage_reduction_percent: [0] },
    } },
  } } };
  const p = player({ hero: 'npc_dota_hero_luna', abilities: [{ name: 'luna_moon_glaive', level: 1, cooldown: 0 }] });
  const result = simulateClear(p, dummyStats, { ...dummyComposition, units: { dummy: 2 } }, 600, resources, 1, data);
  assert.ok(Math.abs(result.seconds - 5.1) < 0.11, 'two targets receive primary + three separate bounces per attack');
});

test('neutral time upgrades and camp uncertainty envelope affect rewards and safety', () => {
  const p = player(), s = calculateSelfStats(p)!;
  const early = estimateCamp(p, s, 'medium', 100, { ...resources, health: 2500 });
  const late = estimateCamp(p, s, 'medium', 1800, { ...resources, health: 2500 });
  assert.ok(late.gold > early.gold); assert.ok(late.xp > early.xp);
  assert.ok(early.clearRange[0] <= early.clearRange[1]);
  assert.ok(late.damageTaken > early.damageTaken);
});

test('expected camp gold comes from the unit bounties in each composition, not the number of visits', () => {
  const samples = COMPOSITIONS.filter(c => c.kind === 'small');
  const expectedGold = samples.reduce((sum, c) => sum +
    Object.entries(c.units).reduce((total, [name, count]) => total + COMBAT.neutrals[name].gold * count, 0), 0) / samples.length;
  const result = estimateCamp(player(), { ...dummyStats, damage: 10000 }, 'small', 100, { ...resources, health: 2500 });
  assert.ok(result.feasible);
  assert.equal(result.gold, expectedGold);
});

test('grid paths avoid walls and diagonal corner cutting, blocked endpoints fail closed', () => {
  const grid = { width: 5, height: 5, cellSize: 1, origin: { x: 0, y: 0 },
    rows: ['11111', '11011', '11011', '11011', '11111'] };
  const nav = new GridNavigation({ ...MAP, grid });
  const path = nav.path({ x: 1.5, y: 2.5 }, { x: 3.5, y: 2.5 })!;
  assert.ok(path.distance > 4);
  for (const p of path.points) assert.equal(grid.rows[Math.floor(p.y)][Math.floor(p.x)], '1');
  assert.equal(nav.path({ x: 2.5, y: 2.5 }, { x: 3.5, y: 2.5 }), null);
  const corner = new GridNavigation({ ...MAP, grid: { ...grid, width: 2, height: 2, rows: ['10', '01'] } });
  assert.equal(corner.path({ x: 0.5, y: 0.5 }, { x: 1.5, y: 1.5 }), null);
});

test('snapshot camp centers are traversable and paths use actual grid rather than straight-line heuristic', () => {
  for (const camp of MAP.camps) assert.ok(NAVIGATION.path(camp, camp), camp.id);
  const other = MAP.camps.find(c => c.id === 'neutralcamp_good_9')!;
  const route = NAVIGATION.path(camp, other)!;
  assert.ok(route.distance >= Math.hypot(camp.x - other.x, camp.y - other.y));
  assert.ok(route.points.length > 2);
});

test('route search is deterministic, own-side only, bounded, and carries health/mana through steps', () => {
  const p = player(), s = calculateSelfStats(p)!, tracker = new CampTracker();
  const a = planJungleRoute(p, s, tracker), b = planJungleRoute(p, s, tracker);
  assert.deepEqual(a, b); assert.equal(a.status, 'ready'); assert.ok(a.steps.length > 3);
  assert.ok(a.seconds <= 180);
  assert.equal(a.horizonSeconds, 180);
  assert.equal(a.score, a.gold);
  const cleared = new Map<string, number>();
  let previousFinish = p.clock;
  for (const step of a.steps) {
    assert.ok(step.campId.includes('_good_')); assert.ok(step.healthAfter >= p.maxHealth * 0.3);
    assert.ok(step.manaAfter >= 0); assert.ok(step.finish >= step.arrival);
    assert.ok(Math.abs(step.arrival - previousFinish - step.travelSeconds - step.waitSeconds) < 1e-6);
    if (cleared.has(step.campId)) {
      assert.ok(step.spawnAt! >= nextSpawn(cleared.get(step.campId)!));
      assert.ok(step.arrival >= step.spawnAt!);
    }
    cleared.set(step.campId, step.finish); previousFinish = step.finish;
  }
  assert.equal(tracker.cleared.size, 0, 'search must not mutate observed state');
  assert.equal(planJungleRoute(player({ health: 100 }), s, tracker).status, 'recover');
});

test('spawns are strict next-minute events; self-blocking and gaps do not produce false refresh evidence', () => {
  assert.equal(nextSpawn(0), 60); assert.equal(nextSpawn(60), 120); assert.equal(nextSpawn(59.99), 60);
  assert.equal(nextSpawn(120 - 1e-10), 180, 'floating-point rounding must not create a same-tick second pack');
  const tracker = new CampTracker(), p = player({ clock: 59 }), s = calculateSelfStats(p)!;
  tracker.markCleared(camp.id, 50);
  tracker.ingest(p, s); tracker.ingest({ ...p, clock: 60 }, s);
  assert.equal(nextSpawn(tracker.cleared.get(camp.id)!), 120);
  assert.throws(() => tracker.markCleared('invalid', 60), /未知/);
});

test('manual cleared camp can be revisited after a spawn, not by waiting in its spawn box', () => {
  const p = player({ clock: 650 }), s = calculateSelfStats(p)!;
  const single: MapData = { ...MAP, camps: [camp] };
  const tracker = new CampTracker(single); tracker.markCleared(camp.id, 645);
  const fromInside = planJungleRoute(p, s, tracker);
  assert.ok(fromInside.steps.length > 0);
  assert.ok(fromInside.steps[0].waitSeconds > 0 && fromInside.steps[0].waitAt);
  assert.ok(!insideCamp(fromInside.steps[0].waitAt!, camp), 'first leave the box, then wait and return');
  const outside = { ...p, position: { x: camp.bounds.max.x + 200, y: camp.y } };
  const direct = { path: (from: { x: number; y: number }, to: { x: number; y: number }) =>
    ({ distance: Math.hypot(from.x - to.x, from.y - to.y), points: [from, to] }) };
  const plan = planJungleRoute(outside, s, tracker, direct);
  assert.equal(plan.status, 'ready'); assert.ok(plan.steps[0].waitSeconds > 0);
  assert.equal(plan.steps[0].availability, 'respawn-assumed');
  assert.ok(plan.steps[0].arrival >= 660);
});

const directNavigation = { path: (from: { x: number; y: number }, to: { x: number; y: number }) =>
  ({ distance: Math.hypot(from.x - to.x, from.y - to.y), points: [from, to] }) };
const makeCamp = (id: string, x: number, kind: Camp['kind'] = 'small'): Camp => ({
  id, label: id, team: 'radiant', kind, x, y: 0,
  bounds: { min: { x: x - 40, y: -40 }, max: { x: x + 40, y: 40 } },
});
const fastEstimate: typeof estimateCamp = (_p, _s, _kind, start, before) => ({
  seconds: 2, clearRange: [2, 2], healthAfter: before.health - 10, manaAfter: before.mana - 20,
  damageTaken: 10, gold: 100, xp: 100, readyAt: { ...before.readyAt, test_spell: start + 20 }, casts: {}, feasible: true,
});

test('a three-camp circuit farms three refresh cycles within the horizon, with resources carried across all nine visits', () => {
  const camps = [makeCamp('a', 0), makeCamp('b', 300), makeCamp('c', 600)];
  const tracker = new CampTracker({ ...MAP, camps });
  const p = player({ position: camps[0], health: 1000, maxHealth: 1000, mana: 1000, maxMana: 1000, abilities: [] });
  const result = planJungleRoute(p, dummyStats, tracker, directNavigation, fastEstimate);
  assert.equal(result.steps.length, 9);
  assert.equal(result.steps.at(-1)!.healthAfter, 910);
  assert.equal(result.steps.at(-1)!.manaAfter, 820);
  assert.equal(result.gold, 900);
  for (const camp of camps) {
    const visits = result.steps.filter(step => step.campId === camp.id);
    assert.equal(visits.length, 3);
    assert.deepEqual(visits.map(step => step.spawnAt), [null, 660, 720]);
  }
  assert.ok(result.steps.some(step => step.waitSeconds > 12), 'a useful refresh wait is no longer dropped at 12 seconds');
  assert.ok(result.steps.at(-1)!.finish <= p.clock + RULES.horizon);
  assert.equal(tracker.cleared.size, 0);
});

test('visit count responds to clearing speed instead of being filled to a fixed route length', () => {
  const camps = [makeCamp('a', 0), makeCamp('b', 300), makeCamp('c', 600)];
  const tracker = new CampTracker({ ...MAP, camps });
  const p = player({ position: camps[0], health: 1000, maxHealth: 1000, mana: 1000, maxMana: 1000, abilities: [] });
  const fast = planJungleRoute(p, dummyStats, tracker, directNavigation, fastEstimate);
  const slow = planJungleRoute(p, dummyStats, tracker, directNavigation,
    (...args) => ({ ...fastEstimate(...args), seconds: 40, clearRange: [40, 40] }));
  assert.ok(slow.steps.length > 0 && fast.steps.length > slow.steps.length);
  assert.ok(slow.steps.at(-1)!.finish <= p.clock + 180);
});

test('real Luna/Sven static models produce multi-minute routes with more than three visits', () => {
  for (const hero of ['luna', 'sven']) {
    const p = player({
      hero: `npc_dota_hero_${hero}`, level: 20, clock: 1200, health: 2600, maxHealth: 2600, mana: 800, maxMana: 800,
      abilities: [{ name: hero === 'luna' ? 'luna_moon_glaive' : 'sven_great_cleave', level: 4, cooldown: 0 }],
      items: ['item_power_treads', 'item_butterfly', 'item_greater_crit'].map(name => ({ name, passive: true })),
    });
    const route = planJungleRoute(p, calculateSelfStats(p)!, new CampTracker());
    assert.ok(route.steps.length > 6, `${hero} should not stop at three camps`);
    assert.ok(route.seconds > 120 && route.seconds <= 180);
    assert.ok(route.steps.some(step => step.availability === 'respawn-assumed'));
  }
});

test('longer-horizon search considers the whole reachable own jungle, including valuable camps outside the old nearest eight', () => {
  const camps = Array.from({ length: 10 }, (_, i) => makeCamp(`camp-${i}`, i * 200, i === 9 ? 'ancient' : 'small'));
  const tracker = new CampTracker({ ...MAP, camps });
  const p = player({ position: camps[0], health: 1000, maxHealth: 1000, mana: 1000, maxMana: 1000, abilities: [] });
  const estimate: typeof estimateCamp = (...args) => ({ ...fastEstimate(...args), gold: args[2] === 'ancient' ? 1000 : 1 });
  const result = planJungleRoute(p, dummyStats, tracker, directNavigation, estimate);
  assert.ok(result.steps.some(step => step.campId === 'camp-9'));
  assert.ok(result.steps.length > 3);
});

test('fixed-window total rewards win over a very short route with an inflated reward/elapsed ratio', () => {
  const camps = [makeCamp('quick', 0), makeCamp('valuable', 100, 'ancient')];
  const tracker = new CampTracker({ ...MAP, camps });
  const p = player({ position: camps[0], health: 1000, maxHealth: 1000, mana: 1000, maxMana: 1000, abilities: [] });
  const estimate: typeof estimateCamp = (...args) => ({
    ...fastEstimate(...args), gold: args[2] === 'ancient' ? 300 : 8, xp: 0,
    seconds: args[2] === 'ancient' ? 55 : 1, clearRange: args[2] === 'ancient' ? [55, 55] : [1, 1],
  });
  const result = planJungleRoute(p, dummyStats, tracker, directNavigation, estimate);
  assert.ok(result.gold >= 600, 'do not choose one 8-gold/second stop over hundreds of gold across the window');
  assert.equal(result.score, result.gold);
});

test('fewer richer clears beat a route with more low-gold high-XP clears', () => {
  // Both clusters are reachable, but crossing between them consumes the window.
  const rich = makeCamp('cash', -8500, 'ancient');
  const cheap = Array.from({ length: 8 }, (_, i) => makeCamp(`cheap-${i}`, 8500 + i * 120));
  const p = player({ position: { x: 0, y: 0 }, health: 1000, maxHealth: 1000, mana: 1000, maxMana: 1000, abilities: [] });
  const stats = { ...dummyStats, moveSpeed: 100 };
  const estimate: typeof estimateCamp = (...args) => ({
    ...fastEstimate(...args), gold: args[2] === 'ancient' ? 300 : 15,
    xp: args[2] === 'ancient' ? 0 : 10000,
    seconds: args[2] === 'ancient' ? 20 : 2, clearRange: args[2] === 'ancient' ? [20, 20] : [2, 2],
  });
  const cheapRoute = planJungleRoute(p, stats, new CampTracker({ ...MAP, camps: cheap }), directNavigation, estimate);
  const chosen = planJungleRoute(p, stats, new CampTracker({ ...MAP, camps: [rich, ...cheap] }), directNavigation, estimate);
  assert.ok(chosen.steps.length < cheapRoute.steps.length);
  assert.ok(chosen.gold > cheapRoute.gold);
  assert.ok(chosen.xp < cheapRoute.xp, 'experience must not displace a richer route');
  assert.deepEqual(chosen.steps.map(step => step.campId), ['cash', 'cash']);
  assert.equal(chosen.gold, 600);
  assert.equal(chosen.score, chosen.gold);
});

test('experience cannot affect gold ranking, beam ordering, or equal-gold tie breaking', () => {
  const camps = [makeCamp('a-gold', -100, 'ancient'), makeCamp('b-experience', 100)];
  const p = player({ position: { x: 0, y: 0 }, health: 1000, maxHealth: 1000, mana: 1000, maxMana: 1000, abilities: [] });
  // 90-second clears plus walking allow only one clear in this window.
  for (const otherGold of [99, 100]) {
    const route = (otherXp: number) => planJungleRoute(p, dummyStats, new CampTracker({ ...MAP, camps }), directNavigation,
      (...args) => ({
        ...fastEstimate(...args), seconds: 90, clearRange: [90, 90],
        gold: args[2] === 'ancient' ? 100 : otherGold, xp: args[2] === 'ancient' ? 17 : otherXp,
      }));
    const noXp = route(0), hugeXp = route(1_000_000);
    assert.deepEqual(hugeXp, noXp);
    assert.deepEqual(hugeXp.steps.map(step => step.campId), ['a-gold']);
    assert.equal(hugeXp.score, 100);
    assert.equal(hugeXp.xp, 17, 'XP is still reported for display');
  }
});

test('the money objective never bypasses health or horizon constraints to chase a lucrative camp', () => {
  const p = player({ position: { x: 0, y: 0 }, health: 1000, maxHealth: 1000, mana: 1000, maxMana: 1000, abilities: [] });
  for (const reason of ['unsafe', 'too-far']) {
    const camps = [makeCamp('safe', 0), makeCamp('lucrative', reason === 'unsafe' ? 100 : 10000, 'ancient')];
    const estimate: typeof estimateCamp = (...args) => ({
      ...fastEstimate(...args), gold: args[2] === 'ancient' ? 1_000_000 : 100,
      seconds: args[2] === 'ancient' ? 90 : 2, clearRange: args[2] === 'ancient' ? [90, 90] : [2, 2],
      healthAfter: args[2] === 'ancient' && reason === 'unsafe' ? 100 : args[4].health - 10,
    });
    const route = planJungleRoute(p, { ...dummyStats, moveSpeed: 100 }, new CampTracker({ ...MAP, camps }), directNavigation, estimate);
    assert.equal(route.status, 'ready');
    assert.ok(route.steps.every(step => step.campId === 'safe'));
    assert.ok(route.seconds <= 180);
    assert.ok(route.steps.every(step => step.healthAfter >= p.maxHealth * 0.3));
  }
});

test('arrival after a minute does not justify walking into an empty spawn box before the minute', () => {
  const c = { ...makeCamp('target', 1000), bounds: { min: { x: 900, y: -100 }, max: { x: 1100, y: 100 } } };
  const path = { distance: 1000, points: [{ x: 0, y: 0 }, { x: 850, y: 0 }, c] };
  const travel = travelToCamp(path, c, 0, [c], [60], 50, 100, 230, MAP)!;
  assert.ok(travel.waitSeconds > 0);
  assert.ok(Math.abs(travel.arrival - 61.32) < 1e-6);
  assert.ok(travel.waitAt && !insideCamp(travel.waitAt, c));
});

test('a delayed box exit blocks that refresh even when the destination is another camp', () => {
  const from = makeCamp('source', 0), to = makeCamp('target', 1000);
  const path = directNavigation.path(from, to);
  const travel = travelToCamp(path, to, 1, [from, to], [60, 60], 59.8, 100, 239.8, MAP)!;
  assert.equal(travel.availableAt[0], 120, 'source still occupied at 60, must not become available at 60');
  const occupied = travelToCamp(path, to, 1, [from, to], [0, 60], 59.8, 100, 239.8, MAP)!;
  assert.equal(occupied.availableAt[0], 0, 'a camp that already contains a pack is not made empty by crossing a minute');
});

test('single-camp farming walks out and delays a missed refresh instead of inventing a pack while blocking it', () => {
  const c = makeCamp('single', 0), tracker = new CampTracker({ ...MAP, camps: [c] });
  tracker.markCleared(c.id, 50);
  const p = player({ clock: 59.8, position: c, health: 1000, maxHealth: 1000, mana: 1000, maxMana: 1000, abilities: [] });
  const result = planJungleRoute(p, dummyStats, tracker, directNavigation, fastEstimate);
  assert.equal(result.steps[0].spawnAt, 120);
  assert.ok(result.steps[0].arrival > 120);
  assert.ok(result.steps[0].waitAt && !insideCamp(result.steps[0].waitAt, c));
});

test('camp clear inference requires in-camp kills plus departure, gold alone does not count', () => {
  const tracker = new CampTracker(), p = player({ clock: 600 }), s = calculateSelfStats(p)!;
  tracker.ingest(p, s);
  const kill = { ...p, clock: 602, lastHits: p.lastHits! + 3 };
  tracker.observeKills(p, kill); tracker.ingest(kill, s);
  assert.equal(tracker.cleared.size, 0);
  tracker.ingest({ ...kill, clock: 603, position: { x: 0, y: 0 } }, s);
  assert.equal(tracker.cleared.get(camp.id), 602);
  assert.ok(tracker.calibration >= 0.5 && tracker.calibration <= 2);
  const noKills = new CampTracker(); noKills.ingest(p, s);
  noKills.ingest({ ...p, clock: 605, position: { x: 0, y: 0 } }, s);
  assert.equal(noKills.cleared.size, 0);
});

test('engine defaults off, resets on match/hero/clock change, immediately withdraws unsafe and stale routes', () => {
  const engine = new TacticalEngine();
  assert.equal(engine.ingest(payload(), 10000), false); assert.equal(engine.state().route, null);
  engine.setEnabled(true); engine.ingest(payload(), 10000);
  assert.ok(engine.state().route?.steps.length);
  engine.ingest(payload({ alive: false }), 10100);
  assert.equal(engine.state().route, null);
  engine.ingest(payload({ clock: 610 }), 21000);
  assert.ok(engine.state().route); assert.equal(engine.expire(29001), true); assert.equal(engine.state().route, null);
  engine.ingest(payload({ clock: 620 }), 30000); engine.markCleared(camp.id, 31000);
  engine.ingest({ ...payload({ clock: 630 }), map: { ...payload().map, matchid: 'test-2', clock_time: 630 } }, 33000);
  assert.equal(engine.tracker.cleared.size, 0);
  engine.setEnabled(false); assert.equal(engine.state().player, null); assert.equal(engine.state().route, null);
});

test('capture removes credentials/player identity recursively but preserves hero/ability names and replay shape', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'gsi-capture-test-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const raw = { ...payload(), auth: { token: 'SECRET' }, previously: { player: { steamid: 'private-id', name: 'private-name' } } };
  const safe = sanitizeGsiCapture(raw);
  assert.ok(!/SECRET|private-id|private-name/.test(JSON.stringify(safe)));
  assert.ok(JSON.stringify(safe).includes('npc_dota_hero_sven'));
  const writer = new GsiCapture(directory);
  writer.record('safe-room', raw, 10000);
  writer.record('safe-room', { ...raw, hero: { team2: {} } }, 11000);
  await writer.flush();
  const files = await fs.readdir(directory); assert.equal(files.length, 1);
  const content = await fs.readFile(path.join(directory, files[0]), 'utf8');
  assert.equal(content.trim().split('\n').length, 1);
  const replay = new TacticalEngine(); replay.setEnabled(true);
  const record = JSON.parse(content); replay.ingest(record.payload, record.receivedAt);
  assert.ok(replay.state().route);
});
