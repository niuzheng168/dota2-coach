import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { buildShadow742Index, observeShadow742, shadow742StillCurrent } from '../src/tactics/shadow-742.ts';
import type { Shadow742Observation, Shadow742Target } from '../src/tactics/shadow-742.ts';

type Hit = [number, string, string, number?, number?];
function track(id: number, account: number, label: string, changes: Record<string, unknown> = {}) {
  return { id, accountId: account, hero: 'luna', team: 'radiant', start: 1000 * id, duration: 500,
    events: [[320, label, 'body', 6000, -7000]] as Hit[], trace: [[314, 10, 20, 1, 0]],
    firstHeld: {}, points: [{ t: 345, lh: 32 }], ...changes };
}
const corpus = (matches: ReturnType<typeof track>[]) => ({ patch: { version: '7.42' }, matches });
const indexOf = (...rows: ReturnType<typeof track>[]) => buildShadow742Index(corpus(rows));
function fact(targetId: string, changes: Partial<Shadow742Target> = {}): Shadow742Target {
  return { targetId, matchId: 'demo', seenAtSec: 315, area: 'own', state: 'present', source: 'manual-confirmed', ...changes };
}
function observation(changes: Partial<Shadow742Observation> = {}): Shadow742Observation {
  return { informationScope: 'player-visible', patch: '7.42', matchId: 'demo', matchStartUnix: 1000000, contextId: 'ctx',
    hero: 'luna', team: 'radiant', timeSec: 315, self: { alive: true, canAct: true, observedAtSec: 315, position: { x: 10, y: 20 } },
    visibleTargets: [fact('good_1'), fact('good_2'), fact('lane:bot')], ...changes };
}
const base = indexOf(track(10, 1, 'good_1'), track(11, 2, 'good_1'), track(12, 3, 'good_2'), track(13, 4, 'evil_2'));

test('empirical probabilities, not a template: changing only archive labels changes the historical candidates', () => {
  const o = observation({ visibleTargets: [fact('good_1'), fact('evil_2')] });
  const r = observeShadow742(base, o);
  assert.equal(r.status, 'observed'); assert.equal(r.pathSafety, 'unknown'); assert.equal(r.liveActivation, 'disabled');
  assert.deepEqual(r.candidates.map(c => c.targetId), ['good_1']);
  assert.deepEqual([r.candidates[0].supportingAccounts, r.evidence.accounts, r.evidence.hiddenOrOtherVotes], [2, 4, 2]);
  assert.equal(r.candidates[0].historicalFrequency, .5); // Not 1.0 after hiding the other outcomes.
  assert.deepEqual(r.candidates[0].descriptiveWilson95, [.150039, .849961]);
  assert.ok(r.candidates[0].cases.every(c => c.matchId !== 'demo' && c.timeSec === 315 && c.labelAtSec === 320));
  const changed = indexOf(track(10, 1, 'lane:bot'), track(11, 2, 'lane:bot'), track(12, 3, 'lane:bot'), track(13, 4, 'lane:bot'));
  assert.equal(observeShadow742(changed, o).reason, 'no-visible-target-with-history');
  assert.deepEqual(observeShadow742(changed, observation()).candidates.map(c => [c.targetId, c.historicalFrequency]), [['lane:bot', 1]]);
});

test('same hero, clock and visible set retrieve different destinations from different own positions', () => {
  const positions = { trace: [[314, 0, 0, 1], [344, 1800, 0, 1]], points: [{ t: 375 }],
    events: [[321, 'good_1', 'body'], [348, 'good_2', 'body']] };
  const idx = indexOf(track(2, 1, 'unused', positions), track(4, 2, 'unused', positions));
  const query = (x: number) => observation({ timeSec: 330, self: { observedAtSec: 330, alive: true, canAct: true, position: { x, y: 0 } },
    visibleTargets: ['good_1', 'good_2'].map(id => fact(id, { seenAtSec: 330 })) });
  assert.deepEqual(observeShadow742(idx, query(0)).candidates.map(c => c.targetId), ['good_1']);
  assert.deepEqual(observeShadow742(idx, query(1800)).candidates.map(c => c.targetId), ['good_2']);
  assert.equal(observeShadow742(idx, query(9000)).reason, 'insufficient-independent-history');
});

