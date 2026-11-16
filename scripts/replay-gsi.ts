import fs from 'node:fs';
import readline from 'node:readline';
import { TacticalEngine } from '../src/tactics/index.ts';
import { planJungleRoute } from '../src/tactics/jungle.ts';

const file = process.argv[2];
if (!file) throw new Error('Usage: npm run replay:gsi -- <sanitized-capture.jsonl> [--numeric-benchmark]');
const numericBenchmark = process.argv.includes('--numeric-benchmark');
// Offline recording inspection must never silently initiate paid requests using a server .env.
const engine = new TacticalEngine({ reasoner: null, ...(numericBenchmark ? { benchmarkPlanner: planJungleRoute } : {}) });
engine.setEnabled(true);
let frames = 0, plans = 0, previousPlan: number | null = null;
for await (const line of readline.createInterface({ input: fs.createReadStream(file), crlfDelay: Infinity })) {
  if (!line.trim()) continue;
  const record = JSON.parse(line);
  if (!record.payload || !Number.isFinite(record.receivedAt)) throw new Error(`Invalid capture line ${frames + 1}`);
  engine.ingest(record.payload, record.receivedAt);
  frames++;
  const state = engine.state();
  if (state.route && state.route.plannedAt !== previousPlan) {
    previousPlan = state.route.plannedAt; plans++;
    console.log(JSON.stringify({
      clock: state.player?.clock, status: state.route.status, next: state.route.nextAction,
      camps: state.route.steps.map(step => step.campId), seconds: state.route.seconds,
      gold: state.route.gold, xp: state.route.xp, calibration: state.calibration,
    }));
  }
}
console.error(JSON.stringify({ frames, plans, calibration: engine.state().calibration,
  mode: numericBenchmark ? 'numeric-benchmark-not-llm' : 'observations-only-no-paid-requests' }));
engine.dispose();
