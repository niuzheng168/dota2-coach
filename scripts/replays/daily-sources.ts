import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { setTimeout as sleep } from 'node:timers/promises';
import { currentPatch, d2ptCandidates, d2ptLineups, eligibleTime, heroKeys, HEROES, majorRelease, playerCandidates, proCandidates, pubCandidates, officialTier, TOP_RANK, type Candidate, type Patch, type Hero } from '../../src/replays/daily-study.ts';
import { replayArea } from '../../src/replays/storage.ts';
import { heroDir,readJson,writeBytes,writeJson } from '../../src/replays/daily-memory.ts';

export const valveList='https://www.dota2.com/datafeed/patchnoteslist?language=english';
const od='https://api.opendota.com/api';
const next=new Map<string,number>(), denied=new Set<string>(), outages=new Map<string,number>();
export const sha=(b:Buffer|string)=>createHash('sha256').update(b).digest('hex');
export async function publicJson(url:string,limit=8*1024**2) {
  const host=new URL(url).hostname;
  if(denied.has(host))throw Error(`Public service ${host} refused this run; no bypass`);
  if(host==='api.opendota.com' && (outages.get(host)??0)>=3)throw Error('OpenDota temporarily unavailable this run; will retry later');
  const due=Math.max(Date.now(),next.get(host)??0);
  next.set(host,due+(host==='dota2protracker.com'?2500:1300));
  if(due>Date.now())await sleep(due-Date.now());
  let res:Response;
  try{res=await fetch(url,{redirect:'error',signal:AbortSignal.timeout(22_000),headers:{'User-Agent':'DotaCoach offline public replay study','Accept':'application/json'}});}
  catch(e){outages.set(host,(outages.get(host)??0)+1);throw e;}
  if(!res.ok){
    if(res.status===401||res.status===403||res.status===429)denied.add(host);
    if(res.status>=500)outages.set(host,(outages.get(host)??0)+1);
    await res.body?.cancel();throw Error(`${host} HTTP ${res.status}`);
  }
  outages.delete(host);
  if(!res.body || Number(res.headers.get('content-length'))>limit){await res.body?.cancel();throw Error('Oversized/empty public response');}
  const chunks:Buffer[]=[];let size=0;
  for await(const part of res.body){size+=part.length;if(size>limit)throw Error('Oversized public response');chunks.push(Buffer.from(part));}
  const bytes=Buffer.concat(chunks);
  try{return {json:JSON.parse(bytes.toString('utf8')),bytes};}catch{throw Error(`Invalid public JSON: ${host}`);}
}
export async function freshPatch(){const {json,bytes}=await publicJson(valveList);return {...currentPatch(json,Date.now()/1000),source:valveList,
  checkedAt:new Date().toISOString(),sha256:sha(bytes)};}
export const archiveDirs=(id:number,root:string)=>[path.join(root,'raw',String(id)),replayArea('farm742'),replayArea('farm742-more'),replayArea()];
export const cachedArchive=(id:number,root:string)=>archiveDirs(id,root).find(d=>
  ['archive','acquisition.json','opendota.json'].every(x=>fs.existsSync(path.join(d,`${id}.${x}`))));