test('coarse task aggregates before ranking; a family is not evidence for a specific unseen/unsupported camp', () => {
  const idx = indexOf(track(10, 1, 'good_1'), track(11, 2, 'good_2'), track(12, 3, 'lane:bot'));
  const o = observation({ visibleTargets: [fact('good_15'), fact('good_99'), fact('evil_1')] });
  const r = observeShadow742(idx, o);
  assert.equal(r.reason, 'visible-family-only'); assert.deepEqual(r.candidates, []);
  assert.deepEqual(r.broadCandidates.map(c => [c.family, c.supportingAccounts, c.historicalFrequency]), [['own-camp', 2, .666667]]);
  assert.deepEqual(r.broadCandidates[0].visibleTargets.map(t => t.targetId), ['good_15']);
  for (const c of r.broadCandidates[0].cases) { assert.equal(c.family, 'own-camp'); assert.ok(!('label' in c)); }
  assert(shadow742StillCurrent(r, o)); assert(!shadow742StillCurrent(r, { ...o, visibleTargets: [fact('good_15', { state: 'empty' })] }));
  const onlyMinority = observeShadow742(idx, observation({ visibleTargets: [fact('lane:mid')] }));
  assert.equal(onlyMinority.status, 'abstain'); assert.equal(onlyMinority.evidence.broadDisposition, 'majority-not-visible');
  const tied = indexOf(track(10, 1, 'good_1'), track(11, 2, 'good_2'), track(12, 3, '?'), track(13, 4, 'evil_2'));
  const noMajority = observeShadow742(tied, o); assert.equal(noMajority.evidence.broadDisposition, 'no-majority');
  assert.deepEqual(noMajority.broadCandidates, []); // Do not renormalize the two eligible votes to 100%.
});

test('inventory uses positive sightings <= the cutoff, never future purchases or absence as possession', () => {
  const idx = indexOf(track(10, 1, 'good_1', { firstHeld: { bfury: 315 } }), track(11, 2, 'good_1', { firstHeld: { bfury: 314 } }),
    track(12, 3, 'good_2', { firstHeld: { bfury: 315.001 } }), track(13, 4, 'good_2'));
  const o = observation(), item = { matchId: 'demo', itemId: 'bfury', seenAtSec: 315 };
  const query = (itemsSeen: Shadow742Observation['self']['itemsSeen']) => observeShadow742(idx, { ...o, self: { ...o.self, itemsSeen } });
  assert.deepEqual(query([item]).evidence.conditionedItems, ['bfury']);
  assert.deepEqual(query([item]).candidates.map(c => [c.targetId, c.supportingAccounts]), [['good_1', 2]]);
  const unconditioned = observeShadow742(idx, o);
  for (const bad of [{ ...item, seenAtSec: 315.01 }, { ...item, matchId: 'other' }, { ...item, itemId: 'injected' }])
    assert.deepEqual(query([bad]), unconditioned);
  assert.deepEqual(query([{ ...item, itemId: 'manta' }]), unconditioned); // explicit backoff, not an inferred negative.
});

