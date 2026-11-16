import fs from 'node:fs';
const read=(p:string)=>JSON.parse(fs.readFileSync(p,'utf8'));
const file='.tmp/replays/hundred-progress.json',progress=read(file);
if(!progress.complete||progress.active.length||progress.completed.length!==100)throw new Error('Wait for finite acquisition to finish');
const recoveries=read('.tmp/replays/hundred-recovered.json').filter((r:any)=>r.success);
const catalog=read('.tmp/replays/hundred-candidates.json');
const priority=new Map<number,number>(catalog.matches.map((m:any,i:number)=>[m.matchId,i]));
progress.superseded??=[];
for(const recovery of recoveries){
  if(progress.completed.some((m:any)=>m.matchId===recovery.matchId))continue;
  const m=read(`.tmp/replays/study/${recovery.matchId}.route.json`);
  if(!m.complete||!m.validation.lastHitParity||!priority.has(m.matchId))throw new Error('Invalid recovered source');
  const replace=progress.completed.toSorted((a:any,b:any)=>priority.get(b.matchId)!-priority.get(a.matchId)!)[0];
  if(priority.get(replace.matchId)!<priority.get(m.matchId)!)continue;
  progress.completed=progress.completed.filter((r:any)=>r.matchId!==replace.matchId);
  progress.superseded.push({...replace,reason:'Replaced by recovered higher-priority source (pro first, then displayed MMR)',replacedBy:recovery.matchId});
  progress.completed.push({...recovery.source,reused:false,recovered:true,build:m.gameBuild,duration:m.duration,
    team:m.team,won:m.won,validation:m.validation});
}
progress.reconciledAt=new Date().toISOString();
fs.writeFileSync(file,JSON.stringify(progress,null,2)+'\n');
console.log(JSON.stringify({count:progress.completed.length,pro:progress.completed.filter((m:any)=>m.providerPro).length,
  recovered:progress.completed.filter((m:any)=>m.recovered).length,superseded:progress.superseded.length}));
