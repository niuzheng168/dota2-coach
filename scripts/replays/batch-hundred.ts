/** Finite, resumable two-worker acquisition. Failures are recorded, never counted. */
import { replayArea, replayChildEnv } from '../../src/replays/storage.ts';
import fs from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { downloadReplay } from './download.ts';
const root=replayArea(),catalog=JSON.parse(await fs.readFile(`${root}/hundred-candidates.json`,'utf8'));
const exists=(p:string)=>fs.stat(p).then(()=>true,()=>false);
const run=(exe:string,args:string[])=>new Promise<void>((resolve,reject)=>{
  const p=spawn(exe,args,{windowsHide:true,env:replayChildEnv(root),stdio:['ignore','pipe','pipe']});let output='';
  p.stdout.on('data',b=>{output=(output+b).slice(-5000);});p.stderr.on('data',b=>{output=(output+b).slice(-5000);});
  const timer=setTimeout(()=>p.kill(),600000);
  p.on('error',reject);p.on('close',code=>{clearTimeout(timer);code===0?resolve():reject(new Error(`Child exit ${code}: ${output.slice(-1500)}`));});
});
const python='.tmp/replay-tools/Scripts/python.exe';
const stateFile=`${root}/hundred-progress.json`;
const prior=await exists(stateFile)?JSON.parse(await fs.readFile(stateFile,'utf8')):null;
const state:any=prior??{version:1,startedAt:new Date().toISOString(),target:100,completed:[],failures:[],active:[]};
// Rebuild active list after an interrupted process; preserve successes/failures.
state.active=[];
const terminal=new Set<number>([...state.completed.map((r:any)=>r.matchId),...state.failures.map((r:any)=>r.matchId)]);
const queue=catalog.matches.filter((r:any)=>!terminal.has(r.matchId));
let writeChain=Promise.resolve();
const save=()=>{
  const text=JSON.stringify({...state,updatedAt:new Date().toISOString()},null,2)+'\n';
  writeChain=writeChain.then(async()=>{await fs.writeFile(stateFile+'.next',text);await fs.rename(stateFile+'.next',stateFile);});return writeChain;
};
async function worker(){
  while(queue.length&&state.completed.length+state.active.length<100){
    const source=queue.shift(),id=source.matchId;
    state.active.push(id);await save();
    let stage='metadata';
    try{
      const reused=await exists(`${root}/${id}.facts.json`);
      let bytes:Buffer;
      if(await exists(`${root}/${id}.opendota.json`))bytes=await fs.readFile(`${root}/${id}.opendota.json`);
      else {
        // Bounded backoff for transient failures, without retrying bad evidence.
        let received:Buffer|undefined;
        for(let attempt=0;attempt<3&&!received;attempt++){
          await new Promise(r=>setTimeout(r,attempt?10000*attempt:2200));
          try{
            const r=await fetch(`https://api.opendota.com/api/matches/${id}`,{redirect:'error',signal:AbortSignal.timeout(60000)});
            if(!r.ok){
              const retryAfter=Number(r.headers.get('retry-after'));
              if(r.status===429)await new Promise(resolve=>setTimeout(resolve,Math.min(300000,Math.max(60000,(retryAfter||60)*1000))));
              throw new Error(`OpenDota HTTP ${r.status}`);
            }
            received=Buffer.from(await r.arrayBuffer());
          }catch(error){if(attempt===2)throw error;}
        }
        bytes=received!;
        if(bytes.length>8*1024*1024)throw new Error('Metadata too large');
      }
      const api=JSON.parse(bytes.toString()),patch=catalog.patchEvidence;
      if(api.match_id!==id||!api.players?.some((p:any)=>p.hero_id===48))throw new Error('Wrong match/hero');
      if(api.start_time<patch.from||(patch.until!==null&&api.start_time>=patch.until))throw new Error('Outside 7.41f patch window');
      if(api.duration<600)throw new Error('Shorter than 10 minutes; not a complete farming study window');
      stage='archive';
      if(!await exists(`${root}/${id}.acquisition.json`))await downloadReplay(id,root,bytes);
      stage='decompress';
      if(!await exists(`${root}/${id}.dem`))await run(python,['scripts/replays/decompress.py',String(id),'--root',root]);
      stage='parse';
      if(!reused)await run(python,['scripts/replays/extract.py',String(id),'--root',root]);
      stage='validate-and-pack';
      await run(process.execPath,['--max-old-space-size=4096','scripts/replays/pack-study.ts',String(id)]);
      const evidence=JSON.parse(await fs.readFile(`${root}/study/${id}.evidence.json`,'utf8'));
      state.completed.push({...source,reused,build:evidence.build,duration:evidence.duration,team:evidence.team,won:evidence.won,validation:evidence.validation});
      console.log(JSON.stringify({completed:state.completed.length,id,player:source.player,reused}));
    }catch(error){
      const reason=error instanceof Error?error.message:'Unknown failure';
      state.failures.push({matchId:id,player:source.player,stage,reason});
      console.log(JSON.stringify({failed:id,stage,reason}));
    }finally{state.active=state.active.filter((x:number)=>x!==id);await save();}
  }
}
await Promise.all([worker(),worker()]);
// A worker can have stopped while the other held the last reserved slot and failed.
while(state.completed.length<100&&queue.length)await worker();
state.finishedAt=new Date().toISOString();state.complete=state.completed.length===100;await save();
if(!state.complete)process.exitCode=1;
console.log(JSON.stringify({complete:state.complete,count:state.completed.length,failed:state.failures.length}));