test('no repeated-account votes; minimum two / maximum seven distinct voters; exclude current match/account and future history', () => {
  const rows = [track(10, 1, 'good_1'), track(11, 1, 'good_1'), track(12, 2, 'good_1'), track(13, 3, 'good_1')];
  const r = observeShadow742(indexOf(...rows), observation({ matchStartUnix: 13000, accountId: 2 }));
  assert.equal(r.status, 'abstain'); assert.equal(r.evidence.accounts, 1);
  const cap = observeShadow742(indexOf(...Array.from({ length: 10 }, (_, i) => track(10 + i, 10 + i, 'good_1'))), observation());
  assert.equal(cap.evidence.availableAccounts, 10); assert.equal(cap.evidence.accounts, 7);
  assert.equal(new Set(cap.candidates[0].cases.map(c => c.accountId)).size, 7);
  const known = observation({ matchId: '13', matchStartUnix: undefined,
    visibleTargets: [fact('good_1', { matchId: 13 })] });
  const valid = observeShadow742(indexOf(...rows), known);
  assert.equal(valid.evidence.accountIsolation, 'known-focal-accounts-excluded');
  assert.equal(valid.evidence.accounts, 2); assert.ok(valid.candidates[0].cases.every(c => Number(c.matchId) < 13 && c.accountId !== '3'));
  assert.equal(observeShadow742(indexOf(...rows), { ...known, matchStartUnix: 999999 }).reason, 'conflicting-match-metadata');
  assert.equal(observeShadow742(indexOf(...rows), { ...known, accountId: 99 }).reason, 'conflicting-match-metadata');
  assert.equal(observeShadow742(base, observation({ matchStartUnix: undefined })).reason, 'need-match-start');
});

test('both heroes/sides; enemy jungle, unverified lane segments and static camp markers can never unlock a route', () => {
  const idx = indexOf(track(10, 1, 'evil_1', { hero: 'troll_warlord', team: 'dire' }),
    track(11, 2, 'evil_1', { hero: 'troll_warlord', team: 'dire' }),
    track(12, 3, 'lane:top', { hero: 'troll_warlord', team: 'dire' }));
  const o = observation({ hero: 'troll_warlord', team: 'dire', visibleTargets: [fact('good_1'), fact('evil_1'),
    fact('lane:top', { area: 'enemy' }), fact('evil_999')] });
  const result = observeShadow742(idx, o);
  assert.deepEqual(result.candidates.map(v => v.targetId), ['evil_1']);
  assert.deepEqual(observeShadow742(idx, { ...o, visibleTargets: [fact('good_1'), fact('lane:top', { area: 'unknown' })] }).candidates, []);
  assert.equal(observeShadow742(idx, { ...o, team: 'radiant' }).status, 'abstain');
  assert.equal(observeShadow742(idx, { ...o, patch: '7.42b' }).reason, 'unsupported-context');
  for (const key of ['route', 'nextAction', 'gold', 'safe', 'autoExecute']) assert.ok(!(key in result));
});

test('unseen vs observed empty; old, wrong-match, future or non-POV sightings are not evidence; ambiguities fail closed', () => {
  const run = (visibleTargets: Shadow742Target[]) => observeShadow742(base, observation({ visibleTargets }));
  const unseen = run([]); assert.equal(unseen.visibility.emptyOwn, 0); assert.equal(unseen.visibility.unobserved, 'unknown');
  const empty = run([fact('good_1', { state: 'empty' })]); assert.equal(empty.visibility.emptyOwn, 1);
  assert.equal(empty.reason, 'only-observed-empty-targets');
  for (const change of [{ seenAtSec: 311.9 }, { seenAtSec: 315.001 }, { matchId: 33 }, { source: 'replay' },
    { source: 'screen' }, { source: 'minimap' },
    { area: 'enemy' }, { area: 'unknown' }, { state: 'unknown' }] as Partial<Shadow742Target>[])
    assert.equal(run([fact('good_1', change)]).candidates.length, 0);
  const old = fact('good_1', { seenAtSec: 313 });
  assert.equal(run([fact('good_1', { state: 'empty' }), old]).visibility.emptyOwn, 1);
  assert.deepEqual(run([old, fact('good_1', { seenAtSec: 316, state: 'empty' })]), run([old])); // future fact not consulted
  const now = fact('good_1'), contradict = fact('good_1', { state: 'empty' });
  for (const facts of [[now, contradict], [contradict, now], [now, contradict, now]]) {
    const r = run(facts); assert.equal(r.candidates.length, 0); assert.equal(r.visibility.indeterminate, 1); assert.equal(r.visibility.emptyOwn, 0);
  }
});

