/**
 * Local, read-only shadow research. No server, planner, game or model dependencies.
 * Only observeShadow742's *intersection* with current visible facts is for display.
 * The historical index and its ungated ranks are NOT current map state.
 */
export type Shadow742Hero = 'luna' | 'troll_warlord';
export type Shadow742Team = 'radiant' | 'dire';
export type Shadow742Family = 'lane' | 'own-camp';
type Id = string | number;
type Position = { x: number; y: number };
export interface Shadow742Target {
  targetId: string; matchId: Id; seenAtSec: number;
  source: 'manual-confirmed';
  state: 'present' | 'empty' | 'unknown';
  /** The observed target's segment, NOT certification of a safe route to it. */
  area: 'own' | 'enemy' | 'unknown';
}
export interface Shadow742Observation {
  informationScope: 'player-visible'; patch: string; matchId: Id; timeSec: number;
  hero: Shadow742Hero; team: Shadow742Team;
  /** A known corpus match supplies its own immutable start and focal account(s). */
  matchStartUnix?: number; accountId?: Id; contextId?: string; capturedAtMs?: number;
  self: {
    observedAtSec: number; alive: boolean; canAct?: boolean; position: Position;
    /** Positive main-slot sightings only. Not an assertion of current ownership. */
    itemsSeen?: readonly { itemId: string; seenAtSec: number; matchId: Id }[];
  };
  visibleTargets: readonly Shadow742Target[];
  cancelled?: boolean; dangerVisible?: boolean;
}
export interface Shadow742Options { signal?: AbortSignal; /** For checking a supplied wall timestamp. */ nowMs?: number }

export const SHADOW742 = Object.freeze({
  patch: '7.42', firstAnchor: 315, stepSec: 30, endSec: 1200, horizonSec: 30,
  freshSec: 3, freshMs: 5000, clockRadiusSec: 90, positionRadius: 2400,
  positionScale: 1200, k: 7, minAccounts: 2,
});
// Vocabulary actually sampled by the compact replay recorder, not item -> route rules.
const recordedItems = new Set(['power_treads', 'phase_boots', 'mask_of_madness', 'yasha', 'manta',
  'bfury', 'maelstrom', 'mjollnir', 'sange_and_yasha', 'black_king_bar']);
const heroes = new Set<string>(['luna', 'troll_warlord']);
const teams = new Set<string>(['radiant', 'dire']);
const lanes = /^lane:(top|mid|bot)$/;
// Static 7.42 spawner vocabulary from the published map, not availability, a
// region polygon or a route. Never validate a manual marker against *test labels*.
const namedCamps = new Set([
  ...[1, 2, 4, 5, 7, 8, 9, 11, 12, 13, 14, 15, 16, 20].map(n => `good_${n}`),
  ...[1, 2, 4, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 20].map(n => `evil_${n}`),
]);
const finite = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const record = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const positiveId = (v: unknown) => ((typeof v === 'string' && /^[1-9]\d*$/.test(v)) ||
  (Number.isSafeInteger(v) && Number(v) > 0)) ? String(v) : null;
const matchKey = (v: unknown) => positiveId(v) ?? (typeof v === 'string' && /^[\w-]{1,80}$/.test(v) && /\D/.test(v) ? v : null);
const pos = (x: unknown, y: unknown) => finite(x) && finite(y) && Math.abs(x) <= 12000 && Math.abs(y) <= 12000;
const fresh = (seen: unknown, t: number): seen is number =>
  finite(seen) && seen >= 0 && seen <= t && t - seen <= SHADOW742.freshSec;
const cmp = (a: string, b: string) => a < b ? -1 : a > b ? 1 : 0;
const ownCamp = (id: string, team: Shadow742Team) => namedCamps.has(id) && id.startsWith(team === 'radiant' ? 'good_' : 'evil_');
const siteAllowed = (id: string, team: Shadow742Team) => lanes.test(id) || ownCamp(id, team);
export const shadow742Family = (site: string, team: Shadow742Team): Shadow742Family | null =>
  lanes.test(site) ? 'lane' : ownCamp(site, team) ? 'own-camp' : null;
