import assert from 'node:assert/strict';
import { test } from 'node:test';
import { COMBAT, MAP } from '../src/tactics/combat-data.ts';
import { initialResources, simulateClear, estimateCamp } from '../src/tactics/clear-model.ts';
import { calculateSelfStats } from '../src/tactics/self-stats.ts';
import { TacticalEngine } from '../src/tactics/index.ts';
import { CampTracker, planJungleRoute } from '../src/tactics/jungle.ts';
import type { PlayerSnapshot } from '../src/tactics/types.ts';

const camp = MAP.camps.find(c => c.id === 'neutralcamp_good_5')!;
const p: PlayerSnapshot = {
  hero: 'npc_dota_hero_luna', position: camp, clock: 600, team: 'radiant', level: 10,
  health: 1018, maxHealth: 1018, mana: 556, maxMana: 556, alive: true, canAct: true, lastHits: 100,
  abilities: [{ name: 'luna_moon_glaive', level: 4, cooldown: 0 }, { name: 'special_bonus_unique_luna_7', level: 1, cooldown: 0 }],
  items: [{ name: 'item_power_treads', passive: true, attribute: 'agi' }, { name: 'item_mask_of_madness', passive: true, cooldown: 0 }],
};
const unit = { ...COMBAT.neutrals.npc_dota_neutral_kobold, name: 'dummy', health: 100, healthRegen: 0,
  armor: 0, magicResistance: 0, damage: 0, abilities: [], gold: 50, xp: 10 };
const data = { ...COMBAT, neutrals: { dummy: unit } };
const pack = { id: 'test', kind: 'small' as const, units: { dummy: 4 } };
const plain = { ...p, abilities: [], items: [] };
const stats = { ...calculateSelfStats(plain)!, attacksPerSecond: 1, damage: 100, healthRegen: 0, manaRegen: 0, lifesteal: 0 };

test('only death events before the cutoff earn money; a partial camp is not cleared', () => {
  const result = simulateClear(plain, stats, pack, 600, initialResources(plain), 1, data, { limitSeconds: 2 });
  assert.equal(result.complete, false); assert.equal(result.feasible, true);
  assert.equal(result.gold, 100); assert.equal(result.kills?.length, 2);
  assert.deepEqual(result.kills?.map(k => k.seconds), [1, 2]);
  const before = simulateClear(plain, stats, pack, 600, initialResources(plain), 1, data, { limitSeconds: .99 });
  assert.equal(before.gold, 0); assert.equal(before.kills?.length, 0);
  const full = simulateClear(plain, stats, pack, 600, initialResources(plain), 1, data);
  assert.equal(full.complete, true); assert.equal(full.gold, 200);
});

test('lifesteal heals only actual dealt attack damage, not overkill or uncast spells', () => {
  const result = simulateClear(plain, { ...stats, lifesteal: .22, damage: 10000 },
    { ...pack, units: { dummy: 1 } }, 600, { ...initialResources(plain), health: 500 }, 1, data);
  assert.equal(result.healthAfter, 522);
});

test('a spell cast before the cutoff earns nothing if its cast point ends outside the window', () => {
  const mage = { ...plain, abilities: [{ name: 'luna_lucent_beam', level: 4, cooldown: 0 }] };
  const target = { ...data, neutrals: { dummy: { ...unit, health: 200 } } };
  const before = simulateClear(mage, stats, { ...pack, units: { dummy: 1 } }, 600, initialResources(mage), 1, target, { limitSeconds: .3 });
  const after = simulateClear(mage, stats, { ...pack, units: { dummy: 1 } }, 600, initialResources(mage), 1, target, { limitSeconds: .4 });
  assert.equal(before.gold, 0); assert.equal(before.manaAfter, mage.mana);
  assert.equal(after.gold, 50); assert.equal(after.kills?.[0].seconds, .4);
  assert.equal(after.casts.luna_lucent_beam, 1);
});

test('mask spends mana once, retains cooldown/active time across camps, and does not assume unknown readiness', () => {
  const self = calculateSelfStats(p)!;
  const a = simulateClear(p, self, pack, 600, initialResources(p), 1, data);
  assert.equal(a.casts.item_mask_of_madness, 1);
  assert.equal(a.readyAt.item_mask_of_madness, 616);
  assert.equal(a.activeUntil?.item_mask_of_madness, 606);
  const b = simulateClear(p, self, pack, 602, {
    health: a.healthAfter, mana: a.manaAfter, readyAt: a.readyAt, activeUntil: a.activeUntil,
  }, 1, data);
  assert.equal(b.casts.item_mask_of_madness, undefined);
  assert.equal(b.readyAt.item_mask_of_madness, 616);
  const unknown = { ...p, items: p.items.map(i => i.name === 'item_mask_of_madness' ? { ...i, cooldown: undefined } : i) };
  assert.equal(simulateClear(unknown, self, pack, 600, initialResources(unknown), 1, data).casts.item_mask_of_madness, undefined);
  const active = simulateClear(p, { ...self, damage: 0, lifesteal: 0, healthRegen: 0 },
    { ...pack, units: { dummy: 1 } }, 600, { health: 900, mana: 0, readyAt: {}, activeUntil: { item_mask_of_madness: 606 } },
    1, { ...data, neutrals: { dummy: { ...unit, damage: 10 } } }, { limitSeconds: 1 });
  const inactive = simulateClear(p, { ...self, damage: 0, lifesteal: 0, healthRegen: 0 },
    { ...pack, units: { dummy: 1 } }, 600, { health: 900, mana: 0, readyAt: {} },
    1, { ...data, neutrals: { dummy: { ...unit, damage: 10 } } }, { limitSeconds: 1 });
  assert.ok(active.damageTaken > inactive.damageTaken, 'mask armor penalty must not be omitted');
});

