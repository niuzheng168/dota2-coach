import assert from 'node:assert/strict';
import fs from 'node:fs';
import {test} from 'node:test';
test('double-stack case verifies old outside units and real spawn timestamps without claiming all map stacks',()=>{
  const c=JSON.parse(fs.readFileSync('data/luna-stacking-case.json','utf8'));
  assert.equal(c.matchId,9089477184);assert.equal(c.events.length,2);
  const first=c.events[0];
  assert.deepEqual(first.camps.map((p:any)=>p.id),['neutralcamp_evil_11','neutralcamp_evil_20']);
  for(const e of c.events)for(const p of e.camps){
    assert.equal(p.oldInside,0);assert.equal(p.oldSurvivingAfter,p.oldAlive);
    assert.equal(p.newAlive,p.oldAlive+p.freshSpawns.length);
    assert.ok(p.freshSpawns.every((s:any)=>s.spawnedAt>=e.minute&&s.spawnedAt<=e.afterTime));
  }
  assert.ok(c.events[1].camps.some((p:any)=>p.id==='neutralcamp_good_13'));
  assert.ok(c.observations.some((s:string)=>s.includes('不能把全图')));
  assert.ok(c.limitations.some((s:string)=>s.includes('不是玩家视野')));
});
