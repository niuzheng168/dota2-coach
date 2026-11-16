import fs from 'node:fs/promises';
import { reasonEconomy } from '../src/sandbox/economy-reasoner.ts';

// Explicit command only. The UI/server never loads cloud credentials or runs a paid loop.
const session = JSON.parse(await fs.readFile(new URL('../.tmp/sandbox-session.json', import.meta.url), 'utf8'));
const origin = new URL(session.origin);
if (origin.protocol !== 'http:' || origin.hostname !== '127.0.0.1' || origin.username || origin.password ||
    origin.pathname !== '/' || origin.search || origin.hash || typeof session.token !== 'string')
  throw new Error('只允许调用本机沙盒 API');
const signal = AbortSignal.timeout(180_000);
async function api(path: string, body?: unknown): Promise<any> {
  const res = await fetch(new URL(path, origin), { method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: `Bearer ${session.token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body), signal, redirect: 'error' });
  const value = await res.json() as any;
  if (!res.ok) throw new Error(value.error || `沙盒 API HTTP ${res.status}`);
  return value;
}
const context = await api('/api/context'), baseline = await api('/api/initial');
if (context.rules.version !== 'luna-sandbox-v2-lane-pull') throw new Error('请先重启到线野 v2 沙盒，不在旧环境上声称拉野实验');
// Check before reading cloud credentials, previous reports, or making any paid request.
// Missing scope from an old server is also a refusal, not permission to proceed.
if (context.recommendationScope?.status !== 'allowed')
  throw new Error(context.recommendationScope?.reason || '旧沙盒缺少可活动区域校验，未启动付费规划');
const { configuredResponsesClient, REPLAY_CASES } = await import('../src/tactics/reasoning-planner.ts');
const client = configuredResponsesClient();
if (!client) throw new Error('请先配置独立规划模型和 Foundry 凭据文件');
const reportUrl = new URL('../.tmp/sandbox-economy-model-report.json', import.meta.url);
let previousSteps = [];
const refining = process.argv.includes('--refine');
if (process.argv.includes('--finish-existing') || refining) {
  const previous = JSON.parse(await fs.readFile(reportUrl, 'utf8'));
  if (previous.seed !== baseline.seed || previous.environment !== context.rules.version ||
      previous.mechanics?.physicsRevision !== context.rules.physicsRevision ||
      previous.objective !== 'lane-plus-neutral-gold' || !Array.isArray(previous.trials))
    throw new Error('旧报告的版本、种子或收益目标不一致');
  previousSteps = refining && previous.decision?.steps ? [previous.decision.steps]
    : previous.trials.filter((t: any) => !t.error).map((t: any) => t.plan);
}
if (process.argv.includes('--compare-baseline')) {
  const old = JSON.parse(await fs.readFile(new URL('../data/sandbox-luna-api-run.json', import.meta.url), 'utf8'));
  previousSteps.push(old.selected.visits.map((v: any) => ({ kind: 'camp', campId: v.campId, laneId: null,
    position: null, duration: 90, untilClock: null, targetPriority: v.targetPriority, attackMode: 'push', casts: v.casts })));
}
const trials: any[] = [], requests: any[] = [];
const started = performance.now();
try {
  const decision = await reasonEconomy(client.model, async (body, requestSignal) => {
    const at = performance.now(), response = await client.transport(body, requestSignal);
    requests.push({ elapsedMs: Math.round(performance.now() - at), status: response.status, usage: response.usage,
      tools: response.output?.filter((o: any) => o.type === 'function_call').map((o: any) => o.name) });
    return response;
  }, {
    environment: context.rules, objective: { metric: 'lane-plus-neutral-gold', horizonSeconds: 180, minimumHealthFraction: 0.3 },
    recommendationScope: context.recommendationScope,
    userHypothesis: '仅在已核验的己方可活动区域内发育；下半区线野循环、刷新与拉野的具体选择交给模型比较，不提供固定路线。',
    player: baseline.player, stats: baseline.stats, readyAt: baseline.readyAt,
    lanes: context.lanes, laneSummary: baseline.laneSummary, spawnState: baseline.spawnState,
    camps: context.camps.filter((c: any) => c.team === baseline.player.team),
    campExits: context.campExits.filter((e: any) => context.camps.some((c: any) => c.id === e.campId && c.team === baseline.player.team)),
    visibleUnits: baseline.units.filter((u: any) => u.laneId || context.camps.some((c: any) => c.id === u.campId && c.team === baseline.player.team))
      .map(({ waypoints: _waypoints, ...u }: any) => u),
    mechanics: context.mechanics, replayCases: REPLAY_CASES,
    limitations: ['No enemy heroes/towers or vision uncertainty; do not infer real-match safety from the toy world.',
      'No fixed-region bonus and no artificial long-route penalty. Evaluate actual time/resource outcomes.'],
  }, async steps => {
    let result;
    try { result = await api('/api/evaluate', { seed: baseline.seed, steps }); }
    catch (error) {
      trials.push({ trial: trials.length + 1, plan: steps, error: error instanceof Error ? error.message : 'API 仿真失败' });
      throw error;
    }
    const { finalState: _state, ...metrics } = result;
    trials.push({ trial: trials.length + 1, plan: steps, ...metrics });
    console.error(`线野提案 ${trials.length}: 野 ${result.score.neutralGold.toFixed(1)} + 线 ${result.score.laneGold.toFixed(1)} = ${result.score.totalGold.toFixed(1)}；${result.status}`);
    return result;
  }, signal, previousSteps, refining ? { refine: true, maxRounds: 3 } : {});
  if (decision.steps) await api('/api/plan', { seed: baseline.seed, steps: decision.steps,
    model: decision.model, summary: decision.summary });
  const result = { objective: 'lane-plus-neutral-gold', environment: context.rules.version, mechanics: context.rules, seed: baseline.seed,
    model: client.model, elapsedMs: Math.round(performance.now() - started), trials, requests, decision, measuredInGame: false };
  await fs.writeFile(reportUrl, JSON.stringify(result, null, 2));
  console.log(JSON.stringify({ model: client.model, appliedPlan: !!decision.steps, score: decision.verified?.score,
    summary: decision.summary, elapsedMs: result.elapsedMs, measuredInGame: false }, null, 2));
} catch (error) {
  const result = { objective: 'lane-plus-neutral-gold', environment: context.rules.version, mechanics: context.rules, seed: baseline.seed,
    model: client.model, elapsedMs: Math.round(performance.now() - started), trials, requests,
    error: error instanceof Error ? error.message : '线野实验失败',
    errorCode: error instanceof Error ? (error.cause as { code?: string } | undefined)?.code : undefined,
    measuredInGame: false };
  await fs.writeFile(reportUrl, JSON.stringify(result, null, 2));
  console.error(result.error); process.exitCode = 1;
}
