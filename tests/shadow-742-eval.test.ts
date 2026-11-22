import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { buildShadow742Index, observeShadow742, shadow742HistoricalRanks, shadow742HistoricalVotes } from '../src/tactics/shadow-742.ts';
import { evaluateShadow742, shadow742ClockBaseline, shadow742Fold, shadow742FrozenFold } from '../scripts/replays/shadow-742-eval.ts';

const root = new URL('../', import.meta.url);
const read = (file: string) => JSON.parse(readFileSync(new URL(file, root), 'utf8'));
const file = 'data/farming-742-routes.json', raw = read(file), index = buildShadow742Index(raw);
const cli = 'scripts/replays/shadow-742.ts';
const run = (...args: string[]) => JSON.parse(execFileSync(process.execPath, [cli, ...args], { cwd: root, encoding: 'utf8' }));
function player(id: number, accountId: number, start: number, changes: object = {}) {
  return { id, accountId, start, duration: 400, hero: 'luna', team: 'radiant', events: [[320, 'good_1', 'body']],
    trace: [[315, 0, 0, 1]], points: [{ t: 345 }], firstHeld: {}, ...changes };
}

test('time cutoff ties are atomic, remove entire matches sharing a focal account across heroes/sides, overlap/unknown end barred', () => {
  const idx = buildShadow742Index({ patch: '7.42', matches: [
    player(1, 10, 1000), player(2, 20, 2000), player(2, 30, 2000, { hero: 'troll_warlord', team: 'dire' }),
    player(3, 33, 3000, { duration: undefined }), player(4, 40, 4000, { duration: 2000 }),
    player(5, 50, 6000), player(6, 30, 6000), player(7, 70, 7000),
  ] });
  const frozen = shadow742FrozenFold(idx);
  assert.deepEqual(frozen.audit.testMatchIds, ['5', '6', '7']);
  assert.deepEqual(frozen.audit.trainMatchIds, ['1']);
  assert.deepEqual(frozen.audit.pastGamesExcludedForAccount, ['2']);
  assert.deepEqual(frozen.audit.pastGamesExcludedForUnknownOrOverlappingEnd, ['3', '4']);
  const f = shadow742Fold(idx, [idx.games.find(g => g.id === '6')!]);
  assert(!f.train.some(g => g.id === '2')); assert(!f.train.some(g => g.start >= 6000));
});

test('clock baseline is fitted on history, account-balanced, indifferent to self XY and inventory', () => {
  const rows = Array.from({ length: 8 }, (_, i) => player(i + 1, 7, 1000, { events: [[320, 'good_2', 'body']] }))
    .concat([player(20, 8, 1000), player(21, 9, 1000)]);
  for (const matches of [rows, rows.map(m => ({ ...m, trace: [[315, 9000, 9000, 1]], firstHeld: { bfury: 314 } }))]) {
    const train = buildShadow742Index({ patch: '7.42', matches }).games;
    assert.deepEqual(shadow742ClockBaseline(train, 'luna', 'radiant', 315), ['good_1', 'good_2']);
    assert.deepEqual(shadow742ClockBaseline(train.filter(g => g.tracks[0].accountId === '7'), 'luna', 'radiant', 315), []);
  }
});

test('minute-only games cannot enter train, test or baseline, even if their self economy/terminal checkpoints exist', () => {
  const spatial = [player(1, 1, 1000), player(2, 2, 2000), player(3, 3, 3000), player(4, 4, 4000)];
  const minute = Array.from({ length: 85 }, (_, i) => player(100 + i, 100 + i, 10000 + i, { events: [], trace: [],
    points: [{ t: 600, lh: 200 }, { t: 1200, lh: 400 }], firstHeld: { bfury: 500 } }));
  const pure = evaluateShadow742(buildShadow742Index({ patch: '7.42', matches: spatial }));
  const mixed = evaluateShadow742(buildShadow742Index({ patch: '7.42', matches: [...spatial, ...minute] }));
  assert.equal(mixed.corpus.matches, 4); assert.equal(mixed.corpus.omittedNonspatialTrackIds.length, 85);
  assert.deepEqual(mixed.frozenChronological, pure.frozenChronological); assert.deepEqual(mixed.rollingOrigin, pure.rollingOrigin);
  assert.throws(() => buildShadow742Index({ patch: '7.42', matches: minute }), /No spatial replay tracks/);
});

