import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { setImmediate } from 'node:timers/promises';
import { COMBAT, MAP } from '../src/tactics/combat-data.ts';
import { calculateSelfStats } from '../src/tactics/self-stats.ts';
import { TacticalEngine } from '../src/tactics/index.ts';
import { configuredReasoner, createResponsesReasoner, PLANNER_INSTRUCTIONS } from '../src/tactics/reasoning-planner.ts';
import type { ModelDecision, RouteReasoner } from '../src/tactics/reasoning-planner.ts';
import { parseModelVisits, replayModelRoute } from '../src/tactics/model-route.ts';
import type { ModelVisit, PlanningInput } from '../src/tactics/model-route.ts';
import type { PlayerSnapshot } from '../src/tactics/types.ts';
import { initialResources, simulateClear } from '../src/tactics/clear-model.ts';

const camp = MAP.camps.find(c => c.id === 'neutralcamp_good_5')!;
const player: PlayerSnapshot = {
  hero: 'npc_dota_hero_luna', team: 'radiant', position: camp, clock: 600, level: 10,
  health: 5000, maxHealth: 5000, mana: 1000, maxMana: 1000, alive: true, canAct: true, lastHits: 100,
  abilities: [{ name: 'luna_moon_glaive', level: 4, cooldown: 0 }, { name: 'luna_lucent_beam', level: 4, cooldown: 0 }],
  items: [{ name: 'item_mask_of_madness', passive: true, cooldown: 0 }],
};
const input = (): PlanningInput => ({ player: structuredClone(player), stats: calculateSelfStats(player)!, cleared: new Map(), recent: [] });
const visit = (id = camp.id): ModelVisit => ({ campId: id, casts: [], targetPriority: [] });
const call = (name: string, args: unknown, id = 'tool-1') => ({
  status: 'completed', output: [{ type: 'function_call', name, arguments: JSON.stringify(args), call_id: id }],
});
const decision = (visits = [visit()]): ModelDecision => ({ id: 'model-decision-test', model: 'test-model',
  visits, summary: '这是测试模型明确提出的路线，不是服务端候选排名。', evidenceIds: [] });
const payload = (overrides: Partial<PlayerSnapshot> = {}) => {
  const p = { ...player, ...overrides };
  return {
    map: { clock_time: p.clock, matchid: 'reasoner-test' }, player: { team_name: p.team, last_hits: p.lastHits },
    hero: { name: p.hero, xpos: p.position.x, ypos: p.position.y, level: p.level, health: p.health, max_health: p.maxHealth,
      mana: p.mana, max_mana: p.maxMana, alive: p.alive },
    abilities: Object.fromEntries(p.abilities.map((a, i) => [`ability${i}`, a])),
    items: Object.fromEntries(p.items.map((a, i) => [`slot${i}`, a])),
  };
};
const settle = async () => { for (let i = 0; i < 4; i++) await setImmediate(); };

test('runtime has no numeric route fallback when the independent model is not configured', () => {
  assert.equal(configuredReasoner({}), null);
  assert.equal(configuredReasoner({ JUNGLE_PLANNER_PROVIDER: 'openai', JUNGLE_PLANNER_MODEL: 'gpt-6-astra' }), null);
  const engine = new TacticalEngine({ reasoner: null });
  engine.setEnabled(true); engine.ingest(payload(), 1000);
  assert.equal(engine.state().route, null);
  assert.equal(engine.state().planner?.kind, 'llm');
  assert.equal(engine.state().planner?.configured, false);
  assert.match(engine.state().status, /不会回退到规则/);
});

test('dedicated Foundry file supplies paired route credentials without using or changing voice credentials', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'jungle-foundry-'));
  const filename = path.join(directory, 'planner.env');
  await fs.writeFile(filename, [
    'FOUNDRY_ENDPOINT=https://planner-only.services.ai.azure.com/api/projects/planner',
    'AOAI_ENDPOINT=https://planner-only.openai.azure.com/openai/v1',
    'FOUNDRY_KEY=fake-planner-key',
  ].join('\n'), { mode: 0o600 });
  t.after(async () => { await fs.unlink(filename); await fs.rmdir(directory); });
  const env = {
    JUNGLE_PLANNER_PROVIDER: 'azure', JUNGLE_PLANNER_MODEL: 'actual-planner-deployment',
    JUNGLE_PLANNER_FOUNDRY_ENV_FILE: filename, AZURE_API_KEY: 'fake-default-voice-key',
  };
  let requests = 0;
  t.mock.method(globalThis, 'fetch', async (url: URL, options: RequestInit) => {
    requests++;
    assert.equal(url.href, 'https://planner-only.openai.azure.com/openai/v1/responses');
    assert.equal(new Headers(options.headers).get('api-key'), 'fake-planner-key');
    assert.equal(options.redirect, 'error');
    assert.equal(JSON.parse(String(options.body)).model, 'actual-planner-deployment');
    return Response.json(call('finish_plan', { candidateId: null, summary: '仅验证独立连接。', evidenceIds: [] }));
  });
  const before = JSON.stringify(env);
  const reasoner = configuredReasoner(env)!;
  assert.equal(reasoner.model, 'actual-planner-deployment');
  const result = await reasoner.decide(input(), new AbortController().signal);
  assert.equal(requests, 1);
  assert.equal(JSON.stringify(env), before);
  assert.ok(!JSON.stringify(result).includes('fake-planner-key'));
  assert.ok(!JSON.stringify(result).includes('planner-only'));

  for (const override of [
    { JUNGLE_PLANNER_PROVIDER: 'openai' },
    { JUNGLE_PLANNER_ENDPOINT: 'https://other.example/responses' },
    { JUNGLE_PLANNER_API_KEY: 'different-key' },
    { JUNGLE_PLANNER_FOUNDRY_ENV_FILE: path.join(directory, 'missing.env') },
  ]) assert.equal(configuredReasoner({ ...env, ...override }), null, 'invalid dedicated configuration fails closed');
  await fs.writeFile(filename, 'FOUNDRY_KEY=fake-secret-without-endpoints');
  assert.equal(configuredReasoner(env), null);
  assert.equal(requests, 1, 'bad files/overrides must not invoke a default resource');
});

