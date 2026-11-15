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
import { TacticalEngine } from '../src/tactics/index.ts';
import { GsiCapture, sanitizeGsiCapture } from '../src/tactics/capture.ts';
import { parseKeyValues, numbers } from '../scripts/lib/valve-kv.ts';
import type { CombatData, MapData, PlayerSnapshot, SelfStats } from '../src/tactics/types.ts';

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

test('neutral time upgrades and camp uncertainty envelope affect rewards and safety', () => {
  const p = player(), s = calculateSelfStats(p)!;
  const early = estimateCamp(p, s, 'medium', 100, { ...resources, health: 2500 });
  const late = estimateCamp(p, s, 'medium', 1800, { ...resources, health: 2500 });
  assert.ok(late.gold > early.gold); assert.ok(late.xp > early.xp);
  assert.ok(early.clearRange[0] <= early.clearRange[1]);
  assert.ok(late.damageTaken > early.damageTaken);
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
  assert.deepEqual(a, b); assert.equal(a.status, 'ready'); assert.equal(a.steps.length, 3);
  assert.ok(a.seconds <= 180);
  for (const step of a.steps) {
    assert.ok(step.campId.includes('_good_')); assert.ok(step.healthAfter >= p.maxHealth * 0.3);
    assert.ok(step.manaAfter >= 0); assert.ok(step.finish >= step.arrival);
  }
  assert.equal(tracker.cleared.size, 0, 'search must not mutate observed state');
  assert.equal(planJungleRoute(player({ health: 100 }), s, tracker).status, 'recover');
});

test('spawns are strict next-minute events; self-blocking and gaps do not produce false refresh evidence', () => {
  assert.equal(nextSpawn(0), 60); assert.equal(nextSpawn(60), 120); assert.equal(nextSpawn(59.99), 60);
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
  assert.equal(planJungleRoute(p, s, tracker).steps.length, 0);
  const outside = { ...p, position: { x: camp.bounds.max.x + 200, y: camp.y } };
  const direct = { path: (from: { x: number; y: number }, to: { x: number; y: number }) =>
    ({ distance: Math.hypot(from.x - to.x, from.y - to.y), points: [from, to] }) };
  const plan = planJungleRoute(outside, s, tracker, direct);
  assert.equal(plan.status, 'ready'); assert.ok(plan.steps[0].waitSeconds > 0);
  assert.equal(plan.steps[0].availability, 'respawn-assumed');
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
