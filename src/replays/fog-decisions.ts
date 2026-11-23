/** Historical offline teaching only. No omniscient hero/unit array is ever an input to map judgement. */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { gameTimeAt } from './farming-routes.ts';
import { gameClockSecond, hitsByHalf } from './daily-minutes.ts';
import type { Evidence, Hero } from './daily-study.ts';

export const FOG_FORMAT='fog-decisions-v2' as const;
export const fogAnswerFile=`answer.${FOG_FORMAT}.json`;
type XY=[number,number];
type State='visible'|'hidden'|'unknown';
type Marker={xy:XY|null;alive:boolean|null};
type Witness={slot:number;hero:string|null;state:State;markers:Marker[]};
type Group={xy:XY;straightDistance:number;units:[string,number,number|null][];listedGross:[number,number]|null;
  nearestAllyDistance:number|null;nearestAlliedTowerDistance:number|null};
type Frame={tick:number;bodyXY:XY|null;allies:{hero:string;xy:XY}[];enemies:Witness[];
  farmCoverage:'incomplete'|'sampled-visible-only';lane:Group|null;neutral:Group|null};
export type TeamView={format:'team-map-v2';matchId:number;build:number;postGameTick:number;demSha256:string;hero:Hero;team:number;pid:number;
  frames:Frame[];combat:[number,string,string,'DAMAGE'|'DEATH'][]};