export const shadow742Rate = (n: number, d: number) => d ? Math.round(n / d * 1e6) / 1e6 : null;

export interface Shadow742Prefix { x: number; y: number; observedAtSec: number; itemsSeen: string[] }
interface Label { site: string; atSec: number | null }
export interface Shadow742Window {
  timeSec: number; prefix: Shadow742Prefix | null; selfState: 'alive' | 'dead' | 'no-fresh-self';
  span: 'full' | 'right-censored' | 'past-recording'; label: Label | null;
}
export interface Shadow742Track { hero: Shadow742Hero; team: Shadow742Team; accountId: string; windows: Shadow742Window[] }
export interface Shadow742Game { id: string; start: number; endUnix: number | null; tracks: Shadow742Track[] }
export interface Shadow742Index { patch: '7.42'; games: Shadow742Game[]; omittedNonspatialTrackIds?: string[] }

function chronological(rows: unknown, minFields: number, name: string): unknown[][] {
  if (!Array.isArray(rows)) throw new Error(`Missing ${name}`);
  let prev = -Infinity;
  for (const r of rows) {
    if (!Array.isArray(r) || r.length < minFields || !finite(r[0]) || r[0] < prev)
      throw new Error(`Invalid or unsorted ${name}`);
    prev = r[0];
  }
  return rows;
}
// First index strictly after t; never interpolate across a future trace sample/teleport.
function after(rows: unknown[][], t: number): number {
  let lo = 0, hi = rows.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if ((rows[mid][0] as number) <= t) lo = mid + 1; else hi = mid;
  }
  return lo;
}
function prefixAt(trace: unknown[][], items: [string, number][], t: number): Pick<Shadow742Window, 'prefix' | 'selfState'> {
  const p = trace[after(trace, t) - 1];
  if (!p || !fresh(p[0], t) || !pos(p[1], p[2]) || (p[3] !== 0 && p[3] !== 1))
    return { prefix: null, selfState: 'no-fresh-self' };
  if (p[3] === 0) return { prefix: null, selfState: 'dead' };
  return { selfState: 'alive', prefix: { x: p[1] as number, y: p[2] as number, observedAtSec: p[0],
    itemsSeen: items.filter(([, seen]) => seen <= t).map(([item]) => item) } };
}
// ONLY offline supervision. No victim coordinates, path interpolation, or skipping
// an inconvenient enemy/ambiguous first kill to select the next admissible one.
function nextLabel(events: unknown[][], t: number, team: Shadow742Team, mapCamps: Set<string> | null): Label {
  let atSec: number | null = null;
  const sites = new Set<string>();
  for (let i = after(events, t); i < events.length; i++) {
    const [time, rawSite, actor] = events[i], at = time as number;
    if (at > t + SHADOW742.horizonSec || (atSec !== null && at !== atSec)) break;
    if (actor === 'illusion') continue;
    atSec = at;
    const site = typeof rawSite === 'string' ? rawSite : '';
    if (actor !== 'body' || (!lanes.test(site) && !(namedCamps.has(site) && (!mapCamps || mapCamps.has(site))))) {
      sites.add('@unresolved'); continue;
    }
    sites.add(siteAllowed(site, team) ? site : '@opponent-camp');
  }
  return { atSec, site: atSec === null ? '@no-body-hit' : sites.size === 1 ? [...sites][0] : '@unresolved' };
}
function windows(m: Record<string, unknown>, team: Shadow742Team, duration: number | null, mapCamps: Set<string> | null) {
  const trace = chronological(m.trace, 4, 'trace'), events = chronological(m.events, 3, 'events');
  const items = Object.entries(record(m.firstHeld) ? m.firstHeld : {})
    .filter((v): v is [string, number] => recordedItems.has(v[0]) && finite(v[1]) && v[1] >= 0)
    .sort(([a], [b]) => cmp(a, b));
  // End-of-observation is a censoring bound, never a query feature. In particular
  // a missing 20-minute point is not equivalent to a zero-gold/no-action window.
  const points = Array.isArray(m.points) ? m.points : [];
  const through = Math.min(SHADOW742.endSec, duration ?? Infinity, Math.max(0,
    Number(trace.at(-1)?.[0] ?? 0), Number(events.at(-1)?.[0] ?? 0),
    record(m.endPoint) && finite(m.endPoint.t) ? m.endPoint.t : 0,
    ...points.flatMap(p => record(p) && finite(p.t) ? [p.t] : [])));
  const result: Shadow742Window[] = [];
  for (let t = SHADOW742.firstAnchor; t + SHADOW742.horizonSec <= SHADOW742.endSec; t += SHADOW742.stepSec) {
    const span = t >= through ? 'past-recording' : t + SHADOW742.horizonSec > through ? 'right-censored' : 'full';
    result.push({ timeSec: t, ...prefixAt(trace, items, t), span,
      label: span === 'full' ? nextLabel(events, t, team, mapCamps) : null });
  }
  return result;
}
const patchVersion = (v: unknown) => record(v) ? v.version : v;
export function buildShadow742Index(corpus: unknown): Shadow742Index {
  if (!record(corpus) || !Array.isArray(corpus.matches) || !corpus.matches.length) throw new Error('Missing 7.42 matches');
  if (corpus.patch != null && patchVersion(corpus.patch) !== SHADOW742.patch) throw new Error('Not a 7.42 corpus');
  const mapCamps = record(corpus.map) && Array.isArray(corpus.map.camps)
    ? new Set(corpus.map.camps.flatMap(c => record(c) && typeof c.id === 'string' ? [c.id] : [])) : null;
  const games = new Map<string, Shadow742Game>(), omittedNonspatialTrackIds: string[] = [];
  for (const row of corpus.matches) {
    if (!record(row) || patchVersion(row.patch ?? corpus.patch) !== SHADOW742.patch) throw new Error('Unverified match patch');
    const id = positiveId(row.id), accountId = positiveId(row.accountId), start = row.start;
    if (!id || !accountId || !finite(start) || start <= 0 ||
        !heroes.has(String(row.hero)) || !teams.has(String(row.team))) throw new Error(`Unattributable match/track ${row.id}`);
    // Minute-only D2PT rows must not enter folds, counts, baseline, or negatives.
    // Eligibility is stream availability, never whether a desired kill happened.
    if (!Array.isArray(row.events) || !Array.isArray(row.trace) || !row.trace.some(p =>
      Array.isArray(p) && finite(p[0]) && pos(p[1], p[2]) && (p[3] === 0 || p[3] === 1))) {
      omittedNonspatialTrackIds.push(`${id}:${row.hero}:${row.team}`); continue;
    }
    if (record(corpus.patch) && ((finite(corpus.patch.from) && start < corpus.patch.from) ||
        (finite(corpus.patch.until) && start >= corpus.patch.until))) throw new Error(`Match ${id} outside patch interval`);
    const hero = row.hero as Shadow742Hero, team = row.team as Shadow742Team;
    const terminal = record(row.endPoint) ? row.endPoint.t : null;
    const duration = finite(row.duration) && row.duration > 0 ? row.duration : finite(terminal) && terminal > 0 ? terminal : null;
    const endUnix = duration === null ? null : start + Math.ceil(duration);
    const g = games.get(id) ?? { id, start, endUnix, tracks: [] };
    if (g.start !== start || g.tracks.some(p => p.accountId === accountId || (p.team === team && p.hero === hero)))
      throw new Error(`Conflicting/duplicate match track ${id}`);
    g.endUnix = g.endUnix === null || endUnix === null ? null : Math.max(g.endUnix, endUnix);
    g.tracks.push({ hero, team, accountId, windows: windows(row, team, duration, mapCamps) });
    games.set(id, g);
  }
  if (!games.size) throw new Error('No spatial replay tracks; minute-only data cannot supervise this task');
  return { patch: '7.42', games: [...games.values()].sort((a, b) => a.start - b.start || cmp(a.id, b.id)), omittedNonspatialTrackIds };
}

