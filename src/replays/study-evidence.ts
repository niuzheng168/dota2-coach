import { farmingWindow } from './farming-routes.ts';
import type { FarmFrame } from './farming-routes.ts';

/** Uniform factual extraction. No route scoring, safety classifier or strategy rules. */
export function studyEvidence(facts:any, match:any) {
  const pid=facts.heroPath[0].playerIdField,team=match.team==='radiant'?2:3;
  const at=(time:number)=>facts.frames.find((f:any)=>f.gameTime>=time)??facts.frames.at(-1);
  const snapshots=[];
  for(let t=0;t<match.duration;t+=60){
    const f=at(t),h=f.heroes.find((h:any)=>h.playerIdField===pid);
    const players=f.heroes.map((p:any)=>({hero:p.name,team:p.team,alive:p.lifeState===0,
      x:p.position?.x,y:p.position?.y,level:p.level,networth:p.economy?.m_iNetWorth}));
    const total=(which:number)=>f.heroes.filter((p:any)=>p.team===which).reduce((n:number,p:any)=>n+(p.economy?.m_iNetWorth??0),0);
    snapshots.push({time:f.gameTime,ownNetworth:h.economy.m_iNetWorth,
      alliedMinusEnemyNetworth:total(team)-total(team===2?3:2),
      heroes:players,aliveTowers:f.units.filter((u:any)=>u.class==='CDOTA_BaseNPC_Tower').map((u:any)=>u.name),
      // These are omniscient samples. Live context must never receive this field.
      enemyLaneCreeps:f.units.filter((u:any)=>u.class==='CDOTA_BaseNPC_Creep_Lane'&&u.team!==team)
        .map((u:any)=>[Math.round(u.position?.x),Math.round(u.position?.y),u.health])});
  }
  const windows=[];
  for(let t=0;t<match.duration-1;t+=30){
    const end=Math.min(t+30,match.duration),a=match.frames.find((f:FarmFrame)=>f.time>=t);
    if(!a)continue;
    let economy;try{economy=farmingWindow(match.frames,t,end);}catch{continue;}
    const sample=match.frames.filter((f:FarmFrame)=>f.time>=economy.from&&f.time<=economy.to);
    const kills=match.kills.filter((k:any)=>k.time>=economy.from&&k.time<economy.to);
    const camps:Record<string,number>={};
    for(const k of kills)if(k.kind==='neutral')camps[`${k.campId??'unknown'}${k.illusion?':illusion':':body'}`]=(camps[`${k.campId??'unknown'}${k.illusion?':illusion':':body'}`]??0)+1;
    windows.push({from:economy.from,to:economy.to,lane:economy.laneGold,neutral:economy.neutralGold,
      heroGold:economy.heroGold,buildingGold:economy.buildingGold,lastHits:economy.lastHits,stacks:economy.stacks,
      body:[a.x,a.y],endBody:[sample.at(-1)?.x,sample.at(-1)?.y],level:a.level,hp:a.hp,mana:a.mana,
      deadSamples:sample.filter((f:FarmFrame)=>!f.alive).length,loadout:a.loadout,camps,
      laneKillEvents:{body:kills.filter((k:any)=>k.kind==='lane'&&!k.illusion).length,illusion:kills.filter((k:any)=>k.kind==='lane'&&k.illusion).length}});
  }
  const transitions:any[]=[];
  for(let i=1;i<match.frames.length;i++){
    const a=match.frames[i-1],b=match.frames[i];
    if(a.alive!==b.alive)transitions.push({time:b.time,event:b.alive?'respawn':'death',position:[b.x,b.y]});
    if(b.stacks>a.stacks)transitions.push({time:b.time,event:'own-stack-counter-increase',count:b.stacks-a.stacks,
      position:[b.x,b.y],note:'Camp attribution and pulling technique not proven by this counter.'});
    if(a.loadout!==b.loadout)transitions.push({time:b.time,event:'loadout-change',loadout:b.loadout});
  }
  return {version:1,matchId:match.matchId,player:match.player,providerPro:match.providerPro,
    tournament:match.tournament,matchMmr:match.matchMmr,team:match.team,won:match.won,
    duration:match.duration,startIso:match.startIso,build:match.gameBuild,validation:match.validation,
    informationScope:'omniscient-offline-replay-only',
    caveats:['Snapshots include enemy information without visibility masks: never infer that the player saw it.',
      'Counter gold includes owned units; kill events have separate illusion flags but no per-unit gold parity proof.',
      '30-second windows and 60-second world snapshots are uniform, not strategy-selected highlights.',
      'Stack counters do not prove who pulled which camp; no pull/aggro or camp-refresh causal claim without further replay inspection.',
      'No counterfactual route was executed. Associations are not causal improvements or guarantees.'],
    windows,loadouts:match.loadouts,transitions,
    casts:match.casts.filter((c:any)=>c.name!=='item_power_treads'),
    towerEvents:facts.towers.filter((t:any)=>t.diedGameTime!=null).map((t:any)=>({time:t.diedGameTime,team:t.team,name:t.name,position:t.position})),
    worldSnapshots:snapshots};
}
