import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs';
import { test } from 'node:test';
import { MAP } from '../src/tactics/combat-data.ts';
import { distance, insideCamp } from '../src/tactics/map-data.ts';
import { CAMP_EXITS, LANES, SANDBOX_RULES, SandboxWorld } from '../src/sandbox/world.ts';
import { EconomyPlanRunner, evaluateEconomyPlan, parseEconomySteps } from '../src/sandbox/economy.ts';
import type { EconomyStep } from '../src/sandbox/economy.ts';
import { ECONOMY_INSTRUCTIONS, reasonEconomy } from '../src/sandbox/economy-reasoner.ts';
import { createSandboxServer } from '../src/sandbox/server.ts';
import { SANDBOX_RECOMMENDATION_SCOPE } from '../src/sandbox/recommendation-scope.ts';

const campId = 'neutralcamp_good_5';
const exit = CAMP_EXITS.find(c => c.campId === campId)!.exits.find(e => e.direction === 'west')!.position!;
const step = (overrides: Partial<EconomyStep> = {}): EconomyStep => ({
  kind: 'camp', campId, laneId: null, position: null, duration: 30, untilClock: null,
  targetPriority: [], attackMode: 'push', casts: [], ...overrides,
});
function pullWorld(at: number) {
  const w = new SandboxWorld();
  w.units = w.campUnits(campId);
  for (const u of w.units) u.damage = 0;
  w.setOrder({ type: 'move', position: exit }); w.advance(at);
  return w;
}

test('v2 starts with existing fighting waves, not an empty lane; pre-roll creates no hero income', () => {
  const w = new SandboxWorld(), initial = w.snapshot();
  assert.equal(initial.version, 'luna-sandbox-v2-lane-pull');
  assert.equal(initial.elapsedSeconds, 0); assert.equal(initial.score.totalGold, 0);
  assert.ok(w.units.some(u => u.laneId && u.packId?.endsWith('@540')));
  assert.ok(w.units.some(u => u.laneId && u.hp < u.maxHp));
  assert.ok(initial.laneSummary!.find(l => l.laneId === 'bot')!.enemyGold > 162);
  assert.deepEqual(initial, new SandboxWorld().snapshot());
  assert.ok(initial.events.every(e => e.clock >= 600));
});

test('properly timed pull changes positions and creates a stack, but never creates immediate gold', () => {
  const w = pullWorld(53), count = w.campUnits(campId).length;
  w.setOrder({ type: 'pull', campId, position: exit }); w.advance(7);
  assert.equal(w.clock, 660);
  assert.ok(w.events.some(e => e.type === 'stack_spawn' && e.campId === campId));
  assert.ok(w.campUnits(campId).length > count);
  assert.ok(w.campUnits(campId).some(u => !insideCamp(u, MAP.camps.find(c => c.id === campId)!)));
  assert.ok(w.campUnits(campId).every(u => u.stacked));
  assert.equal(w.snapshot().score.totalGold, 0);
  assert.deepEqual(Object.keys(w.player.position).sort(), ['x', 'y'], 'navigation must not copy camp metadata onto the hero position');
});

test('early pull returns before the minute; occupying a spawn box prevents stacking', () => {
  const early = pullWorld(10);
  early.setOrder({ type: 'pull', campId, position: exit }); early.advance(30);
  const home = MAP.camps.find(c => c.id === campId)!;
  assert.ok(early.campUnits(campId).every(u => distance(u, home) < 100));
  early.advance(20);
  assert.equal(early.events.some(e => e.type === 'stack_spawn' && e.campId === campId), false);
  const blocked = new SandboxWorld(); for (const u of blocked.units) u.damage = 0;
  blocked.advance(60);
  assert.equal(blocked.events.some(e => e.type === 'stack_spawn' && e.campId === campId), false);
});

test('lane creeps react to a genuinely provoked neutral, but do not all auto-pull untouched camps', () => {
  const w = pullWorld(10);
  const template = new SandboxWorld().units.find(u => u.team === 'radiant' && u.laneId === 'bot')!;
  const ally = structuredClone(template);
  Object.assign(ally, { id: 'controlled-pull-ally', x: exit.x - 1000, y: exit.y, hp: 5000, damage: 1,
    aggroTarget: undefined, aggroUntil: 0, waypoints: [{ x: exit.x + 3000, y: exit.y }] });
  w.units.push(ally);
  w.setOrder({ type: 'pull', campId, position: exit }); w.advance(10);
  assert.ok(w.events.some(e => e.type === 'lane_pull' && e.unitId === ally.id));
  assert.equal(w.snapshot().score.totalGold, 0, 'aggro/creep-on-creep damage grants no hero reward');
  const untouched = new SandboxWorld(); untouched.units = untouched.units.filter(u => u.campId !== campId);
  untouched.player.position = { x: -9500, y: -9500 }; untouched.advance(30);
  assert.equal(untouched.events.filter(e => e.type === 'lane_pull').length, 0);
});

