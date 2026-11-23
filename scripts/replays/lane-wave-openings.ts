/** Offline validation only. First-wave lane membership from spawn proximity + original entity identity;
 * future full-map positions produce the comparator only, never an input to the coach.
 * node scripts/replays/lane-wave-openings.ts  -> writes a compact report input under the replay work root.
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { replayArea } from '../../src/replays/storage.ts';

const map = JSON.parse(fs.readFileSync(new URL('../../data/minute-farm-742-client.json', import.meta.url), 'utf8'));
assert.equal(map.build, 6989);
const roads = ['top', 'mid', 'bot'];
const dist = (a: any, b: any) => Math.hypot(a.x - b.x, a.y - b.y);
const work = replayArea('farm742');
function opening(id: string) {
  const j = JSON.parse(fs.readFileSync(path.join(work, `${id}.facts.json`), 'utf8'));
  assert(j.complete && j.gameBuild === map.build && String(j.matchId) === id);
  const frames = j.frames.filter((f: any) => f.gameTime >= 0 && f.gameTime <= 45);
  const rows = (f: any) => f.units.filter((u: any) => u.class === 'CDOTA_BaseNPC_Creep_Lane' && u.lifeState === 0 && !u.waitingToSpawn);
  const keys = new Map<string, string>(), birth = frames.find((f: any) => f.gameTime < 2 && rows(f).length === 24);
  assert(birth, 'requires a full opening snapshot');
  for (const u of rows(birth)) {
    const choices = map.lanes.filter((l: any) => l.id.includes(u.team === 2 ? '_good_' : '_bad_')).map((l: any) =>
      ({ lane: l.id.split('_').at(-1), d: dist(u.position, { x: l.points[0][0], y: l.points[0][1] }) })).sort((a: any, b: any) => a.d - b.d);
    assert(choices[0].d < 768 && choices[1].d - choices[0].d > 512);
    keys.set(u.id, choices[0].lane);
  }
  const origin = frames.find((f: any) => f.gameTime >= 12); // fixed BEFORE looking at any contact
  const truth = roads.map(lane => {
    for (const f of frames) {
      const wave = rows(f).filter((u: any) => keys.get(u.id) === lane);
      let closest = 501; let mid: { x: number; y: number } | undefined;
      for (const a of wave.filter((u: any) => u.team === 2)) for (const b of wave.filter((u: any) => u.team === 3)) {
        const d = dist(a.position, b.position);
        if (d <= 500 && d < closest) { closest = d; mid = { x: (a.position.x + b.position.x) / 2, y: (a.position.y + b.position.y) / 2 }; }
      }
      if (mid) { assert(f.gameTime > origin.gameTime); return { lane, clock: f.gameTime, ...mid }; }
    }
    throw new Error(`No opening contact for ${id} ${lane}`);
  });
  return { match: id, clock: origin.gameTime, units: rows(origin).map((u: any) => ({ name: u.unitName, team: u.team, ...u.position })), truth };
}
const cases = ['9099209663','9099283649','9100049738'].map(opening);
const out = path.join(work, 'tmp', 'opening-wave-6989.json');
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(cases));
console.log(out);
