import fs from 'node:fs/promises';
import { MAP } from '../src/tactics/combat-data.ts';
import { calculateSelfStats } from '../src/tactics/self-stats.ts';
import { CampTracker, planJungleRoute } from '../src/tactics/jungle.ts';
import type { PlayerSnapshot } from '../src/tactics/types.ts';

// Fixed, checked-in synthetic input. Deliberately not a live-game performance claim.
const input = JSON.parse(await fs.readFile(new URL('../tests/fixtures/luna-741f.json', import.meta.url), 'utf8'));
const player = input.player as PlayerSnapshot;
const stats = calculateSelfStats(player)!;
const route = planJungleRoute(player, stats, new CampTracker());
console.log(JSON.stringify({
  plannerMode: 'numeric-benchmark-not-llm',
  label: input.label, patch: MAP.patch, validation: 'synthetic-only-not-measured',
  assumptions: input.assumptions, player, stats, route,
}, null, 2));