test('stacked bounty is reduced and only paid on a hero kill, not on marking a stack', () => {
  const w = new SandboxWorld();
  const unit = w.campUnits(campId)[0], original = unit.gold;
  unit.stacked = true; unit.gold *= SANDBOX_RULES.stackRewardMultiplier;
  w.units = [unit]; unit.hp = 1; unit.damage = 0;
  w.setOrder({ type: 'attack', targetId: unit.id }); w.advance(1);
  assert.equal(w.snapshot().score.neutralGold, original * 0.8);
  assert.equal(w.rewards.length, 1);
});

test('lane and neutral last-hit income are both scored over 180 seconds; playback and evaluation agree', () => {
  const plans = [
    step({ casts: [{ name: 'item_mask_of_madness', seconds: 0 }] }),
    step({ kind: 'lane', campId: null, laneId: 'bot', position: LANES.find(l => l.id === 'bot')!.points[3],
      duration: 40, casts: [{ name: 'item_mask_of_madness', seconds: 2 }] }),
  ];
  const evaluation = evaluateEconomyPlan(741, plans), w = new SandboxWorld(), runner = new EconomyPlanRunner(w, plans);
  for (let n = 0; n < 18; n++) runner.advance(10);
  assert.deepEqual(w.snapshot(), evaluation.finalState);
  assert.ok(evaluation.score.laneGold > 0); assert.ok(evaluation.score.neutralGold > 0);
  assert.equal(evaluation.score.totalGold, evaluation.score.laneGold + evaluation.score.neutralGold);
  assert.equal(evaluation.score.totalGpmOver180Seconds, evaluation.score.totalGold / 3);
  assert.equal(evaluation.measuredInGame, false);
});

test('last-hit mode waits for a killable unit instead of automatically pushing the wave', () => {
  const w = new SandboxWorld(), enemy = w.units.find(u => u.team === 'dire' && u.laneId === 'bot')!;
  w.units = [enemy]; Object.assign(enemy, w.player.position, { damage: 0, hp: 5000, maxHp: 5000, waypoints: [] });
  w.setOrder({ type: 'farm_lane', laneId: 'bot', position: { ...w.player.position },
    attackMode: 'last_hit', targetPriority: ['lane_ranged', 'lane_melee'] });
  w.advance(1); assert.equal(w.rewards.length, 0); assert.equal(enemy.hp, 5000);
  enemy.hp = 1; w.advance(1);
  assert.equal(w.snapshot().score.laneGold, enemy.gold);
});

test('chasing a moving lane creep never strands the hero in a blocked cell or exceeds movement budget', () => {
  const w = new SandboxWorld();
  w.player.health = 50000; w.player.maxHealth = 50000;
  w.setOrder({ type: 'farm_lane', laneId: 'bot', position: LANES.find(l => l.id === 'bot')!.points[3],
    attackMode: 'push', targetPriority: [] });
  const g = MAP.grid;
  for (let i = 0; i < 450; i++) {
    const before = { ...w.player.position }; w.advance(0.1);
    const p = w.player.position, x = Math.floor((p.x - g.origin.x) / g.cellSize), y = Math.floor((p.y - g.origin.y) / g.cellSize);
    assert.equal(g.rows[y]?.[x], '1', `unwalkable hero position at ${w.clock}`);
    assert.ok(distance(before, p) <= w.stats.moveSpeed / 10 + 1e-7);
  }
  assert.doesNotThrow(() => w.setOrder({ type: 'farm', campId: 'neutralcamp_good_4', targetPriority: [] }));
});

