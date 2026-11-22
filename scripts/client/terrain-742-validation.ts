/** Independent replay test, never used to open cells, choose a route or fit a
 * speed multiplier. Geometry comes only from the installed map. */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { inflateSync } from 'node:zlib';
import { loadTerrain742, openGnv742 } from '../../src/tactics/terrain-742.ts';
import type { XY742 } from '../../src/tactics/terrain-742.ts';

const root = path.resolve(import.meta.dirname, '../..'), rel = (p: string) => path.join(root, p);
const read = (p: string) => JSON.parse(fs.readFileSync(rel(p), 'utf8'));
const tracesPath = 'data/farming-742-terrain-traces.json', reportPath = 'data/farming-742-terrain-validation.json';
const sampleFormat = 'zlib/base64, 23-byte LE records: 5 x i32 deltas (tick, game ms, X*32, Y*32, Z*32) from zero, u16 recorded BASE speed, u8 bits alive=1 moving=2';
export type Capture742 = { id: number; hero: string; team: string; build: number; demSha256: string; samples: number; samplesSha256: string; samplesZlib: string };
export type Sample742 = XY742 & { tick: number; ms: number; z: number; base: number; alive: boolean; moving: boolean };
type Camp = { id: string; origin: [number, number, number] };
const rr = (v: number) => Math.round(v * 1000) / 1000;
const dist = (p: XY742, q: XY742) => Math.hypot(p.x - q.x, p.y - q.y);
const inc = (o: Record<string, number>, k: string) => { o[k] = (o[k] ?? 0) + 1; };
const quantile = (list: number[], p: number) => {
  const sorted = list.toSorted((a, b) => a - b);
  return sorted.length ? rr(sorted[Math.max(0, Math.ceil(p * sorted.length) - 1)]) : null;
};
const mean = (list: number[]) => list.length ? rr(list.reduce((a, b) => a + b, 0) / list.length) : null;

export function decodeCapture742(m: Capture742) {
  assert(m.build === 6989 && Number.isInteger(m.samples) && m.samples >= 5000 && m.samples <= 12000);
  const b = inflateSync(Buffer.from(m.samplesZlib, 'base64'), { maxOutputLength: m.samples * 23 });
  assert.equal(b.length, m.samples * 23);
  assert.equal(createHash('sha256').update(b).digest('hex'), m.samplesSha256, 'Trace bytes corrupted');
  const a = [0, 0, 0, 0, 0], rows: Sample742[] = [];
  let repeatedClock = 0;
  for (let i = 0; i < b.length; i += 23) {
    assert(b.readInt32LE(i) > 0 && b.readInt32LE(i + 4) >= 0, 'Invalid source clock');
    for (let j = 0; j < a.length; j++) a[j] += b.readInt32LE(i + 4 * j);
    const state = b[i + 22]; assert(state <= 3);
    if (a[1] === rows.at(-1)?.ms) { repeatedClock++; continue; } // never count a pause as extra wall-clock walking
    rows.push({ tick: a[0], ms: a[1], x: a[2] / 32, y: a[3] / 32, z: a[4] / 32, base: b.readUInt16LE(i + 20),
      alive: !!(state & 1), moving: !!(state & 2) });
  }
  assert(rows[0].ms < 298000 && rows.at(-1)!.ms >= 1200000 && rows.at(-1)!.ms <= 1201000);
  return { rows, repeatedClock };
}
function continuous(a: Sample742, b: Sample742) {
  const dt = b.ms - a.ms;
  return a.alive && b.alive && dt >= 80 && dt <= 200 && b.tick - a.tick <= 6;
}
function stepSpeed(a: Sample742, b: Sample742) { return 1000 * dist(a, b) / (b.ms - a.ms); }
function walking(a: Sample742, b: Sample742) {
  const v = stepSpeed(a, b);
  return continuous(a, b) && a.moving && b.moving && v >= 120 && v <= 650;
}
function travelled(rows: Sample742[], a: number, b: number) {
  let d = 0;
  for (let i = a; i < b; i++) d += dist(rows[i], rows[i + 1]);
  return d;
}
const speedBefore = (rows: Sample742[], first: number, start: number) =>
  travelled(rows, first, start) * 1000 / (rows[start].ms - rows[first].ms);
