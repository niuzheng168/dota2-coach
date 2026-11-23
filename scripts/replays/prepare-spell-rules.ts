/** Build-pinned Valve hero/item KV, never the website's latest stats. All raw bytes and snapshots live on the study volume. */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { pathToFileURL } from 'node:url';
import { kvObject,numbers,parseKeyValues,type KV } from '../lib/valve-kv.ts';
import { replayArea } from '../../src/replays/storage.ts';
import { writeBytes,writeJson } from '../../src/replays/daily-memory.ts';
import { defensiveItems,digestJson,loadRules,rulesFile,type Rules,type SpellRule,type NumericRule } from '../../src/replays/spell-rules.ts';

const hash=(b:Buffer)=>createHash('sha256').update(b).digest('hex');
const gitHash=(kind:'blob'|'tree',b:Buffer)=>createHash('sha1').update(`${kind} ${b.length}\0`).update(b).digest('hex');
const hex=(s:unknown):s is string=>typeof s==='string'&&/^[a-f0-9]{40}$/.test(s);
const sourcePath='game/dota/pak01_dir/scripts/npc',repo='SteamDatabase/GameTracking-Dota2';
const pins=()=>JSON.parse(fs.readFileSync(new URL('../../data/farming-742-expanded.json',import.meta.url),'utf8'));
export function spellRule(def:KV,slot:number|null):SpellRule{
  const vals:Record<string,NumericRule>={};
  const add=(k:string,v:KV|string)=>{
    if(!/^[A-Za-z][A-Za-z0-9_]{0,100}$/.test(k))return;
    const a=numbers(v),extra=Object.fromEntries(Object.entries(kvObject(v)).filter(([n,x])=>
      /^(special_bonus_|Requires(Scepter|Shard)|CalculateSpellDamageTooltip|DamageTypeTooltip)/i.test(n)&&typeof x==='string'&&x.length<=120));
    if(a.length||Object.keys(extra).length)vals[k]={base:a,...(Object.keys(extra).length&&{conditional:extra as Record<string,string>})};
  };
  for(const k of ['AbilityDamage','AbilityCooldown','AbilityManaCost','AbilityCastRange','AbilityCastPoint','AbilityDuration','AbilityChannelTime',
    'AbilityCharges','AbilityChargeRestoreTime','AbilityLevelRequirements'])if(def[k]!==undefined)add(k,def[k]);
  for(const [k,v] of Object.entries(kvObject(def.AbilityValues)))add(k,v);
  return {slot,damageType:typeof def.AbilityUnitDamageType==='string'?def.AbilityUnitDamageType:null,
    behavior:typeof def.AbilityBehavior==='string'?def.AbilityBehavior.split('|').map(x=>x.trim().replace('DOTA_ABILITY_BEHAVIOR_','')):[],
    gates:['IsGrantedByScepter','IsGrantedByShard','HasScepterUpgrade','HasShardUpgrade','Innate'].filter(k=>def[k]==='1'),values:vals};
}
async function get(url:string,max:number){
  const r=await fetch(url,{headers:{'User-Agent':'DotaCoach-verified-public-game-rules'},signal:AbortSignal.timeout(35_000)});
  assert(r.ok,`Public game-file source HTTP ${r.status}`);const b=Buffer.from(await r.arrayBuffer());assert(b.length<=max);return b;
}
async function cached(file:string,load:()=>Promise<Buffer>,check:(b:Buffer)=>boolean){
  if(fs.existsSync(file)){const b=fs.readFileSync(file);assert(check(b),'Cached Valve source digest mismatch');return b;}
  const b=await load();assert(check(b),'Valve source verification failed');writeBytes(file,b,true);return b;
}
export async function prepareRules(root:string,patch:string,build:number,requestedCommit?:string,localHeroes?:string){
  const existing=loadRules(root,patch,build);if(existing){assert(!requestedCommit||existing.commit===requestedCommit,'Conflicting build pin');return existing;}
  const known=pins();
  const pin=known.patch.version===patch?known.builds.find((x:any)=>x.build===build):undefined,commit=requestedCommit??pin?.commit;
  assert(hex(commit)&&(!pin||pin.commit===commit),'Provide an immutable --commit for this exact replay build');
  const cache=path.join(root,'spell-rules','sources'),raw=`https://raw.githubusercontent.com/${repo}/${commit}/`;
  const commitDir=path.join(cache,commit);
  const steam=await cached(path.join(commitDir,'steam.inf'),()=>get(raw+'game/dota/steam.inf',4096),b=>
    (!pin||hash(b)===pin.sha256)&&['ClientVersion','ServerVersion'].every(k=>b.toString().split(/\r?\n/).includes(`${k}=${build}`)));
  const listing=JSON.parse((await cached(path.join(commitDir,'npc-directory.json'),()=>get(
    `https://api.github.com/repos/${repo}/contents/${sourcePath}?ref=${commit}`,1024**2),b=>Array.isArray(JSON.parse(b.toString())))).toString());
  const treeId=listing.find((x:any)=>x.name==='heroes'&&x.type==='dir')?.sha,itemId=listing.find((x:any)=>x.name==='items.txt'&&x.type==='file')?.sha;
  assert(hex(treeId)&&hex(itemId),'Missing hero tree/item blob at pinned commit');
  const tree=JSON.parse((await cached(path.join(cache,`${treeId}.tree.json`),()=>get(
    `https://api.github.com/repos/${repo}/git/trees/${treeId}`,1024**2),b=>{
      const t=JSON.parse(b.toString());
      return t.sha===treeId&&t.truncated===false&&t.tree.every((x:any)=>x.type==='blob'&&x.mode==='100644'&&hex(x.sha)&&/^[\w.-]+$/.test(x.path))&&
        gitHash('tree',Buffer.concat(t.tree.flatMap((x:any)=>[Buffer.from(`100644 ${x.path}\0`),Buffer.from(x.sha,'hex')])))===treeId;
    })).toString()).tree;
  const blob=async(id:string,relative:string,local:string|undefined)=>cached(path.join(cache,'blobs',id+'.txt'),async()=>{
    if(local&&fs.existsSync(local)){const b=Buffer.from(fs.readFileSync(local,'utf8').replace(/\r\n/g,'\n'));if(gitHash('blob',b)===id)return b;}
    return get(raw+sourcePath+'/'+relative,12*1024**2);
  },b=>gitHash('blob',b)===id);
  const heroes:Rules['heroes']={};
  for(const row of tree){
    const m=/^(npc_dota_hero_[a-z0-9_]+)\.txt$/.exec(row.path);if(!m||m[1]==='npc_dota_hero_base')continue;
    const kv=kvObject(parseKeyValues((await blob(row.sha,'heroes/'+row.path,localHeroes&&path.join(localHeroes,row.path))).toString()).DOTAHeroes);
    const def=kvObject(kv[m[1]]);if(!def.AbilityDefinitions)continue;
    const entries=Object.entries(def).filter(([k,v])=>/^Ability\d+$/.test(k)&&typeof v==='string'),spells:Record<string,SpellRule>={};
    for(const [name,obj] of Object.entries(kvObject(def.AbilityDefinitions))){
      const a=kvObject(obj),s=entries.find(([,v])=>v===name);
      if(name.startsWith('special_bonus_')||!s&&a.IsGrantedByScepter!=='1'&&a.IsGrantedByShard!=='1'&&a.Innate!=='1')continue;
      spells[name]=spellRule(a,s?Number(s[0].slice(7)):null);
    }
    heroes[m[1]]={blob:row.sha,spells};
  }
  assert(Object.keys(heroes).length>=120,'Incomplete Valve hero directory');
  const itemKV=kvObject(parseKeyValues((await blob(itemId,'items.txt',path.join(path.dirname(root),'farm742-camps','upstream',String(build),'pak01_dir/scripts/npc/items.txt'))).toString()).DOTAAbilities);
  const items=Object.fromEntries(defensiveItems.filter(k=>itemKV[k]).map(k=>[k,spellRule(kvObject(itemKV[k]),null)]));
  const rules:Rules={format:'valve-spell-rules-v1',patch,build,commit,steamInfSha256:hash(steam),heroesTree:treeId,itemsBlob:itemId,heroes,items};
  writeJson(rulesFile(root,patch,build),{sha256:digestJson(rules),rules},true);return rules;
}
// Nightly runs may encounter a fresh build; a stable patch label alone cannot certify spell values.
// Check only public history, once per build/run. Refusal/outage must not prevent learning real recorded hits.
const attempted=new Set<string>();
export async function ensureDailyRules(root:string,patch:string,build:number){
  const key=rulesFile(root,patch,build);
  if(fs.existsSync(key)||attempted.has(key))return;
  attempted.add(key);
  try{
    const known=pins();let commit=known.patch.version===patch?known.builds.find((b:any)=>b.build===build)?.commit:undefined;
    if(!commit){
      const recent=JSON.parse((await get(`https://api.github.com/repos/${repo}/commits?path=game%2Fdota%2Fsteam.inf&per_page=18`,512*1024)).toString());
      assert(Array.isArray(recent));
      const ordered=recent.filter(c=>hex(c.sha)).sort((a,b)=>Number(new RegExp(`\\b${build}\\b`).test(b.commit?.message))-
        Number(new RegExp(`\\b${build}\\b`).test(a.commit?.message)));
      for(const c of ordered.slice(0,8)){
        const inf=(await get(`https://raw.githubusercontent.com/${repo}/${c.sha}/game/dota/steam.inf`,4096)).toString().split(/\r?\n/);
        if(['ClientVersion','ServerVersion'].every(k=>inf.includes(`${k}=${build}`))){commit=c.sha;break;}
      }
    }
    assert(hex(commit),'Exact build not found in bounded Valve file history');
    await prepareRules(root,patch,build,commit,path.join(path.dirname(root),'minute-farm-742-build','scripts','npc','heroes'));
  }catch(err){console.warn(`Build ${build}: verified public spell rules unavailable; replay measurements only (${String((err as Error).message).slice(0,145)})`);}
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  const {values:v}=parseArgs({options:{patch:{type:'string'},build:{type:'string'},commit:{type:'string'},'local-heroes':{type:'string'}}});
  assert(v.patch&&v.build);const root=replayArea('daily-study');
  const r=await prepareRules(root,v.patch,Number(v.build),v.commit,v['local-heroes']);
  console.log(JSON.stringify({patch:r.patch,build:r.build,commit:r.commit,heroes:Object.keys(r.heroes).length,items:Object.keys(r.items).length}));
}
