import { FRAME_SECONDS, MAX_TICKS } from './data.ts';
import { SimulationWorld, parseCommand } from './world.ts';
import type { SimCommand, WorldState } from './world.ts';

export class SimulationTimeline {
  world:SimulationWorld;
  maxVisitedTick=0;
  revision=0;
  branch=0;
  private checkpoints=new Map<number,WorldState>();
  private commands:{tick:number;command:SimCommand}[]=[];
  constructor(seed=741){this.world=new SimulationWorld(seed);this.save();}
  private save(){this.checkpoints.set(this.world.tick,structuredClone(this.world.state));}
  private step(){
    this.world.advanceOne();
    for(const entry of this.commands)if(entry.tick===this.world.tick)this.world.execute(entry.command);
    if(this.world.tick%25===0)this.save();
    this.maxVisitedTick=Math.max(this.maxVisitedTick,this.world.tick);
  }
  seek(tick:number){
    if(!Number.isInteger(tick)||tick<0||tick>MAX_TICKS)throw new Error(`时间轴帧必须为 0–${MAX_TICKS} 整数`);
    if(tick<this.world.tick){
      const nearest=Math.max(...[...this.checkpoints.keys()].filter(t=>t<=tick));
      this.world.state=structuredClone(this.checkpoints.get(nearest)!);
    }
    while(this.world.tick<tick)this.step();
    this.revision++;return this.snapshot();
  }
  advance(ticks:number){
    if(!Number.isInteger(ticks)||ticks<1||ticks>50)throw new Error('单次推进须为 1–50 帧');
    return this.seek(Math.min(MAX_TICKS,this.world.tick+ticks));
  }
  command(value:unknown){
    if(this.world.tick>=MAX_TICKS)throw new Error('已到时间轴末尾；请回退或重置');
    if(this.commands.length>=1000)throw new Error('行动记录已达到上限，请重置场景');
    const command=parseCommand(value),before=structuredClone(this.world.state);
    try{this.world.execute(command);}catch(error){this.world.state=before;throw error;}
    const branched=this.maxVisitedTick>this.world.tick;
    if(branched){
      for(const t of this.checkpoints.keys())if(t>this.world.tick)this.checkpoints.delete(t);
      this.commands=this.commands.filter(e=>e.tick<=this.world.tick);this.maxVisitedTick=this.world.tick;this.branch++;
    }
    this.commands.push({tick:this.world.tick,command:structuredClone(command)});this.save();this.revision++;
    return {...this.snapshot(),branched};
  }
  snapshot(){return {...this.world.snapshot(),timeline:{maxTick:MAX_TICKS,maxTime:MAX_TICKS*FRAME_SECONDS,
    visitedTick:this.maxVisitedTick,revision:this.revision,branch:this.branch,commands:this.commands.length}};}
}
