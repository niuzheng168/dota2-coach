import { COMBAT, COMPOSITIONS } from '../tactics/combat-data.ts';
import { physicalMultiplier, levelValue } from '../tactics/rules.ts';
import { distance } from '../tactics/map-data.ts';
import type { Point } from '../tactics/types.ts';
import { CLIENT_MAP, DEFINITIONS, FRAME_SECONDS as DT, ITEM_LABELS, MAX_TICKS, NAV, NEUTRAL_SPAWN_TICKS, REFERENCE, isWalkable, itemValue, nearestWalkable } from './data.ts';
import type { UnitSpec } from './data.ts';
import { heroAttributes, validateLoadout } from './stats.ts';
import { burstAbility } from '../tactics/skills.ts';
import type { HeroAttributes, Loadout } from './stats.ts';
import { campBlockers, campStatuses } from './camps.ts';

export type SimTeam='radiant'|'dire'|'neutral';
export interface SimUnit extends Point {
  id:string;kind:'hero'|'lane'|'neutral'|'tower';name:string;team:SimTeam;hp:number;maxHp:number;mana:number;maxMana:number;
  armor:number;magicResistance:number;damageMin:number;damageMax:number;attackSpeed:number;attackInterval:number;
  attackPoint:number;attackRange:number;acquisitionRange:number;projectileSpeed:number;moveSpeed:number;healthRegen:number;manaRegen:number;
  facing:number;radius:number;gold:number;targetId:string|null;nextAttack:number;windup:{release:number;targetId:string}|null;
  laneRoute?:string;routeIndex:number;campId?:string;home?:Point;aggroUntil:number;deadAt:number|null;
  nav:Point[];navIndex:number;navDestination:Point|null;
  level?:number;loadout?:Loadout;attributes?:HeroAttributes;maskUntil?:number;maskReady?:number;beamReady?:number;
}
export type HeroOrder={type:'stop'}|{type:'move';position:Point}|{type:'attack';targetId:string};
export type SimCommand=
  {type:'add_hero';hero:string;level:number;team:'radiant'|'dire';position:Point;loadout:Loadout}|
  {type:'remove_hero'}|{type:'order';order:HeroOrder}|{type:'equip';loadout:Loadout}|
  {type:'cast';name:'mask'|'beam';targetId?:string};