test('mask self-silence prevents casting during its active interval, including the next camp', () => {
  const mage = { ...p, abilities: [{ name: 'luna_lucent_beam', level: 4, cooldown: 0 }] };
  const r = simulateClear(mage, calculateSelfStats(mage)!, pack, 600,
    { ...initialResources(mage), activeUntil: { item_mask_of_madness: 606 } }, 1, data, { limitSeconds: 2 });
  assert.equal(r.casts.luna_lucent_beam, undefined);
});

test('talent is applied only if learned and improves bounce damage', () => {
  const withTalent = simulateClear(p, stats, pack, 600, { ...initialResources(p), readyAt: {} }, 1, data, { limitSeconds: 1 });
  const noTalent = simulateClear({ ...p, abilities: p.abilities.slice(0, 1) }, stats, pack, 600, { ...initialResources(p), readyAt: {} }, 1, data, { limitSeconds: 1 });
  assert.ok(withTalent.gold >= noTalent.gold);
  const harder = { ...data, neutrals: { dummy: { ...unit, health: 68 } } };
  const a = simulateClear(p, stats, { ...pack, units: { dummy: 2 } }, 600, { ...initialResources(p), readyAt: {} }, 1, harder, { limitSeconds: 1 });
  const b = simulateClear({ ...p, abilities: p.abilities.slice(0, 1) }, stats, { ...pack, units: { dummy: 2 } }, 600, { ...initialResources(p), readyAt: {} }, 1, harder, { limitSeconds: 1 });
  assert.equal(a.gold, 100); assert.equal(b.gold, 50);
});

test('flooded estimates use evolutionary packs and weighted kill events conserve reward', () => {
  const water = MAP.camps.find(c => c.id === 'neutralcamp_good_4')!;
  const result = estimateCamp(p, calculateSelfStats(p)!, water.kind, 600, initialResources(p), 1,
    { camp: water, spawnAt: 600, limitSeconds: 20 });
  assert.ok(result.kills?.length);
  assert.ok(result.kills.every(k => /frog|tadpole/.test(k.unit)));
  assert.ok(Math.abs(result.kills.reduce((sum, k) => sum + k.gold * (k.weight ?? 1), 0) - result.gold) < 1e-8);
});

test('a partial terminal step earns money without a fictitious follow-on respawn', () => {
  const only = { ...camp, flooded: undefined };
  const tracker = new CampTracker({ ...MAP, camps: [only] });
  const estimate: typeof estimateCamp = (_p, _s, _kind, _start, r, _calibration, options) => ({
    seconds: Math.min(70, options?.limitSeconds ?? 70), clearRange: [70, 70],
    healthAfter: r.health, manaAfter: r.mana, damageTaken: 0, gold: 100, xp: 0, readyAt: r.readyAt, casts: {},
    complete: (options?.limitSeconds ?? 70) >= 70, feasible: true,
  });
  const route = planJungleRoute(p, stats, tracker, { path: (a, b) => ({ distance: 10, points: [a, b] }) }, estimate);
  assert.equal(route.steps.at(-1)?.complete, false);
  assert.ok(route.gold > 100);
  assert.ok(route.seconds <= 180);
  assert.equal(tracker.cleared.size, 0);
});

const payload = () => ({
  hero: { name: p.hero, xpos: camp.x, ypos: camp.y, level: p.level, health: p.health, max_health: p.maxHealth, mana: p.mana, max_mana: p.maxMana, alive: true },
  map: { clock_time: 600, matchid: 'live-test' }, player: { team_name: 'radiant', last_hits: 100 },
  abilities: Object.fromEntries(p.abilities.map((a, i) => ['ability' + i, a])),
  items: Object.fromEntries(p.items.map((i, n) => ['slot' + n, i])),
});

test('treads confirmation survives fresh GSI, resets on match, and stale/conflicting vision cannot override it', () => {
  const engine = new TacticalEngine({ reasoner: null }); engine.setEnabled(true); engine.ingest(payload(), 10000);
  const unknown = engine.state().stats!.damage;
  engine.setOptions({ treadsAttribute: 'agi' }, 10001);
  assert.equal(engine.state().stats!.damage, unknown + 10);
  engine.ingest(payload(), 11000);
  assert.equal(engine.state().player!.items[0].attribute, 'agi');
  const good = { contextId: engine.state().contextId, hero: p.hero, clock: 600, capturedAt: 11000, confidence: .99, treadsAttribute: 'str' };
  engine.observe(good, 11001); engine.observe({ ...good, capturedAt: 12500 }, 12501);
  assert.match(engine.state().observation!.status, /连续两帧/);
  assert.equal(engine.state().options!.treadsAttribute, 'agi', 'model never overwrites explicit confirmation');
  for (const bad of [
    { ...good, contextId: 'different-match' }, { ...good, hero: 'npc_dota_hero_axe' }, { ...good, clock: 800 },
    { ...good, capturedAt: 1 }, { ...good, confidence: .8 }, { ...good, capturedAt: 90000 },
  ]) assert.throws(() => engine.observe(bad, 13000), /视觉/);
  engine.ingest({ ...payload(), map: { clock_time: 0, matchid: 'new' } }, 14000);
  assert.equal(engine.state().options!.treadsAttribute, 'unknown');
  assert.notEqual(engine.state().contextId, good.contextId);
  assert.throws(() => engine.setOptions({ treadsAttribute: 'hacked' }, 14001), /无效/);
});
