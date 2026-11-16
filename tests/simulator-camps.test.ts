import assert from 'node:assert/strict';
import { test } from 'node:test';
import { campStatuses } from '../src/simulation/camps.ts';
import { DEFINITIONS, FRAME_SECONDS, NEUTRAL_SPAWN_TICKS, REFERENCE, nearestWalkable } from '../src/simulation/data.ts';
import { SimulationWorld } from '../src/simulation/world.ts';
import { SimulationTimeline } from '../src/simulation/timeline.ts';
import { COMPOSITIONS } from '../src/tactics/combat-data.ts';
import { insideCamp } from '../src/tactics/map-data.ts';
import type { Camp } from '../src/tactics/types.ts';

const camp=(REFERENCE.camps as Camp[]).find(c=>c.id==='neutralcamp_good_5')!;
const status=(w:SimulationWorld)=>w.snapshot().camps.find(c=>c.id===camp.id)!;
const advance=(w:SimulationWorld,ticks:number)=>{for(let i=0;i<ticks;i++)w.advanceOne();};

test('all 28 camps expose empty state and strictly next refresh at exact integer-frame boundaries',()=>{
  const w=new SimulationWorld();
  const initial=w.snapshot().camps;
  assert.equal(initial.length,28);assert.equal(new Set(initial.map(c=>c.id)).size,28);
  assert.ok(initial.every(c=>!c.hasNeutrals&&c.aliveCount===0&&c.currentGold===0&&c.nextSpawnTime===60&&c.secondsUntilSpawn===60));
  advance(w,NEUTRAL_SPAWN_TICKS-1);
  assert.equal(status(w).secondsUntilSpawn,FRAME_SECONDS);assert.equal(status(w).nextSpawnTick,300);
  w.advanceOne();
  assert.ok(status(w).hasNeutrals);assert.equal(status(w).nextSpawnTick,600);assert.equal(status(w).secondsUntilSpawn,60);
  for(const [tick,next]of [[600,900],[3000,3300]]){
    const s=campStatuses(tick,[])[0];assert.equal(s.nextSpawnTick,next);assert.equal(s.secondsUntilSpawn,60);
  }
});

test('future bounty uses the simulator pack distribution and unit bounds; reading estimates never consumes RNG',()=>{
  const w=new SimulationWorld(42),before=structuredClone(w.state);
  const states=w.snapshot().camps;
  for(const c of states){
    const spec=(REFERENCE.camps as Camp[]).find(x=>x.id===c.id)!;
    const packs=COMPOSITIONS.filter(p=>p.kind===spec.kind);
    const sums=packs.map(p=>Object.entries(p.units).reduce((sum,[name,count])=>{
      sum[0]+=DEFINITIONS.units[name].goldMin*count;sum[1]+=DEFINITIONS.units[name].goldMax*count;return sum;
    },[0,0]));
    assert.equal(c.nextSpawnGold.min,Math.min(...sums.map(s=>s[0])));
    assert.equal(c.nextSpawnGold.max,Math.max(...sums.map(s=>s[1])));
    assert.equal(c.nextSpawnGold.mean,sums.reduce((n,s)=>n+(s[0]+s[1])/2,0)/sums.length);
    assert.equal(c.nextSpawnGold.basis,'simulator-uniform-packs');
  }
  states[0].nextSpawnGold.mean=-999;
  assert.ok(w.snapshot().camps[0].nextSpawnGold.mean>0,'snapshot cannot mutate cached estimates');
  assert.deepEqual(w.state,before,'display is a pure read');
});

test('live camp bounty excludes corpses, includes pulled units, and does not scale down with HP',()=>{
  const w=new SimulationWorld();advance(w,300);
  const pack=w.state.units.filter(u=>u.campId===camp.id),gold=pack.reduce((sum,u)=>sum+u.gold,0);
  assert.equal(status(w).currentGold,gold);assert.equal(status(w).aliveCount,pack.length);
  pack[0].hp=1;assert.equal(status(w).currentGold,gold);
  pack[0].hp=0;pack[0].deadAt=w.tick;
  const live=pack.slice(1),remaining=gold-pack[0].gold;
  // Controlled fixture: remove all other units and pull remaining creeps out
  // of the box. Camp ownership and spawn blocking must be independent.
  w.state.units=pack;
  for(const u of live){u.x=camp.bounds.max.x+100;u.y=camp.y;}
  let c=status(w);
  assert.equal(c.aliveCount,live.length);assert.equal(c.currentGold,remaining);
  assert.equal(c.outsideCount,live.length);assert.equal(c.hasNeutrals,true);assert.equal(c.spawnBlocked,false);
  for(const u of live)u.hp=0;
  c=status(w);assert.equal(c.hasNeutrals,false);assert.equal(c.currentGold,0);assert.equal(c.spawnBlocked,false);
  assert.equal(w.snapshot().economy.totalGold,0,'potential bounty never appears as earned gold');
});

