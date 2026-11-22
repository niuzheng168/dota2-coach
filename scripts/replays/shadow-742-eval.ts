/** Evaluation only. This module is never imported by the player-facing observer. */
import assert from 'node:assert/strict';
import {
  observeShadow742, SHADOW742, shadow742BroadMajority, shadow742Family, shadow742HistoricalRanks,
  shadow742HistoricalVotes, shadow742History, shadow742Rate,
} from '../../src/tactics/shadow-742.ts';
import type {
  Shadow742Case, Shadow742Family, Shadow742Game, Shadow742Hero, Shadow742Index, Shadow742Team, Shadow742Window,
} from '../../src/tactics/shadow-742.ts';

const unique = (ids: string[]) => [...new Set(ids)].sort();
const accounts = (games: readonly Shadow742Game[]) => unique(games.flatMap(g => g.tracks.map(p => p.accountId)));
const ids = (games: readonly Shadow742Game[]) => games.map(g => g.id);

/** Split before looking at labels. Game is indivisible, including multi-track games. */
export function shadow742Fold(index: Shadow742Index, test: Shadow742Game[]) {
  const cutoffUnix = Math.min(...test.map(g => g.start));
  const testIds = new Set(ids(test)), testAccounts = new Set(accounts(test));
  const train = shadow742History(index, cutoffUnix, testIds, testAccounts);
  const prior = index.games.filter(g => g.start < cutoffUnix && !testIds.has(g.id));
  assert(train.every(g => !testIds.has(g.id) && g.endUnix !== null && g.endUnix < cutoffUnix));
  assert(accounts(train).every(a => !testAccounts.has(a)));
  return { test, train, cutoffUnix,
    audit: { cutoffUnix, cutoffUTC: new Date(cutoffUnix * 1000).toISOString(),
      latestTrainEndUnix: train.length ? Math.max(...train.map(g => g.endUnix!)) : null,
      trainMatchIds: ids(train), trainAccountIds: accounts(train), testMatchIds: ids(test), testAccountIds: [...testAccounts],
      pastGamesExcludedForAccount: ids(prior.filter(g => g.tracks.some(p => testAccounts.has(p.accountId)))),
      pastGamesExcludedForUnknownOrOverlappingEnd: ids(prior.filter(g => g.endUnix === null || g.endUnix >= cutoffUnix)),
      matchOverlap: 0, focalAccountOverlap: 0 } };
}
export function shadow742FrozenFold(index: Shadow742Index) {
  const n = index.games.length;
  const cutoff = index.games[n - Math.ceil(n / 4)].start;
  return shadow742Fold(index, index.games.filter(g => g.start >= cutoff));
}

/**
 * Frequency-only comparator on the SAME isolated train archive. All eligible
 * clocks within ±90s contribute; give each account total mass one regardless of
 * the number of matches/windows it had. No self position, inventory or test labels.
 */