test('mixed planner is model-authored, preserves API score split, and never substitutes a route', async () => {
  const plan = [step({ casts: [{ name: 'item_mask_of_madness', seconds: 0 }] })];
  let requests = 0;
  const result = await reasonEconomy('fake-only', async body => {
    requests++; assert.equal(body.instructions, ECONOMY_INSTRUCTIONS);
    const args = requests === 1 ? { steps: plan } : { candidateId: 'candidate-1', summary: '仅验证 API 工具，非实战结论。' };
    if (requests > 1) {
      const feedback = JSON.parse((body.input as any[]).find(i => i.type === 'function_call_output').output);
      assert.ok('laneGold' in feedback.score); assert.ok('neutralGold' in feedback.score);
    }
    return { status: 'completed', output: [{ type: 'function_call', call_id: `t${requests}`,
      name: requests === 1 ? 'simulate_economy_plan' : 'finish_plan', arguments: JSON.stringify(args) }] };
  }, { objective: 'lane-plus-neutral-gold' }, async steps => evaluateEconomyPlan(741, steps), new AbortController().signal);
  assert.deepEqual(result.steps, plan); assert.equal(requests, 2);
  assert.throws(() => parseEconomySteps([step({ kind: 'lane', laneId: 'unknown' })]));
  assert.throws(() => parseEconomySteps([step({ casts: [{ name: 'luna_eclipse', seconds: 0 }] })]), /unsupported/);
});

test('withdrawn historical scores reproduce, but own camp IDs fail to prevent opponent-jungle contact', () => {
  const record = JSON.parse(fs.readFileSync(new URL('../data/sandbox-luna-economy-api-run.json', import.meta.url), 'utf8'));
  assert.equal(record.physicsRevision, SANDBOX_RULES.physicsRevision);
  for (const example of record.examples) {
    const result = evaluateEconomyPlan(example.seed, example.steps);
    assert.ok(Math.abs(result.score.totalGold - example.score.totalGold) < 1e-7);
    assert.equal(example.admissibility.status, 'withdrawn');
    assert.equal(result.recommendationScope.status, 'blocked');
    assert.equal(result.territoryAudit.realGameSafetyVerified, false);
  }
  assert.ok(record.examples[1].score.totalGold > record.examples[0].score.totalGold,
    'retain the unfavorable result instead of fabricating a lower-half strategy bonus');
  const initial = evaluateEconomyPlan(741, record.examples[0].steps);
  assert.ok(initial.score.laneGold > 0);
  assert.ok(initial.mechanicEvents.some(e => e.type === 'stack_spawn' && e.campId === 'neutralcamp_good_9'));
  assert.equal(initial.status, 'unavailable');
  assert.ok(record.examples[0].steps.every((s: EconomyStep) => !s.campId ||
    MAP.camps.some(c => c.id === s.campId && c.team === 'radiant')));
  assert.equal(initial.territoryAudit.status, 'violated');
  assert.ok(Math.abs(initial.territoryAudit.opponentCampGold - 13.6) < 1e-8);
  assert.equal(initial.territoryAudit.opponentCampRewards[0].campId, 'neutralcamp_evil_9');
  assert.equal(initial.territoryAudit.opponentCampRewards[0].clock, 722.5);
  assert.ok(initial.territoryAudit.opponentCampContacts.some(c => c.kind === 'proximity_aggro' && c.clock === 711.5));
  const pure = evaluateEconomyPlan(741, record.examples[1].steps);
  assert.equal(pure.territoryAudit.status, 'not-observed');
  assert.equal(pure.territoryAudit.pathAreaVerified, false, 'absence of enemy camp rewards does not certify a path');
});

test('opponent camp audit catches incidental glaives even when the primary target is an own-side neutral', () => {
  const w = new SandboxWorld(), own = w.campUnits(campId)[0];
  const other = w.units.find(u => u.campId === 'neutralcamp_evil_9')!;
  Object.assign(own, { ...w.player.position, hp: 1000, damage: 0 });
  Object.assign(other, { x: own.x + 100, y: own.y, hp: 1, damage: 0 });
  w.units = [own, other];
  w.setOrder({ type: 'attack', targetId: own.id }); w.advance(1);
  assert.ok(w.territoryAudit().opponentCampContacts.some(c => c.kind === 'hero_damage' && c.unitId === other.id));
  assert.equal(w.territoryAudit().opponentCampGold, other.gold);
});

