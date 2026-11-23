/** Acquire once per match, extract once per focal hero. Never execute code from the data share. */
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { promisify } from 'node:util';
import { compactEvidence, type Candidate, type Patch } from '../../src/replays/daily-study.ts';
import { MINUTE_FORMAT } from '../../src/replays/daily-minutes.ts';
import { checkedTeamView, decisionEvidence, FOG_FORMAT, type TeamView } from '../../src/replays/fog-decisions.ts';
import { checkedSpellTape, spellThreats, THREAT_FORMAT, type SpellTape } from '../../src/replays/spell-threats.ts';
import { loadRules, digestJson } from '../../src/replays/spell-rules.ts';
import { ensureDailyRules } from './prepare-spell-rules.ts';
import { heroDir, readJson, writeBytes, writeJson } from '../../src/replays/daily-memory.ts';
import type { Decisions } from '../../src/replays/fog-decisions.ts';
import { replayChildEnv } from '../../src/replays/storage.ts';
import { archiveDirs, sha } from './daily-sources.ts';
import { downloadReplay, replayUrl } from './download.ts';

const project = path.resolve(import.meta.dirname, '../..'), exec = promisify(execFile);
const python = path.join(project, '.tmp', 'replay-tools', process.platform === 'win32' ? 'Scripts/python.exe' : 'bin/python');
const M = 1024 ** 2;
async function hashFile(file:string, max:number) {
  const size = fs.statSync(file).size;
  assert(size > 0 && size <= max, `Invalid size: ${path.basename(file)}`);
  const h=createHash('sha256'); for await (const chunk of fs.createReadStream(file,{highWaterMark:M})) h.update(chunk);
  return h.digest('hex');
}
function linkOrCopy(from:string,to:string) {
  try { fs.linkSync(from,to); } catch(e) {
    if ((e as NodeJS.ErrnoException).code === 'EEXIST') throw e;
    fs.copyFileSync(from,to,fs.constants.COPYFILE_EXCL);
  }
}
function dropStage(tmp:string,stage:string) {
  if(!fs.existsSync(stage))return;
  const actual=fs.realpathSync(stage), parent=fs.realpathSync(tmp);
  assert(path.dirname(actual)===parent && /^acq-[1-9]\d*-[a-zA-Z0-9]{6}$/.test(path.basename(actual)), 'Unsafe scratch cleanup');
  fs.rmSync(actual,{recursive:true});
}
async function pythonTool(script:'extract'|'decompress'|'team_view',args:string[],scratch:string) {
  const child=replayChildEnv(scratch);
  const env:NodeJS.ProcessEnv=Object.fromEntries(['SystemRoot','WINDIR','SystemDrive','PATH','USERPROFILE','TEMP','TMP','TMPDIR',
    'PYTHONDONTWRITEBYTECODE'].filter(k=>!!child[k]).map(k=>[k,child[k]]));
  try {
    await exec(python,['-B',path.join(project,'scripts','replays',`${script}.py`),...args],
      { cwd:project,env,windowsHide:true,timeout:20*60_000,maxBuffer:2*M });
  } catch(e) {
    const err=e as {code?:string;stderr?:string;killed?:boolean};
    throw Error(`${script} ${err.killed?'timeout':err.code ?? 'failed'}: ${err.stderr?.trim().split(/\r?\n/).at(-1)?.slice(0,240)??'no parser result'}`);
  }
}
/** Separate layer so immutable old full/route-core facts can gain real team vision without being overwritten. */
async function readTeamLayer<T>(root:string,c:Pick<Candidate,'id'|'hero'>,folder:string,facts:any,pid:number,
  kind:'team-map-v2'|'team-spells-v2',verify:(v:any,f:any,h:Candidate['hero'],pid:number)=>T){
  const file=path.join(folder,kind+'.json'),tmp=path.join(root,'tmp');
  if(!fs.existsSync(file)){
    fs.mkdirSync(tmp,{recursive:true});const stage=fs.mkdtempSync(path.join(tmp,`acq-${c.id}-`)),fresh=path.join(stage,'team-map.json');
    try{
      await pythonTool('team_view',[String(c.id),'--root',path.join(root,'raw',String(c.id)),'--hero',c.hero,'--pid',String(pid),'--output',fresh,
        ...(kind==='team-spells-v2'?['--spells-only']:[])],stage);
      assert(fs.statSync(fresh).size<=128*M,'Oversized team view');
      const bytes=fs.readFileSync(fresh);verify(JSON.parse(bytes.toString('utf8')),facts,c.hero,pid);
      writeBytes(file,bytes,true);
    }finally{dropStage(tmp,stage);}
  }
  assert(fs.statSync(file).size<=128*M,'Oversized stored team view');
  const bytes=fs.readFileSync(file);
  return {value:verify(JSON.parse(bytes.toString('utf8')),facts,c.hero,pid),hash:sha(bytes),file:path.relative(root,file)};
}
export async function teamView(root:string,c:Pick<Candidate,'id'|'hero'>,folder:string,facts:any,pid:number){
  const r=await readTeamLayer<TeamView>(root,c,folder,facts,pid,'team-map-v2',checkedTeamView);
  return {view:r.value,teamViewSha256:r.hash,teamViewFile:r.file};
}
export async function threatLayer(root:string,d:Decisions,folder:string,facts:any,pid:number,p:{demSha256:string;factsSha256:string;metadataSha256:string}){
  const dir=heroDir(root,d.patch,d.hero),file=path.join(dir,THREAT_FORMAT,`${d.matchId}.json`);
  const r=await readTeamLayer<SpellTape>(root,{id:d.matchId,hero:d.hero},folder,facts,pid,'team-spells-v2',checkedSpellTape);
  const common={source:'team-spell-witnesses',demSha256:p.demSha256,factsSha256:p.factsSha256,metadataSha256:p.metadataSha256,
    decisionsSha256:digestJson(d),spellTapeSha256:r.hash,spellTapeFile:r.file,threatFile:path.relative(root,file)};
  if(fs.existsSync(file)){
    const old=readJson(file);assert(Object.entries(common).every(([k,v])=>old[k]===v)&&
      digestJson(old.threats)===old.threatsSha256&&old.threats?.format===THREAT_FORMAT&&old.threats.matchId===d.matchId&&
      old.threats.hero===d.hero&&old.threats.patch===d.patch&&old.threats.build===facts.gameBuild,'Mismatched immutable threat layer');
    // A newly available ruleset must not silently change a previously admitted answer's input or its cost.
    return {threats:old.threats as ReturnType<typeof spellThreats>,provenance:{spellTapeSha256:r.hash,spellTapeFile:r.file,
      threatFile:common.threatFile,threatsSha256:old.threatsSha256 as string,spellRulesCommit:old.spellRulesCommit as string|null}};
  }
  const rules=loadRules(root,d.patch,facts.gameBuild),threats=spellThreats(d,facts,r.value,pid,rules);
  const provenance={spellTapeSha256:r.hash,spellTapeFile:r.file,threatsSha256:digestJson(threats),spellRulesCommit:rules?.commit??null,
    threatFile:path.relative(root,file)};
  writeJson(file,{...common,...provenance,threats},true);
  return {threats,provenance};
}
async function rawReplay(c:Candidate, root:string, api:any, bytes:Buffer, existing:string|undefined) {
  const id=c.id, dest=path.join(root,'raw',String(id)), tmp=path.join(root,'tmp'), metaHash=sha(bytes);
  const checked=async(dir:string)=>{
    const f=(ext:string)=>path.join(dir,`${id}.${ext}`), m=readJson(f('acquisition.json'));
    assert(m.matchId===id && m.metadataSha256===metaHash && m.replayUrl===replayUrl(api.replay_url,id).href &&
      fs.statSync(f('archive')).size===m.archiveBytes && fs.statSync(f('dem')).size===m.demBytes &&
      await hashFile(f('archive'),512*M)===m.archiveSha256 && await hashFile(f('dem'),2048*M)===m.demSha256,
      'Replay/metadata failed acquisition hash checks');
    return m;
  };
  if(fs.existsSync(dest))return {dir:dest,manifest:await checked(dest)};
  fs.mkdirSync(tmp,{recursive:true});fs.mkdirSync(path.dirname(dest),{recursive:true});
  const stage=fs.mkdtempSync(path.join(tmp,`acq-${id}-`)), f=(ext:string)=>`${id}.${ext}`;
  try{
    if(existing){
      for(const ext of ['archive','opendota.json'])linkOrCopy(path.join(existing,f(ext)),path.join(stage,f(ext)));
      fs.copyFileSync(path.join(existing,f('acquisition.json')),path.join(stage,f('acquisition.json')),fs.constants.COPYFILE_EXCL);
      if(fs.existsSync(path.join(existing,f('dem'))))linkOrCopy(path.join(existing,f('dem')),path.join(stage,f('dem')));
    }else await downloadReplay(id,stage,bytes);
    if(!fs.existsSync(path.join(stage,f('dem'))))await pythonTool('decompress',[String(id),'--root',stage],stage);
    const manifest=await checked(stage);
    assert(path.dirname(fs.realpathSync(stage))===fs.realpathSync(tmp) && path.basename(dest)===String(c.id) &&
      path.dirname(fs.realpathSync(path.dirname(dest)))===fs.realpathSync(root),'Unsafe raw replay destination');
    fs.renameSync(stage,dest);
    return {dir:dest,manifest};
  }finally{dropStage(tmp,stage);}
}
export async function replayEvidence(c:Candidate, api:any, bytes:Buffer, existing:string|undefined, patch:Patch,root:string) {
  const {dir,manifest}=await rawReplay(c,root,api,bytes,existing);
  const folder=path.join(root,'evidence',patch.name,c.hero,String(c.id)), target=path.join(folder,'facts.json');
  fs.mkdirSync(folder,{recursive:true});
  let facts:any;
  if(fs.existsSync(target))facts=readJson(target);
  else{
    // Only reuse a previous extraction when *this* hero's path and the full DEM digest match.
    for(const d of archiveDirs(c.id,root).slice(1)){
      const old=path.join(d,`${c.id}.facts.json`);
      if(!fs.existsSync(old)||fs.statSync(old).size>480*M)continue;
      const previous=readJson(old);
      if(previous.demSha256!==manifest.demSha256||previous.heroPath?.[0]?.name!==`npc_dota_hero_${c.hero}`)continue;
      compactEvidence(c,api,previous,manifest.demSha256,patch);
      linkOrCopy(old,target);facts=previous;break;
    }
    if(!facts){
      // A crash can leave a partial, never treat it as facts or overwrite it on resume.
      const work=fs.mkdtempSync(path.join(root,'tmp',`parse-${c.id}-`)), fresh=path.join(work,'facts.json');
      await pythonTool('extract',[String(c.id),'--root',dir,'--hero',c.hero,'--route-core','--output',fresh],work);
      facts=readJson(fresh);compactEvidence(c,api,facts,manifest.demSha256,patch);
      fs.renameSync(fresh,target);
    }
  }
  const evidence=compactEvidence(c,api,facts,manifest.demSha256,patch), factsSha256=await hashFile(target,512*M);
  const pid=facts.heroPath[0].playerIdField;
  const {view,teamViewSha256,teamViewFile}=await teamView(root,c,folder,facts,pid);
  const decisions=decisionEvidence(evidence,facts,view,pid);
  const evidenceSha256=sha(JSON.stringify(evidence)),decisionsSha256=sha(JSON.stringify(decisions));
  let evidencePath=path.join(folder,`evidence.${FOG_FORMAT}.json`);
  if(fs.existsSync(evidencePath)&&sha(JSON.stringify(readJson(evidencePath).decisions))!==decisionsSha256)
    evidencePath=path.join(folder,`evidence.${decisionsSha256}.json`); // preserve previous extractor outputs
  const provenance={parser:facts.parser,gameBuild:evidence.build,demSha256:manifest.demSha256,archiveSha256:manifest.archiveSha256,
    metadataSha256:manifest.metadataSha256,factsSha256,metadataUrl:manifest.metadataUrl,replayUrl:manifest.replayUrl,
    raw:path.relative(root,dir),facts:path.relative(root,target),evidenceSha256,evidenceFile:path.relative(root,evidencePath),
    minuteFormat:MINUTE_FORMAT,teamViewSha256,teamViewFile,decisionsSha256};
  if(!fs.existsSync(evidencePath))writeJson(evidencePath,{evidence,decisions,provenance},true);
  await ensureDailyRules(root,patch.name,facts.gameBuild);
  const enhanced=await threatLayer(root,decisions,folder,facts,pid,provenance);
  return {evidence,decisions,threats:enhanced.threats,provenance:{...provenance,...enhanced.provenance},folder};
}
