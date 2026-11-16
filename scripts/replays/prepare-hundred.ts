/** Import observed D2PT DOM rows, not undocumented browser/application state. */
import fs from 'node:fs/promises';
import { createHash } from 'node:crypto';
const sha=(b:Buffer|string)=>createHash('sha256').update(b).digest('hex');
const root='.tmp/replays';
const dom=JSON.parse(await fs.readFile(`${root}/d2pt-100-dom-catalog.json`,'utf8'));
const patchSource='https://www.dota2.com/datafeed/patchnoteslist?language=english';
const response=await fetch(patchSource,{signal:AbortSignal.timeout(30000)});
if(!response.ok)throw new Error('Official patch evidence unavailable');
const bytes=Buffer.from(await response.arrayBuffer()),patches=JSON.parse(bytes.toString()).patches;
const patch=patches.find((p:any)=>p.patch_number==='7.41f');
if(!patch)throw new Error('Requested patch not found');
const next=patches.filter((p:any)=>p.patch_timestamp>patch.patch_timestamp).sort((a:any,b:any)=>a.patch_timestamp-b.patch_timestamp)[0];
await fs.writeFile(`${root}/hundred-patches.json`,bytes);
const historyUrl='https://api.github.com/repos/SteamDatabase/GameTracking-Dota2/commits?path=game%2Fdota%2Fsteam.inf&per_page=100';
const hr=await fetch(historyUrl,{signal:AbortSignal.timeout(30000)});
if(!hr.ok)throw new Error('Build history unavailable');
const history=await hr.json() as any[],builds=[];
for(const c of history){
  const recordedAt=c.commit.committer.date;
  if(Date.parse(recordedAt)/1000<patch.patch_timestamp)continue;
  const source=`https://raw.githubusercontent.com/SteamDatabase/GameTracking-Dota2/${c.sha}/game/dota/steam.inf`;
  const r=await fetch(source,{signal:AbortSignal.timeout(30000)});
  if(!r.ok)throw new Error('Build evidence unavailable');
  const text=await r.text(),build=Number(text.match(/^ServerVersion=(\d+)/m)?.[1]);
  if(!Number.isSafeInteger(build))throw new Error('Malformed build evidence');
  builds.push({build,commit:c.sha,recordedAt,source,text,sha256:sha(text)});
}
const matches=dom.rows.map((row:any)=>{
  const player=row.links.find((a:any)=>a.href.startsWith('/player/'));
  const mmr=row.cells[1].trim();
  return {matchId:row.matchId,player:player?.text.trim()||'Unnamed D2PT player',
    playerSource:player?new URL(player.href,'https://dota2protracker.com').href:null,
    source:`https://dota2protracker.com/matches/${row.matchId}`,
    providerPro:row.providerPro,tournament:mmr==='PRO',
    matchMmr:/^\d{4,5}$/.test(mmr)?Number(mmr):null,rankLabel:/^\d{4,5}$/.test(mmr)?null:mmr,
    providerIdentity:'D2PT observed player label and Pros Only filter; not independent account ownership authentication'};
}).sort((a:any,b:any)=>Number(b.providerPro)-Number(a.providerPro)||(b.matchMmr??0)-(a.matchMmr??0)||b.matchId-a.matchId);
if(new Set(matches.map((m:any)=>m.matchId)).size!==matches.length)throw new Error('Duplicate source IDs');
const result={version:1,targetCount:100,capturedAt:dom.capturedAt,catalogSource:dom.source,
  filters:dom.filters,selection:'Pros Only candidates first, then displayed match-average MMR descending; no win or income filtering. Invalid/expired candidates excluded with an audit record.',
  patchEvidence:{source:patchSource,checkedAt:new Date().toISOString(),sha256:sha(bytes),patch:'7.41f',
    from:patch.patch_timestamp,until:next?.patch_timestamp??null,builds},matches};
await fs.writeFile(`${root}/hundred-candidates.json`,JSON.stringify(result,null,2)+'\n');
console.log(JSON.stringify({candidates:matches.length,pro:matches.filter((m:any)=>m.providerPro).length,builds:builds.map(b=>b.build),patchFrom:patch.patch_timestamp,until:next?.patch_timestamp??null}));
