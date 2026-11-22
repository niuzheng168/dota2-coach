/** Offline "what if" for ONE observed group at ONE supplied attack position.
 * Never called by the shadow bridge; returns no navigation/decision to a live client. */
import fs from 'node:fs';
import { estimateWalk742 } from '../../src/tactics/terrain-742.ts';
import { estimateCamp742 } from '../../src/tactics/camp-742.ts';
import type { CampInput742 } from '../../src/tactics/camp-742.ts';
import type { XY742 } from '../../src/tactics/terrain-742.ts';

export interface Segment742 {
  patch: '7.42'; mapBuild: 6989; departureGameSeconds: number;
  from: XY742; attackPosition: XY742; effectiveMoveSpeed: number;
  // No implicit zero. May include acquisition/turn/attack point and projectile.
  arrivalToFirstHitSeconds: number | null;
  camp: Omit<CampInput742, 'patch' | 'clock' | 'engageToFirstHitSeconds'>;
}
export function studySegment(s: Segment742) {
  if (s?.patch !== '7.42' || s.mapBuild !== 6989 || !Number.isFinite(s.departureGameSeconds) || s.departureGameSeconds < 0)
    throw Error('This scenario requires 7.42 build 6989 and a known departure game clock');
  if (!s.from || !s.attackPosition || !s.camp) throw Error('Missing world positions or exact unit group');
  const walk = estimateWalk742(s.from, s.attackPosition, s.effectiveMoveSpeed);
  if (!walk.ok) return { scope: 'offline-scenario', walk, clear: null, travelAndClearSeconds: null, reason: 'No supported static walk' };
  const engage = s.arrivalToFirstHitSeconds;
  if (typeof engage !== 'number' || !Number.isFinite(engage) || engage < 0 || engage > 30)
    return { scope: 'offline-scenario', walk, clear: null, travelAndClearSeconds: null, reason: 'Missing or unsupported arrival-to-first-hit delay' };
  const firstHitClock = s.departureGameSeconds + walk.seconds + engage;
  const clear = estimateCamp742({ ...s.camp, patch: s.patch, clock: firstHitClock, engageToFirstHitSeconds: engage });
  const span = clear.timing?.engageToLastDeathSeconds;
  return { scope: 'offline-scenario', walk, firstHitClock, clear,
    travelAndClearSeconds: span ? span.map(t => Math.round((walk.seconds + t) * 1000) / 1000) : null,
    reason: span ? 'Assumes unchanged observed group, unobstructed uninterrupted attacks and supplied contact delay; not an in-game ETA.' : clear.reasons.join('; ') };
}
if (import.meta.main) {
  const file = process.argv[2];
  if (!file) throw Error('Usage: node scripts/client/farm-742-segment.ts data/farming-742-segment-example.json');
  console.log(JSON.stringify(studySegment(JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''))), null, 2));
}