export async function matchMetadata(id:number,root:string){
  const existing=cachedArchive(id,root), url=`${od}/matches/${id}`;
  const bytes=existing?fs.readFileSync(path.join(existing,`${id}.opendota.json`)):(await publicJson(url)).bytes;
  return {api:JSON.parse(bytes.toString('utf8')),bytes,existing};
}
const rankCache=new Map<number,any>();
export async function ranking(account:number){
  if(!rankCache.has(account))rankCache.set(account,(await publicJson(`${od}/players/${account}`)).json);
  return rankCache.get(account);
}
export async function discover(patch:Patch, root:string, run:string, report:(msg:string)=>void) {
  const all:Record<Hero,Candidate[]>={luna:[],troll_warlord:[],sven:[]}, sources:any[]=[], now=Date.now()/1000;
  const capture=async(label:string,get:()=>Promise<{json:any;bytes:Buffer}>,use:(j:any)=>void)=>{
    try{const {json,bytes}=await get();use(json);const digest=sha(bytes);
      // Save the qualifying source as evidence, deduplicated across pages/days. Never send the catalog to the model.
      const file=path.join(root,'catalogs',digest+'.json');
      if(!fs.existsSync(file))writeBytes(file,bytes,true);
      sources.push({label,sha256:digest,ok:true,at:new Date().toISOString()});}
    catch(e){const error=(e as Error).message;sources.push({label,ok:false,error});report(`${label}: ${error}`);}
  };
  function catalog(j:any,hero:Hero,source:string){
    all[hero].push(...d2ptCandidates(j,hero,patch,now,source));
    for(const h of heroKeys)all[h].push(...d2ptLineups(j,h,patch,now,source));
  }
  const constants='https://raw.githubusercontent.com/odota/dotaconstants/master/build/patch.json';
  const majorFile=path.join(root,'catalogs','opendota-major-patches.json');
  let odRelease:Patch['odRelease'];
  await capture(constants,()=>publicJson(constants),j=>{odRelease=majorRelease(j,patch);
    if(odRelease)writeJson(majorFile,j);});
  if(!odRelease&&fs.existsSync(majorFile))odRelease=majorRelease(readJson(majorFile),patch);
  for(const hero of heroKeys){
    const id=HEROES[hero], url=`https://dota2protracker.com/api/hero-matches?hero_id=${id}&position=pos+1&sort_by=mmr`;
    await capture(url,()=>publicJson(url),j=>catalog(j,hero,url));
    // Previously captured public match directories only supply IDs/qualification, never inferred replay facts.
    for(const area of ['farm742-more','farm742'] as const) {
      const f=path.join(replayArea(area),`catalog-${id}.json`);if(!fs.existsSync(f))continue;
      await capture(`cached ${area}/catalog-${id}.json`,async()=>{const bytes=fs.readFileSync(f);return {bytes,json:JSON.parse(bytes.toString())};},
        j=>catalog(j,hero,`cached:${area}/catalog-${id}.json`));
    }
  }
  const tiers=new Map<number,string>();
  await capture(`${od}/leagues`,()=>publicJson(`${od}/leagues`),j=>{
    if(!Array.isArray(j))throw Error('Invalid league list');
    for(const l of j)if(Number.isSafeInteger(l.leagueid)&&typeof l.tier==='string')tiers.set(l.leagueid,l.tier);
  });
  for(const hero of heroKeys){
    const url=`${od}/heroes/${HEROES[hero]}/matches`;
    await capture(url,()=>publicJson(url),j=>all[hero].push(...proCandidates(j,hero,patch,now,url)
      .filter(c=>!tiers.has(c.league!)||officialTier(tiers.get(c.league!)))));
  }
  // Immortal-average is only a lead: require a separately verified top-200 focal player before acquisition.
  let cursor='';
  for(let page=0;page<3;page++){
    const url=`${od}/publicMatches?min_rank=80${cursor}`;let last=0;
    await capture(url,()=>publicJson(url),j=>{for(const h of heroKeys)all[h].push(...pubCandidates(j,h,patch,now,url));
      last=j.at(-1)?.match_id;});
    if(!Number.isSafeInteger(last)||last<=0||cursor===`&less_than_match_id=${last}`)break;cursor=`&less_than_match_id=${last}`;
  }
  // Look at hero specialists' recent ranked games, not just the incidental picks in three public pages.
  // Hero score/Immortal badge alone is never sufficient: query the actual regional rank and recheck at ingestion.
  for(const hero of heroKeys){
    const url=`${od}/rankings?hero_id=${HEROES[hero]}`,seeds:number[]=[];
    await capture(url,()=>publicJson(url),j=>{
      if(!Array.isArray(j?.rankings))throw Error('No hero specialist list');
      for(const p of j.rankings)if(Number.isSafeInteger(p.account_id)&&p.account_id>0&&!seeds.includes(p.account_id))seeds.push(p.account_id);
    });
    let used=0;
    for(const account of seeds.slice(0,12)){
      let p;try{p=await ranking(account);}catch{break;}
      if(p?.profile?.account_id!==account||!Number.isInteger(p.leaderboard_rank)||p.leaderboard_rank<1||p.leaderboard_rank>TOP_RANK)continue;
      const full=`${od}/players/${account}/matches?hero_id=${HEROES[hero]}&lobby_type=7&date=7&limit=30`;
      await capture(full,()=>publicJson(full),j=>all[hero].push(...playerCandidates(j,account,hero,patch,now,full)));
      if(++used>=6)break;
    }
  }
  // Same-patch recent catalogs are reusable on a transient public-service outage. Never load model output or old patches as input.
  const days=path.join(root,'runs');
  for(const d of fs.readdirSync(days).filter(n=>/^\d{4}-\d{2}-\d{2}$/.test(n)).sort().slice(-8)){
    for(const x of fs.readdirSync(path.join(days,d),{withFileTypes:true})){
      if(!x.isDirectory()||!/^[a-f0-9-]{36}$/.test(x.name))continue;
      const file=path.join(days,d,x.name,'catalog.json');if(!fs.existsSync(file))continue;
      const prev=readJson(file);if(prev.patch?.name!==patch.name||prev.patch.from!==patch.from)continue;
      for(const h of heroKeys)for(const c of prev.candidates?.[h]??[]){
        if(c.hero===h&&Number.isSafeInteger(c.id)&&c.id>0&&eligibleTime(c.start,patch,now)&&
          ['ranked','league','rank-probe'].includes(c.kind))
          all[h].push({...c,originCatalog:c.originCatalog??path.relative(root,file)});
      }
    }
  }
  for(const hero of heroKeys){
    const hd=heroDir(root,patch.name,hero),pending=path.join(hd,'verified');
    if(fs.existsSync(pending))for(const name of fs.readdirSync(pending).filter(n=>/^[1-9]\d{0,15}\.json$/.test(n))){
      if(fs.existsSync(path.join(hd,'matches',name)))continue;
      const v=readJson(path.join(pending,name)),q=v.qualification;
      if(v.patch===patch.name&&q?.hero===hero&&q.id===v.matchId&&eligibleTime(q.start,patch,now))
        all[hero].push({...q,originCatalog:q.originCatalog??q.catalog});
    }
    const weight=(c:Candidate)=>c.kind==='league'?0:c.kind==='ranked'?1:2;
    all[hero].sort((a,b)=>weight(a)-weight(b)||(b.mmr??0)-(a.mmr??0)||b.start-a.start||b.id-a.id);
    const seen=new Set<number>();all[hero]=all[hero].filter(c=>!seen.has(c.id)&&!!seen.add(c.id));
    const ready=new Set(all[hero].filter(c=>!!cachedArchive(c.id,root)).map(c=>c.id));
    all[hero].sort((a,b)=>Number(ready.has(b.id))-Number(ready.has(a.id))); // still requires every eligibility check
  }
  writeJson(path.join(run,'catalog.json'),{patch,odRelease,discoveredAt:new Date().toISOString(),sources,candidates:all});
  return {all,tiers,odRelease};
}