function clockModel(train: Shadow742Game[], hero: Shadow742Hero, team: Shadow742Team, t: number) {
  const players = new Map<string, string[]>();
  for (const game of train) for (const p of game.tracks) {
    if (p.hero !== hero || p.team !== team) continue;
    for (const w of p.windows) if (w.prefix && w.label && Math.abs(w.timeSec - t) <= SHADOW742.clockRadiusSec) {
      const samples = players.get(p.accountId) ?? []; samples.push(w.label.site); players.set(p.accountId, samples);
    }
  }
  if (players.size < SHADOW742.minAccounts) return { sites: [], broad: null };
  const mass = new Map<string, number>();
  for (const samples of players.values()) for (const site of samples) if (!site.startsWith('@'))
    mass.set(site, (mass.get(site) ?? 0) + 1 / samples.length);
  const familyMass = { lane: 0, 'own-camp': 0 };
  for (const [site, weight] of mass) {
    const family = shadow742Family(site, team); if (family) familyMass[family] += weight;
  }
  const broad = (['lane', 'own-camp'] as const).find(f => 2 * familyMass[f] > players.size + 1e-10) ?? null;
  return { sites: [...mass].sort(([a, x], [b, y]) => Math.abs(x - y) < 1e-10 ? a.localeCompare(b, 'en') : y - x).map(([site]) => site), broad };
}
export function shadow742ClockBaseline(train: Shadow742Game[], hero: Shadow742Hero, team: Shadow742Team, t: number) {
  return clockModel(train, hero, team, t).sites;
}
interface Row {
  match: string; account: string; cluster: string; hero: Shadow742Hero; team: Shadow742Team; t: number;
  span: Shadow742Window['span']; self: Shadow742Window['selfState']; label: string | null;
  retrieval: string[]; baseline: string[]; retrievalDisposition: string; displayed: boolean;
  broadRetrieval: Shadow742Family | null; broadBaseline: Shadow742Family | null; broadDisposition: string;
}
interface AuditExample {
  matchId: string; accountId: string; hero: Shadow742Hero; team: Shadow742Team; t: number;
  historicalRank: string[]; baselineRank: string[]; broad: Shadow742Family | null; broadBaseline: Shadow742Family | null;
  observedLater: string; observedAtSec: number | null; voters: Shadow742Case[];
}
// Same account across games OR two focal accounts sharing a test game stay in
// one bootstrap unit. This reduces to focal account in the fixed 16-game set.
function clusterIds(games: Shadow742Game[]) {
  const parents = new Map<string, string>();
  function root(a: string): string {
    const b = parents.get(a);
    if (b === undefined) { parents.set(a, a); return a; }
    if (a === b) return a;
    const r = root(b); parents.set(a, r); return r;
  }
  for (const game of games) {
    const group = game.tracks.map(p => p.accountId);
    for (const a of group) parents.set(root(a), root(group[0]));
  }
  return new Map([...parents.keys()].map(a => [a, root(a)]));
}

function scoreFold(fold: ReturnType<typeof shadow742Fold>, clusters: Map<string, string>,
  examples: AuditExample[]): Row[] {
  const rows: Row[] = [], cache = new Map<string, ReturnType<typeof clockModel>>();
  const archive: Shadow742Index = { patch: '7.42', games: fold.train };
  for (const game of fold.test) for (const p of game.tracks) for (const w of p.windows) {
    const row: Row = { match: game.id, account: p.accountId, cluster: clusters.get(p.accountId)!,
      hero: p.hero, team: p.team, t: w.timeSec, span: w.span, self: w.selfState, label: w.label?.site ?? null,
      retrieval: [], baseline: [], retrievalDisposition: w.span !== 'full' ? w.span : w.selfState, displayed: false,
      broadRetrieval: null, broadBaseline: null, broadDisposition: w.span !== 'full' ? w.span : w.selfState };
    if (w.span === 'full' && w.prefix) {
      // Only query prefix/identity go into either scorer, never w.label, duration,
      // t+1 position, inferred visible targets or death coordinates.
      const votes = shadow742HistoricalVotes(fold.train, p.hero, p.team, w.timeSec, w.prefix);
      row.retrieval = shadow742HistoricalRanks(votes, p.team).map(r => r.targetId);
      row.retrievalDisposition = row.retrieval.length ? 'ranked' :
        votes.cases.length < SHADOW742.minAccounts ? 'too-few-independent-neighbours' : 'only-nontarget-outcomes';
      row.broadRetrieval = shadow742BroadMajority(votes, p.team)?.family ?? null;
      row.broadDisposition = row.broadRetrieval ? 'ranked' : votes.cases.length < SHADOW742.minAccounts ?
        'too-few-independent-neighbours' : 'no-strict-majority';
      const key = `${p.hero}:${p.team}:${w.timeSec}`;
      if (!cache.has(key)) cache.set(key, clockModel(fold.train, p.hero, p.team, w.timeSec));
      const baseline = cache.get(key)!;
      row.baseline = baseline.sites; row.broadBaseline = baseline.broad;
      // Literal public gate with the data that actually exists, not an oracle
      // mask assembled from this player's future, nearby camps or a killed unit.
      const output = observeShadow742(archive, { patch: '7.42', matchId: game.id, matchStartUnix: game.start, timeSec: w.timeSec,
        informationScope: 'player-visible', hero: p.hero, team: p.team, accountId: p.accountId,
        self: { observedAtSec: w.prefix.observedAtSec, alive: true, position: { x: w.prefix.x, y: w.prefix.y } },
        visibleTargets: [] });
      row.displayed = output.status === 'observed';
      assert(!row.displayed && output.candidates.length === 0 && output.broadCandidates.length === 0,
        'Inferred a visible target without a POV observation');
      if (row.retrieval.length && !examples.some(e => e.hero === p.hero)) {
        examples.push({ matchId: game.id, accountId: p.accountId, hero: p.hero, team: p.team, t: w.timeSec,
          historicalRank: row.retrieval, baselineRank: row.baseline.slice(0, 3), broad: row.broadRetrieval, broadBaseline: row.broadBaseline, observedLater: w.label!.site,
          observedAtSec: w.label!.atSec, voters: votes.cases });
      }
    }
    rows.push(row);
  }
  return rows;
}

