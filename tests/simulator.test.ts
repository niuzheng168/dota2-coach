import assert from 'node:assert/strict';
import { test } from 'node:test';
import { once } from 'node:events';
import { SimulationWorld, parseCommand } from '../src/simulation/world.ts';
import type { SimCommand, SimUnit } from '../src/simulation/world.ts';
import { SimulationTimeline } from '../src/simulation/timeline.ts';
import { DEFINITIONS, FRAME_SECONDS, MAX_TICKS, nearestWalkable, isWalkable } from '../src/simulation/data.ts';
import { heroAttributes } from '../src/simulation/stats.ts';
import { createSandboxServer } from '../src/sandbox/server.ts';

const empty={items:[],treads:'agi' as const};
const loadout={items:['item_power_treads','item_mask_of_madness','item_greater_crit','item_platemail','item_cloak'],treads:'agi' as const};
const heroCommand=(position={x:-2200,y:-3356},equipment=loadout):SimCommand=>({
  type:'add_hero',hero:'npc_dota_hero_luna',team:'radiant',level:10,position:nearestWalkable(position)!,loadout:equipment,
});
const run=(w:SimulationWorld,ticks:number)=>{for(let i=0;i<ticks;i++)w.advanceOne();};

test('200ms frames: no hero, six actual lanes independently move, attack each other, die and trigger tower projectiles',()=>{
  const w=new SimulationWorld(),initial=w.snapshot();
  assert.equal(FRAME_SECONDS,.2);assert.equal(initial.hero,null);
  assert.equal(initial.units.filter(u=>u.kind==='tower').length,22);
  assert.equal(initial.units.filter(u=>u.kind==='lane').length,24);
  const a=w.state.units.find(u=>u.kind==='lane')!,position={x:a.x,y:a.y};
  run(w,5);assert.equal(w.time,1);assert.ok(Math.hypot(a.x-position.x,a.y-position.y)>100);
  let towerDamage=0,laneDamage=0,towerProjectile=false;const damageEvents=new Set<string>();
  for(let i=5;i<750;i++){
    w.advanceOne();
    if(w.state.projectiles.some(p=>p.sourceKind==='tower'))towerProjectile=true;
    for(const e of w.state.events){
      const key=JSON.stringify(e);if(damageEvents.has(key))continue;damageEvents.add(key);
      if(e.type==='damage'&&e.sourceKind==='tower')towerDamage+=e.amount!;
      if(e.type==='damage'&&e.sourceKind==='lane'&&e.targetKind==='lane')laneDamage+=e.amount!;
    }
  }
  const s=w.snapshot();
  assert.equal(s.hero,null);assert.equal(s.time,150);assert.ok(s.totals.laneAttacks>0);
  assert.ok(s.totals.towerAttacks>0&&towerDamage>0&&towerProjectile);
  assert.ok(laneDamage>0&&s.totals.kills>0);assert.equal(s.economy.totalGold,0,'creep/tower kills never pay a nonexistent hero');
});
test('hero stats derive from level and equipment; active mask changes movement/armor/attack speed and cooldown',()=>{
  const bare=heroAttributes('npc_dota_hero_luna',10,empty),gear=heroAttributes('npc_dota_hero_luna',10,loadout);
  assert.equal(bare.moveSpeed,325);assert.equal(gear.moveSpeed,370);assert.ok(gear.damageMin>bare.damageMin);
  assert.ok(gear.armor>bare.armor&&gear.magicResistance>bare.magicResistance&&gear.attackSpeed>bare.attackSpeed);
  assert.ok(Math.abs(gear.critChance-.3)<1e-8);assert.equal(gear.critMultiplier,2.25);
  const w=new SimulationWorld();w.execute(heroCommand());const mana=w.hero!.mana;
  w.execute({type:'cast',name:'mask'});
  assert.ok(Math.abs(w.hero!.moveSpeed-392.2)<1e-8);assert.equal(w.hero!.mana,mana-25);
  assert.ok(w.hero!.armor<gear.armor&&w.hero!.attackSpeed>gear.attackSpeed);
  assert.throws(()=>w.execute({type:'cast',name:'mask'}),/冷却/);
  run(w,30);assert.equal(w.hero!.moveSpeed,370,'active expires on exact 6-second boundary');
  assert.equal(w.hero!.armor,gear.armor);
});
test('equipment slots and treads work, rejected inventory mutations are atomic; dead heroes cannot heal via equipment',()=>{
  const t=new SimulationTimeline();t.command(heroCommand());let before=t.snapshot();
  for(const items of [['item_unknown'],Array(7).fill('item_cloak'),['item_cloak','item_cloak']]){
    assert.throws(()=>t.command({type:'equip',loadout:{items,treads:'agi'}}));
    assert.deepEqual(t.snapshot(),before);
  }
  t.command({type:'equip',loadout:{items:['item_power_treads','item_ogre_axe'],treads:'str'}});
  assert.ok(t.world.hero!.maxHp>before.hero!.maxHp);
  const h=t.world.hero!;h.hp=0;
  assert.throws(()=>t.command({type:'equip',loadout:empty}),/死亡/);assert.equal(h.hp,0);
});
test('hero motion respects the verified grid, changes facing, and stays within per-frame movement budget',()=>{
  const w=new SimulationWorld();w.execute(heroCommand(undefined,empty));
  const destination=nearestWalkable({x:-1983,y:-4815})!;
  w.execute({type:'order',order:{type:'move',position:destination}});
  const startFacing=w.hero!.facing;
  for(let i=0;i<70;i++){
    const h=w.hero!,before={x:h.x,y:h.y},speed=h.moveSpeed;w.advanceOne();
    assert.ok(Math.hypot(h.x-before.x,h.y-before.y)<=speed*.2+.001);
    assert.ok(isWalkable(h));assert.ok(Number.isFinite(h.facing));
  }
  assert.notEqual(w.hero!.facing,startFacing);
  assert.ok(Math.hypot(w.hero!.x-destination.x,w.hero!.y-destination.y)<1);
});
test('hero attacks an actual spawned neutral camp and receives each killed unit bounty once',()=>{
  const w=new SimulationWorld();run(w,300);
  const target=w.state.units.find(u=>u.campId==='neutralcamp_good_5')!;
  w.execute(heroCommand({x:target.x-320,y:target.y}));
  w.execute({type:'order',order:{type:'attack',targetId:target.id}});w.execute({type:'cast',name:'mask'});
  const before=w.hero!.facing;run(w,100);
  assert.ok(w.snapshot().economy.neutralGold>0);assert.equal(w.snapshot().economy.laneGold,0);
  assert.equal(new Set(w.state.rewards.map(r=>r.targetId)).size,w.state.rewards.length);
  assert.ok(w.state.rewards.every(r=>r.sourceId===w.hero!.id&&r.kind==='neutral'));
  assert.notEqual(w.hero!.facing,before);assert.ok(w.state.totals.heroAttacks>0);
  const gold=w.snapshot().economy.totalGold;run(w,10);assert.equal(w.snapshot().economy.totalGold,gold);
});
test('hero can attack and last-hit lane creeps with ranged projectiles',()=>{
  const w=new SimulationWorld();run(w,175);
  const target=w.state.units.find(u=>u.kind==='lane'&&u.team==='dire'&&u.hp>0)!;
  w.execute(heroCommand({x:target.x-200,y:target.y+100}));
  w.execute({type:'order',order:{type:'attack',targetId:target.id}});
  let projectile=false;
  for(let i=0;i<100;i++){w.advanceOne();if(w.state.projectiles.some(p=>p.sourceKind==='hero'))projectile=true;}
  assert.ok(projectile);assert.ok(w.snapshot().economy.laneGold>0);
  assert.ok(w.state.rewards.some(r=>r.targetId===target.id));
});
test('hero can damage and destroy a real enemy tower; tower retaliates; no creep gold awarded for a building',()=>{
  const w=new SimulationWorld();
  const tower=w.state.units.find(u=>u.kind==='tower'&&u.name==='npc_dota_badguys_tower1_bot')!;
  w.execute(heroCommand({x:tower.x-360,y:tower.y}));
  w.execute({type:'order',order:{type:'attack',targetId:tower.id}});w.execute({type:'cast',name:'mask'});
  const hp=w.hero!.hp;
  for(let i=0;i<200&&tower.hp>0;i++)w.advanceOne();
  assert.equal(tower.hp,0);assert.ok(w.state.totals.towerAttacks>0);assert.ok(w.hero!.hp<hp);
  assert.equal(w.state.rewards.filter(r=>r.targetId===tower.id).length,0);
  assert.ok(w.hero!.hp>0);
});
test('magic resistance reduces spell damage, beam spends resources and prevents allied/building casts',()=>{
  const w=new SimulationWorld();run(w,300);const target=w.state.units.find(u=>u.campId==='neutralcamp_good_5')!;
  w.execute(heroCommand({x:target.x-400,y:target.y}));
  // Unit test isolates the damage channel; integration above uses untouched spawned stats.
  target.hp=5000;target.maxHp=5000;target.magicResistance=.5;
  const hp=target.hp,mana=w.hero!.mana;
  w.execute({type:'cast',name:'beam',targetId:target.id});
  const applied=hp-target.hp;assert.equal(applied,150);assert.equal(w.hero!.mana,mana-120);
  assert.throws(()=>w.execute({type:'cast',name:'beam',targetId:target.id}),/冷却/);
  assert.throws(()=>w.execute({type:'cast',name:'beam',targetId:w.state.units.find(u=>u.kind==='tower')!.id}));
});
test('critical chance is actually consumed by combat, seeded and never applied to towers',()=>{
  function trial(){const w=new SimulationWorld(8);run(w,300);const target=w.state.units.find(u=>u.campId==='neutralcamp_good_5')!;
    w.execute(heroCommand({x:target.x-200,y:target.y}));target.hp=100000;target.maxHp=100000;
    target.damageMin=target.damageMax=0;
    w.execute({type:'order',order:{type:'attack',targetId:target.id}});
    const attacks=new Map<string,boolean>();
    for(let i=0;i<150;i++){w.advanceOne();for(const e of w.state.events)if(e.type==='attack'&&e.sourceKind==='hero')attacks.set(`${e.tick}:${e.targetId}`,!!e.critical);}
    return [...attacks.values()];}
  const result=trial();assert.ok(result.includes(true)&&result.includes(false));assert.deepEqual(result,trial());
});
test('precise timeline restores all unit/projectile/RNG/HP/gold state, forward-seeking and command branching are deterministic',()=>{
  const t=new SimulationTimeline();t.seek(300);
  const target=t.world.state.units.find(u=>u.campId==='neutralcamp_good_5')!;
  t.command(heroCommand({x:target.x-320,y:target.y}));t.command({type:'order',order:{type:'attack',targetId:target.id}});
  t.command({type:'cast',name:'mask'});t.seek(400);
  const end=structuredClone(t.world.state);t.seek(310);t.seek(400);assert.deepEqual(t.world.state,end);
  t.seek(0);assert.equal(t.snapshot().hero,null);assert.equal(t.snapshot().economy.totalGold,0);
  t.seek(400);assert.deepEqual(t.world.state,end);
  t.seek(299);t.command(heroCommand());assert.equal(t.branch,1);assert.equal(t.maxVisitedTick,299);
  t.seek(400);assert.equal(t.snapshot().economy.totalGold,0,'discarded attack/cast commands do not run on new branch');
  assert.equal(t.snapshot().hero!.loadout!.items.length,5);
  assert.throws(()=>t.seek(.5));assert.throws(()=>t.seek(MAX_TICKS+1));
});
test('registered non-Luna hero stats can execute the same core without changing AI or inventory code',()=>{
  const key='test_hero_registry';DEFINITIONS.heroes[key]={...DEFINITIONS.heroes.npc_dota_hero_luna,label:'Extension fixture'};
  try{
    const w=new SimulationWorld();w.execute({...heroCommand(),hero:key});
    assert.equal(w.hero!.name,key);assert.ok(w.hero!.attributes);w.execute({type:'equip',loadout:{items:['item_cloak'],treads:'agi'}});
    w.advanceOne();assert.equal(w.time,.2);
  }finally{delete DEFINITIONS.heroes[key];}
});
test('simulator HTTP is authenticated, isolated from audit, rejects stale state and exposes the requested interactive page',async t=>{
  const {server,token}=createSandboxServer();server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(()=>new Promise<void>(resolve=>server.close(()=>resolve())));
  const a=server.address();assert.ok(a&&typeof a==='object');const origin=`http://127.0.0.1:${a.port}`;
  const call=async(path:string,body?:any,expected=200)=>{
    const r=await fetch(origin+path,{method:body?'POST':'GET',headers:{Authorization:`Bearer ${token}`,'Content-Type':'application/json'},body:body?JSON.stringify(body):undefined});
    assert.equal(r.status,expected);return await r.json() as any;
  };
  assert.equal((await fetch(origin+'/api/sim/state')).status,401);
  const initial=await call('/api/sim/state');assert.equal(initial.hero,null);
  const at120=await call('/api/sim/seek',{tick:600,revision:initial.timeline.revision});
  assert.ok(at120.totals.towerAttacks>0);
  await call('/api/sim/command',{command:heroCommand(),revision:initial.timeline.revision},409);
  assert.deepEqual(await call('/api/sim/state'),at120);
  const added=await call('/api/sim/command',{command:heroCommand(),revision:at120.timeline.revision});
  assert.ok(added.hero);assert.equal((await call('/api/state')).plan,null);
  assert.match(await (await fetch(origin+'/')).text(),/地图战斗模拟台/);
  assert.throws(()=>parseCommand({type:'add_hero',hero:'npc_dota_hero_luna',level:10,team:'radiant',position:{x:Infinity,y:0},loadout:empty}));
});