test('every fixed/rolling reference is earlier and account-/match-disjoint; no conditioning on held-out truth', () => {
  const fold = shadow742FrozenFold(index);
  assert.deepEqual(fold.audit.testMatchIds, ['9098789898', '9099209663', '9099283649', '9100049738']);
  assert.deepEqual(fold.audit.pastGamesExcludedForAccount, ['9098472218']);
  assert.equal(fold.train.length, 11); assert.equal(fold.audit.trainAccountIds.length, 9);
  for (const f of [fold, ...index.games.map(g => shadow742Fold(index, [g]))]) {
    const held = new Set(f.test.flatMap(g => g.tracks.map(p => p.accountId)));
    const allowed = new Map(f.train.map(g => [g.id, g]));
    for (const g of f.test) for (const p of g.tracks) for (const w of p.windows) if (w.prefix && w.label) {
      const v = shadow742HistoricalVotes(f.train, p.hero, p.team, w.timeSec, w.prefix);
      assert.equal(new Set(v.cases.map(c => c.accountId)).size, v.cases.length);
      for (const c of v.cases) {
        assert(!held.has(c.accountId)); const ref = allowed.get(c.matchId)!;
        assert(ref.endUnix! < f.cutoffUnix); assert(f.test.every(game => game.id !== c.matchId));
        assert(ref.tracks.some(q => q.accountId === c.accountId && q.hero === p.hero && q.team === p.team));
        if (c.labelAtSec !== null) assert(c.timeSec < c.labelAtSec && c.labelAtSec <= c.timeSec + 30);
      }
    }
  }
  // Rewrite ALL future events of a specific held-out match: query ranks and
  // their evidence must stay identical while the offline truth actually changes.
  const changed = structuredClone(raw);
  const victim = changed.matches.find((m: { id: number }) => m.id === 9100049738);
  victim.events = [[316, 'lane:mid', 'body']]; victim.won = !victim.won; victim.mmr = 1;
  const rank = (idx: typeof index) => {
    const f = shadow742FrozenFold(idx), p = f.test.find(g => g.id === '9100049738')!.tracks[0], w = p.windows[0];
    return { label: w.label, votes: shadow742HistoricalRanks(shadow742HistoricalVotes(f.train, p.hero, p.team, w.timeSec, w.prefix!), p.team) };
  };
  const a = rank(index), b = rank(buildShadow742Index(changed));
  assert(a.votes.length); assert.deepEqual(a.votes, b.votes); assert.notDeepEqual(a.label, b.label);
});

test('empty and censored horizons never become lucky correct predictions; coverage includes dead or unsupported windows', () => {
  const rows = [player(1, 1, 1000), player(2, 2, 2000), player(3, 3, 3000), player(4, 4, 4000, {
    duration: 385, points: [], endPoint: { t: 385 }, events: [[380, 'good_1', 'body']],
    trace: [[314, 0, 0, 1], [344, 0, 0, 0], [374, 0, 0, 1]],
  })];
  const report = evaluateShadow742(buildShadow742Index({ patch: '7.42', matches: rows })).frozenChronological.overall;
  assert.deepEqual([report.gridWindows, report.fullWindows, report.rightCensored, report.pastRecording], [29, 2, 1, 26]);
  assert.deepEqual(report.outcomesOnFullWindows, { '@no-body-hit': 2 });
  assert.deepEqual([report.retrieval.coverage.numerator, report.retrieval.coverage.denominator], [1, 2]);
  assert.deepEqual([report.retrieval.top1.numerator, report.retrieval.top1.denominator], [0, 1]);
  assert.deepEqual(report.retrieval.top1.ci95, null); // one held-out cluster, not 2/29 independent experiments.
  assert.deepEqual([report.visibleDisplay.displayed, report.visibleDisplay.denominatorFullWindows], [0, 2]);
});