test('model receives full allied catalog and cases, authors a route, gets tool feedback, then chooses it', async () => {
  let round = 0;
  const far = MAP.camps.find(c => c.id === 'neutralcamp_good_1')!;
  const reasoner = createResponsesReasoner('test-only-model', async body => {
    round++;
    assert.equal(body.store, false);
    assert.equal(body.max_output_tokens, 3000);
    assert.equal(body.instructions, PLANNER_INSTRUCTIONS);
    const messages = body.input as any[];
    const context = JSON.parse(messages[0].content);
    assert.equal(context.camps.length, 14, 'no nearest-eight shortlist');
    assert.ok(context.replayCases.length >= 3);
    assert.ok(!('candidateRoutes' in context));
    assert.ok(!JSON.stringify(body).includes('steamid'));
    if (round === 1) return call('simulate_route', { visits: [visit(far.id)] });
    const feedback = JSON.parse(messages.find(m => m.type === 'function_call_output').output);
    assert.equal(feedback.steps[0].campId, far.id, 'verifier must not replace the model choice with the nearest camp');
    assert.ok(feedback.gold > 0);
    return call('finish_plan', { candidateId: feedback.candidateId, summary: '选择该野点；收益是工具估计，非实测。', evidenceIds: [], gold: 999999 }, 'tool-2');
  });
  const result = await reasoner.decide(input(), new AbortController().signal);
  assert.equal(round, 2);
  assert.equal(result.visits![0].campId, far.id);
  const replay = replayModelRoute(input(), result.visits!);
  assert.notEqual(replay.gold, 999999, 'model-authored money is never used');
  assert.equal(replay.search?.expanded, 0);
  assert.equal(replay.search?.beamWidth, 0);
});

test('unsupported model proposals get explicit feedback rather than invented buffs or substitute routes', async () => {
  let round = 0;
  const reasoner = createResponsesReasoner('fake', async body => {
    if (++round === 1) return call('simulate_route', {
      visits: [{ ...visit(), casts: [{ name: 'luna_lunar_orbit', seconds: 0 }] }],
    });
    const output = JSON.parse((body.input as any[]).find(m => m.type === 'function_call_output').output);
    assert.match(output.error, /unsupported/);
    assert.equal(output.routeWasNotSubstituted, true);
    return call('finish_plan', { candidateId: null, summary: '环月尚未适配，不能据此证明收益。', evidenceIds: [] });
  });
  assert.equal((await reasoner.decide(input(), new AbortController().signal)).visits, null);
});

test('unverified candidate IDs, invented evidence and malformed tool results cannot finish a plan', async () => {
  const bad = createResponsesReasoner('fake', async () => call('finish_plan', { candidateId: 'invented', summary: 'pretend', evidenceIds: [] }));
  await assert.rejects(bad.decide(input(), new AbortController().signal), /4 轮/);
  const evidence = createResponsesReasoner('fake', async () => call('finish_plan', { candidateId: null, summary: 'pretend', evidenceIds: ['fabricated-video'] }));
  await assert.rejects(evidence.decide(input(), new AbortController().signal), /4 轮/);
  const incomplete = createResponsesReasoner('fake', async () => ({ status: 'incomplete', output: [] }));
  await assert.rejects(incomplete.decide(input(), new AbortController().signal), /输出预算/);
});

test('explicit model schedules do not invoke automatic spell or item policies', () => {
  const result = replayModelRoute(input(), [visit()]);
  assert.deepEqual(result.steps[0].casts, {});
  const unit = { ...COMBAT.neutrals.npc_dota_neutral_kobold, name: 'dummy', health: 200, armor: 0, magicResistance: 0, damage: 0, healthRegen: 0, abilities: [] };
  const custom = { ...COMBAT, neutrals: { dummy: unit } };
  const pack = { id: 'test', kind: 'small' as const, units: { dummy: 1 } };
  const explicit = simulateClear(player, calculateSelfStats(player)!, pack, 600, initialResources(player), 1, custom,
    { scheduledCasts: [{ name: 'luna_lucent_beam', seconds: 2 }], targetPriority: [], limitSeconds: 1 });
  assert.deepEqual(explicit.casts, {}, 'must not cast early just because a spell is ready');
  assert.equal(explicit.readyAt.item_mask_of_madness, 600, 'must not auto-activate the mask');
});

