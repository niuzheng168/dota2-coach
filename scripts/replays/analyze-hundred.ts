/** Descriptive measurements, not rules for choosing a farming route. */
import { replayArea } from '../../src/replays/storage.ts';
import fs from 'node:fs';
import { farmingWindow } from '../../src/replays/farming-routes.ts';
const replayDir=replayArea();
const read=(f:string)=>JSON.parse(fs.readFileSync(f,'utf8'));
const progress=read(`${replayDir}/hundred-progress.json`);
if(progress.completed.length!==100||!progress.complete)throw new Error('100 validated cases required');
const quantile=(values:number[],q:number)=>{
  const a=values.filter(Number.isFinite).toSorted((a,b)=>a-b);if(!a.length)return null;
  const p=(a.length-1)*q;return a[Math.floor(p)]+(a[Math.ceil(p)]-a[Math.floor(p)])*(p-Math.floor(p));
};
const rows=progress.completed.map((source:any)=>{
  const m=read(`${replayDir}/study/${source.matchId}.route.json`),e=read(`${replayDir}/study/${source.matchId}.evidence.json`);
  const itemAt=(name:string)=>m.frames.find((f:any)=>m.loadouts[f.loadout].items.some((i:any)=>i.name===name))?.time??null;
  const periods=[[300,600],[600,900],[900,1200],[1200,1500],[600,1200]].filter(([,to])=>m.duration>=to).map(([from,to])=>{
    const window=farmingWindow(m.frames,from,to),frames=m.frames.filter((f:any)=>f.time>=window.from&&f.time<window.to&&f.alive);
    const kills=m.kills.filter((k:any)=>k.time>=window.from&&k.time<window.to);
    const camps=[...new Set(kills.filter((k:any)=>k.kind==='neutral'&&!k.illusion&&k.campId).map((k:any)=>k.campId))];
    return {...window,requested:[from,to],bodyAliveSamples:frames.length,bodyNorthSamples:frames.filter((f:any)=>f.y>=0).length,
      camps,neutralKillEvents:kills.filter((k:any)=>k.kind==='neutral').length,
      unresolvedNeutralKillEvents:kills.filter((k:any)=>k.kind==='neutral'&&!k.campId).length,
      laneIllusionKillEvents:kills.filter((k:any)=>k.kind==='lane'&&k.illusion).length,
      laneBodyKillEvents:kills.filter((k:any)=>k.kind==='lane'&&!k.illusion).length};
  });
  const ten=e.worldSnapshots.find((s:any)=>s.time>=600),twenty=e.worldSnapshots.find((s:any)=>s.time>=1200);
  return {matchId:m.matchId,player:m.player,accountId:m.accountId,team:m.team,won:m.won,
    providerPro:m.providerPro,tournament:m.tournament,matchMmr:m.matchMmr,duration:m.duration,
    maskAt:itemAt('item_mask_of_madness'),mantaAt:itemAt('item_manta'),bkbAt:itemAt('item_black_king_bar'),
    networthDifference10:ten?.alliedMinusEnemyNetworth??null,networthDifference20:m.duration>=1200?twenty?.alliedMinusEnemyNetworth??null:null,
    deaths:e.transitions.filter((t:any)=>t.event==='death').map((t:any)=>t.time),periods};
});
const windows=rows.map((r:any)=>({row:r,window:r.periods.find((p:any)=>p.requested[0]===600&&p.requested[1]===1200)})).filter((r:any)=>r.window);
const byTeam=['radiant','dire'].map(team=>{
  const group=windows.filter((r:any)=>r.row.team===team);
  return {team,n:group.length,aliveSamples:group.reduce((n:number,r:any)=>n+r.window.bodyAliveSamples,0),
    northFraction:group.reduce((n:number,r:any)=>n+r.window.bodyNorthSamples,0)/group.reduce((n:number,r:any)=>n+r.window.bodyAliveSamples,0)};
});
const distribution={fullWindowN:windows.length,
  farmPerMinute:{q25:quantile(windows.map((r:any)=>r.window.farmPerMinute),.25),median:quantile(windows.map((r:any)=>r.window.farmPerMinute),.5),q75:quantile(windows.map((r:any)=>r.window.farmPerMinute),.75)},
  individualLaneFractionMedian:quantile(windows.map((r:any)=>r.window.laneGold/r.window.farmGold),.5),
  bothIncomeSources:windows.filter((r:any)=>r.window.laneGold>0&&r.window.neutralGold>0).length,
  ownStackCounterIncreased:windows.filter((r:any)=>r.window.stacks>0).length,
  illusionLaneKillsObserved:windows.filter((r:any)=>r.window.laneIllusionKillEvents>0).length,
  mantaObtained:{n:rows.filter((r:any)=>r.mantaAt!==null).length,medianSeconds:quantile(rows.filter((r:any)=>r.mantaAt!==null).map((r:any)=>r.mantaAt),.5)},
  maskObtained:rows.filter((r:any)=>r.maskAt!==null).length,
  byTeam,
  belowZeroTeamNetworth10:windows.filter((r:any)=>r.row.networthDifference10<0).length,
  deathBetween10And20:windows.filter((r:any)=>r.row.deaths.some((t:number)=>t>=600&&t<1200)).length};
fs.writeFileSync('data/luna-hundred-analysis.json',JSON.stringify({version:1,
  informationScope:'omniscient-offline-replay-only',methods:[
    'Descriptive aggregation only; no route policy, causal effect, maximum-GPM claim or fine-tuning.',
    'Only full covered periods are compared. Income is recorded lane/neutral counter change, including controlled units.',
    'North fraction counts alive one-second body samples with y >= 0, not claimed territory, safety or farm time.',
    'Item arrival means first observed inventory possession, not purchase time. Censoring excludes never-obtained items from its median.',
    'Repeated player accounts, pro/pubs mixture and source availability prevent a representative population estimate.',
  ],distribution,rows},null,2)+'\n');
console.log(JSON.stringify(distribution,null,2));
