/** Replay evidence about opponents' capabilities. Past receipts, public base rules and engine-only rank audit never mix. */
import assert from 'node:assert/strict';
import type { Hero } from './daily-study.ts';
import type { Decisions } from './fog-decisions.ts';
import { gameTimeAt } from './farming-routes.ts';
import { defensiveItems,type Rules } from './spell-rules.ts';

export const THREAT_FORMAT='spell-threats-v2' as const,threatAnswerFile=`answer.${THREAT_FORMAT}.json`;
type Item=[slot:number,name:string,charges:number|null,cooldown:number|null];
type Frame={tick:number;publicSheets:[hero:string,level:number|null,mana:number|null][];ownItems:Item[]};
type Cast=['cast',tick:number,actor:string,spell:string,auditRank:number|null,side:'recorded-team'|'roster-label'];
type Hit=['hit',tick:number,actor:string,spell:string,target:string,value:number,type:string,focalBody:boolean];
type Mod=['add'|'remove',tick:number,actor:string,modifier:string,reportedStun:number|null];
type Event=Cast|Hit|Mod;
export type SpellTape={format:'team-spells-v2';matchId:number;build:number;postGameTick:number;demSha256:string;hero:Hero;team:number;pid:number;
  frames:Frame[];combat:Event[]};
const rounded=(x:number)=>Math.round(x*1000)/1000;
const nat=(x:any)=>Number.isSafeInteger(x)&&x>=0;
const goodName=(s:any)=>typeof s==='string'&&/^npc_dota_hero_[a-z0-9_]{1,80}$/.test(s);
const goodKey=(s:any)=>typeof s==='string'&&/^[a-z][a-z0-9_]{0,120}$/.test(s);
export function checkedSpellTape(v:SpellTape,facts:any,hero:Hero,pid:number){
  assert(v?.format==='team-spells-v2'&&v.matchId===facts.matchId&&v.demSha256===facts.demSha256&&v.build===facts.gameBuild&&
    v.postGameTick===facts.postGameTick&&v.hero===hero&&v.pid===pid&&v.team===(pid<5?2:3)&&facts.clock,'Spell witnesses do not match DEM/hero/team');
  assert(Array.isArray(v.frames)&&v.frames.length&&Array.isArray(v.combat),'Missing spell tape');
  let last=-1,prev:number|undefined;
  for(const f of v.frames){
    const at=gameTimeAt(facts.clock,f.tick);
    assert(nat(f.tick)&&f.tick>last&&f.tick<=v.postGameTick&&Number.isFinite(at)&&(prev===undefined?at<=5:at>=prev&&at-prev<=5)&&
      Array.isArray(f.publicSheets)&&f.publicSheets.length<=5&&f.publicSheets.every(s=>s.length===3&&goodName(s[0])&&
        (s[1]===null||nat(s[1])&&s[1]>0&&s[1]<=30)&&(s[2]===null||nat(s[2])))&&
      Array.isArray(f.ownItems)&&new Set(f.ownItems.map(i=>i[0])).size===f.ownItems.length&&f.ownItems.every(i=>i.length===4&&
        [0,1,2,3,4,5,15,16].includes(i[0])&&goodKey(i[1])&&i[1].startsWith('item_')&&
        (i[2]===null||nat(i[2]))&&(i[3]===null||Number.isFinite(i[3])&&i[3]>=0)),'Invalid spell/HUD sample');
    last=f.tick;prev=at;
  }
  assert(Math.abs(facts.durationSeconds-prev!)<=5,'Incomplete spell tape');
  for(const x of v.combat){
    assert(Array.isArray(x)&&nat(x[1])&&x[1]<=v.postGameTick&&goodName(x[2])&&
      (x[0]==='cast'?x.length===6&&goodKey(x[3])&&(x[4]===null||nat(x[4])&&x[4]>0&&x[4]<=30)&&['recorded-team','roster-label'].includes(x[5]):
      x[0]==='hit'?x.length===8&&(x[3]===''||goodKey(x[3]))&&goodName(x[4])&&nat(x[5])&&x[5]>0&&
        ['physical','magical','pure','unknown'].includes(x[6])&&typeof x[7]==='boolean'&&(!x[7]||x[4]===`npc_dota_hero_${hero}`):
      (x[0]==='add'||x[0]==='remove')&&x.length===5&&goodKey(x[3])&&x[3].startsWith('modifier_')&&
        (x[4]===null||Number.isFinite(x[4])&&x[4]>0&&x[4]<120)),'Invalid team-visible spell event');
  }
  return v;
}
type Receipt={tick:number;at:number;hero:string;spell:string;value:number;type:string};
/** A single observed rolling window, not independent maxima for each enemy, a simulated combo, or HP loss. */
export function burst5(hits:Receipt[]){
  if(!hits.length)return null;
  let start=0,sum=0,max=0,a=0,b=0;
  for(let i=0;i<hits.length;i++){
    sum+=hits[i].value;
    while(hits[i].at-hits[start].at>5+1e-7)sum-=hits[start++].value;
    if(sum>=max){max=sum;a=start;b=i;}
  }
  const comps=new Map<string,{hero:string;spell:string|null;damageType:string;hits:number;loggedDamage:number;minHit:number;maxHit:number}>();
  for(const x of hits.slice(a,b+1)){
    const k=[x.hero,x.spell,x.type].join('/');
    const c=comps.get(k)??{hero:x.hero,spell:x.spell||null,damageType:x.type,hits:0,loggedDamage:0,minHit:x.value,maxHit:x.value};
    c.hits++;c.loggedDamage+=x.value;c.minHit=Math.min(x.value,c.minHit);c.maxHit=Math.max(x.value,c.maxHit);comps.set(k,c);
  }
  return {firstTick:hits[a].tick,from:rounded(hits[a].at),to:rounded(hits[b].at),loggedDamage:max,components:[...comps.values()]};
}
function atFrame(frames:Frame[],tick:number){
  let lo=0,hi=frames.length;while(lo<hi){const m=(lo+hi)>>>1;if(frames[m].tick<=tick)lo=m+1;else hi=m;}return frames[lo-1];
}
export function stunUnion(intervals:{from:number;to:number}[]){
  let upTo=-Infinity,total=0;for(const s of [...intervals].sort((a,b)=>a.from-b.from)){
    total+=Math.max(0,s.to-Math.max(s.from,upTo));upTo=Math.max(upTo,s.to);
  }return rounded(total);
}
function selfDefence(frame:Frame|undefined,rules:Rules|null){
  return (frame?.ownItems??[]).filter(i=>defensiveItems.includes(i[1])).map(([slot,item,charges,cooldown])=>{
    const vals=rules?.items[item]?.values,get=(key:string)=>vals?.[key]?.base.length===1?vals[key].base[0]:null;
    const per=get('restore_per_charge'),fire=get('hp_restore');
    let ifActivatedHP:number|null=null;
    if(cooldown===0&&charges!==null&&charges>0){
      if((item==='item_magic_stick'||item==='item_magic_wand')&&per!==null)ifActivatedHP=charges*per;
      if(item==='item_faerie_fire'&&fire!==null)ifActivatedHP=fire;
    }
    return {slot,item,charges,cooldownField:cooldown,ifActivatedHP};
  });
}
export function spellThreats(d:Decisions,facts:any,tape:SpellTape,pid:number,rules:Rules|null){
  checkedSpellTape(tape,facts,d.hero,pid);
  assert(d.matchId===tape.matchId&&(!rules||(rules.patch===d.patch&&rules.build===tape.build)),'Cross-patch spell rules');
  const t=(tick:number)=>gameTimeAt(facts.clock,tick),events=[...tape.combat].sort((a,b)=>a[1]-b[1]);
  const roster=new Set(d.points.flatMap(p=>p.before.map?.enemies.flatMap(n=>n.hero?[n.hero]:[])??[]));
  const definitions=rules?{commit:rules.commit,steamInfSha256:rules.steamInfSha256,heroesTree:rules.heroesTree,itemsBlob:rules.itemsBlob,
    heroes:Object.fromEntries([...roster].filter(k=>rules.heroes[k]).map(k=>[k,rules.heroes[k]])),items:rules.items}:null;
  const casters=new Map<string,Map<string,Cast>>(),lastAllyHit=new Map<string,Map<string,Hit>>(),lastSelfHit=new Map<string,Map<string,Hit>>(),
    seen=new Map<string,{tick:number;level:number|null;mana:number|null}>(),ownHits:Receipt[]=[];
  type Span={hero:string;modifier:string;from:number;to:number;endTick:number;nominalAtAdd:number};
  const applied=new Map<string,{tick:number;nominal:number}|null>(),closed:Span[]=[];
  let ei=0,fi=0,last=-1;
  const points=d.points.map(p=>{
    const tick=Number(p.id.split(':d').at(-1)),at=t(tick);assert(nat(tick)&&tick>last&&Math.abs(at-p.before.at)<0.002,'Disordered spell checkpoint');last=tick;
    while(fi<tape.frames.length&&tape.frames[fi].tick<=tick){const f=tape.frames[fi++];
      for(const [hero,level,mana] of f.publicSheets)if(roster.has(hero))seen.set(hero,{tick:f.tick,level,mana});
    }
    while(ei<events.length&&events[ei][1]<=tick){
      const x=events[ei++],hero=x[2],spell=x[3];if(!roster.has(hero))continue;
      if(x[0]==='cast'){const m=casters.get(hero)??new Map();m.set(spell,x);casters.set(hero,m);}
      else if(x[0]==='hit'){
        const m=lastAllyHit.get(hero)??new Map();m.set(spell,x);lastAllyHit.set(hero,m);
        if(x[7]){const s=lastSelfHit.get(hero)??new Map();s.set(spell,x);lastSelfHit.set(hero,s);
          ownHits.push({tick:x[1],at:t(x[1]),hero,spell,value:x[5],type:x[6]});}
      }else{
        const k=`${hero}/${spell}`;
        if(x[0]==='add'&&x[4]!==null)applied.set(k,applied.has(k)?null:{tick:x[1],nominal:x[4]});
        if(x[0]==='remove'){
          const a=applied.get(k);applied.delete(k);
          if(a&&a.tick<x[1])closed.push({hero,modifier:spell,from:rounded(t(a.tick)),to:rounded(t(x[1])),endTick:x[1],nominalAtAdd:a.nominal});
        }
      }
    }
    const age=(tick:number)=>rounded(at-t(tick));
    const auditRanks:{hero:string;spell:string;castAt:number;engineRankAtCast:number}[]=[];
    const opponents=(p.before.map?.enemies??[]).flatMap(enemy=>{
      const hero=enemy.hero;if(!hero)return [];
      const sheet=seen.get(hero),ds=(q:[number,number]|null|undefined)=>q&&enemy.lastSeen?
        Math.round(Math.hypot(q[0]-enemy.lastSeen.xy[0],q[1]-enemy.lastSeen.xy[1])):null;
      const casts=casters.get(hero),ally=lastAllyHit.get(hero),self=lastSelfHit.get(hero);
      const spells=[...new Set([...(casts?.keys()??[]),...(ally?.keys()??[])])].flatMap(spell=>{
        const c=casts?.get(spell),h=ally?.get(spell),s=self?.get(spell);
        // Current base definitions are public; evidence of a cast can be stale and never proves readiness.
        if(c&&c[4]!==null)auditRanks.push({hero,spell,castAt:rounded(t(c[1])),engineRankAtCast:c[4]});
        const impact=(x:Hit|undefined)=>x?{at:rounded(t(x[1])),age:age(x[1]),target:x[4],amount:x[5],damageType:x[6],confirmedFocalBody:x[7]}:null;
        return [{spell:spell||'unattributed',lastVisibleCast:c?{at:rounded(t(c[1])),age:age(c[1]),sideSource:c[5]}:null,
          lastTeamImpact:impact(h),lastHitToMyBody:impact(s)}];
      });
      return [{hero,singleAppearanceSheet:sheet?{age:age(sheet.tick),levelAtSighting:sheet.level,manaAtSighting:sheet.mana}:null,
        distanceFromLastSightingToBody:ds(p.before.map?.bodyXY),distanceFromLastSightingToLane:ds(p.before.map?.lane?.xy),
        lastSightingAge:enemy.lastSeen?.secondsAgo??null,spells}];
    });
    const latest=atFrame(tape.frames,tick),peak=burst5(ownHits.filter(h=>h.at>=at-90));
    const spans=peak?closed.filter(c=>c.to>=peak.from-1&&c.from<=peak.to&&c.endTick<=tick):[];
    const observed=peak?{...peak,defenceAtFirstHit:selfDefence(atFrame(tape.frames,peak.firstTick),rules),
      completedStunsTouchingEncounter:spans.map(({endTick,...s})=>s),nonOverlappingStunSeconds:stunUnion(spans)}:null;
    return {id:p.id,key:p.key,at:p.before.at,before:{opponents,ownDefence:selfDefence(latest,rules),
      ownSampleAt:latest?rounded(t(latest.tick)):null,strongestObserved5sInLast90s:observed},auditRanks};
  });
  return {format:THREAT_FORMAT,patch:d.patch,build:tape.build,matchId:d.matchId,hero:d.hero,demSha256:tape.demSha256,definitions,points};
}
export type Threats=ReturnType<typeof spellThreats>;
/** Return only refs this particular pause can cite; do not admit future receipts or someone else's abilities. */
export function threatRefs(th:Threats,p:Threats['points'][number]){
  const refs=new Set<string>();
  if(p.before.strongestObserved5sInLast90s)refs.add('burst');
  if(p.before.ownDefence.length)refs.add('defence');
  for(const e of p.before.opponents){
    refs.add('map/'+e.hero);
    for(const s of e.spells)refs.add('seen/'+e.hero+'/'+s.spell);
    for(const k of Object.keys(th.definitions?.heroes[e.hero]?.spells??{}))refs.add('rule/'+e.hero+'/'+k);
  }
  if(!refs.size)refs.add('unobserved');return refs;
}
export type ThreatMetric={ref:string;field:string;value:number};
/** Bind a numeric citation to the right hero, target, field and, for a base table, rank. */
export function measure(th:Threats,p:Threats['points'][number],ref:string,field:string):number|undefined{
  const [type,hero,spell]=ref.split('/'),e=p.before.opponents.find(x=>x.hero===hero);
  if(type==='rule'&&e){
    const m=/^([A-Za-z][A-Za-z0-9_]*)@([1-9]\d?)$/.exec(field);if(!m)return;
    return th.definitions?.heroes[hero]?.spells[spell]?.values[m[1]]?.base[Number(m[2])-1];
  }
  if(type==='seen'){
    const s=e?.spells.find(x=>x.spell===spell);if(!s)return;
    return ({bodyDamage:s.lastHitToMyBody?.amount,teamDamage:s.lastTeamImpact?.amount,castAt:s.lastVisibleCast?.at} as Record<string,number|undefined>)[field];
  }
  if(type==='map'&&field==='manaAtSighting')return e?.singleAppearanceSheet?.manaAtSighting??undefined;
  if(ref==='defence')return p.before.ownDefence.find(i=>field===`${i.item}.ifActivatedHP`)?.ifActivatedHP??undefined;
  const b=p.before.strongestObserved5sInLast90s;
  if(ref==='burst'&&b)return field==='loggedDamage'?b.loggedDamage:field==='nonOverlappingStunSeconds'?b.nonOverlappingStunSeconds:undefined;
}
export function hasMeasure(th:Threats,p:Threats['points'][number]){
  return !!p.before.strongestObserved5sInLast90s||p.before.ownDefence.some(i=>i.ifActivatedHP!==null)||
    p.before.opponents.some(e=>e.singleAppearanceSheet?.manaAtSighting!=null||e.spells.length||
      Object.values(th.definitions?.heroes[e.hero]?.spells??{}).some(s=>Object.values(s.values).some(v=>v.base.length)));
}
/** Conditional arithmetic only. Never retrieve actual enemy cooldowns/ranks or assume a last-sighting mana value is current. */
export function baseScenarios(th:Threats,p:Threats['points'][number],hp:number|null,maxHp:number|null){
  const cooldowns:{hero:string;spell:string;castAge:number;remainingFromThatCastByBaseRank:number[]}[]=[];
  const burstPairs:{hero:string;spells:[string,string];minimumListedManaForTwoNewCasts:number;lastSeenMana:number;sheetAge:number;shortfallAtSighting:number}[]=[];
  const b=p.before.strongestObserved5sInLast90s;
  for(const o of p.before.opponents){
    const defs=th.definitions?.heroes[o.hero]?.spells;if(!defs)continue;
    const mana=new Map<string,number>();
    for(const s of o.spells){
      const rule=defs[s.spell];if(!rule||!s.lastVisibleCast||rule.behavior.some(v=>v==='PASSIVE'||v==='TOGGLE'))continue;
      const val=(key:string)=>Object.entries(rule.values).find(([k])=>k.toLowerCase()===key.toLowerCase())?.[1].base??[];
      const cd=val('AbilityCooldown'),age=s.lastVisibleCast.age;
      if(cd.length&&cd.every(v=>v>0)&&!val('AbilityCharges').some(v=>v>0)&&!val('AbilityChargeRestoreTime').some(v=>v>0)&&age<=Math.max(...cd)+2)
        cooldowns.push({hero:o.hero,spell:s.spell,castAge:age,remainingFromThatCastByBaseRank:cd.map(x=>rounded(Math.max(0,x-age)))});
      const cost=val('AbilityManaCost');
      if(cost.length&&cost.every(v=>v>0)&&b?.components.some(x=>x.hero===o.hero&&x.spell===s.spell))mana.set(s.spell,Math.min(...cost));
    }
    const sheet=o.singleAppearanceSheet;
    if(sheet?.manaAtSighting==null)continue;
    const entries=[...mana];for(let i=0;i<entries.length;i++)for(let j=i+1;j<entries.length;j++){
      const min=entries[i][1]+entries[j][1];
      burstPairs.push({hero:o.hero,spells:[entries[i][0],entries[j][0]],minimumListedManaForTwoNewCasts:min,lastSeenMana:sheet.manaAtSighting,
        sheetAge:sheet.age,shortfallAtSighting:Math.max(0,min-sheet.manaAtSighting)});
    }
  }
  const heals=p.before.ownDefence.flatMap(i=>i.ifActivatedHP!==null?[i.ifActivatedHP]:[]);
  const sum=heals.length?heals.reduce((a,b)=>a+b,0):null;
  return {baseCooldownComparisons:cooldowns,previousBurstSpellPairs:burstPairs,knownActiveHealIfUsed:sum,
    hpIfThoseHealsUsedBeforeControl:sum!==null&&hp!==null&&maxHp!==null?Math.min(maxHp,hp+sum):null};
}
