import { validateCampGeometry, validateTowers } from './validation.ts';

export type TracePoint=[time:number,x:number,y:number,alive:number,breakBefore:number];
export interface FarmFrame {
  time:number;x:number;y:number;alive:boolean;level:number;hp:number;mana:number;
  // Separate recorded counters, including owned units/illusions.
  gold:[lane:number,neutral:number,total:number,hero:number,building:number,passive:number];
  lastHits:number;stacks:number;loadout:number;campCounts:number[];
}
const round=(n:number)=>Math.round(n*1000)/1000;
const positioned=(h:any)=>Number.isFinite(h?.position?.x)&&Number.isFinite(h?.position?.y);
const goldKeys=['m_iCreepKillGold','m_iNeutralKillGold','m_iTotalEarnedGold','m_iHeroKillGold','m_iBuildingGold','m_iIncomeGold'];

// Same pause-aware clock formula as the pinned gem 0.14.0 GameClock.
export function gameTimeAt(clock:any,tick:number):number {
  const paused=(at:number)=>(clock.pauses||[]).reduce((n:number,p:any)=>
    n+Math.max(0,Math.min(at,p.end_tick??at)-p.start_tick),0);
  const base=clock.game_start_time_s!=null?clock.game_start_time_s*30-clock.net_tick_offset:
    clock.game_start_tick-paused(clock.game_start_tick);
  if(!Number.isFinite(base))throw new Error('Missing replay clock anchors');
  return (tick-paused(tick)-base)/30;
}
export function recordedTrace(path:any[],interval=.5):TracePoint[] {
  const result:TracePoint[]=[];let previous:any=null,lastKept=-Infinity,pendingBreak=true;
  for(const h of path){
    if(!positioned(h)||!Number.isFinite(h.gameTime)||h.gameTime<0){previous=null;pendingBreak=true;continue;}
    const alive=h.lifeState===0&&h.health>0;
    if(previous){
      const dt=h.gameTime-previous.gameTime;
      // Display discontinuities only: never invent a walking segment across a
      // respawn/teleport, missing samples, or a pause. This does not choose routes.
      if(h.id!==previous.id||h.lifeState!==previous.lifeState||dt<=0||dt>.5||
        Math.hypot(h.position.x-previous.position.x,h.position.y-previous.position.y)>600)pendingBreak=true;
    }
    if(h.gameTime-lastKept>=interval||pendingBreak){
      result.push([round(h.gameTime),round(h.position.x),round(h.position.y),Number(alive),Number(pendingBreak)]);
      lastKept=h.gameTime;pendingBreak=false;
    }
    previous=h;
  }
  return result;
}
export function farmingWindow(frames:FarmFrame[],from:number,to:number){
  if(!Number.isFinite(from)||!Number.isFinite(to)||to<=from)throw new Error('Invalid replay interval');
  const a=frames.find(f=>f.time>=from),b=frames.find(f=>f.time>=to)||frames.at(-1);
  if(!a||!b||b.time<=a.time)throw new Error('Replay does not cover interval');
  const gold=a.gold.map((value,i)=>{
    const delta=b.gold[i]-value;
    if(!Number.isFinite(delta)||delta<0)throw new Error('Missing/nonmonotonic recorded gold');
    return delta;
  });
  const elapsed=b.time-a.time;
  return {from:a.time,to:b.time,elapsed:round(elapsed),laneGold:gold[0],neutralGold:gold[1],
    farmGold:gold[0]+gold[1],farmPerMinute:round((gold[0]+gold[1])*60/elapsed),
    totalGold:gold[2],heroGold:gold[3],buildingGold:gold[4],passiveGold:gold[5],
    lastHits:b.lastHits-a.lastHits,stacks:b.stacks-a.stacks,initialLevel:a.level,finalLevel:b.level};
}
export function farmingMatch(facts:any,api:any,reference:any,source:any){
  if(facts.schemaVersion!==4||!facts.complete||facts.matchId!==api.match_id||source.matchId!==api.match_id)
    throw new Error('Replay completeness/schema/ID mismatch');
  const luna=api.players.find((p:any)=>p.hero_id===48);
  if(!luna)throw new Error('No Luna in requested replay');
  const playerId=luna.player_slot<128?luna.player_slot:luna.player_slot-128+5;
  if(facts.heroPath.some((h:any)=>h.playerIdField!==playerId||h.name!=='npc_dota_hero_luna'))throw new Error('Wrong selected hero identity');
  const campValidation=validateCampGeometry(facts,reference),towerValidation=validateTowers(facts,reference);
  if(campValidation.status!=='matched'||towerValidation.status!=='matched')throw new Error('Replay geometry does not match map');
  const final=facts.frames.at(-1);
  const parity=api.players.map((p:any)=>{
    const id=p.player_slot<128?p.player_slot:p.player_slot-128+5;
    return final.heroes.find((h:any)=>h.playerIdField===id)?.economy?.m_iLastHitCount===p.last_hits;
  });
  if(parity.length!==10||parity.some((p:boolean)=>!p))throw new Error('Terminal last-hit counters do not match the API');
  const bySpawner=new Map(campValidation.rows.map((c:any)=>[c.spawnerIndex,c.id]));
  const campOf=(u:any)=>Number.isInteger(u.spawnerHandle)?bySpawner.get(u.spawnerHandle&0x3fff)||null:null;
  const loadouts:any[]=[],signatures=new Map<string,number>();
  const frames:FarmFrame[]=facts.frames.filter((f:any)=>f.gameTime>=0&&f.tick<=facts.postGameTick).map((f:any)=>{
    const h=f.heroes.find((h:any)=>h.playerIdField===playerId);
    if(!positioned(h)||!h.economy)throw new Error('Missing recorded Luna frame');
    const gold=goldKeys.map(k=>h.economy[k]);
    if(!gold.every(Number.isFinite))throw new Error('Missing farming counters');
    const loadout={
      items:h.items.filter((i:any)=>i.slot<=5||i.slot===15||i.slot===16).map((i:any)=>({slot:i.slot,name:i.name})),
      abilities:h.abilities.filter((a:any)=>a.name?.startsWith('luna_')||(a.level>0&&a.name?.startsWith('special_bonus')))
        .map((a:any)=>({slot:a.slot,name:a.name,level:a.level})),
    };
    const signature=JSON.stringify(loadout);
    if(!signatures.has(signature)){signatures.set(signature,loadouts.length);loadouts.push(loadout);}
    const counts=new Map<string,number>();
    for(const u of f.units){const camp=campOf(u);if(camp&&u.health>0)counts.set(camp as string,(counts.get(camp as string)||0)+1);}
    return {time:round(f.gameTime),x:round(h.position.x),y:round(h.position.y),alive:h.lifeState===0&&h.health>0,
      level:h.level,hp:h.health,mana:round(h.mana),gold:gold as FarmFrame['gold'],
      lastHits:h.economy.m_iLastHitCount,stacks:h.economy.m_iCampsStacked,loadout:signatures.get(signature)!,
      campCounts:reference.camps.map((c:any)=>counts.get(c.id)||0)};
  });
  // Do not locate a kill by nearest camp or by Luna's current position.
  const deathsByTickName=new Map<string,any[]>();
  for(const c of facts.creatures)if(c.diedTick!==null){
    const key=`${c.diedTick}:${c.unitName}`;deathsByTickName.set(key,[...(deathsByTickName.get(key)||[]),c]);
  }
  const kills=facts.lunaCombatLog.filter((e:any)=>e.log_type==='DEATH'&&e.attacker_name==='npc_dota_hero_luna'&&
    (/^npc_dota_neutral_/.test(e.target_name)||/^npc_dota_creep_(goodguys|badguys)_/.test(e.target_name)))
    .map((e:any)=>{
      const matches=deathsByTickName.get(`${e.tick}:${e.target_name}`)||[];
      const campIds=[...new Set(matches.map(campOf))].filter(Boolean);
      const campId=campIds.length===1&&matches.every(c=>campOf(c))?campIds[0]:null;
      const pos=matches.length===1?matches[0].deathPosition:null;
      return {time:round(gameTimeAt(facts.clock,e.tick)),tick:e.tick,kind:e.target_name.startsWith('npc_dota_neutral_')?'neutral':'lane',
        target:e.target_name,campId,x:Number.isFinite(pos?.x)?round(pos.x):null,y:Number.isFinite(pos?.y)?round(pos.y):null,
        attribution:campId?'exact-tick-name-single-camp':matches.length===1?'exact-tick-name-unit':'unresolved',
        illusion:e.attacker_is_illusion===true};
    }).filter((e:any)=>e.time>=0&&e.time<=facts.durationSeconds).sort((a:any,b:any)=>a.tick-b.tick);
  const casts=facts.lunaCombatLog.filter((e:any)=>['ITEM','ABILITY'].includes(e.log_type)&&e.attacker_name==='npc_dota_hero_luna')
    .map((e:any)=>({time:round(gameTimeAt(facts.clock,e.tick)),type:e.log_type,name:e.inflictor_name||e.value_name,illusion:e.attacker_is_illusion===true}));
  return {
    ...source,accountId:luna.account_id,team:luna.player_slot<128?'radiant':'dire',
    won:(luna.player_slot<128)===api.radiant_win,startTime:api.start_time,startIso:new Date(api.start_time*1000).toISOString(),
    duration:round(facts.durationSeconds),gameBuild:facts.gameBuild,complete:true,
    validation:{camps:campValidation.rows.length,towers:towerValidation.count,lastHitParity:parity.every(Boolean),
      maxCampError:campValidation.maxCenterError,maxBoxError:campValidation.maxBoxError,
      unresolvedNeutralKills:kills.filter((e:any)=>e.kind==='neutral'&&!e.campId).length},
    trace:recordedTrace(facts.heroPath),frames,loadouts,kills,casts,
    windows:[[300,600],[600,900],[900,1200],[600,1200]]
      .filter(([from])=>from<frames.at(-1)!.time).map(([from,to])=>farmingWindow(frames,from,to)),
  };
}