test('allowlisted observer does not use or echo full-map state; no execution fallback on missing or stale self', () => {
  const o = observation(), before = structuredClone(o), normal = observeShadow742(base, o);
  assert.deepEqual(o, before);
  const poisoned = { ...o, worldSnapshots: [{ unsafe: 'SECRET' }], futureEvents: [['SECRET']], auto: true, safe: true,
    self: { ...o.self, enemies: ['SECRET'], nextPosition: 'SECRET' },
    visibleTargets: o.visibleTargets.map(f => ({ ...f, route: 'SECRET', isSafe: true })) };
  assert.deepEqual(observeShadow742(base, poisoned), normal);
  for (const bad of [{ ...o, informationScope: 'omniscient-offline-only' }, { ...o, self: { ...o.self, canAct: undefined } },
    { ...o, self: { ...o.self, observedAtSec: 316 } }, { ...o, self: { ...o.self, observedAtSec: 311 } },
    { ...o, self: { ...o.self, position: { x: Infinity, y: 0 } } }, { ...o, patch: '7.41f' }, { ...o, timeSec: 250 }])
    assert.equal(observeShadow742(base, bad as Shadow742Observation).candidates.length, 0);
});

test('abort, threat, death, loss of sight, account/context changes, rewinds and wall/game TTL revoke observations', () => {
  const o = observation({ accountId: 9999, capturedAtMs: 10000 }), options = { nowMs: 10001 };
  const r = observeShadow742(base, o, options);
  assert.equal(r.expiresAtSec, 318); assert(shadow742StillCurrent(r, o, options));
  const signal = AbortSignal.abort(); assert.equal(observeShadow742(base, o, { ...options, signal }).status, 'cancelled');
  assert(!shadow742StillCurrent(r, o, { ...options, signal }));
  for (const change of [{ dangerVisible: true }, { cancelled: true }, { self: { ...o.self, alive: false } }, { self: { ...o.self, canAct: false } }]) {
    assert.equal(observeShadow742(base, { ...o, ...change }, options).status, 'cancelled');
    assert(!shadow742StillCurrent(r, { ...o, ...change }, options));
  }
  for (const change of [{ timeSec: 314 }, { timeSec: 319 }, { matchId: 'other' }, { hero: 'troll_warlord' as const }, { team: 'dire' as const },
    { contextId: 'new' }, { accountId: 999 }, { matchStartUnix: 999999 }, { visibleTargets: [] },
    { visibleTargets: [fact('good_1')] }, { capturedAtMs: undefined }, { capturedAtMs: 9999 }])
    assert(!shadow742StillCurrent(r, { ...o, ...change }, options));
  assert(!shadow742StillCurrent(r, { ...o, capturedAtMs: 15001 }, { nowMs: 15001 })); // old observation expired even with new wall stamp
  assert.equal(observeShadow742(base, o, { nowMs: 16000 }).reason, 'stale-capture');
  assert.equal(observeShadow742(base, o, { nowMs: 9999 }).reason, 'stale-capture');
});

test('label is strictly future, bounded; unknown actors/origins, ties, pulls, illusions and opponent outcomes are not substituted', () => {
  function label(events: Hit[]) { return indexOf(track(10, 1, '', { events })).games[0].tracks[0].windows[0].label; }
  assert.deepEqual(label([[315, 'good_1', 'body'], [316, 'good_2', 'illusion'], [345, 'lane:mid', 'body', 9999, 9999]]),
    { site: 'lane:mid', atSec: 345 });
  assert.deepEqual(label([[345.001, 'good_1', 'body']]), { site: '@no-body-hit', atSec: null });
  assert.deepEqual(label([[316, 'evil_1', 'body'], [317, 'good_1', 'body']]), { site: '@opponent-camp', atSec: 316 });
  for (const [site, actor] of [['?', 'body'], ['lane:?', 'body'], ['lane:top', '?']])
    assert.deepEqual(label([[316, site, actor], [317, 'good_1', 'body']]), { site: '@unresolved', atSec: 316 });
  assert.deepEqual(label([[316, 'good_1', 'body'], [316, 'lane:bot', 'body']]), { site: '@unresolved', atSec: 316 });
  assert.deepEqual(label([[316, 'good_1', 'body'], [316, 'good_1', 'body']]), { site: 'good_1', atSec: 316 });
});