test('empty but occupied camps skip actual spawning; leaving the box does not trigger an immediate late spawn',()=>{
  const w=new SimulationWorld();advance(w,299);
  const position=nearestWalkable(camp)!;assert.ok(insideCamp(position,camp));
  w.execute({type:'add_hero',hero:'npc_dota_hero_luna',level:10,team:'radiant',position,loadout:{items:[],treads:'agi'}});
  assert.equal(status(w).hasNeutrals,false);assert.equal(status(w).spawnBlocked,true);
  assert.deepEqual(status(w).blockerIds,[w.hero!.id]);
  w.advanceOne();
  assert.equal(status(w).hasNeutrals,false);assert.equal(status(w).secondsUntilSpawn,60);
  assert.ok(w.state.events.some(e=>e.type==='camp_blocked'&&e.detail===camp.id));
  w.execute({type:'remove_hero'});
  assert.equal(status(w).spawnBlocked,false);assert.equal(status(w).nextSpawnTime,120);
  advance(w,299);assert.equal(status(w).hasNeutrals,false);assert.equal(status(w).secondsUntilSpawn,.2);
  w.advanceOne();assert.equal(status(w).hasNeutrals,true);assert.equal(status(w).nextSpawnTime,180);
});

test('all living units on inclusive spawn bounds block, even a lane creep or a neutral belonging elsewhere',()=>{
  for(const kind of ['lane','neutral','hero','tower'] as const){
    const u={id:'blocker',kind,hp:1,x:camp.bounds.min.x,y:camp.bounds.max.y,gold:999,campId:'other-camp'};
    const read=()=>campStatuses(299,[u]).find(c=>c.id===camp.id)!;
    assert.equal(read().spawnBlocked,true);assert.equal(read().hasNeutrals,false);assert.equal(read().currentGold,0);
    u.x-=.1;assert.equal(read().spawnBlocked,false);
    u.x+=.1;u.hp=0;assert.equal(read().spawnBlocked,false);
  }
});

test('pulled living neutrals may coexist with another spawned pack without losing their bounty',()=>{
  const w=new SimulationWorld();advance(w,300);
  const pack=w.state.units.filter(u=>u.campId===camp.id);
  w.state.units=pack;
  for(const u of pack){u.x=camp.bounds.max.x+200;u.y=camp.y;u.moveSpeed=0;}
  assert.equal(status(w).hasNeutrals,true);assert.equal(status(w).spawnBlocked,false);
  advance(w,300);
  assert.ok(status(w).aliveCount>pack.length);assert.equal(status(w).outsideCount,pack.length);
  assert.equal(status(w).spawnBlocked,true);
  assert.equal(status(w).currentGold,w.state.units.filter(u=>u.campId===camp.id&&u.hp>0).reduce((sum,u)=>sum+u.gold,0));
});

test('camp states and bounty exactly rewind after real hero kills; forward replay does not duplicate income',()=>{
  const t=new SimulationTimeline();t.seek(300);
  const start=t.snapshot().camps;
  const victim=t.world.state.units.find(u=>u.campId===camp.id)!;
  t.command({type:'add_hero',hero:'npc_dota_hero_luna',level:10,team:'radiant',
    position:nearestWalkable({x:victim.x-320,y:victim.y})!,
    loadout:{items:['item_power_treads','item_mask_of_madness','item_greater_crit'],treads:'agi'}});
  t.command({type:'order',order:{type:'attack',targetId:victim.id}});
  t.command({type:'cast',name:'mask'});
  t.seek(350);
  const cleared=t.snapshot();assert.ok(cleared.economy.neutralGold>0);
  assert.equal(status(t.world).hasNeutrals,false);
  assert.equal(status(t.world).currentGold,0);assert.equal(status(t.world).nextSpawnTime,120);
  t.seek(299);assert.ok(t.snapshot().camps.every(c=>!c.hasNeutrals));
  t.seek(300);assert.equal(status(t.world).currentGold,start.find(c=>c.id===camp.id)!.currentGold);
  t.seek(350);assert.deepEqual(t.snapshot().camps,cleared.camps);assert.deepEqual(t.snapshot().economy,cleared.economy);
});