function near(p: XY742, camps: Camp[]) {
  const nearest = camps.map(c => ({ id: c.id, d: dist(p, { x: c.origin[0], y: c.origin[1] }) })).sort((a, b) => a.d - b.d)[0];
  return nearest.d <= 600 ? nearest.id : null; // proximity label, not kill/clear attribution
}
type Leg = { id: number; gameStart: number; gameEnd: number; from: [number, number]; to: [number, number];
  observedS: number; predictedS: number; errorS: number; prefixSpeed: number; observedSpeed: number; recordedBase: number;
  modelLength: number; observedLength: number; chordLength: number; chordBlocked: boolean; observedPolylineSupported: boolean;
  campA: string | null; campB: string | null };

function describe(legs: Leg[]) {
  const errors = legs.map(v => v.errorS), abs = errors.map(Math.abs), durations = legs.map(v => v.observedS);
  return { n: legs.length, meanAbsS: mean(abs), medianAbsS: quantile(abs, .5), p90AbsS: quantile(abs, .9),
    maxAbsS: legs.length ? rr(Math.max(...abs)) : null, meanSignedS: mean(errors),
    observedMedianS: quantile(durations, .5), observedMaxS: quantile(durations, 1),
    observedMinS: quantile(durations, 0), avgModelMinusObservedUnits: mean(legs.map(v => v.modelLength - v.observedLength)),
    // A benchmark *only*. A blocked chord is never returned to a caller as a path.
    chordDiagnosticMaeS: mean(legs.map(v => Math.abs(v.chordLength / v.prefixSpeed - v.observedS))),
    speedShiftOver10Pct: legs.filter(v => Math.abs(v.observedSpeed / v.prefixSpeed - 1) > .1).length,
    observedPolylineSupported: legs.filter(v => v.observedPolylineSupported).length };
}
function score(m: Capture742, rows: Sample742[], start: number, end: number, speed: number, camps: Camp[]) {
  const map = loadTerrain742(), a = rows[start], b = rows[end], route = map.walk(a, b, speed);
  if (!route.ok) return { refused: route.reason } as const;
  const observedS = (b.ms - a.ms) / 1000, observedLength = travelled(rows, start, end);
  const leg: Leg = { id: m.id, gameStart: a.ms / 1000, gameEnd: b.ms / 1000, from: [a.x, a.y], to: [b.x, b.y],
    observedS, predictedS: route.seconds, errorS: route.seconds - observedS, prefixSpeed: speed,
    recordedBase: a.base, observedSpeed: observedLength / observedS, chordBlocked: !map.segmentIsOpen(a, b),
    chordLength: dist(a, b), modelLength: route.distance, observedLength,
    observedPolylineSupported: rows.slice(start + 1, end + 1).every((p, i) => map.segmentIsOpen(rows[start + i], p)),
    campA: near(a, camps), campB: near(b, camps) };
  return { leg } as const;
}
const encodeLeg = (leg: Leg) => Object.fromEntries(Object.entries(leg).map(([k, v]) => [k, typeof v === 'number' ? rr(v) : v]));
function legGroup(legs: Leg[], rejects: Record<string, number>) {
  return { ...describe(legs), refusals: rejects, blockedChord: describe(legs.filter(l => l.chordBlocked)),
    betweenDistinctCampVicinity: describe(legs.filter(l => l.campA && l.campB && l.campA !== l.campB)) };
}
export function validateTerrain742(captures: Capture742[]) {
  const map = loadTerrain742(), artifact = read('data/farming-742-terrain.json'), corpus = read('data/farming-742-routes.json');
  const ids = corpus.matches.filter((m: any) => m.build === 6989).map((m: any) => m.id).sort();
  assert.deepEqual(captures.map(m => m.id).sort(), ids, 'Hold out ALL exact-build matches');
  assert.deepEqual(ids, [9099209663, 9099283649, 9100049738]);
  const all: Leg[] = [], mixed: Leg[] = [], fixedWalk: Leg[] = [], allHeights: number[] = [];
  const failures: Record<string, number> = {}, schedule: Record<string, number> = {}, pointStates: Record<string, number> = {};
  const results = [];
  let allAlive = 0, allGnvOpen = 0, allSupported = 0, totalRepeated = 0;
  for (const m of captures) {
    const pin = corpus.matches.find((p: any) => p.id === m.id);
    assert(pin.demSha256 === m.demSha256 && pin.hero === m.hero && pin.team === m.team && m.build === pin.build);
    const { rows, repeatedClock } = decodeCapture742(m); totalRepeated += repeatedClock;
    let alive = 0, gnvOpen = 0, supported = 0;
    const hz: number[] = [], legs: Leg[] = [], refused: Record<string, number> = {};
    for (const p of rows) {
      if (!p.alive || p.ms < 300000 || p.ms >= 1200000) continue;
      alive++; const c = map.inspect(p);
      if (c && openGnv742(c.flag)) gnvOpen++;
      if (c?.centerSurfaceZ != null) hz.push(Math.abs(p.z - c.centerSurfaceZ));
      if (map.segmentIsOpen(p, p)) { supported++; inc(pointStates, 'open'); }
      else inc(pointStates, c?.state === 'open' ? 'hull-near-blocked-cell' : (c?.state ?? 'outside'));
    }
    allAlive += alive; allGnvOpen += gnvOpen; allSupported += supported; allHeights.push(...hz);
    // Maximal walking bouts, selected with no access to the map or residual.
    // Split long bouts without overlapping future labels/calibration steps.
    let first = 0, bouts = 0, shortBouts = 0;
    for (let i = 1; i <= rows.length; i++) {
      if (i < rows.length && rows[i - 1].ms >= 300000 && rows[i].ms < 1200000 && walking(rows[i - 1], rows[i])) continue;
      const last = i - 1;
      if (last > first) {
        bouts++;
        let admitted = false;
        for (let a = first; a < last;) {
          let start = a;
          while (start < last && rows[start].ms - rows[a].ms < 1200) start++;
          let end = start;
          while (end < last && rows[end + 1].ms - rows[start].ms <= 10000) end++;
          if (rows[end].ms - rows[start].ms < 2000) break;
          admitted = true;
          const result = score(m, rows, start, end, speedBefore(rows, a, start), artifact.camps);
          if (result.leg) { legs.push(result.leg); all.push(result.leg); }
          else { inc(refused, result.refused); inc(failures, result.refused); }
          a = end;
        }
        if (!admitted) shortBouts++;
      }
      first = i;
    }
    // Stress test: do NOT select the future by looking for continuous motion.
    // Predeclared, disjoint time windows, including stops/turns/farming. Show
    // all exclusions, not just the subset for which the map returns a result.
    const at = (ms: number) => rows.findIndex(p => p.ms >= ms);
    for (let s = 300000, j = 0; s < 1200000; s += 10000, j++) {
      const a = at(s - 1200), b = at(s), c = at(s + [3000, 5000, 7000][j % 3]);
      assert(a >= 0 && b > a && c > b);
      if (!rows.slice(a + 1, b + 1).every((p, i) => walking(rows[a + i], p))) { inc(schedule, 'no-moving-speed-prefix'); continue; }
      if (!rows.slice(b + 1, c + 1).every((p, i) => continuous(rows[b + i], p) && stepSpeed(rows[b + i], p) <= 650)) {
        inc(schedule, 'dead-gap-or-jump'); continue;
      }
      const r = score(m, rows, b, c, speedBefore(rows, a, b), artifact.camps);
      if (!r.leg) { inc(schedule, r.refused); continue; }
      if (rows.slice(b + 1, c + 1).every((p, i) => walking(rows[b + i], p))) { inc(schedule, 'walk'); fixedWalk.push(r.leg); }
      else { inc(schedule, 'mixed'); mixed.push(r.leg); }
    }
    results.push({ id: m.id, hero: m.hero, team: m.team, totalSamples: m.samples, repeatedClock, alive, gnvOpen, supported,
      heightP90Abs: quantile(hz, .9), heightMaxAbs: quantile(hz, 1), movementBouts: bouts, boutsTooShort: shortBouts,
      walking: legGroup(legs, refused) });
  }
  const worst = all.toSorted((a, b) => Math.abs(b.errorS) - Math.abs(a.errorS))[0];
  const campLegs = all.filter(l => l.campA && l.campB && l.campA !== l.campB);
  return { version: 1, patch: '7.42', build: 6989, holdout: ids, provenance: 'VPK-derived routing; untouched full matches. Past 1.2 s XY speed estimate, NEVER future length/time or the base field.',
    q: 'nearest rank', inputs: { mapVpk: artifact.sources.mapVpk.sha256, gnv: artifact.gnv.sha256, trm: artifact.trm.sha256,
      surface: artifact.solidSurfaceQ4.sha256, clips: artifact.clipMask.sha256, policy: artifact.format.routingPolicy },
    tracePayloads: captures.map(m => [m.id, m.samplesSha256]),
    geometry: { alive: allAlive, repeatedClockDiscardedInCapture: totalRepeated, rawGnvOpen: allGnvOpen, fullEnvelopeSupported: allSupported, pointStates,
      centerZVsNetworkZ: { n: allHeights.length, meanAbs: mean(allHeights), medianAbs: quantile(allHeights, .5), p90Abs: quantile(allHeights, .9),
        p99Abs: quantile(allHeights, .99), maxAbs: quantile(allHeights, 1), over32: allHeights.filter(v => v > 32).length } },
    uninterrupted: legGroup(all, failures), scheduled: { candidates: 270, categories: schedule, walking: describe(fixedWalk), mixed: describe(mixed) },
    matches: results, worst: worst ? encodeLeg(worst) : null, longestCampExample: campLegs.length
      ? encodeLeg(campLegs.toSorted((a, b) => b.observedS - a.observedS)[0]) : null };
}