test('proximity contact without a kill is still a scope violation; enemy lane creeps are not enemy camps', () => {
  const w = new SandboxWorld(), other = w.units.find(u => u.campId === 'neutralcamp_evil_9')!;
  Object.assign(other, { x: w.player.position.x + 400, y: w.player.position.y, hp: 10000, damage: 0 });
  w.units = [other]; w.advance(0.1);
  assert.equal(w.territoryAudit().status, 'violated');
  assert.equal(w.territoryAudit().opponentCampGold, 0);
  const lane = new SandboxWorld(), foe = lane.units.find(u => u.team === 'dire' && u.laneId)!;
  Object.assign(foe, { ...lane.player.position, hp: 1, damage: 0 }); lane.units = [foe];
  lane.setOrder({ type: 'attack', targetId: foe.id }); lane.advance(1);
  assert.ok(lane.snapshot().score.laneGold > 0);
  assert.equal(lane.territoryAudit().status, 'not-observed');
});

test('blocked advice scope refuses model calls before evaluating or restoring candidates', async () => {
  let calls = 0;
  await assert.rejects(reasonEconomy('never-called', async () => { calls++; throw new Error('must not call'); },
    { recommendationScope: SANDBOX_RECOMMENDATION_SCOPE },
    async () => { calls++; throw new Error('must not evaluate'); },
    new AbortController().signal, [[step()]]), /路线建议已撤下/);
  assert.equal(calls, 0);
});

test('refinement can compare re-evaluated prior proposals without forcing a preferred route type', async () => {
  const plan = [step({ casts: [{ name: 'item_mask_of_madness', seconds: 0 }] })];
  let calls = 0;
  const result = await reasonEconomy('fake-only', async body => {
    calls++; assert.equal(body.tool_choice, 'required');
    assert.equal((body.input as any[]).filter(i => i.role === 'user').length, 3);
    return { status: 'completed', output: [{ type: 'function_call', call_id: 'select',
      name: 'finish_plan', arguments: JSON.stringify({ candidateId: 'candidate-2', summary: '由模型选择第二个已复核对照。' }) }] };
  }, {}, async p => evaluateEconomyPlan(741, p), new AbortController().signal, [plan, plan], { refine: true, maxRounds: 3 });
  assert.equal(result.candidateId, 'candidate-2'); assert.equal(calls, 1);
});

test('v2 API keeps manual experiments isolated and rejects every route activation while area is unverified', async t => {
  const { server, token } = createSandboxServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => new Promise<void>(resolve => server.close(() => resolve())));
  const a = server.address(); assert.ok(a && typeof a === 'object');
  const api = async (path: string, body?: unknown) => {
    const r = await fetch(`http://127.0.0.1:${a.port}${path}`, { method: body ? 'POST' : 'GET',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
    assert.equal(r.status, 200); return await r.json() as any;
  };
  const steps = [step({ casts: [{ name: 'item_mask_of_madness', seconds: 0 }] })];
  await api('/api/action', { action: { type: 'farm', campId, targetPriority: [] } });
  const before = await api('/api/step', { seconds: 10 });
  const initial = await api('/api/initial'); assert.equal(initial.elapsedSeconds, 0);
  assert.equal((await api('/api/state')).elapsedSeconds, before.elapsedSeconds);
  const evaluated = await api('/api/evaluate', { steps, seed: 742 });
  assert.equal(evaluated.seed, 742); assert.equal(evaluated.objective, 'lane-plus-neutral-gold');
  const after = await api('/api/state'); assert.equal(after.seed, 741); assert.equal(after.plan, null);
  assert.equal(after.elapsedSeconds, 10);
  const context = await api('/api/context');
  assert.equal(context.examples.length, 0);
  assert.equal(context.withdrawnExamples.length, 2);
  assert.equal(context.recommendationScope.allowedArea, null);
  assert.equal(evaluated.recommendationScope.status, 'blocked');
  for (const [path, body] of [
    ['/api/load-example', { id: 'mixed-stack-example' }],
    ['/api/load-example', { id: 'selected-control' }],
    ['/api/plan', { steps, seed: 741, model: 'api-test-only', recommendationScope: { status: 'allowed' } }],
    ['/api/plan', { visits: [{ campId, targetPriority: [], casts: [] }] }],
  ] as const) {
    const response: Response = await fetch(`http://127.0.0.1:${a.port}${path}`, { method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    assert.equal(response.status, 409);
    const error = await response.json() as any; assert.equal(error.code, 'OWN_AREA_UNVERIFIED');
    assert.deepEqual(await api('/api/state'), after, 'rejected activation must not reset or mutate the user experiment');
  }
});