/** Drop the WHOLE replay when another focal track in that game has a forbidden account. */
export function shadow742History(index: Shadow742Index, cutoffUnix: number, bannedIds: ReadonlySet<string>,
  bannedAccounts: ReadonlySet<string>) {
  return index.games.filter(g => g.endUnix !== null && g.endUnix < cutoffUnix &&
    !bannedIds.has(g.id) && g.tracks.every(p => !bannedAccounts.has(p.accountId)));
}

export interface Shadow742Case {
  matchId: string; accountId: string; timeSec: number; label: string; labelAtSec: number | null;
}
interface Neighbor { ref: Shadow742Case; d2: number; hasItems: boolean }
export interface Shadow742Votes { cases: Shadow742Case[]; conditionedItems: string[]; availableAccounts: number }
/** Research primitive on an already isolated archive. Its labels are HISTORY, never visible targets. */
export function shadow742HistoricalVotes(history: readonly Shadow742Game[], hero: Shadow742Hero, team: Shadow742Team,
  timeSec: number, self: Shadow742Prefix): Shadow742Votes {
  const pool: Neighbor[] = [];
  for (const g of history) for (const p of g.tracks) {
    if (p.hero !== hero || p.team !== team) continue;
    for (const w of p.windows) {
      const dt = Math.abs(w.timeSec - timeSec);
      if (dt > SHADOW742.clockRadiusSec || !w.prefix || !w.label) continue;
      const space2 = (w.prefix.x - self.x) ** 2 + (w.prefix.y - self.y) ** 2;
      if (space2 > SHADOW742.positionRadius ** 2) continue;
      pool.push({ d2: space2 / SHADOW742.positionScale ** 2 + (dt / SHADOW742.clockRadiusSec) ** 2,
        hasItems: self.itemsSeen.every(item => w.prefix!.itemsSeen.includes(item)),
        ref: { matchId: g.id, accountId: p.accountId, timeSec: w.timeSec, label: w.label.site, labelAtSec: w.label.atSec } });
    }
  }
  const useItems = self.itemsSeen.length > 0 &&
    new Set(pool.filter(n => n.hasItems).map(n => n.ref.accountId)).size >= SHADOW742.minAccounts;
  const subset = useItems ? pool.filter(n => n.hasItems) : pool;
  subset.sort((a, b) => a.d2 - b.d2 || cmp(a.ref.matchId, b.ref.matchId) || a.ref.timeSec - b.ref.timeSec);
  const seen = new Set<string>(), cases: Shadow742Case[] = [];
  for (const { ref } of subset) if (!seen.has(ref.accountId)) {
    seen.add(ref.accountId);
    if (cases.length < SHADOW742.k) cases.push(ref);
  }
  return { cases, conditionedItems: useItems ? [...self.itemsSeen] : [], availableAccounts: seen.size };
}
/** Never renormalize after excluding opponent/no-hit/unknown or after a visible-target filter. */
export function shadow742HistoricalRanks(votes: Shadow742Votes, team: Shadow742Team) {
  if (votes.cases.length < SHADOW742.minAccounts) return [];
  const bins = new Map<string, Shadow742Case[]>();
  for (const c of votes.cases) if (siteAllowed(c.label, team)) {
    const bin = bins.get(c.label) ?? []; bin.push(c); bins.set(c.label, bin);
  }
  // Map insertion order is closest example first; stable sort resolves count ties by that order.
  return [...bins].map(([targetId, cases]) => ({ targetId, cases })).sort((a, b) => b.cases.length - a.cases.length);
}
/** Aggregate votes BEFORE deciding a family. Ties, mixtures and non-action
 * outcomes can block a majority. Never promote the best visible minority. */
