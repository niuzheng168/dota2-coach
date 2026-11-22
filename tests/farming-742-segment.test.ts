import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { studySegment } from '../scripts/client/farm-742-segment.ts';
import type { Segment742 } from '../scripts/client/farm-742-segment.ts';
const sample: Segment742 = JSON.parse(readFileSync(new URL('../data/farming-742-segment-example.json', import.meta.url), 'utf8'));

test('uses route length / effective speed before simulating full-health members at the first hit clock', () => {
  const r = studySegment(sample);
  assert(r.walk.ok && r.clear && r.travelAndClearSeconds);
  assert.equal(r.firstHitClock, sample.departureGameSeconds + r.walk.seconds + sample.arrivalToFirstHitSeconds!);
  assert.deepEqual(r.clear.bounty!.wholeCamp, [81, 93]);
  assert.equal(r.travelAndClearSeconds[0], Math.round((r.walk.seconds + .55 + r.clear.timing!.firstHitToLastDeathSeconds[0]) * 1000) / 1000);
  const faster = studySegment({ ...sample, effectiveMoveSpeed: sample.effectiveMoveSpeed * 1.2 });
  assert(faster.walk.ok && faster.travelAndClearSeconds && faster.travelAndClearSeconds[0] < r.travelAndClearSeconds[0]);
});
test('no zero-delay guess, straight-line fallback, or false clear ETA when attributes are missing', () => {
  assert.equal(studySegment({ ...sample, arrivalToFirstHitSeconds: null }).travelAndClearSeconds, null);
  assert.equal(studySegment({ ...sample, effectiveMoveSpeed: 0 }).walk.ok, false);
  const bounty = studySegment({ ...sample, camp: { ...sample.camp, attacker: undefined } });
  assert(bounty.clear?.bounty && bounty.travelAndClearSeconds === null);
  assert.throws(() => studySegment({ ...sample, mapBuild: 6975 as 6989 }));
});
