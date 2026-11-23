/** Retry failed pro-source acquisitions separately; never mutate a running journal. */
import { replayArea, replayChildEnv } from '../../src/replays/storage.ts';
import fs from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { downloadReplay } from './download.ts';
const root=replayArea(),read=async(p:string)=>JSON.parse(await fs.readFile(p,'utf8'));
const progress=await read(`${root}/hundred-progress.json`),catalog=await read(`${root}/hundred-candidates.json`);
const exists=(p:string)=>fs.stat(p).then(()=>true,()=>false);
const prior=await exists(`${root}/hundred-recovered.json`)?await read(`${root}/hundred-recovered.json`):[];
const priority=new Map<number,number>(catalog.matches.map((m:any,i:number)=>[m.matchId,i]));
const available=(await fs.readdir(`${root}/study`)).filter(f=>/^\d+\.route\.json$/.test(f))
  .map(f=>Number(f.split('.')[0])).filter(id=>priority.has(id)).sort((a,b)=>priority.get(a)!-priority.get(b)!);
const cutoff=available.length>=100?priority.get(available[99])!:Infinity;
const run=(exe:string,args:string[])=>new Promise<void>((resolve,reject)=>{
  const child=spawn(exe,args,{windowsHide:true,env:replayChildEnv(root),stdio:'ignore'});
  child.on('error',reject);child.on('close',code=>code===0?resolve():reject(new Error(`Child failed ${code}`)));
});
const results=[];
for(const failure of progress.failures){
  const source=catalog.matches.find((m:any)=>m.matchId===failure.matchId);
  if(!source||(!source.providerPro&&!process.argv.includes('--all'))||!['metadata','archive','validate-and-pack'].includes(failure.stage)||
    !/fetch failed|timeout|HTTP 429|HTTP 5\d\d|Hash\/patch\/build mismatch/.test(failure.reason))continue;
  const id=source.matchId;
  const previous=prior.find((r:any)=>r.matchId===id);
  if(previous?.success||previous?.error==='Outside 7.41f patch window'){results.push(previous);continue;}
  if(priority.get(id)!>cutoff)continue;
  try{
    if(!await exists(`${root}/${id}.acquisition.json`)){
      let bytes:Buffer|undefined;
      for(let attempt=0;attempt<3&&!bytes;attempt++){
        try{
          const r=await fetch(`https://api.opendota.com/api/matches/${id}`,{signal:AbortSignal.timeout(60000),redirect:'error'});
          if(!r.ok)throw new Error(`HTTP ${r.status}`);bytes=Buffer.from(await r.arrayBuffer());
        }catch(e){if(attempt===2)throw e;await new Promise(r=>setTimeout(r,5000));}
      }
      const metadata=JSON.parse(bytes!.toString()),patch=catalog.patchEvidence;
      if(metadata.start_time<patch.from||(patch.until!==null&&metadata.start_time>=patch.until))
        throw new Error('Outside 7.41f patch window');
      await downloadReplay(id,root,bytes);
    }
    if(!await exists(`${root}/${id}.dem`))await run('.tmp/replay-tools/Scripts/python.exe',['scripts/replays/decompress.py',String(id),'--root',root]);
    if(!await exists(`${root}/${id}.facts.json`))await run('.tmp/replay-tools/Scripts/python.exe',['scripts/replays/extract.py',String(id),'--root',root]);
    await run(process.execPath,['scripts/replays/pack-study.ts',String(id)]);
    results.push({matchId:id,success:true,source});
  }catch(e){results.push({matchId:id,success:false,error:e instanceof Error?e.message:'Failed'});}
  await fs.writeFile(`${root}/hundred-recovered.json`,JSON.stringify(results,null,2)+'\n');
  console.log(JSON.stringify(results.at(-1)));
}
