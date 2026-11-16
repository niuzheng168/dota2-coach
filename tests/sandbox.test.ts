import assert from 'node:assert/strict';
import { once } from 'node:events';
import http from 'node:http';
import fs from 'node:fs';
import { test } from 'node:test';
import { MAP } from '../src/tactics/combat-data.ts';
import { evaluateRoute, PlanRunner } from '../src/sandbox/experiment.ts';
import { createSandboxServer } from '../src/sandbox/server.ts';
import { SandboxWorld as CurrentSandboxWorld } from '../src/sandbox/world.ts';
import { createResponsesReasoner } from '../src/tactics/reasoning-planner.ts';

const camp = 'neutralcamp_good_5';
// Keep the published v1 experiment reproducible; v2 economics/aggro has separate tests.
class SandboxWorld extends CurrentSandboxWorld { constructor(seed = 741) { super(seed, 'v1'); } }
const visits = [{ campId: camp, targetPriority: [], casts: [{ name: 'item_mask_of_madness', seconds: 0 }] }];
function harmless(world: SandboxWorld) { for (const unit of world.units) unit.damage = 0; }

test('sandbox seed and fixed ticks produce identical states independently of API step chunking', () => {
  const a = new SandboxWorld(741), b = new SandboxWorld(741), c = new SandboxWorld(742);
  assert.deepEqual(a.snapshot(), b.snapshot());
  assert.notDeepEqual(a.units.map(u => u.name), c.units.map(u => u.name));
  for (const w of [a, b]) {
    w.setOrder({ type: 'farm', campId: camp, targetPriority: [] });
    w.cast('item_mask_of_madness');
  }
  a.advance(12);
  for (let i = 0; i < 120; i++) b.advance(0.1);
  assert.deepEqual(a.snapshot(), b.snapshot());
  assert.ok(a.snapshot().score.neutralGold > 0);
  assert.ok(a.player.health > 0);
});

test('lane spawns and neutral refresh use exact boundaries, with occupied-box blocking', () => {
  const w = new SandboxWorld(), target = MAP.camps.find(c => c.id === 'neutralcamp_good_9')!;
  harmless(w);
  w.units = w.units.filter(u => u.campId !== target.id);
  w.advance(59.9);
  assert.equal(w.campUnits(target.id).length, 0);
  assert.equal(w.events.filter(e => e.type === 'lane_spawn').length, 2);
  w.advance(0.1);
  assert.ok(w.campUnits(target.id).length > 0);
  assert.equal(w.events.filter(e => e.type === 'lane_spawn').length, 3);
  w.units = w.units.filter(u => u.campId !== target.id);
  w.player.position = { x: target.x, y: target.y };
  harmless(w); w.advance(60);
  assert.equal(w.campUnits(target.id).length, 0);
  assert.ok(w.events.some(e => e.type === 'spawn_blocked' && e.campId === target.id));
});

test('farm executes only the requested camp; rewards are per death and GPM always uses 180 seconds', () => {
  const w = new SandboxWorld();
  w.setOrder({ type: 'farm', campId: camp, targetPriority: [] });
  w.cast('item_mask_of_madness'); w.advance(15);
  const first = w.snapshot();
  assert.equal(w.campUnits(camp).length, 0);
  assert.ok(first.rewards.every(r => r.campId === camp));
  assert.equal(first.score.neutralGpmOver180Seconds, first.score.neutralGold / 3);
  w.advance(165);
  assert.equal(w.snapshot().score.neutralGold, first.score.neutralGold);
  assert.equal(w.elapsed, 180);
  assert.equal(w.snapshot().score.measuredInGame, false);
});

test('beam damage, mana and cooldown commit at release; mask silences subsequent beam casts', () => {
  const w = new SandboxWorld(); harmless(w);
  const target = w.campUnits(camp)[0]; target.hp = 1;
  const mana = w.player.mana;
  w.cast('luna_lucent_beam', target.id);
  w.advance(0.3);
  assert.equal(w.rewards.length, 0); assert.equal(w.player.mana, mana);
  w.advance(0.1);
  assert.equal(w.rewards.length, 1); assert.equal(w.rewards[0].unitId, target.id);
  assert.equal(w.player.mana, mana - 110);
  assert.ok(w.snapshot().readyAt.luna_lucent_beam > w.clock);
  const mask = new SandboxWorld(); mask.cast('item_mask_of_madness');
  assert.throws(() => mask.cast('luna_lucent_beam', mask.campUnits(camp)[0].id), /沉默/);
  assert.throws(() => mask.cast('item_mask_of_madness'), /冷却/);
  assert.throws(() => mask.cast('luna_lunar_orbit'), /unsupported/);
});

test('casts crossing the terminal horizon award nothing and cannot spend mana after the episode ends', () => {
  const w = new SandboxWorld(); harmless(w);
  w.advance(179.8);
  const target = w.campUnits(camp)[0]; target.hp = 1;
  w.cast('luna_lucent_beam', target.id); w.advance(0.2);
  assert.equal(w.done, true); assert.equal(w.rewards.length, 0); assert.equal(w.player.mana, w.player.maxMana);
  assert.throws(() => w.cast('item_mask_of_madness'), /结束/);
});

test('lifesteal uses primary actual damage, not overkill or glaive splash', () => {
  const w = new SandboxWorld(); harmless(w);
  const target = w.campUnits(camp)[0]; target.hp = 1;
  w.player.health = 500; w.setOrder({ type: 'attack', targetId: target.id }); w.advance(0.9);
  assert.ok(Math.abs(w.player.health - (500 + w.stats.healthRegen * 0.9 + (w.stats.lifesteal || 0))) < 1e-7);
});