export interface SimEvent {
  tick:number;type:string;sourceId?:string;targetId?:string;amount?:number;critical?:boolean;sourceKind?:SimUnit['kind'];targetKind?:SimUnit['kind'];detail?:string;
}
export interface Reward {tick:number;targetId:string;sourceId:string;kind:'neutral'|'lane';gold:number}
export interface Projectile extends Point {
  id:string;sourceId:string;sourceKind:SimUnit['kind'];sourceTeam:SimTeam;targetId:string;
  damage:number;speed:number;damageType:'physical'|'magical';critical:boolean;glaiveBounces:number;glaiveRetention:number;lifesteal:number;
}
export interface WorldState {
  tick:number;seed:number;rng:number;sequence:number;units:SimUnit[];projectiles:Projectile[];
  heroId:string|null;order:HeroOrder;events:SimEvent[];rewards:Reward[];
  totals:{laneSpawns:number;neutralSpawns:number;laneAttacks:number;towerAttacks:number;heroAttacks:number;kills:number};
}
let preparedRoutes:Record<string,Point[]>|null=null;
export function laneRoutes():Record<string,Point[]> {
  if(preparedRoutes)return preparedRoutes;
  const routes:Record<string,Point[]>={};
  for(const lane of REFERENCE.lanePaths){
    const route:Point[]=[];let from=nearestWalkable({x:lane.spawn[0],y:lane.spawn[1]});
    if(!from)throw new Error('Verified lane spawn has no nearby walkable cell');
    route.push(from);
    for(const [x,y] of lane.points){
      const to=nearestWalkable({x,y});
      const section=to&&NAV.path(from,to);if(!section)throw new Error(`Lane path unavailable: ${lane.id}`);
      route.push(...section.points.slice(1));from=to!;
    }
    routes[lane.id]=route;
  }
  preparedRoutes=routes;return routes;
}
const finitePoint=(p:any):p is Point=>p&&Number.isFinite(p.x)&&Number.isFinite(p.y)&&Math.abs(p.x)<=10500&&Math.abs(p.y)<=11000;
export function parseCommand(value:unknown):SimCommand {
  const c=value as any;
  if(!c||typeof c!=='object')throw new Error('需要模拟行动');
  if(c.type==='remove_hero')return {type:c.type};
  if(c.type==='add_hero'){
    if(!Object.hasOwn(DEFINITIONS.heroes,c.hero)||!Number.isInteger(c.level)||c.level<1||c.level>30||
      !['radiant','dire'].includes(c.team)||!finitePoint(c.position)||!isWalkable(c.position))throw new Error('英雄、等级、阵营或放置位置无效（请选可行走位置）');
    return {type:c.type,hero:c.hero,level:c.level,team:c.team,position:{...c.position},loadout:validateLoadout(c.loadout)};
  }
  if(c.type==='equip')return {type:c.type,loadout:validateLoadout(c.loadout)};
  if(c.type==='order'){
    if(c.order?.type==='stop')return {type:c.type,order:{type:'stop'}};
    if(c.order?.type==='move'&&finitePoint(c.order.position)&&isWalkable(c.order.position))
      return {type:c.type,order:{type:'move',position:{...c.order.position}}};
    if(c.order?.type==='attack'&&typeof c.order.targetId==='string'&&c.order.targetId.length<100)
      return {type:c.type,order:{type:'attack',targetId:c.order.targetId}};
  }
  if(c.type==='cast'&&['mask','beam'].includes(c.name)&&(c.targetId===undefined||typeof c.targetId==='string'))
    return {type:c.type,name:c.name,targetId:c.targetId};
  throw new Error('无效行动；装备/位置不合法时不会替换计划');
}
export class SimulationWorld {
  state:WorldState;
  constructor(seed=741){
    if(!Number.isInteger(seed)||seed<0||seed>0xffffffff)throw new Error('seed 无效');
    laneRoutes();
    this.state={tick:0,seed,rng:seed,sequence:0,units:[],projectiles:[],heroId:null,order:{type:'stop'},events:[],rewards:[],
      totals:{laneSpawns:0,neutralSpawns:0,laneAttacks:0,towerAttacks:0,heroAttacks:0,kills:0}};
    for(const t of CLIENT_MAP.towers){
      const u=this.createUnit('tower',t.unitName,t.team===2?'radiant':'dire',{x:t.position[0],y:t.position[1]});
      u.id=`tower:${t.referenceId}`;u.radius=t.hull;this.state.units.push(u);
    }
    this.spawnWaves();
  }
  get tick(){return this.state.tick}
  get time(){return this.tick*DT}
  get hero(){return this.state.units.find(u=>u.id===this.state.heroId)}
  private id(prefix:string){return `${prefix}:${++this.state.sequence}`}
  private random(){this.state.rng=(Math.imul(1664525,this.state.rng)+1013904223)>>>0;return this.state.rng/0x100000000}
  private event(e:Omit<SimEvent,'tick'>){this.state.events.push({tick:this.tick,...e});if(this.state.events.length>80)this.state.events.shift()}
  private createUnit(kind:SimUnit['kind'],name:string,team:SimTeam,p:Point):SimUnit {
    const s=kind==='hero'?DEFINITIONS.heroes[name]:DEFINITIONS.units[name];
    if(!s)throw new Error(`缺少单位定义 ${name}`);
    return {id:this.id(kind),kind,name,team,x:p.x,y:p.y,hp:s.health,maxHp:s.health,mana:s.mana,maxMana:s.mana,
      armor:s.armor,magicResistance:s.magicResistance,damageMin:s.damageMin,damageMax:s.damageMax,
      attackSpeed:s.attackSpeed,attackInterval:s.attackTime*100/s.attackSpeed,attackPoint:s.attackPoint*100/s.attackSpeed,
      attackRange:s.attackRange,acquisitionRange:s.acquisitionRange,projectileSpeed:s.projectileSpeed,moveSpeed:s.moveSpeed,
      healthRegen:s.healthRegen,manaRegen:s.manaRegen,radius:kind==='tower'?144:kind==='hero'?24:16,
      gold:Math.floor(s.goldMin+this.random()*(s.goldMax-s.goldMin+1)),facing:team==='dire'?-Math.PI/2:Math.PI/2,
      targetId:null,nextAttack:0,windup:null,routeIndex:0,aggroUntil:0,deadAt:null,nav:[],navIndex:0,navDestination:null};
  }
  private applyAttributes(h:SimUnit,preserve=true) {
    const hpFraction=preserve?h.hp/h.maxHp:1,mpFraction=preserve&&h.maxMana?h.mana/h.maxMana:1;
    const a=heroAttributes(h.name,h.level!,h.loadout!,this.time<(h.maskUntil||0));
    h.attributes=a;h.maxHp=a.maxHealth;h.maxMana=a.maxMana;
    h.hp=Math.min(h.maxHp,Math.max(0,hpFraction*h.maxHp));h.mana=Math.min(h.maxMana,Math.max(0,mpFraction*h.maxMana));
    for(const k of ['armor','magicResistance','damageMin','damageMax','attackSpeed','attackInterval','attackPoint','attackRange','projectileSpeed','moveSpeed','healthRegen','manaRegen'] as const)h[k]=a[k];
  }
  execute(value:unknown):void {
    const c=parseCommand(value),s=this.state;
    if(c.type==='add_hero'){
      if(this.hero)throw new Error('场景已有英雄；先移除或重置');
      if(s.units.some(u=>u.kind==='tower'&&u.hp>0&&distance(u,c.position)<u.radius+24))throw new Error('不能放置在防御塔碰撞体内');
      const h=this.createUnit('hero',c.hero,c.team,c.position);
      h.level=c.level;h.loadout=c.loadout;h.maskUntil=0;h.maskReady=0;h.beamReady=0;
      this.applyAttributes(h,false);s.heroId=h.id;s.order={type:'stop'};s.units.push(h);this.event({type:'hero_added',sourceId:h.id});return;
    }
    const h=this.hero;
    if(!h)throw new Error('请先加入英雄');
    if(c.type==='remove_hero'){
      s.units=s.units.filter(u=>u.id!==h.id);s.heroId=null;s.order={type:'stop'};
      this.event({type:'hero_removed',sourceId:h.id});return;
    }
    if(h.hp<=0)throw new Error('英雄已死亡，请回退、移除或重置场景');
    if(c.type==='equip'){
      h.loadout=c.loadout;if(!c.loadout.items.includes('item_mask_of_madness'))h.maskUntil=0;
      this.applyAttributes(h);this.event({type:'equipment',sourceId:h.id,detail:c.loadout.items.map(i=>ITEM_LABELS[i]).join(' / ')});return;
    }
    if(c.type==='order'){
      const order=c.order;
      if(order.type==='attack'&&!s.units.some(u=>u.id===order.targetId&&u.hp>0&&u.team!==h.team))throw new Error('攻击目标无效、已死亡或为友军');
      let path:Point[]=[];
      if(c.order.type==='move'){
        const p=NAV.path(h,c.order.position);if(!p)throw new Error('目标不可达');path=p.points.slice(1);
      }
      s.order=c.order;h.targetId=c.order.type==='attack'?c.order.targetId:null;h.windup=null;
      h.nav=path;h.navIndex=0;h.navDestination=c.order.type==='move'?c.order.position:null;
      this.event({type:'order',sourceId:h.id,detail:c.order.type});return;
    }
    if(c.name==='mask'){
      const cost=itemValue('item_mask_of_madness','AbilityManaCost');
      if(!h.loadout!.items.includes('item_mask_of_madness')||(h.maskReady||0)>this.time||h.mana<cost)throw new Error('疯脸未装备、冷却中或魔法不足');
      h.mana-=cost;h.maskUntil=this.time+itemValue('item_mask_of_madness','berserk_duration');h.maskReady=this.time+itemValue('item_mask_of_madness','AbilityCooldown');
      this.applyAttributes(h);this.event({type:'mask',sourceId:h.id});return;
    }
    const target=s.units.find(u=>u.id===c.targetId&&u.hp>0&&u.team!==h.team);
    if(!target||target.kind==='tower'||distance(h,target)>800+target.radius)throw new Error('月光只能对范围内敌方非建筑单位释放');
    if((h.maskUntil||0)>this.time)throw new Error('疯脸沉默期间不能释放月光');
    const level=Math.min(4,Math.ceil(h.level!/3)),a=COMBAT.heroes[h.name]?.abilities.luna_lucent_beam;
    if(!a)throw new Error('该英雄没有已适配技能');
    const spell=burstAbility({name:'luna_lucent_beam',level,cooldown:0},a);
    if(!spell)throw new Error('月光数值未适配');
    const cost=spell.mana;
    if(h.mana<cost||(h.beamReady||0)>this.time)throw new Error('月光冷却中或魔法不足');
    h.mana-=cost;h.beamReady=this.time+spell.cooldown;h.facing=Math.atan2(target.y-h.y,target.x-h.x);
    this.damage(target,spell.damage,h.id,h.kind,h.team,'magical',false);
    this.event({type:'beam',sourceId:h.id,targetId:target.id});
  }
  private spawnWaves(){
    for(const lane of REFERENCE.lanePaths){
      const route=laneRoutes()[lane.id],team=lane.team as 'radiant'|'dire';
      for(let i=0;i<4;i++){
        const name=`npc_dota_creep_${team==='radiant'?'goodguys':'badguys'}_${i===3?'ranged':'melee'}`;
        // Stagger along the verified navigation path, not an invented lane.
        const u=this.createUnit('lane',name,team,route[0]);u.laneRoute=lane.id;u.routeIndex=0;
        u.nextAttack=this.time+i*.2;safelyWalk(u,route,i*18);this.state.units.push(u);this.state.totals.laneSpawns++;
      }
    }
    this.event({type:'wave_spawn',amount:24});
  }
  private spawnNeutrals(){
    for(const camp of REFERENCE.camps){
      if(campBlockers(camp,this.state.units).length){this.event({type:'camp_blocked',detail:camp.id});continue;}
      const packs=COMPOSITIONS.filter(p=>p.kind===camp.kind),pack=packs[Math.floor(this.random()*packs.length)];let i=0;
      for(const [name,count] of Object.entries(pack.units))for(let n=0;n<count;n++){
        const pos=nearestWalkable({x:camp.x+Math.cos(i*2.399)*70,y:camp.y+Math.sin(i++*2.399)*70})||{x:camp.x,y:camp.y};
        const u=this.createUnit('neutral',name,'neutral',pos);u.campId=camp.id;u.home={x:camp.x,y:camp.y};
        this.state.units.push(u);this.state.totals.neutralSpawns++;
      }
    }
    this.event({type:'neutral_spawn'});
  }
  private alive(id:string|null|undefined){return id?this.state.units.find(u=>u.id===id&&u.hp>0):undefined}
  private enemy(a:SimUnit,b:SimUnit){return a.id!==b.id&&b.hp>0&&a.team!==b.team&&(a.team!=='neutral'||b.team!=='neutral')}
  private inRange(a:SimUnit,b:SimUnit){return distance(a,b)<=a.attackRange+b.radius+1e-6}
  private target(u:SimUnit):SimUnit|undefined {
    if(u.kind==='hero'){
      const order=this.state.order;return order.type==='attack'?this.alive(order.targetId):undefined;
    }
    const current=this.alive(u.targetId);
    if(u.kind==='neutral'){
      if(current&&this.time<u.aggroUntil&&distance(u,u.home!)<1100)return current;
      u.targetId=null;
      const h=this.hero;if(h&&h.hp>0&&distance(h,u)<=250){u.targetId=h.id;u.aggroUntil=this.time+7;return h;}
      return undefined;
    }
    if(current&&this.enemy(u,current)&&distance(u,current)<(u.kind==='tower'?u.attackRange+current.radius:u.acquisitionRange+250))return current;
    const candidates=this.state.units.filter(t=>this.enemy(u,t)&&
      (t.team!=='neutral'||t.targetId!==null)&&distance(u,t)<=u.acquisitionRange+t.radius);
    candidates.sort((a,b)=>{
      const priority=(t:SimUnit)=>u.kind==='tower'?(t.kind==='lane'?0:t.kind==='hero'?1:2):t.kind==='tower'?2:t.kind==='hero'?1:0;
      return priority(a)-priority(b)||distance(u,a)-distance(u,b)||a.id.localeCompare(b.id);
    });
    return candidates[0];
  }
  private move(u:SimUnit,target:Point,budget:number){
    if(budget<=0||distance(u,target)<1)return;
    const before={x:u.x,y:u.y},d=distance(u,target),next={x:u.x+(target.x-u.x)*Math.min(1,budget/d),y:u.y+(target.y-u.y)*Math.min(1,budget/d)};
    if(clearSegment(before,next,this.state.units,u.id)){
      u.x=next.x;u.y=next.y;u.nav=[];u.navIndex=0;u.navDestination=null;
    }else{
      if(!u.navDestination||distance(u.navDestination,target)>128||u.navIndex>=u.nav.length){
        const to=nearestWalkable(target);const p=to&&NAV.path(u,to);
        u.nav=p?.points.slice(1)||[];u.navIndex=0;u.navDestination=to;
      }
      this.walkNav(u,budget);
    }
    if(distance(before,u)>.01)u.facing=Math.atan2(u.y-before.y,u.x-before.x);
  }
  private walkNav(u:SimUnit,budget:number){
    while(u.navIndex<u.nav.length&&budget>0){
      const p=u.nav[u.navIndex],d=distance(u,p);
      const next=d<=budget?p:{x:u.x+(p.x-u.x)*budget/d,y:u.y+(p.y-u.y)*budget/d};
      if(!clearSegment(u,next,this.state.units,u.id))break;
      const before={x:u.x,y:u.y};u.x=next.x;u.y=next.y;
      if(distance(before,u)>.01)u.facing=Math.atan2(u.y-before.y,u.x-before.x);
      budget-=d;if(distance(u,p)<.001)u.navIndex++;else break;
    }
  }
  private walkLane(u:SimUnit,budget:number){
    const route=laneRoutes()[u.laneRoute!];
    while(u.routeIndex<route.length&&budget>0){
      const p=route[u.routeIndex],d=distance(u,p);
      const before={x:u.x,y:u.y};this.move(u,p,Math.min(d,budget));const used=distance(before,u);budget-=used;
      if(distance(u,p)<2)u.routeIndex++;else break;if(used<.001&&d>=2)break;
    }
    if(u.routeIndex>=route.length){u.hp=0;u.deadAt=this.tick;this.event({type:'lane_reached_base',sourceId:u.id});}
  }
  private attack(u:SimUnit,target:SimUnit){
    u.facing=Math.atan2(target.y-u.y,target.x-u.x);
    if(u.windup){
      if(u.windup.targetId!==target.id){u.windup=null;return;}
      if(this.time+1e-8<u.windup.release)return;
      u.windup=null;u.nextAttack=this.time+Math.max(DT,u.attackInterval-u.attackPoint);
      let critical=false,multiplier=1;
      if(u.attributes&&target.kind!=='tower')for(const c of u.attributes.critSources){if(this.random()<c.chance){critical=true;multiplier=Math.max(multiplier,c.multiplier);}}
      const damage=(u.damageMin+(u.damageMax-u.damageMin)*this.random())*multiplier;
      const glaive=u.name==='npc_dota_hero_luna'&&target.kind!=='tower'?Math.min(4,Math.floor((u.level!+1)/2)):0;
      const values=COMBAT.heroes.npc_dota_hero_luna.abilities.luna_moon_glaive.values;
      const p:Projectile={id:this.id('shot'),x:u.x,y:u.y,sourceId:u.id,sourceKind:u.kind,sourceTeam:u.team,targetId:target.id,damage,
        speed:u.projectileSpeed,damageType:'physical',critical,glaiveBounces:glaive?levelValue(values.bounces,glaive):0,
        glaiveRetention:1-levelValue(values.damage_reduction_percent,glaive)/100,lifesteal:u.attributes?.lifesteal||0};
      if(p.speed>0)this.state.projectiles.push(p);else this.hit(p,target);
      const key=u.kind==='hero'?'heroAttacks':u.kind==='tower'?'towerAttacks':u.kind==='lane'?'laneAttacks':null;
      if(key)this.state.totals[key]++;
      this.event({type:'attack',sourceId:u.id,targetId:target.id,sourceKind:u.kind,targetKind:target.kind,critical});return;
    }
    if(this.time+1e-8>=u.nextAttack)u.windup={targetId:target.id,release:this.time+Math.max(DT,u.attackPoint)};
  }
  private damage(target:SimUnit,raw:number,sourceId:string,sourceKind:SimUnit['kind'],sourceTeam:SimTeam,type:'physical'|'magical',critical:boolean){
    if(target.hp<=0||target.team===sourceTeam)return 0;
    const applied=Math.min(target.hp,Math.max(0,raw)*(type==='physical'?physicalMultiplier(target.armor):1-target.magicResistance));
    target.hp-=applied;this.event({type:'damage',sourceId,targetId:target.id,sourceKind,targetKind:target.kind,amount:applied,critical});
    if(target.kind==='neutral'){
      for(const ally of this.state.units.filter(u=>u.campId===target.campId&&u.hp>0)){
        ally.targetId=sourceId;ally.aggroUntil=this.time+7;
      }
    }
    if(target.hp<=0){
      target.hp=0;target.deadAt=this.tick;target.windup=null;this.state.totals.kills++;
      if(sourceKind==='hero'&&(target.kind==='lane'||target.kind==='neutral')){
        this.state.rewards.push({tick:this.tick,sourceId,targetId:target.id,kind:target.kind,gold:target.gold});
        this.event({type:'gold',sourceId,targetId:target.id,amount:target.gold,targetKind:target.kind});
      }
      this.event({type:'death',sourceId,targetId:target.id,sourceKind,targetKind:target.kind});
    }
    return applied;
  }
  private hit(p:Projectile,target:SimUnit){
    const dealt=this.damage(target,p.damage,p.sourceId,p.sourceKind,p.sourceTeam,p.damageType,p.critical);
    const source=this.alive(p.sourceId);
    if(source&&p.lifesteal&&target.kind!=='tower')source.hp=Math.min(source.maxHp,source.hp+dealt*p.lifesteal);
    const hit=new Set([target.id]);let from=target;
    for(let n=1;n<=p.glaiveBounces;n++){
      const next=this.state.units.filter(u=>u.hp>0&&u.team!==p.sourceTeam&&u.kind!=='tower'&&u.id!==from.id&&distance(u,from)<=500)
        .sort((a,b)=>Number(hit.has(a.id))-Number(hit.has(b.id))||distance(a,from)-distance(b,from))[0];
      if(!next)break;
      this.damage(next,p.damage*p.glaiveRetention**n,p.sourceId,p.sourceKind,p.sourceTeam,'physical',false);
      hit.add(next.id);from=next;
    }
  }
  advanceOne():void {
    if(this.tick>=MAX_TICKS)return;this.state.tick++;
    if(this.tick%150===0)this.spawnWaves();
    if(this.tick%NEUTRAL_SPAWN_TICKS===0)this.spawnNeutrals();
    const h=this.hero;if(h&&h.hp>0)this.applyAttributes(h);
    // Stable execution order is explicit; no wall-clock time or ambient RNG.
    for(const u of this.state.units){
      if(u.hp<=0)continue;
      u.hp=Math.min(u.maxHp,u.hp+u.healthRegen*DT);u.mana=Math.min(u.maxMana,u.mana+u.manaRegen*DT);
      if(u.kind==='hero'&&this.state.order.type==='move'){this.walkNav(u,u.moveSpeed*DT);u.windup=null;continue;}
      const target=this.target(u);u.targetId=target?.id||null;
      if(target){
        if(this.inRange(u,target))this.attack(u,target);
        else {u.windup=null;if(u.kind!=='tower')this.move(u,target,u.moveSpeed*DT);}
      }else{
        u.windup=null;
        if(u.kind==='lane')this.walkLane(u,u.moveSpeed*DT);
        else if(u.kind==='neutral'&&u.home&&distance(u,u.home)>24)this.move(u,u.home,u.moveSpeed*DT);
      }
    }
    const shots=this.state.projectiles;this.state.projectiles=[];
    for(const p of shots){
      const target=this.alive(p.targetId);if(!target)continue;
      const d=distance(p,target),length=p.speed*DT;
      if(d<=length+target.radius)this.hit(p,target);
      else{p.x+=(target.x-p.x)*length/d;p.y+=(target.y-p.y)*length/d;this.state.projectiles.push(p);}
    }
    this.state.units=this.state.units.filter(u=>u.kind==='tower'||u.kind==='hero'||u.deadAt===null||this.tick-u.deadAt<5);
  }
  snapshot(){
    const h=this.hero;
    const gold=(kind:Reward['kind'])=>this.state.rewards.filter(r=>r.kind===kind).reduce((s,r)=>s+r.gold,0);
    return {tick:this.tick,time:this.time,frameSeconds:DT,seed:this.state.seed,heroId:this.state.heroId,order:structuredClone(this.state.order),
      hero:h?structuredClone(h):null,units:this.state.units.map(({nav,navIndex,navDestination,loadout,attributes,...u})=>structuredClone(u)),
      projectiles:structuredClone(this.state.projectiles),events:structuredClone(this.state.events),camps:campStatuses(this.tick,this.state.units),
      economy:{neutralGold:gold('neutral'),laneGold:gold('lane'),totalGold:gold('neutral')+gold('lane'),lastHits:this.state.rewards.length},
      totals:{...this.state.totals},heroPath:h?.nav.slice(h.navIndex)||[],ended:this.tick===MAX_TICKS};
  }
}
function safelyWalk(u:SimUnit,path:Point[],budget:number){
  while(u.routeIndex<path.length&&budget>0){
    const p=path[u.routeIndex],d=distance(u,p),f=d?Math.min(1,budget/d):1;
    u.x+=(p.x-u.x)*f;u.y+=(p.y-u.y)*f;budget-=d;if(f===1)u.routeIndex++;else break;
  }
}
function clearSegment(from:Point,to:Point,units:SimUnit[],selfId:string){
  const steps=Math.max(1,Math.ceil(distance(from,to)/16));let previous=from;
  for(let i=1;i<=steps;i++){
    const p={x:from.x+(to.x-from.x)*i/steps,y:from.y+(to.y-from.y)*i/steps};
    if(!isWalkable(p)||!isWalkable({x:p.x,y:previous.y})||!isWalkable({x:previous.x,y:p.y}))return false;
    if(units.some(u=>u.id!==selfId&&u.kind==='tower'&&u.hp>0&&distance(p,u)<u.radius+16))return false;
    previous=p;
  }return true;
}
