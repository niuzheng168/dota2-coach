import { COMPOSITIONS } from '../tactics/combat-data.ts';
import { insideCamp } from '../tactics/map-data.ts';
import type { Camp, CampKind } from '../tactics/types.ts';
import { DEFINITIONS, FRAME_SECONDS, NEUTRAL_SPAWN_TICKS, REFERENCE } from './data.ts';
import type { SimUnit } from './world.ts';

type CampUnit=Pick<SimUnit,'id'|'kind'|'hp'|'campId'|'x'|'y'|'gold'>;
export interface SpawnGold {
  min:number;max:number;mean:number;
  basis:'simulator-uniform-packs';
}
export interface CampStatus {
  id:string;hasNeutrals:boolean;aliveCount:number;outsideCount:number;
  spawnBlocked:boolean;blockerIds:string[];
  nextSpawnTick:number;nextSpawnTime:number;secondsUntilSpawn:number;
  currentGold:number;nextSpawnGold:SpawnGold;
}

// Shared with spawning: all living units in the actual rectangle block it,
// not just the neutrals originally assigned to this camp. Corpses do not.
export function campBlockers(camp:Camp,units:readonly CampUnit[]):CampUnit[] {
  return units.filter(u=>u.hp>0&&insideCamp(u,camp));
}

const estimates=new Map<CampKind,SpawnGold>();
function spawnGold(kind:CampKind):SpawnGold {
  if(!estimates.has(kind)){
    const packs=COMPOSITIONS.filter(p=>p.kind===kind).map(pack=>{
      let min=0,max=0;
      for(const [name,count] of Object.entries(pack.units)){
        const spec=DEFINITIONS.units[name];
        if(!spec)throw new Error(`缺少营地金币定义 ${name}`);
        min+=count*spec.goldMin;max+=count*spec.goldMax;
      }
      return {min,max,mean:(min+max)/2};
    });
    if(!packs.length)throw new Error(`缺少营地组合 ${kind}`);
    estimates.set(kind,{
      min:Math.min(...packs.map(p=>p.min)),max:Math.max(...packs.map(p=>p.max)),
      mean:packs.reduce((sum,p)=>sum+p.mean,0)/packs.length,basis:'simulator-uniform-packs',
    });
  }
  return {...estimates.get(kind)!};
}

// Derived from the viewed frame, never from wall time or a future RNG draw.
// Pulling creeps outside the box does not erase their ownership or their bounty.
export function campStatuses(tick:number,units:readonly CampUnit[]):CampStatus[] {
  const nextSpawnTick=(Math.floor(tick/NEUTRAL_SPAWN_TICKS)+1)*NEUTRAL_SPAWN_TICKS;
  return (REFERENCE.camps as Camp[]).map(camp=>{
    const alive=units.filter(u=>u.kind==='neutral'&&u.campId===camp.id&&u.hp>0);
    const blockerIds=campBlockers(camp,units).map(u=>u.id);
    return {
      id:camp.id,hasNeutrals:alive.length>0,aliveCount:alive.length,
      outsideCount:alive.filter(u=>!insideCamp(u,camp)).length,
      spawnBlocked:blockerIds.length>0,blockerIds,
      nextSpawnTick,nextSpawnTime:nextSpawnTick*FRAME_SECONDS,
      secondsUntilSpawn:Number(((nextSpawnTick-tick)*FRAME_SECONDS).toFixed(1)),
      currentGold:alive.reduce((sum,u)=>sum+u.gold,0),nextSpawnGold:spawnGold(camp.kind),
    };
  });
}
