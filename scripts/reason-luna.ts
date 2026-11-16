import fs from 'node:fs/promises';
import { configuredReasoner } from '../src/tactics/reasoning-planner.ts';
import { calculateSelfStats } from '../src/tactics/self-stats.ts';
import { replayModelRoute } from '../src/tactics/model-route.ts';

const reasoner = configuredReasoner();
if (!reasoner) {
  console.error('独立规划模型未配置；未发起请求，也未运行规则回退。请设置 JUNGLE_PLANNER_MODEL 和有效的独立凭据文件或 API 访问。');
  process.exitCode = 1;
} else {
  const fixture = JSON.parse(await fs.readFile(new URL('../tests/fixtures/luna-741f.json', import.meta.url), 'utf8'));
  const input = { player: fixture.player, stats: calculateSelfStats(fixture.player)!, cleared: new Map<string, number>(), recent: [] };
  const startedAt = performance.now();
  try {
    const decision = await reasoner.decide(input, AbortSignal.timeout(25_000));
    const estimate = decision.visits ? replayModelRoute(input, decision.visits) : null;
    console.log(JSON.stringify({ inputLabel: fixture.label, elapsedMs: Math.round(performance.now() - startedAt),
      paidModelDecision: decision, toolEstimateNotMeasured: estimate,
      estimatedNeutralGpmOverFullHorizon: estimate ? estimate.gold / (estimate.horizonSeconds / 60) : null,
      measuredInGame: false }, null, 2));
  } catch (error) {
    console.error(JSON.stringify({ model: reasoner.model, elapsedMs: Math.round(performance.now() - startedAt),
      error: error instanceof Error ? error.message : '规划失败', measuredInGame: false }));
    process.exitCode = 1;
  }
}