export function shadow742BroadMajority(votes: Shadow742Votes, team: Shadow742Team) {
  const n = votes.cases.length;
  if (n < SHADOW742.minAccounts) return null;
  for (const family of ['lane', 'own-camp'] as const) {
    const cases = votes.cases.filter(c => shadow742Family(c.label, team) === family);
    if (2 * cases.length > n) return { family, cases };
  }
  return null;
}
function wilson95(hits: number, n: number): [number, number] {
  const z = 1.959963984540054, p = hits / n, d = 1 + z ** 2 / n;
  const center = (p + z ** 2 / (2 * n)) / d, half = z * Math.sqrt(p * (1 - p) / n + z ** 2 / (4 * n ** 2)) / d;
  return [Math.max(0, Math.round((center - half) * 1e6) / 1e6), Math.min(1, Math.round((center + half) * 1e6) / 1e6)];
}

function currentTargets(o: Shadow742Observation) {
  const latest = new Map<string, { t: number; verdict: Shadow742Target['state']; fact: Shadow742Target }>();
  for (const f of o.visibleTargets) {
    if (!f || typeof f.targetId !== 'string' || !siteAllowed(f.targetId, o.team) ||
        matchKey(f.matchId) !== matchKey(o.matchId) || !fresh(f.seenAtSec, o.timeSec) ||
        f.source !== 'manual-confirmed') continue;
    const old = latest.get(f.targetId);
    if (old && old.t > f.seenAtSec) continue;
    // A later empty/unknown/enemy sighting revokes the old one. Simultaneous
    // contradictions mean unknown (not "empty"), irrespective of input order.
    let verdict = f.area === 'own' && (f.state === 'present' || f.state === 'empty') ? f.state : 'unknown' as const;
    if (old && old.t === f.seenAtSec && old.verdict !== verdict) verdict = 'unknown';
    latest.set(f.targetId, { t: f.seenAtSec, fact: f, verdict });
  }
  const present = new Map([...latest].flatMap(([key, v]) => v.verdict === 'present' ? [[key, v.fact] as const] : []));
  const empty = [...latest.values()].filter(v => v.verdict === 'empty').length;
  return { present, empty, indeterminate: latest.size - empty - present.size };
}
export interface Shadow742Candidate {
  targetId: string; seenAtSec: number; source: Shadow742Target['source'];
  supportingAccounts: number; historicalFrequency: number; descriptiveWilson95: [number, number]; cases: Shadow742Case[];
}
export interface Shadow742BroadCandidate {
  family: Shadow742Family; supportingAccounts: number; historicalFrequency: number; descriptiveWilson95: [number, number];
  /** These are *input* facts, not exact-site predictions. No unobserved site from the archive is displayed here. */
  visibleTargets: Pick<Shadow742Target, 'targetId' | 'seenAtSec' | 'source'>[];
  cases: { matchId: string; accountId: string; timeSec: number; labelAtSec: number | null; family: Shadow742Family }[];
}
export interface Shadow742Result {
  status: 'observed' | 'abstain' | 'cancelled'; reason: string; mode: 'read-only-shadow';
  liveActivation: 'disabled'; pathSafety: 'unknown'; matchId: string | null; contextId: string | null;
  hero: Shadow742Hero | null; team: Shadow742Team | null; timeSec: number | null; expiresAtSec: number | null;
  observedAccountId: string | null; capturedAtMs: number | null; observationStartUnix: number | null;
  visibility: { presentOwn: number; emptyOwn: number; indeterminate: number; unobserved: 'unknown' };
  candidates: Shadow742Candidate[]; broadCandidates: Shadow742BroadCandidate[];
  evidence: { scope: 'historical-only'; accounts: number; matches: number; availableAccounts: number;
    conditionedItems: string[]; hiddenOrOtherVotes: number; broadDisposition: 'not-scored' | 'no-majority' | 'majority-not-visible' | 'observed';
    accountIsolation: 'known-focal-accounts-excluded' | 'account-not-provided' };
}
function gate(o: Shadow742Observation, options: Shadow742Options): string | null {
  if (options.signal?.aborted || o?.cancelled === true) return 'cancelled';
  if (!o || o.informationScope !== 'player-visible') return 'not-player-visible';
  if (o.patch !== SHADOW742.patch || !heroes.has(o.hero) || !teams.has(o.team)) return 'unsupported-context';
  if (!matchKey(o.matchId) || !finite(o.timeSec) || o.timeSec < 0) return 'invalid-match-clock';
  if (o.capturedAtMs !== undefined) {
    const now = options.nowMs ?? Date.now();
    if (!finite(now) || !finite(o.capturedAtMs) || o.capturedAtMs > now || now - o.capturedAtMs > SHADOW742.freshMs) return 'stale-capture';
  }
  if (o.dangerVisible === true || o.self?.alive === false || o.self?.canAct === false) return 'self-or-threat-cancelled';
  if (o.timeSec < 300 || o.timeSec + SHADOW742.horizonSec > SHADOW742.endSec) return 'outside-study-window';
  if (!o.self || !fresh(o.self.observedAtSec, o.timeSec) || o.self.alive !== true || o.self.canAct !== true ||
      !pos(o.self.position?.x, o.self.position?.y)) return 'missing-fresh-self';
  if (!Array.isArray(o.visibleTargets) || o.visibleTargets.length > 256) return 'invalid-target-facts';
  return null;
}
export function observeShadow742(index: Shadow742Index, o: Shadow742Observation, options: Shadow742Options = {}): Shadow742Result {
  const result: Shadow742Result = {
    mode: 'read-only-shadow', liveActivation: 'disabled', pathSafety: 'unknown', status: 'abstain', reason: '',
    matchId: matchKey(o?.matchId), contextId: typeof o?.contextId === 'string' ? o.contextId : null,
    hero: heroes.has(o?.hero) ? o.hero : null, team: teams.has(o?.team) ? o.team : null,
    timeSec: finite(o?.timeSec) ? o.timeSec : null, expiresAtSec: null, candidates: [], broadCandidates: [],
    observedAccountId: positiveId(o?.accountId), capturedAtMs: finite(o?.capturedAtMs) ? o.capturedAtMs : null,
    observationStartUnix: finite(o?.matchStartUnix) ? o.matchStartUnix : null,
    visibility: { presentOwn: 0, emptyOwn: 0, indeterminate: 0, unobserved: 'unknown' },
    evidence: { scope: 'historical-only', accounts: 0, matches: 0, availableAccounts: 0, conditionedItems: [],
      hiddenOrOtherVotes: 0, broadDisposition: 'not-scored', accountIsolation: 'account-not-provided' },
  };
  const reject = (reason: string) => {
    result.reason = reason;
    if (reason === 'cancelled' || reason === 'self-or-threat-cancelled') result.status = 'cancelled';
    return result;
  };
  const invalid = gate(o, options); if (invalid) return reject(invalid);
  const sight = currentTargets(o), targets = sight.present;
  Object.assign(result.visibility, { presentOwn: targets.size, emptyOwn: sight.empty, indeterminate: sight.indeterminate });
  if (!targets.size) return reject(sight.empty && !sight.indeterminate ? 'only-observed-empty-targets' : 'no-current-visible-own-target');
  const known = index.games.find(g => g.id === result.matchId);
  const account = positiveId(o.accountId);
  if ((o.accountId != null && !account) || (o.matchStartUnix !== undefined && (!finite(o.matchStartUnix) || o.matchStartUnix <= 0)) ||
      (known && o.matchStartUnix !== undefined && known.start !== o.matchStartUnix)) return reject('conflicting-match-metadata');
  const knownTrack = known?.tracks.find(p => p.hero === o.hero && p.team === o.team);
  if (account && knownTrack && account !== knownTrack.accountId) return reject('conflicting-match-metadata');
  const cutoff = known?.start ?? o.matchStartUnix;
  if (!cutoff) return reject('need-match-start');
  const banned = new Set(known?.tracks.map(p => p.accountId));
  if (account) banned.add(account);
  if (account || knownTrack) result.evidence.accountIsolation = 'known-focal-accounts-excluded';
  const history = shadow742History(index, cutoff, new Set([result.matchId!]), banned);
  const items = Array.isArray(o.self.itemsSeen) ? [...new Set(o.self.itemsSeen.filter(i => i && recordedItems.has(i.itemId) &&
    matchKey(i.matchId) === result.matchId && finite(i.seenAtSec) && i.seenAtSec >= 0 && i.seenAtSec <= o.timeSec).map(i => i.itemId))].sort() : [];
  const votes = shadow742HistoricalVotes(history, o.hero, o.team, o.timeSec,
    { x: o.self.position.x, y: o.self.position.y, observedAtSec: o.self.observedAtSec, itemsSeen: items });
  const n = votes.cases.length;
  Object.assign(result.evidence, { accounts: n, matches: new Set(votes.cases.map(c => c.matchId)).size,
    availableAccounts: votes.availableAccounts, conditionedItems: votes.conditionedItems, hiddenOrOtherVotes: n });
  if (n < SHADOW742.minAccounts) return reject('insufficient-independent-history');
  const broad = shadow742BroadMajority(votes, o.team);
  result.evidence.broadDisposition = broad ? 'majority-not-visible' : 'no-majority';
  if (broad) {
    const visibleTargets = [...targets.values()].filter(f => shadow742Family(f.targetId, o.team) === broad.family)
      .map(({ targetId, seenAtSec, source }) => ({ targetId, seenAtSec, source })).sort((a, b) => cmp(a.targetId, b.targetId));
    if (visibleTargets.length) {
      result.evidence.broadDisposition = 'observed';
      result.broadCandidates.push({ family: broad.family, visibleTargets, supportingAccounts: broad.cases.length,
        historicalFrequency: shadow742Rate(broad.cases.length, n)!, descriptiveWilson95: wilson95(broad.cases.length, n),
        cases: broad.cases.map(({ matchId, accountId, timeSec, labelAtSec }) => ({ matchId, accountId, timeSec, labelAtSec, family: broad.family })) });
    }
  }
  for (const { targetId, cases } of shadow742HistoricalRanks(votes, o.team)) {
    const f = targets.get(targetId); if (!f) continue;
    result.candidates.push({ targetId, seenAtSec: f.seenAtSec, source: f.source, cases,
      supportingAccounts: cases.length, historicalFrequency: shadow742Rate(cases.length, n)!, descriptiveWilson95: wilson95(cases.length, n) });
    result.evidence.hiddenOrOtherVotes -= cases.length;
  }
  const basis = [...result.candidates, ...result.broadCandidates.flatMap(c => c.visibleTargets)];
  if (!basis.length) return reject('no-visible-target-with-history');
  result.status = 'observed'; result.reason = result.candidates.length ? 'visible-historical-overlap' : 'visible-family-only';
  result.expiresAtSec = Math.min(o.self.observedAtSec, ...basis.map(c => c.seenAtSec)) + SHADOW742.freshSec;
  return result;
}

/** Recheck against a newly acquired player observation, never a route permission. */
export function shadow742StillCurrent(r: Shadow742Result, o: Shadow742Observation, options: Shadow742Options = {}): boolean {
  if (r.status !== 'observed' || gate(o, options) !== null || r.matchId !== matchKey(o.matchId) ||
      r.contextId !== (o.contextId ?? null) || r.hero !== o.hero || r.team !== o.team ||
      r.observedAccountId !== positiveId(o.accountId) || r.observationStartUnix !== (o.matchStartUnix ?? null) ||
      r.timeSec === null || r.expiresAtSec === null || o.timeSec < r.timeSec || o.timeSec > r.expiresAtSec) return false;
  if (r.capturedAtMs !== null && (!finite(o.capturedAtMs) || o.capturedAtMs < r.capturedAtMs ||
      (options.nowMs ?? Date.now()) - r.capturedAtMs > SHADOW742.freshMs)) return false;
  const visible = currentTargets(o).present;
  const basis = [...r.candidates, ...r.broadCandidates.flatMap(c => c.visibleTargets)];
  return basis.length > 0 && basis.every(c => {
    const f = visible.get(c.targetId); return f !== undefined && f.seenAtSec >= c.seenAtSec;
  });
}