test('published fixed report matches local recomputation; primary/paired denominators and unavailable uncertainty are explicit', () => {
  const saved = read('data/farming-742-shadow-fixed16.json'), { source, ...body } = saved;
  assert.equal(source.sha256, createHash('sha256').update(readFileSync(new URL(file, root))).digest('hex'));
  assert.deepEqual(evaluateShadow742(index), body);
  const s = body.frozenChronological.overall, roll = body.rollingOrigin.overall;
  assert.deepEqual([s.gridWindows, s.freshAliveWindows, s.visibleDisplay.displayed], [116, 108, 0]);
  assert.deepEqual([s.retrieval.coverage.numerator, s.retrieval.top1.numerator, s.retrieval.top1.denominator], [15, 7, 14]);
  assert.deepEqual([s.pairedTop1.baseline.numerator, s.pairedTop1.baseline.denominator], [6, 14]);
  const broad = s.nextFamily, rollingBroad = roll.nextFamily;
  assert.deepEqual([broad.retrieval.coverage.numerator, broad.retrieval.top1.numerator, broad.retrieval.top1.denominator], [4, 4, 4]);
  assert.deepEqual([broad.pairedTop1.retrieval.numerator, broad.pairedTop1.baseline.numerator, broad.pairedTop1.difference.denominator], [2, 2, 2]);
  assert.equal(broad.pairedTop1.difference.ci95, null);
  assert.deepEqual([rollingBroad.retrieval.coverage.numerator, rollingBroad.retrieval.top1.numerator, rollingBroad.retrieval.top1.denominator], [72, 40, 71]);
  assert.deepEqual([rollingBroad.pairedTop1.retrieval.numerator, rollingBroad.pairedTop1.baseline.numerator,
    rollingBroad.pairedTop1.difference.denominator], [29, 26, 54]);
  assert.equal(s.pairedTop1.difference.clusters, 1); assert.equal(s.pairedTop1.difference.ci95, null);
  assert.deepEqual([roll.matches, roll.accounts, roll.fullWindows, roll.retrieval.coverage.numerator], [16, 13, 464, 126]);
  assert.deepEqual([roll.pairedTop1.retrieval.numerator, roll.pairedTop1.baseline.numerator, roll.pairedTop1.difference.denominator],
    [49, 36, 118]); assert.equal(roll.visibleDisplay.liveAccuracy, null);
  assert(roll.pairedTop1.difference.ci95[0] < roll.pairedTop1.difference.rate);
  for (const section of [body.frozenChronological, body.rollingOrigin]) {
    assert.equal(Object.values<{ fullWindows: number }>(section.byClockMinutes).reduce((n, g) => n + g.fullWindows, 0), section.overall.fullWindows);
    assert.equal(Object.values<{ fullWindows: number }>(section.byHeroTeam).reduce((n, g) => n + g.fullWindows, 0), section.overall.fullWindows);
  }
});

test('file and stdin CLI match the pure function; auto selects a published expanded corpus instead of the fixed demo', () => {
  const f = 'data/farming-742-shadow-example.json', o = read(f), expected = observeShadow742(index, o);
  assert.equal(expected.status, 'observed');
  assert.deepEqual(run('observe', '--input', f, '--corpus', 'fixed'), expected);
  const pipe = spawnSync(process.execPath, [cli, 'observe', '--input', '-', '--corpus', 'fixed'],
    { cwd: root, encoding: 'utf8', input: JSON.stringify({ ...o, visibleTargets: [] }) });
  assert.equal(pipe.status, 0); assert.equal(JSON.parse(pipe.stdout).reason, 'no-current-visible-own-target');
  const auto = run('evaluate', '--summary');
  assert.equal(auto.source.file, existsSync(new URL('data/farming-742-expanded.json', root)) ? 'data/farming-742-expanded.json' : file);
  assert.equal(auto.rollingOrigin.visibleDisplay.displayed, 0);
});
