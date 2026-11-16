import assert from 'node:assert/strict';
import {test} from 'node:test';
import { studyEvidence } from '../src/replays/study-evidence.ts';
test('study extraction keeps uniform factual windows, stack caveats and explicit omniscient scope',()=>{
  const hero={playerIdField:0,team:2,name:'npc_dota_hero_luna',lifeState:0,position:{x:1,y:2},level:10,economy:{m_iNetWorth:5000}};
  const facts={heroPath:[hero],frames:[0,30,60].map(gameTime=>({gameTime,heroes:[hero],units:[]})),towers:[]};
  const frames=[0,30,60].map((time,i)=>({time,x:1+i,y:2,alive:i!==1,level:10,hp:500,mana:200,
    gold:[100*i,200*i,400*i,0,0,100*i],lastHits:7*i,stacks:i>0?1:0,loadout:0,campCounts:[]}));
  const match={matchId:1,player:'test',team:'radiant',won:false,duration:60,frames,
    loadouts:[],kills:[],casts:[],validation:{}};
  const result=studyEvidence(facts,match);
  assert.equal(result.informationScope,'omniscient-offline-replay-only');
  assert.equal(result.windows.length,2);assert.equal(result.windows[0].neutral,200);
  assert.equal(result.windows[0].lane,100);assert.equal(result.windows[0].stacks,1);
  const stack=result.transitions.find(t=>t.event==='own-stack-counter-increase');
  assert.ok(stack);assert.match(stack.note,/not proven/);assert.ok(!('campId' in stack));
  assert.deepEqual(result.transitions.filter(t=>['death','respawn'].includes(t.event)).map(t=>t.event),['death','respawn']);
  assert.ok(result.caveats.some(s=>s.includes('visibility')));
});