const round=(n:number)=>Math.round(n*1000)/1000;
const distance=(a:XY|null|undefined,b:XY|null|undefined)=>a&&b?Math.round(Math.hypot(a[0]-b[0],a[1]-b[1])):null;
const xy=(p:unknown):p is XY=>Array.isArray(p)&&p.length===2&&p.every(n=>Number.isFinite(n)&&Math.abs(n)<32768);
const name=(s:unknown):s is string=>typeof s==='string'&&/^npc_dota_hero_[a-z0-9_]+$/.test(s);
const sane=(n:any)=>Number.isSafeInteger(n)&&n>=0;
const nullable=(n:any)=>n===null||sane(n);
const only=(obj:object,allowed:string[])=>Object.keys(obj).every(k=>allowed.includes(k));
function group(g:Group|null){
  return g===null || g&&only(g,['xy','straightDistance','units','listedGross','nearestAllyDistance','nearestAlliedTowerDistance'])&&
    xy(g.xy)&&sane(g.straightDistance)&&Array.isArray(g.units)&&g.units.length>0&&g.units.every(u=>
    Array.isArray(u)&&u.length===3&&/^npc_dota_[a-z0-9_]+$/.test(u[0])&&sane(u[1])&&nullable(u[2]))&&
    (g.listedGross===null||g.listedGross.length===2&&g.listedGross.every(sane)&&g.listedGross[1]>=g.listedGross[0])&&
    nullable(g.nearestAllyDistance)&&nullable(g.nearestAlliedTowerDistance);
}
export function checkedTeamView(v:TeamView,facts:any,hero:Hero,pid:number){
  const team=pid<5?2:3,slots=team===2?[5,6,7,8,9]:[0,1,2,3,4];
  assert(v?.format==='team-map-v2'&&v.matchId===facts.matchId&&v.demSha256===facts.demSha256&&v.build===facts.gameBuild&&
    v.postGameTick===facts.postGameTick&&v.hero===hero&&v.pid===pid&&v.team===team&&facts.clock,'Team view DEM/slot/clock mismatch');
  assert(Array.isArray(v.frames)&&v.frames.length&&Array.isArray(v.combat),'Missing team-view samples');
  let last=-1,previousTime:number|undefined,known=false;
  for(const f of v.frames){
    const t=gameTimeAt(facts.clock,f.tick);
    assert(sane(f.tick)&&f.tick>last&&f.tick<=v.postGameTick&&Number.isFinite(t)&&
      (previousTime===undefined?t<=5:t>=previousTime&&t-previousTime<=5),'Incomplete or disordered team-view clock');
    last=f.tick;previousTime=t;
    assert((f.bodyXY===null||xy(f.bodyXY))&&['sampled-visible-only','incomplete'].includes(f.farmCoverage)&&
      Array.isArray(f.enemies)&&Array.isArray(f.allies)&&new Set(f.enemies.map(e=>e.slot)).size===f.enemies.length&&f.enemies.every(e=>
        slots.includes(e.slot)&&(e.hero===null||name(e.hero))&&['visible','hidden','unknown'].includes(e.state)&&
        only(e,['slot','hero','state','markers'])&&Array.isArray(e.markers)&&(e.state==='visible')===(e.markers.length>0)&&
        e.markers.every(m=>only(m,['xy','alive'])&&(m.xy===null||xy(m.xy))&&(m.alive===null||typeof m.alive==='boolean')))&&
      f.allies.every(a=>only(a,['hero','xy'])&&name(a.hero)&&xy(a.xy))&&group(f.lane)&&group(f.neutral),'Invalid or unmasked team map');
    known ||= f.enemies.some(e=>e.state==='visible');
  }
  assert(known&&Math.abs(facts.durationSeconds-previousTime!)<=5,'No verified playing-team vision or terminal samples');
  assert(v.combat.every(e=>Array.isArray(e)&&e.length===4&&sane(e[0])&&e[0]<=v.postGameTick&&name(e[1])&&name(e[2])&&
    ['DAMAGE','DEATH'].includes(e[3])),'Invalid team-visible combat events');
  return v;
}
function indexAt<T extends {tick:number}>(a:T[],tick:number){
  let l=0,r=a.length;while(l<r){const m=(l+r)>>>1;if(a[m].tick<=tick)l=m+1;else r=m;}return l-1;
}
function mapTimeline(v:TeamView,clock:any){
  const time=(tick:number)=>gameClockSecond(gameTimeAt(clock,tick)),slots=v.team===2?[5,6,7,8,9]:[0,1,2,3,4];
  type Seen={t:number;xy:XY;alive:boolean|null;hero:string|null};
  const seen=new Map<number,Seen>(), contacts:{tick:number;t:number;enemy:string;ally:string;at:XY;type:string}[]=[];
  const myName=`npc_dota_hero_${v.hero}`;
  for(const [tick,a,b,type] of v.combat){
    const f=v.frames[indexAt(v.frames,tick)];if(!f||time(tick)-time(f.tick)>1.5)continue;
    const enemy=f.enemies.find(e=>e.state==='visible'&&e.markers.length===1&&e.markers[0].alive===true&&(e.hero===a||e.hero===b));
    const at=enemy?.markers[0]?.xy;if(!enemy?.hero||!at)continue;
    const ally=enemy.hero===a?b:a, allyXY=ally===myName?f.bodyXY:f.allies.find(q=>q.hero===ally)?.xy;
    // A global spell, a visible but unlocated event, or heroes just standing together is not a witnessed local fight.
    if(!allyXY||distance(allyXY,at)!>1500)continue;
    contacts.push({tick,t:time(tick),enemy:enemy.hero,ally,at,type});
  }
  contacts.sort((a,b)=>a.tick-b.tick);
  let lastLane:{t:number;xy:XY;count:number}|null=null,prior=-1;
  return v.frames.map(f=>{
    const t=time(f.tick),enemies=slots.map(slot=>{
      const e=f.enemies.find(e=>e.slot===slot),state=e?.state??'unknown';
      if(e?.hero&&seen.has(slot)&&seen.get(slot)!.hero!==e.hero)seen.delete(slot);
      const markers=e?.markers.map(m=>({xy:m.xy,alive:m.alive}))??[],one=markers.length===1?markers[0]:null;
      if(one?.xy)seen.set(slot,{t,xy:one.xy,alive:one.alive,hero:e?.hero??null});
      const s=seen.get(slot);
      return {slot,hero:e?.hero??s?.hero??null,state,markers,now:one,
        lastSeen:s?{secondsAgo:round(t-s.t),xy:s.xy,aliveAtSighting:s.alive}:null};
    });
    if(f.lane&&f.lane.straightDistance<=4000)lastLane={t,xy:f.lane.xy,count:f.lane.units.length};
    while(prior+1<contacts.length&&contacts[prior+1].tick<=f.tick)prior++;
    const pairs=new Map<string,{enemy:string;ally:string;lastSecondsAgo:number;at:XY;events:number;distanceFromBody:number|null;distanceFromLane:number|null}>();
    for(let i=prior;i>=0&&t-contacts[i].t<=15;i--){
      const hit=contacts[i],key=`${hit.enemy}/${hit.ally}`;
      const p=pairs.get(key)??{enemy:hit.enemy,ally:hit.ally,lastSecondsAgo:round(t-hit.t),at:hit.at,events:0,
        distanceFromBody:distance(hit.at,f.bodyXY),distanceFromLane:distance(hit.at,f.lane?.xy)};
      p.events++;pairs.set(key,p);
    }
    return {tick:f.tick,t:round(t),bodyXY:f.bodyXY,enemies,allies:f.allies,farmCoverage:f.farmCoverage,lane:f.lane,neutral:f.neutral,
      lastLocalLane:lastLane&&t-lastLane.t<=45?{secondsAgo:round(t-lastLane.t),xy:lastLane.xy,thenVisibleUnits:lastLane.count}:null,
      localHeroExchanges:[...pairs.values()]};
  });
}
export const fogSituations=['all-off-map','distant-contact','nearby-threat'] as const;
export type FogSituation=typeof fogSituations[number];
function contextFlags(f:ReturnType<typeof mapTimeline>[number]|null){
  const live=f?.enemies.flatMap(n=>n.markers).filter(m=>m.alive&&m.xy)??[];
  return {
    'all-off-map':!!f?.bodyXY&&f.enemies.every(n=>n.state==='hidden')&&!!f.lane&&f.lane.straightDistance<=2600,
    'nearby-threat':!!f?.bodyXY&&live.some(n=>distance(n.xy,f.bodyXY)!<=1300),
    'distant-contact':!!f?.bodyXY&&f.localHeroExchanges.some(p=>p.lastSecondsAgo<=3&&p.distanceFromBody!==null&&p.distanceFromBody>=3500)};
}
type FarmKind='lane'|'neutral';
function bodyEvents(facts:any,hero:Hero,team:number){
  const byTick=new Map<number,Set<FarmKind>>(),campByTick=new Map<number,Set<string>>();
  const camps=new Map<number,string>((facts.camps??[]).map((c:any)=>[c.spawnerIndex,c.id]));
  const deaths=new Map<string,Set<string|null>>();
  for(const c of facts.creatures??[]){
    if(!sane(c.diedTick)||c.team!==4)continue;
    const k=`${c.diedTick}/${c.unitName}`,s=deaths.get(k)??new Set();
    s.add(sane(c.spawnerHandle)&&c.spawnerHandle!==0xffffff?camps.get(c.spawnerHandle&0x3fff)??null:null);deaths.set(k,s);
  }
  for(const hs of hitsByHalf(facts,hero,team,gameClockSecond(facts.durationSeconds))?.values()??[])for(const h of hs){
    if(h.actor!=='body')continue;
    const kinds=byTick.get(h.tick)??new Set();kinds.add(h.kind);byTick.set(h.tick,kinds);
    const camp=deaths.get(`${h.tick}/${h.target}`),codes=campByTick.get(h.tick)??new Set();
    if(h.kind==='neutral'){codes.add(camp?.size===1?[...camp][0]??'?':'?');campByTick.set(h.tick,codes);}
  }
  return [...byTick].sort(([a],[b])=>a-b).map(([tick,ks])=>{
    const cs=campByTick.get(tick);return {tick,kind:ks.size===1?[...ks][0]:null,
      camp:cs?.size===1&&!cs.has('?')?[...cs][0]:null};
  });
}
export function decisionEvidence(e:Evidence,facts:any,v:TeamView,pid:number) {
  checkedTeamView(v,facts,e.hero,pid);
  const map=mapTimeline(v,facts.clock),hits=bodyEvents(facts,e.hero,v.team),time=(tick:number)=>gameClockSecond(gameTimeAt(facts.clock,tick));
  const frames=facts.frames as {tick:number;heroes:any[]}[],hero=(f:typeof frames[number]|undefined)=>f?.heroes.find(h=>h.playerIdField===pid&&h.team===v.team&&h.name===`npc_dota_hero_${e.hero}`);
  const marks=new Map<number,Set<string>>();
  const mark=(tick:number,why:string)=>{const a=marks.get(tick)??new Set();a.add(why);marks.set(tick,a);};
  // Minute windows never disappear, even while dead or in a short terminal tail.
  for(const w of e.windows){
    const fr=map.find(f=>f.t>=w.from&&f.t<w.to);
    const fallback=frames.find(f=>time(f.tick)>=w.from&&time(f.tick)<=w.to+0.00001);
    mark(fr?.tick??fallback?.tick??facts.postGameTick,'minute');
  }
  for(let i=1;i<hits.length;i++){
    const a=hits[i-1],b=hits[i],dt=time(b.tick)-time(a.tick);
    if(!a.kind||!b.kind||dt<=0||dt>30)continue;
    const camp=a.kind==='neutral'&&b.kind==='neutral'&&a.camp&&b.camp&&a.camp!==b.camp;
    if(a.kind===b.kind&&!camp)continue;
    const j=indexAt(map,a.tick-1)+1,fr=map[j],tick=fr&&fr.tick<b.tick&&time(fr.tick)-time(a.tick)<=1.5?fr.tick:a.tick;
    mark(tick,camp?'between-confirmed-camps':'between-farm-types'); // Pause chosen retrospectively; NOT sent to the reviewer.
  }
  // Add decision points even when no last hit changes. Debounce repeated fog flicker without dropping distinct farm switches.
  const was=new Map<string,boolean>(),mostRecent=new Map<string,number>();
  for(const f of map){
    if(f.t<0||f.t>=facts.durationSeconds)continue;
    for(const [key,on] of Object.entries(contextFlags(f))){
      if(on&&!was.get(key)&&f.t-(mostRecent.get(key)??-Infinity)>=15){mark(f.tick,key);mostRecent.set(key,f.t);}
      was.set(key,on);
    }
  }
  const points=[...marks].sort(([a],[b])=>a-b).map(([tick,why])=>{
    const t=time(tick),mi=Math.min(e.windows.length-1,Math.max(0,Math.floor(t/60)));
    const mapSample=map[indexAt(map,tick)],at=mapSample&&t-mapSample.t<=2?mapSample:null;
    const fi=indexAt(frames,tick),hf=frames[fi],h=hero(hf),equipped=(h?.items??[]).filter((i:any)=>i.slot<=5||i.slot===15||i.slot===16)
      .map((i:any)=>({slot:i.slot,item:i.name,cooldownField:i.cooldown??null}));
    const before={at:round(t),map:at,own:h?{sampleTime:round(time(hf.tick)),alive:h.lifeState===0,hp:h.health??null,maxHp:h.maxHealth??null,
      mana:Number.isFinite(h.mana)?Math.floor(h.mana):null,level:h.level??null,equipped,
      skills:(h.abilities??[]).filter((i:any)=>i.level>0&&i.name?.startsWith(`${e.hero}_`))
        .map((i:any)=>({skill:i.name,level:i.level,cooldownField:i.cooldown??null}))}:null};
    const next=hits[indexAt(hits,tick)+1],nextHit=next&&time(next.tick)-t<=30?{at:round(time(next.tick)),kind:next.kind,camp:next.camp}:null;
    const end=Math.min(t+30,facts.durationSeconds),afterFrame=frames.findLast(f=>time(f.tick)<=end+0.00001);
    const ec=h?.economy,ec2=hero(afterFrame)?.economy;
    const delta=(k:string)=>sane(ec?.[k])&&sane(ec2?.[k])?ec2[k]-ec[k]:null;
    const after={nextBodyLastHit:nextHit,counterFrom:hf?round(time(hf.tick)):null,counterTo:afterFrame?round(time(afterFrame.tick)):null,
      laneGold:delta('m_iCreepKillGold'),neutralGold:delta('m_iNeutralKillGold'),lh:delta('m_iLastHitCount')};
    const id=`${e.matchId}:${e.hero}:d${tick}`;
    return {id,key:createHash('sha256').update(id).digest('hex').slice(0,12),minute:mi,triggers:[...why],before,after};
  });
  assert(e.windows.every(w=>points.some(p=>p.minute===w.minute&&p.triggers.includes('minute'))),'Missing decision minute');
  assert(new Set(points.map(p=>p.key)).size===points.length,'Decision key collision');
  return {format:FOG_FORMAT,patch:e.patch,hero:e.hero,matchId:e.matchId,points};
}
export type Decisions=ReturnType<typeof decisionEvidence>;
/** Retrieval tags, not verdicts. Match ongoing conditions too, not just the first tick of an episode. */
export function situationAt(point:Decisions['points'][number],s:FogSituation){
  return contextFlags(point.before.map)[s];
}