test('async model results are replayed from latest position/resources, not shifted from the old snapshot', async () => {
  let now = 1000, resolve!: (value: ModelDecision) => void;
  const reasoner: RouteReasoner = { model: 'test', decide: async () => new Promise(r => { resolve = r; }) };
  const engine = new TacticalEngine({ reasoner, now: () => now });
  let publications = 0; engine.onChange = () => publications++;
  engine.setEnabled(true); engine.ingest(payload(), now); await settle();
  assert.equal(engine.state().planner?.pending, true); assert.equal(engine.state().route, null);
  now = 2000;
  const next = MAP.camps.find(c => c.id === 'neutralcamp_good_9')!;
  engine.ingest(payload({ position: next, clock: 601, health: 4600 }), now);
  resolve(decision()); await settle();
  const route = engine.state().route!;
  assert.equal(route.plannedAt, 601);
  assert.ok(route.steps[0].travelSeconds > 0);
  assert.equal(route.reasoning?.decisionId, 'model-decision-test');
  assert.equal(engine.state().planner?.pending, false);
  assert.equal(publications, 1);
  engine.dispose();
});

test('disable, death, new match, missing GSI and stale-data expiry cancel pending model work', async () => {
  for (const stop of ['disable', 'death', 'match', 'missing', 'stale']) {
    let now = 1000, resolve!: (value: ModelDecision) => void, signal!: AbortSignal;
    const engine = new TacticalEngine({ now: () => now, reasoner: { model: 'test',
      decide: async (_input, s) => { signal = s; return new Promise(r => { resolve = r; }); } } });
    engine.setEnabled(true); engine.ingest(payload(), now); await settle();
    now = 2000;
    if (stop === 'disable') engine.setEnabled(false);
    if (stop === 'death') engine.ingest(payload({ alive: false }), now);
    if (stop === 'match') engine.ingest({ ...payload(), map: { matchid: 'another', clock_time: 0 } }, now);
    if (stop === 'missing') engine.ingest({}, now);
    if (stop === 'stale') { now = 10000; engine.expire(now); }
    assert.equal(signal.aborted, true, stop);
    resolve(decision()); await settle();
    assert.equal(engine.state().route, null, stop);
    engine.dispose();
  }
});

test('repeated requests and ordinary GSI do not create paid request storms or bypass cooldown by toggling', async () => {
  let now = 1000, calls = 0;
  const engine = new TacticalEngine({ now: () => now, reasoner: { model: 'test', async decide() { calls++; return decision(); } } });
  engine.setEnabled(true); engine.ingest(payload(), now); await settle();
  for (let i = 0; i < 20; i++) { now += 500; engine.ingest(payload({ clock: 600 + i }), now); engine.requestRoute(now); }
  await settle(); assert.equal(calls, 1);
  engine.setEnabled(false); engine.setEnabled(true); now += 1000; engine.ingest(payload(), now); await settle();
  assert.equal(calls, 1);
  now = 32000; engine.ingest(payload({ clock: 631 }), now); await settle();
  assert.equal(calls, 2);
  engine.dispose();
});

test('route format and foreign camps are rejected without a hidden replacement search', () => {
  assert.throws(() => parseModelVisits([]));
  assert.throws(() => parseModelVisits([{ ...visit(), casts: [{ name: 'luna_lucent_beam', seconds: -1 }] }]));
  assert.throws(() => replayModelRoute(input(), [visit('neutralcamp_evil_1')]), /非己方/);
});

test('model orchestration aborts even if an authentication/transport adapter has not settled', async () => {
  const controller = new AbortController();
  const reasoner = createResponsesReasoner('fake', async () => new Promise(() => {}));
  const result = reasoner.decide(input(), controller.signal);
  controller.abort(new Error('test deadline'));
  await assert.rejects(result, /test deadline/);
});

test('last model request is reserved for choosing a tested candidate, without automatically selecting by score', async () => {
  let round = 0;
  const reasoner = createResponsesReasoner('fake', async body => {
    round++;
    if (round < 3) {
      assert.equal(body.tool_choice, 'required');
      return call('simulate_route', { visits: [visit()] }, `trial-${round}`);
    }
    assert.deepEqual(body.tool_choice, { type: 'function', name: 'finish_plan' });
    return call('finish_plan', { candidateId: 'candidate-1', summary: '由模型提交已经验证的第一个方案。', evidenceIds: [] });
  }, { maxRounds: 3 });
  assert.deepEqual((await reasoner.decide(input(), new AbortController().signal)).visits, [visit()]);
  assert.equal(round, 3);
});
