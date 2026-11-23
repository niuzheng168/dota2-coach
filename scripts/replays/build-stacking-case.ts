import { replayArea } from '../../src/replays/storage.ts';
import fs from 'node:fs';
import {createHash} from 'node:crypto';
import {validateCampGeometry} from '../../src/replays/validation.ts';
const replayDir=replayArea();
const read=(f:string)=>JSON.parse(fs.readFileSync(f,'utf8'));
const id=9089477184,facts=read(`${replayDir}/${id}.facts.json`),detailBytes=fs.readFileSync(`${replayDir}/study/${id}.stacking-detail.json`);
const detail=JSON.parse(detailBytes.toString()),reference=read('data/dota2-map-reference.json');
if(detail.matchId!==id||detail.build!==facts.gameBuild||!detail.complete)throw new Error('Wrong detail replay');
const mapping=validateCampGeometry(facts,reference),by=new Map(mapping.rows.map((c:any)=>[c.spawnerIndex,c.id]));
const campOf=(u:any)=>by.get(u.spawnerHandle&0x3fff);
const inside=(u:any,c:any)=>u.position&&u.position.x>=c.bounds.min.x&&u.position.x<=c.bounds.max.x&&u.position.y>=c.bounds.min.y&&u.position.y<=c.bounds.max.y;
const events=[780,840].map(minute=>{
  const before=detail.frames.filter((f:any)=>f.time<minute).at(-1),after=detail.frames.find((f:any)=>f.time>=minute+.4);
  const camps=reference.camps.flatMap((c:any)=>{
    const old=before.units.filter((u:any)=>campOf(u)===c.id&&u.lifeState===0&&!u.waiting);
    const now=after.units.filter((u:any)=>campOf(u)===c.id&&u.lifeState===0&&!u.waiting);
    const ids=new Set(old.map((u:any)=>u.id)),added=now.filter((u:any)=>!ids.has(u.id));
    if(!old.length||!added.length)return [];
    const spawned=added.map((u:any)=>facts.creatures.find((e:any)=>e.id===u.id));
    if(spawned.some((u:any)=>!u||u.spawnedGameTime<minute||u.spawnedGameTime>after.time))
      throw new Error('Added units are not verified fresh spawns');
    return [{id:c.id,label:c.label,bounds:c.bounds,oldAlive:old.length,oldInside:old.filter((u:any)=>inside(u,c)).length,
      oldSurvivingAfter:now.filter((u:any)=>ids.has(u.id)).length,newAlive:now.length,
      freshSpawns:spawned.map((u:any)=>({id:u.id,unit:u.unitName,spawnedAt:u.spawnedGameTime})),
      oldPositions:old.map((u:any)=>({id:u.id,...u.position}))}];
  });
  const heroId=facts.heroPath[0].id,luna=before.units.find((u:any)=>u.id===heroId);
  if(!luna)throw new Error('Main Luna identity not found');
  return {minute,beforeTime:before.time,afterTime:after.time,luna:{id:heroId,position:luna.position},camps};
});
const beams=detail.combatLog.filter((e:any)=>e.log_type==='DAMAGE'&&e.inflictor_name==='luna_lucent_beam'&&e.time>=770&&e.time<=840)
  .map((e:any)=>({time:e.time,target:e.target_name,value:e.value,illusion:e.attacker_is_illusion}));
const output={version:1,matchId:id,build:facts.gameBuild,source:`https://dota2protracker.com/matches/${id}`,
  parser:facts.parser,demSha256:facts.demSha256,detailSha256:createHash('sha256').update(detailBytes).digest('hex'),
  informationScope:'omniscient-offline-replay-only',events,beams,
  observations:[
    '12:59.933 与 13:00.467 之间，evil_11、evil_20 的旧怪均在各自刷新框外，并各出现一组有真实 spawnedGameTime 的新怪；旧怪仍存活。',
    '13:59.933 与 14:00.467 之间，evil_4、evil_5 同样保留框外旧怪并刷出新组；Luna 自己的叠野计数在两个整分均 +2。',
    '12:57.133 对泥土傀儡、13:55.133 对萨特欺诈者有本体月光伤害事件。结合轨迹和实际刷新，是研究双拉衔接的强证据，但不伪造客户端输入指令。',
    '14:00 的 good_13 也出现新组；不能把全图同时发生的叠野都归因于 Luna。该营地不作为她的双拉证据。',
    '实体创建可能提前几十秒预热；本例只用 waitingToSpawn 变为 false 对应的 spawnedGameTime，不把 createdGameTime 当刷新时间。',
  ],
  limitations:['未提取仇恨目标/用户指令，不能严格还原每次拉动输入。','这是把野怪拉离刷新框叠野，不是已证实的拉兵线接野。','m_bSelectionRingVisible 不是玩家视野掩码，不能拿它放行实战信息。','不把本场施法秒数硬编码为所有营地的拉野规则。']};
fs.writeFileSync('data/luna-stacking-case.json',JSON.stringify(output,null,2)+'\n');
console.log(JSON.stringify({matchId:id,events:events.map(e=>({minute:e.minute,camps:e.camps.map((c:any)=>({id:c.id,old:c.oldAlive,new:c.freshSpawns.length,inside:c.oldInside}))})),beams},null,2));
