import assert from 'node:assert/strict';
import {test} from 'node:test';
import fs from 'node:fs';
import { visibleFarmingContext,reminderStillApplicable } from '../src/tactics/situational-farming.ts';
import type { FarmingObservation, FarmingReminder } from '../src/tactics/situational-farming.ts';
const fixture=JSON.parse(fs.readFileSync('tests/fixtures/luna-741f.json','utf8'));
function observation():FarmingObservation {
  return {informationScope:'player-visible',contextId:'ctx',matchId:'m1',capturedAt:10000,
    player:{...structuredClone(fixture.player),clock:600},
    facts:[{id:'f1',entityId:'wave1',kind:'wave-seen',source:'screen',matchId:'m1',
      capturedAt:10000,gameTime:600,position:{x:100,y:200},count:4,confidence:.95}]};
}
test('situational context refuses omniscient replay data and stale own state',()=>{
  assert.throws(()=>visibleFarmingContext({...observation(),informationScope:'omniscient-offline-replay-only'} as any,10000));
  assert.throws(()=>visibleFarmingContext(observation(),16000));
});
test('visible-only allowlist removes hidden data instead of passing arbitrary objects to an LLM',()=>{
  const o:any=observation();o.worldSnapshots=[{enemySecret:'hidden'}];o.player.enemySecret='hidden';o.facts[0].enemySecret='hidden';
  o.facts.push({...o.facts[0],id:'wrong-match',matchId:'m2'});
  o.facts.push({...o.facts[0],id:'replay-leak',source:'replay'});
  o.facts.push({...o.facts[0],id:'old',capturedAt:100});
  const context=visibleFarmingContext(o,10000);
  assert.equal(context.visibleFacts.length,1);assert.ok(!JSON.stringify(context).includes('hidden'));
  assert.equal(context.objective,'lane-plus-neutral-gold-under-current-risk-constraints');
  assert.match(context.liveActivation,/disabled/);
});
test('empty observed camp is distinct from unknown camp; no default spawn or route is invented',()=>{
  const o=observation();o.facts[0]={...o.facts[0],kind:'camp-seen',entityId:'camp1',count:0};
  const c=visibleFarmingContext(o,10000);
  assert.equal(c.visibleFacts[0].count,0);assert.equal(c.visibleFacts.length,1);
  assert.ok(!('route' in c));assert.ok(c.unknowns.some(s=>s.includes('unseen camp')));
});
test('historical lessons are explicitly historical, not raw replay snapshots',()=>{
  const lesson:any={matchId:123,evidenceIds:['123:w10'],fact:'线野收入',interpretation:'可能是接线',coachQuestion:'该线现在可见且可达吗？',worldSnapshots:[{hidden:true}]};
  const c=visibleFarmingContext(observation(),10000,[lesson]);
  assert.equal(c.historicalLessons.length,1);assert.match(c.historicalLessons[0].scope,/historical/);
  assert.ok(!JSON.stringify(c).includes('worldSnapshots'));
});
test('reminders are revoked on context mismatch, missing basis, death, expiry and clock rewind',()=>{
  const o=observation(),r:FarmingReminder={contextId:'ctx',matchId:'m1',basedOnClock:600,expiresAtClock:612,message:'先确认这波兵线。',basisFactIds:['f1']};
  assert.equal(reminderStillApplicable(r,o,10000),true);
  for(const changed of [{...r,matchId:'m2'},{...r,contextId:'new'},{...r,basedOnClock:601},
    {...r,expiresAtClock:599},{...r,expiresAtClock:900},{...r,basisFactIds:['invented']}])
    assert.equal(reminderStillApplicable(changed,o,10000),false);
  o.player.alive=false;assert.equal(reminderStillApplicable(r,o,10000),false);
});