if (process.argv[1] && path.resolve(process.argv[1]) === import.meta.filename) {
  const args = process.argv.slice(2);
  if (args.some(a => !['--from-captures', '--check', '--write'].includes(a)) || args.includes('--check') && args.includes('--write'))
    throw new Error('Usage: terrain-742-validation.ts [--from-captures] [--check|--write]');
  let captures: Capture742[];
  if (args.includes('--from-captures')) {
    const ids = [9099209663, 9099283649, 9100049738];
    captures = ids.map(id => read(`.tmp/farm742-terrain/${id}.trace.json`));
    const bundle = { version: 1, sampleFormat, captures };
    if (args.includes('--check')) {
      const existing = read(tracesPath);
      for (const [i, c] of captures.entries()) {
        assert.deepEqual(decodeCapture742(c), decodeCapture742(existing.captures[i]));
        existing.captures[i].samplesZlib = c.samplesZlib;
      }
      assert.deepEqual(existing, bundle);
    } else if (args.includes('--write')) fs.writeFileSync(rel(tracesPath), JSON.stringify(bundle) + '\n');
  } else {
    const bundle = read(tracesPath); assert(bundle.version === 1 && bundle.sampleFormat === sampleFormat); captures = bundle.captures;
  }
  const report = validateTerrain742(captures);
  if (args.includes('--check')) assert.deepEqual(read(reportPath), report);
  else if (args.includes('--write')) fs.writeFileSync(rel(reportPath), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify({ geometry: report.geometry, uninterrupted: report.uninterrupted, scheduled: report.scheduled }, null, 2));
}