test('early end without last checkpoint: drop entire incomplete horizon even with a known hit; never interpolate a future body', () => {
  const partial = { duration: 356, points: [{ t: 300 }], endPoint: { t: 356 },
    trace: [[314, 10, 20, 1], [345.001, 9000, 9000, 1]],
    events: [[319, 'good_1', 'body'], [350, 'good_2', 'body']] };
  const [a, b, c] = indexOf(track(10, 1, '', partial)).games[0].tracks[0].windows;
  assert.equal(a.span, 'full'); assert.equal(a.label?.site, 'good_1');
  assert.equal(b.span, 'right-censored'); assert.equal(b.label, null); assert.equal(b.selfState, 'no-fresh-self');
  assert.equal(c.span, 'past-recording'); assert.equal(c.label, null);
  const unknownEnd = indexOf(track(10, 1, '', { duration: undefined, points: [], trace: [[314, 3, 4, 1]], events: [[316, '?', 'body']] }));
  assert.equal(unknownEnd.games[0].endUnix, null); assert.equal(unknownEnd.games[0].tracks[0].windows[0].label, null);
  const dead = indexOf(track(10, 1, '', { trace: [[314, 3, 4, 0], [315.1, 8, 9, 1]] }));
  assert.equal(dead.games[0].tracks[0].windows[0].selfState, 'dead'); assert.equal(dead.games[0].tracks[0].windows[0].prefix, null);
});

test('real replay check: exact own samples and post-anchor labels; suffix, economy, MMR and victory are never prefix features', () => {
  const raw = JSON.parse(readFileSync(new URL('../data/farming-742-routes.json', import.meta.url), 'utf8'));
  const row = raw.matches.find((m: { id: number }) => m.id === 9098402606);
  const get = (r: typeof row, t: number) => buildShadow742Index({ patch: raw.patch, matches: [r] }).games[0].tracks[0].windows.find(w => w.timeSec === t)!;
  const at705 = get(row, 705), at735 = get(row, 735);
  assert.deepEqual(at705.prefix, { x: 4760, y: -4140, observedAtSec: 704.367, itemsSeen: ['power_treads'] });
  assert.deepEqual(at705.label, { site: 'good_2', atSec: 707.367 });
  assert.deepEqual(at735.prefix, { x: 4191, y: -4968, observedAtSec: 734.367, itemsSeen: ['bfury', 'power_treads'] });
  assert.deepEqual(at735.label, { site: 'good_1', atSec: 735.3 });
  const mutated = { ...row, won: !row.won, mmr: 1, finalLH: 0, worldSnapshots: ['LEAK'], firstHeld: { ...row.firstHeld, manta: 705.001 },
    points: row.points.map((p: { t: number }) => ({ t: p.t, lh: 0, lane: 9999, neutral: 0 })),
    trace: row.trace.map((p: number[]) => p[0] <= 705 ? p : [p[0], 10000, 10000, 0]),
    events: [[705.001, 'lane:top', 'body', -9999, 9999]] };
  assert.deepEqual(get(mutated, 705).prefix, at705.prefix); assert.notDeepEqual(get(mutated, 705).label, at705.label);
});

test('fail closed on unverified patch, anonymous/duplicate tracks, inconsistent start or malformed chronology', () => {
  const row = track(10, 1, 'good_1');
  for (const c of [{ matches: [row] }, { patch: '7.41f', matches: [row] }, corpus([row, row]),
    corpus([{ ...row, accountId: 0 }]), corpus([{ ...row, events: [[321, '?', 'body'], [316, '?', 'body']] }]),
    { patch: { version: '7.42', from: row.start + 1 }, matches: [row] }]) assert.throws(() => buildShadow742Index(c));
});