const draws = 2000, seed = 0x74251a;
/** Percentile interval for ratios (including paired signed differences).
 * Resample independent test clusters, never individual 30-second windows. */
function ratio(rows: Row[], denominator: (v: Row) => boolean, value: (v: Row) => number) {
  const groups = new Map<string, [number, number]>();
  let n = 0, d = 0;
  for (const r of rows) if (denominator(r)) {
    const y = value(r), cell = groups.get(r.cluster) ?? [0, 0];
    cell[0] += y; cell[1]++; groups.set(r.cluster, cell); n += y; d++;
  }
  const cells = [...groups].sort(([a], [b]) => a.localeCompare(b, 'en')).map(([, c]) => c);
  let ci95: [number, number] | null = null;
  if (cells.length >= 2) {
    const samples: number[] = []; let rng = seed;
    for (let b = 0; b < draws; b++) {
      let num = 0, den = 0;
      for (let j = 0; j < cells.length; j++) {
        rng = (Math.imul(1664525, rng) + 1013904223) >>> 0;
        const [a, c] = cells[Math.floor(rng / 2 ** 32 * cells.length)]; num += a; den += c;
      }
      samples.push(shadow742Rate(num, den)!);
    }
    samples.sort((a, b) => a - b);
    ci95 = [samples[Math.floor(.025 * (draws - 1))], samples[Math.ceil(.975 * (draws - 1))]];
  }
  return { numerator: n, denominator: d, rate: shadow742Rate(n, d), clusters: cells.length, ci95 };
}
function counts(keys: string[]) {
  const bins: Record<string, number> = {};
  for (const k of keys) bins[k] = (bins[k] ?? 0) + 1;
  return bins;
}
const full = (r: Row) => r.span === 'full';
const freshAlive = (r: Row) => full(r) && r.self === 'alive';
const resolved = (r: Row) => full(r) && r.label !== null && r.label !== '@unresolved';
function method(rows: Row[], key: 'retrieval' | 'baseline') {
  const issued = (r: Row) => Number(r[key].length > 0);
  const scored = (r: Row) => resolved(r) && issued(r) > 0;
  const hit1 = (r: Row) => Number(r[key].length > 0 && r[key][0] === r.label);
  return { coverage: ratio(rows, full, issued), coverageAmongFreshAlive: ratio(rows, freshAlive, issued),
    issuedWithUnresolvedTruth: rows.filter(r => issued(r) && r.label === '@unresolved').length,
    top1: ratio(rows, scored, hit1), top3: ratio(rows, scored, r => Number(r[key].slice(0, 3).includes(r.label!))),
    top1HitsPerFullWindow: ratio(rows, full, hit1) };
}
function summarize(rows: Row[]) {
  const both = (r: Row) => resolved(r) && r.retrieval.length > 0 && r.baseline.length > 0;
  const broadHit = (r: Row, method: 'broadRetrieval' | 'broadBaseline') =>
    Number(r[method] !== null && r[method] === shadow742Family(r.label ?? '', r.team));
  const familyMethod = (key: 'broadRetrieval' | 'broadBaseline') => ({
    coverage: ratio(rows, full, r => Number(r[key] !== null)),
    top1: ratio(rows, r => resolved(r) && r[key] !== null, r => broadHit(r, key)),
    top1HitsPerFullWindow: ratio(rows, full, r => broadHit(r, key)),
    issuedWithUnresolvedTruth: rows.filter(r => r[key] !== null && r.label === '@unresolved').length,
  });
  const broadBoth = (r: Row) => resolved(r) && r.broadRetrieval !== null && r.broadBaseline !== null;
  return { matches: unique(rows.map(r => r.match)).length, accounts: unique(rows.map(r => r.account)).length,
    gridWindows: rows.length, fullWindows: rows.filter(full).length,
    rightCensored: rows.filter(r => r.span === 'right-censored').length,
    pastRecording: rows.filter(r => r.span === 'past-recording').length,
    freshAliveWindows: rows.filter(freshAlive).length, selfStateOnFullWindows: counts(rows.filter(full).map(r => r.self)),
    outcomesOnFullWindows: counts(rows.filter(full).map(r => !r.label || r.label.startsWith('@') ? r.label! :
      r.label.startsWith('lane:') ? 'lane-of-origin' : 'own-spawn-camp')),
    nextFamily: { retrieval: familyMethod('broadRetrieval'), baseline: familyMethod('broadBaseline'),
      disposition: counts(rows.filter(full).map(r => r.broadDisposition)),
      pairedTop1: { retrieval: ratio(rows, broadBoth, r => broadHit(r, 'broadRetrieval')),
        baseline: ratio(rows, broadBoth, r => broadHit(r, 'broadBaseline')),
        difference: ratio(rows, broadBoth, r => broadHit(r, 'broadRetrieval') - broadHit(r, 'broadBaseline')) } },
    retrievalDisposition: counts(rows.filter(full).map(r => r.retrievalDisposition)),
    retrieval: method(rows, 'retrieval'), baseline: method(rows, 'baseline'),
    pairedTop1: { retrieval: ratio(rows, both, r => Number(r.retrieval[0] === r.label)),
      baseline: ratio(rows, both, r => Number(r.baseline[0] === r.label)),
      difference: ratio(rows, both, r => Number(r.retrieval[0] === r.label) - Number(r.baseline[0] === r.label)) },
    visibleDisplay: { displayed: rows.filter(r => r.displayed).length, denominatorFullWindows: rows.filter(full).length,
      rate: shadow742Rate(rows.filter(r => r.displayed).length, rows.filter(full).length), targetPOVAnnotations: 0,
      canActAnnotations: 0, liveAccuracy: null } };
}
function breakdown(rows: Row[]) {
  const compact = (group: Row[]) => {
    const s = summarize(group);
    return { matches: s.matches, accounts: s.accounts, fullWindows: s.fullWindows, freshAliveWindows: s.freshAliveWindows,
      nextFamily: { retrievalCoverage: s.nextFamily.retrieval.coverage, retrievalTop1: s.nextFamily.retrieval.top1,
        baselineCoverage: s.nextFamily.baseline.coverage, baselineTop1: s.nextFamily.baseline.top1,
        pairedTop1Difference: s.nextFamily.pairedTop1.difference },
      retrievalCoverage: s.retrieval.coverage, retrievalTop1: s.retrieval.top1,
      baselineCoverage: s.baseline.coverage, baselineTop1: s.baseline.top1, pairedTop1Difference: s.pairedTop1.difference };
  };
  return { overall: summarize(rows),
    byHero: Object.fromEntries(['luna', 'troll_warlord'].map(h => [h, summarize(rows.filter(r => r.hero === h))])),
    byHeroTeam: Object.fromEntries(['luna', 'troll_warlord'].flatMap(h => ['radiant', 'dire'].map(t => [
      `${h}/${t}`, compact(rows.filter(r => r.hero === h && r.team === t))]))),
    byClockMinutes: Object.fromEntries([5, 10, 15].map(t => [`${t}-${t + 5}`, compact(rows.filter(r => r.t >= 60 * t && r.t < 60 * (t + 5)))])) };
}
export function evaluateShadow742(index: Shadow742Index) {
  const frozen = shadow742FrozenFold(index), frozenExamples: AuditExample[] = [], rollingExamples: AuditExample[] = [];
  const frozenRows = scoreFold(frozen, clusterIds(frozen.test), frozenExamples);
  const rollingRows: Row[] = [], rollingAudit = [], clusters = clusterIds(index.games);
  for (const game of index.games) {
    const fold = shadow742Fold(index, [game]);
    rollingRows.push(...scoreFold(fold, clusters, rollingExamples));
    rollingAudit.push({ matchId: game.id, startUnix: game.start, withheldAccountIds: fold.audit.testAccountIds,
      trainMatches: fold.train.length, trainAccounts: fold.audit.trainAccountIds.length,
      excludedPastForAccount: fold.audit.pastGamesExcludedForAccount.length, latestTrainEndUnix: fold.audit.latestTrainEndUnix });
  }
  return {
    version: 2, scope: 'offline-read-only-shadow', training: 'empirical nearest-history votes; no weight training',
    task: 'First body farming last-hit origin in (t,t+30s], not a chosen destination, clear, safety or outcome of advice.',
    parameters: SHADOW742, corpus: { matches: index.games.length, focalTracks: index.games.reduce((n, g) => n + g.tracks.length, 0),
      focalAccounts: accounts(index.games).length, unknownEndMatches: index.games.filter(g => g.endUnix === null).length,
      omittedNonspatialTrackIds: index.omittedNonspatialTrackIds ?? [] },
    definitions: {
      features: 'Patch/hero/team, clock, last fresh alive self XY at/before t, positive own main-slot item sightings through t only. No event origin, death XY, tower/camp status, opponents, gold or winner.',
      nextFamily: 'Primary coarse experiment: lane-of-origin vs own-spawn camp. Strict >1/2 of ALL historical votes/mass, minimum two accounts; otherwise abstain. No-action, opponent and unresolved stay in the voting denominator. Diagnostic only unless current user manually confirms an allowed target of that family; not evidence for a specific site.',
      censoring: 'Complete horizon only, even when a hit is known before truncation; later points/timestamps bound observation end only. Missing/dead self abstains.',
      baseline: 'Same train/hero/team, ±90s clock-only distribution, all accounts equally weighted; same min two accounts, no position/inventory.',
      coverage: 'Nonempty historical ranking / all fully observed label horizons (includes dead and no-fresh-self); also conditional on fresh alive prefix.',
      accuracy: 'Exact site among ranked windows with resolved truth. No-body-hit and opponent-camp count as misses. Unknown truth separately counted; top1HitsPerFullWindow includes all abstentions/unknowns as nonhits.',
      paired: 'Both methods on precisely the same ranked/resolved windows. Signed retrieval minus clock-baseline top-1 rate.',
      interval: `Percentile bootstrap, ${draws} replicates, seed ${seed}; resample connected test match/account clusters, pool their windows. Null below two contributing clusters; small-n intervals can be degenerate. Conditional on this archive, not live generalization.`,
      visibleDisplay: 'Actual observer called without invented POV targets or canAct. Zero display on unannotated compact data is NOT an estimate of live coverage, safety or utility.',
      split: 'Newest ceil(N/4) match IDs by start, include all cutoff ties. Remove whole earlier games sharing a held-out focal account or with unknown/end>=cutoff. end=start+ceil(game duration or recorded terminal clock), not a measured replay publication time.',
      rolling: 'Secondary past-only leave-one-match-and-its-focal-accounts-out at each kickoff. Includes cold starts; earlier test games can become archive for later, unrelated accounts. Not independent of the fixed temporal test.',
      examples: 'First rankable window per hero in chronological test order, selected without looking at correctness. All ranks and next-hit outcomes are retrospective, not observations of live visibility.',
    },
    frozenChronological: { split: frozen.audit, ...breakdown(frozenRows), examples: frozenExamples },
    rollingOrigin: { splits: rollingAudit, ...breakdown(rollingRows), examples: rollingExamples },
  };
}
