import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { recordedTrace, gameTimeAt, farmingWindow } from '../src/replays/farming-routes.ts';
import type { FarmFrame } from '../src/replays/farming-routes.ts';

const corpus=JSON.parse(fs.readFileSync('data/luna-farming-routes.json','utf8'));
test('recorded trace keeps source positions and breaks jumps, deaths and missing data instead of inventing travel',()=>{
  const point=(time:number,x:number,lifeState=0)=>({gameTime:time,position:{x,y:40},id:'hero',health:lifeState?0:100,lifeState});
  const path=[point(0,0),point(.1,10),point(.2,4000),point(.3,4010),point(.4,4010,2),point(.5,5000)];
  const trace=recordedTrace(path);
  assert.deepEqual(trace.map(p=>[p[0],p[1],p[4]]),[[0,0,1],[.2,4000,1],[.4,4010,1],[.5,5000,1]]);
  for(const p of trace)assert.ok(path.some(h=>h.position.x===p[1]&&h.position.y===p[2]));
  const missing=recordedTrace([point(0,0),{gameTime:.1,position:null},point(.2,20)]);
  assert.equal(missing.at(-1)![4],1);
});
test('pause-aware replay event time follows recorded clock anchors',()=>{
  const clock={game_start_time_s:100,net_tick_offset:0,pauses:[{start_tick:3150,end_tick:3210}]};
  assert.equal(gameTimeAt(clock,3000),0);assert.equal(gameTimeAt(clock,3180),5);assert.equal(gameTimeAt(clock,3240),6);
  assert.equal(gameTimeAt({game_start_tick:3000,pauses:[]},3030),1);
});
test('farming income uses separate counters and actual elapsed sample times, excluding other income',()=>{
  const f=(time:number,gold:FarmFrame['gold'],lastHits:number)=>({time,gold,lastHits,stacks:0,level:10} as FarmFrame);
  const frames=[f(.2,[0,0,0,0,0,0],0),f(60.2,[100,200,450,40,20,90],8)];
  const s=farmingWindow(frames,0,60);
  assert.equal(s.laneGold,100);assert.equal(s.neutralGold,200);assert.equal(s.farmPerMinute,300);
  assert.equal(s.totalGold,450);assert.equal(s.lastHits,8);assert.equal(s.elapsed,60);
  assert.throws(()=>farmingWindow(frames,60,0));
  assert.throws(()=>farmingWindow([f(0,[100,0,0,0,0,0],0),f(60,[0,0,0,0,0,0],0)],0,60),/nonmonotonic/);
});
test('six newly acquired D2PT matches have complete replay/build/map/counter evidence and no guessed camp attribution',()=>{
  assert.equal(corpus.matches.length,6);assert.equal(corpus.patch,'7.41f');
  const source=corpus.generatedFrom;
  assert.equal(source.filters.prosOnly,true);assert.equal(source.filters.pubs,true);assert.equal(source.filters.tournaments,false);
  for(const build of source.patchEvidence.builds){
    assert.equal(createHash('sha256').update(build.text).digest('hex'),build.sha256);
    assert.ok(build.source.includes(build.commit));
  }
  let unresolved=0;
  for(const m of corpus.matches){
    assert.ok(m.complete&&m.matchMmr>=8000&&m.validation.lastHitParity);
    assert.equal(m.validation.camps,28);assert.equal(m.validation.towers,22);
    assert.ok([6974,6975].includes(m.gameBuild));assert.ok(m.startTime>=source.patchEvidence.from);
    assert.equal(m.source,`https://dota2protracker.com/matches/${m.matchId}`);
    assert.ok(m.trace.length>2000&&m.frames.length>1300);
    for(const window of m.windows)assert.deepEqual(farmingWindow(m.frames,window.from,window.to),window);
    for(const k of m.kills)if(k.kind==='neutral'&&!k.campId){unresolved++;assert.notEqual(k.attribution,'exact-tick-name-single-camp');}
  }
  assert.ok(unresolved>0,'ambiguous deaths are retained, not snapped to the nearest camp');
  assert.equal(corpus.matches.find((m:any)=>m.matchId===9089477184).windows[3].farmGold,8275);
});
