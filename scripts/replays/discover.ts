import fs from 'node:fs/promises';
import { replayUrl } from './download.ts';

// Bounded public catalog discovery, not replay scraping behind a login/challenge.
// Selection concerns corpus eligibility only; it does not encode a farming route.
const limit = Number(process.argv[2] || 3);
if (!Number.isInteger(limit) || limit < 1 || limit > 10) throw new Error('Discovery limit must be 1–10');
const pins = JSON.parse(await fs.readFile('data/dota2-replay-source-pins.json', 'utf8'));
async function get(url: string) {
  const response = await fetch(url, { signal: AbortSignal.timeout(20_000), redirect: 'error' });
  if (!response.ok) throw new Error(`Public API HTTP ${response.status}; do not bypass a challenge`);
  return await response.json() as any;
}
const source = 'https://api.opendota.com/api/heroes/48/matches';
const entries: any[] = [];
let discoveryError: string | null = null;
try {
  const catalog = await get(source);
  if (!Array.isArray(catalog)) throw new Error('Unexpected catalog');
  // Inspect at most 10 listings; do not crawl an unbounded history.
  for (const row of catalog.slice(0, 10)) {
    if (!Number.isSafeInteger(row.match_id) || row.start_time < pins.patchWindow.fromTimestamp) continue;
    if (pins.patchWindow.untilTimestamp && row.start_time >= pins.patchWindow.untilTimestamp) continue;
    await new Promise(resolve => setTimeout(resolve, 300));
    const details = await get(`https://api.opendota.com/api/matches/${row.match_id}`);
    const luna = details.players?.find((p: any) => p.hero_id === 48);
    if (!luna || details.match_id !== row.match_id || details.league?.tier !== 'professional' ||
        typeof luna.name !== 'string' || !luna.name.trim() || !Number.isInteger(luna.rank_tier) || luna.rank_tier < 80) continue;
    let available = false;
    try { replayUrl(details.replay_url, row.match_id); available = true; } catch { /* Never guess salt/URL. */ }
    entries.push({ matchId: row.match_id, startTime: details.start_time, playerName: luna.name || null,
      accountId: luna.account_id, rankTier: luna.rank_tier ?? null, league: details.league.name,
      replayUrlPresent: available, status: 'candidate-only-needs-download-build-and-completeness-check' });
    if (entries.length === limit) break;
  }
} catch (error) {
  discoveryError = error instanceof Error ? error.message : 'Public discovery failed';
  process.exitCode = 1;
}
const result = { source, checkedAt: new Date().toISOString(), patchWindow: pins.patchWindow, entries,
  status: discoveryError ? 'partial-error' : 'complete-bounded-query', error: discoveryError,
  note: 'Professional league/provider name is recorded evidence, not independently authenticated identity or exact MMR. Subpatch is not certified until binary build is checked.' };
await fs.mkdir('.tmp/replays', { recursive: true });
await fs.writeFile('.tmp/replays/candidates.json', JSON.stringify(result, null, 2) + '\n');
console.log(JSON.stringify(result, null, 2));
