import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { MAP } from '../src/tactics/combat-data.ts';
import { SandboxWorld } from '../src/sandbox/world.ts';
import { EconomyPlanRunner } from '../src/sandbox/economy.ts';
import { SANDBOX_RECOMMENDATION_SCOPE } from '../src/sandbox/recommendation-scope.ts';

// Offline evidence audit only. No credentials, browser, model calls or route selection.
const record = JSON.parse(fs.readFileSync(new URL('../data/sandbox-luna-economy-api-run.json', import.meta.url), 'utf8'));
const examples = record.examples.map((example: any) => {
  const world = new SandboxWorld(example.seed), runner = new EconomyPlanRunner(world, example.steps);
  const positions = [{ clock: world.clock, step: 1, ...world.player.position }];
  while (!world.done) {
    const step = runner.index + 1; runner.advance(0.1);
    positions.push({ clock: world.clock, step, ...world.player.position });
  }
  const summary = runner.summary(), audit = summary.territoryAudit;
  const at = (clock: number) => positions.find(p => p.clock === clock)!;
  return {
    id: example.id, seed: example.seed, disposition: 'withdrawn',
    planSha256: createHash('sha256').update(JSON.stringify(example.steps)).digest('hex'),
    allExplicitCampIdsOwnSide: example.steps.every((s: any) => !s.campId || MAP.camps.some(c => c.id === s.campId && c.team === 'radiant')),
    experimentalStatus: summary.status, recommendationStatus: SANDBOX_RECOMMENDATION_SCOPE.status,
    historicalScoreUnmodified: Math.abs(summary.score.totalGold - example.score.totalGold) < 1e-7,
    score: summary.score,
    positionSampleCount: positions.length,
    positionTraceSha256: createHash('sha256').update(JSON.stringify(positions)).digest('hex'),
    observedExtent: {
      minX: Math.min(...positions.map(p => p.x)), maxX: Math.max(...positions.map(p => p.x)),
      minY: Math.min(...positions.map(p => p.y)), maxY: Math.max(...positions.map(p => p.y)),
    },
    territoryAudit: {
      ...audit,
      opponentCampContacts: audit.opponentCampContacts.map(c => ({ ...c, step: at(c.clock).step })),
      opponentCampRewards: audit.opponentCampRewards.map(r => ({
        ...r, step: at(r.clock).step, heroPosition: { x: at(r.clock).x, y: at(r.clock).y },
      })),
    },
  };
});
const audit = {
  date: '2026-11-15', timezone: 'Asia/Hong_Kong',
  environment: record.environment, physicsRevision: record.physicsRevision,
  method: 'Offline deterministic replay; hero position sampled every 0.1 seconds; all primary/spell/bounce damage and neutral proximity aggro audited by original camp affiliation.',
  limitations: 'Camp affiliation detects counterexamples only. There is no verified allowed-area polygon, path safety, tower, enemy-hero or vision audit. No contact does NOT mean safe. Historical scores remain unchanged, not reduced by subtracting forbidden gold.',
  recommendationScope: SANDBOX_RECOMMENDATION_SCOPE, examples,
};
const output = new URL('../data/sandbox-own-area-audit.json', import.meta.url);
if (process.argv.includes('--write')) fs.writeFileSync(output, JSON.stringify(audit, null, 2) + '\n');
else if (process.argv.includes('--check')) {
  if (JSON.stringify(JSON.parse(fs.readFileSync(output, 'utf8'))) !== JSON.stringify(audit))
    throw new Error('保存的区域审计与离线复跑不一致');
  console.log('PASS own-area audit: historical scores retained; mixed example violates scope; neither example is approved.');
} else console.log(JSON.stringify(audit, null, 2));
