/** Game-clock farming evidence. No timers are used to invent a wave, a camp spawn or a kill. */
import assert from 'node:assert/strict';
import { gameTimeAt } from './farming-routes.ts';
import type { Hero } from './daily-study.ts';

export const MINUTE_FORMAT = 'farm-minute-v2' as const;
export const minuteAnswerFile = `answer.${MINUTE_FORMAT}.json`;
export type FarmSample = { t:number; lh:number; lane:number; neutral:number; nw:number|null; stacks:number|null;
  items:string[]; xy:[number,number]|null };
type Kind='lane'|'neutral';
type Actor='body'|'illusion'|'unknown';
type Hit={tick:number;kind:Kind;actor:Actor;target:string};
const ms=(t:number)=>Math.round(t*1000)/1000;
/** Remove sub-millisecond floating-point noise around a clock anchor, not real 30 Hz ticks. */
export function gameClockSecond(t:number) {
  const anchor=Math.round(t/30)*30;return Math.abs(t-anchor)<0.0005?anchor:t;
}
const logKey:Record<Hero,string>={luna:'lunaCombatLog',troll_warlord:'trollCombatLog',sven:'svenCombatLog'};

export function hitsByHalf(facts:any, hero:Hero, team:number, end:number) {
  const log=facts[logKey[hero]];
  if(!Array.isArray(log)||!facts.clock)return null; // unavailable ≠ observed zero
  const enemy=team===2?'badguys':'goodguys',lane=new RegExp(`^npc_dota_(?:creep_${enemy}_|${enemy}_siege)`);
  const bins=new Map<number,Hit[]>();
  for(const e of log){
    if(e.log_type!=='DEATH'||e.attacker_name!==`npc_dota_hero_${hero}`||e.attacker_team!==team||
      !Number.isSafeInteger(e.tick)||e.tick>facts.postGameTick)continue;
    const kind:Kind|null=e.target_team===4&&e.target_name?.startsWith('npc_dota_neutral_')?'neutral':
      e.target_team===(team===2?3:2)&&lane.test(e.target_name)?'lane':null;
    if(!kind)continue;
    const t=gameClockSecond(gameTimeAt(facts.clock,e.tick));
    if(t<0||t>end+1e-6)continue;
    const bin=Math.min(Math.floor(t/30),Math.ceil(end/30)-1); // terminal tick belongs to the closing half
    const list=bins.get(bin)??[];
    list.push({tick:e.tick,kind,target:e.target_name,actor:e.attacker_is_illusion_present===true&&typeof e.attacker_is_illusion==='boolean'
      ? e.attacker_is_illusion?'illusion':'body':'unknown'});
    bins.set(bin,list);
  }
  return bins;
}
function recordedKills(hits:Hit[]){
  const counts={body:{lane:0,neutral:0},illusion:{lane:0,neutral:0},unknown:{lane:0,neutral:0}};
  const ticks=new Map<number,Set<Kind>>();
  for(const h of hits){
    counts[h.actor][h.kind]++;
    if(h.actor==='body'){const kinds=ticks.get(h.tick)??new Set<Kind>();kinds.add(h.kind);ticks.set(h.tick,kinds);}
  }
  // Compress consecutive matching last-hit types. At the same tick no order between lane and neutral can be established.
  const order:string[]=[];
  for(const [,kinds] of [...ticks].sort(([a],[b])=>a-b)){
    const kind=kinds.size===2?'L+N':kinds.has('lane')?'L':'N';
    if(order.at(-1)!==kind)order.push(kind);
  }
  return {...counts,bodyOrder:order.join('>')};
}
function difference(a:FarmSample,b:FarmSample) {
  const lhGain=b.lh-a.lh,laneGold=b.lane-a.lane,neutralGold=b.neutral-a.neutral;
  const stacks=a.stacks===null||b.stacks===null?null:b.stacks-a.stacks;
  assert(lhGain>=0&&laneGold>=0&&neutralGold>=0&&(stacks===null||stacks>=0),'Non-monotonic replay counters');
  return {lhGain,laneGold,neutralGold,stacks};
}
export function farmingMinutes(samples:FarmSample[],facts:any,hero:Hero,team:number,prefix:string){
  const last=samples.at(-1)!,end=gameClockSecond(facts.durationSeconds),hits=hitsByHalf(facts,hero,team,end);
  assert(end>0 && Math.abs(end-last.t)<0.00001 && samples[0].t<5,'Missing horn/terminal economy sample');
  let cursor=0;
  // For cumulative counters use a single shared snapshot at, or immediately before, each boundary.
  // The first observed sample after the horn is the baseline. Never interpolate a kill or round into the next minute.
  function boundary(t:number){
    while(cursor+1<samples.length&&samples[cursor+1].t<=t)cursor++;
    const s=samples[cursor];assert(Math.abs(t-s.t)<=5,'Missing 30-second economy boundary');return s;
  }
  function half(minute:number,offset:0|30,a:FarmSample){
    const until=Math.min(minute*60+offset+30,end),b=until===end?last:boundary(until);
    assert(b.t>a.t,'Unresolved half-minute interval');
    return {b,row:{id:`${prefix}:m${minute}:${offset===0?'a':'b'}`,offset,partial:until<minute*60+offset+30,
      sampledFrom:ms(a.t),sampledTo:ms(b.t),...difference(a,b),fromXY:a.xy,toXY:b.xy,
      kills:hits?recordedKills(hits.get(minute*2+offset/30)??[]):null}};
  }
  let a=samples[0];const windows=[];
  for(let minute=0;minute*60<end;minute++){
    const first=half(minute,0,a);
    const second=minute*60+30<end?half(minute,30,first.b):null;
    const b=second?.b??first.b;
    windows.push({id:`${prefix}:m${minute}`,minute,from:minute*60,to:Math.min((minute+1)*60,end),
      partial:end<(minute+1)*60,lh:b.lh,netWorth:b.nw,...difference(a,b),items:b.items,
      halves:second?[first.row,second.row]:[first.row]});
    a=b;
  }
  return windows;
}
export type MinuteWindow=ReturnType<typeof farmingMinutes>[number];