test('lane last hits are separate from neutral income; allied-creep kills grant the hero no money', () => {
  const w = new SandboxWorld(), enemy = w.units.find(u => u.team === 'dire')!;
  w.units = [enemy]; Object.assign(enemy, w.player.position, { hp: 1, damage: 0 });
  w.setOrder({ type: 'attack', targetId: enemy.id }); w.advance(1);
  assert.equal(w.snapshot().score.neutralGold, 0);
  assert.equal(w.snapshot().score.laneGold, enemy.gold);
  const other = new SandboxWorld(), ally = other.units.find(u => u.team === 'radiant')!, foe = other.units.find(u => u.team === 'dire')!;
  other.units = [ally, foe]; Object.assign(ally, { x: 0, y: 0, hp: 1 }); Object.assign(foe, { x: 0, y: 0, hp: 1 });
  other.advance(1);
  assert.equal(other.snapshot().score.totalGold, 0);
});

test('visible plan playback and isolated trials share the same simulation; invalid actions fail closed', () => {
  const trial = evaluateRoute(741, visits, 'v1'), world = new SandboxWorld(741), runner = new PlanRunner(world, visits);
  for (let i = 0; i < 18; i++) runner.advance(10);
  assert.deepEqual(world.snapshot(), trial.finalState);
  assert.deepEqual(runner.route(), trial.route);
  assert.equal(trial.route.search?.expanded, 0);
  assert.throws(() => new SandboxWorld(-1), /seed/);
  assert.throws(() => new SandboxWorld().advance(Infinity), /推进时间/);
  assert.throws(() => new SandboxWorld().setOrder({ type: 'teleport' }), /无效行动/);
  assert.throws(() => new PlanRunner(new SandboxWorld(), [{ ...visits[0], casts: [{ name: 'luna_eclipse', seconds: 0 }] }]), /unsupported/);
});

test('checked-in real-model proposals and unseen-seed evaluations reproduce without any cloud calls', () => {
  const recorded = JSON.parse(fs.readFileSync(new URL('../data/sandbox-luna-api-run.json', import.meta.url), 'utf8'));
  for (const trial of recorded.trials) {
    const result = evaluateRoute(recorded.seed, trial.visits, 'v1');
    assert.equal(result.route.gold, trial.gold);
    assert.equal(result.route.status, trial.status);
  }
  for (const holdout of recorded.holdouts) {
    const result = evaluateRoute(holdout.seed, recorded.selected.visits, 'v1');
    assert.equal(result.route.gold, holdout.neutralGold);
    assert.equal(result.route.status, holdout.status);
  }
  assert.equal(recorded.measuredInGame, false);
});

test('sandbox API isolates trials, rejects unauthenticated/cross-site calls and can drive model tools without cloud', async t => {
  const { server, token } = createSandboxServer();
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => new Promise<void>(resolve => server.close(() => resolve())));
  const address = server.address(); assert.ok(address && typeof address === 'object');
  const origin = `http://127.0.0.1:${address.port}`;
  const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
  const api = async (path: string, body?: unknown) => {
    const response = await fetch(origin + path, { method: body === undefined ? 'GET' : 'POST',
      headers, body: body === undefined ? undefined : JSON.stringify(body) });
    assert.equal(response.status, 200);
    return await response.json() as any;
  };
  assert.equal((await fetch(origin + '/api/state')).status, 401);
  assert.equal((await fetch(origin + '/api/state', { headers: { ...headers, Origin: 'https://attacker.example' } })).status, 403);
  const badHost = await new Promise<number | undefined>((resolve, reject) => {
    http.get(origin + '/api/state', { headers: { ...headers, Host: 'attacker.example' } }, response => {
      response.resume(); resolve(response.statusCode);
    }).on('error', reject);
  });
  assert.equal(badHost, 403);
  const before = await api('/api/state');
  const a = await api('/api/evaluate', { visits }), b = await api('/api/evaluate', { visits });
  assert.deepEqual(a, b); assert.deepEqual(before, await api('/api/state'));
  const differentSeed = await api('/api/evaluate', { seed: 742, visits });
  assert.equal(differentSeed.finalState.seed, 742); assert.deepEqual(before, await api('/api/state'));
  assert.equal((await fetch(origin + '/api/plan', { method: 'POST', headers,
    body: JSON.stringify({ seed: 742, visits }) })).status, 409);
  let calls = 0;
  const reasoner = createResponsesReasoner('fake-cloud-free', async body => {
    const output = ++calls === 1 ? { name: 'simulate_route', arguments: JSON.stringify({ visits }) } :
      { name: 'finish_plan', arguments: JSON.stringify({ candidateId: 'candidate-1', summary: 'API 仿真通过，非实战。', evidenceIds: [] }) };
    assert.equal(body.store, false);
    return { status: 'completed', output: [{ type: 'function_call', call_id: `tool-${calls}`, ...output }] };
  }, { simulate: async (_input, v) => (await api('/api/evaluate', { visits: v })).route });
  const result = await reasoner.decide({ player: before.player, stats: before.stats, cleared: new Map(), recent: [] }, new AbortController().signal);
  assert.deepEqual(result.visits, visits);
  const planned = await fetch(origin + '/api/plan', { method: 'POST', headers,
    body: JSON.stringify({ visits: result.visits, model: result.model, summary: result.summary }) });
  assert.equal(planned.status, 409, 'legacy format may not bypass the missing-area advice gate');
  assert.deepEqual(before, await api('/api/state'));
  await api('/api/action', { action: { type: 'farm', campId: camp, targetPriority: [] } });
  await api('/api/action', { action: { type: 'cast', name: 'item_mask_of_madness' } });
  const after = await api('/api/step', { seconds: 180 });
  assert.equal(after.plan, null);
  assert.ok(after.score.neutralGold > 0);
  assert.ok(!JSON.stringify(after).includes(token));
});
