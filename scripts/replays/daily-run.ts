import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { setTimeout as sleep } from 'node:timers/promises';
import { hkDay, heroKeys, HEROES, TARGET, verifyMatch, type Candidate, type Hero } from '../../src/replays/daily-study.ts';
import { THREAT_FORMAT, threatAnswerFile } from '../../src/replays/spell-threats.ts';
import { checkedFogReview, spellInstructions, fogReviewInput, spellSchema } from '../../src/replays/fog-review.ts';
import { dayLedger, heroDir, listCases, readJson, rebuildMemory, writeJson, type Case } from '../../src/replays/daily-memory.ts';
import { replayArea } from '../../src/replays/storage.ts';
import { discover, freshPatch, matchMetadata, ranking, sha } from './daily-sources.ts';
import { replayEvidence } from './daily-evidence.ts';
import { createReviewClient, reviewProfile, ReviewTargetError } from './review-model.ts';

const root=replayArea('daily-study'),day=hkDay(),dayDir=path.join(root,'runs',day),runId=randomUUID(),run=path.join(dayDir,runId);
const byHero=<T>(fn:(h:Hero)=>T)=>Object.fromEntries(heroKeys.map(h=>[h,fn(h)])) as Record<Hero,T>;
const msg=(e:unknown)=>String(e instanceof Error?e.message:e).replace(/[\r\n]+/g,' ').slice(0,350);
const ts=()=>new Date().toISOString();
const profile=reviewProfile({}); // Always the explicitly selected Copilot/Sol/xhigh study profile (no fallback).
const status={day,timeZone:'Asia/Hong_Kong',runId,state:'starting',phase:'startup',patch:null as Awaited<ReturnType<typeof freshPatch>>|null,
  targetPerHero:TARGET,analysisFormat:THREAT_FORMAT,model:profile,startedAt:ts(),updatedAt:ts(),current:null as {hero:Hero;matchId:number}|null,
  completed:byHero(()=>0),verifiedReplays:byHero(()=>0),candidateCount:byHero(()=>0),examinedThisRun:byHero(()=>0),reviewSlotsToday:byHero(()=>0),
  recentIssues:[] as string[]};
fs.mkdirSync(run,{recursive:true});fs.mkdirSync(path.join(root,'tmp'),{recursive:true});
const progress=()=>{status.updatedAt=ts();writeJson(path.join(dayDir,'status.json'),status);};
function event(type:string,data:Record<string,unknown>={}){
  fs.appendFileSync(path.join(dayDir,'events.ndjson'),JSON.stringify({at:ts(),runId,type,...data})+'\n');
}
function issue(message:string){status.recentIssues.push(message);if(status.recentIssues.length>12)status.recentIssues.shift();}
class Stop extends Error {}
const until=Date.now()+12*3600_000;
function clock(){if(hkDay()!==day||Date.now()>=until)throw new Stop('Day/deadline reached; resume on a new run');}
async function patchNow(){
  try{return await freshPatch();}catch(e){
    if(/403|401|429|refused/.test(msg(e)))throw new Stop(msg(e));
    await sleep(3000);try{return await freshPatch();}catch(e2){throw new Stop(`Valve patch not verified: ${msg(e2)}`);}
  }
}
async function runDay(){
  clock();const patch=await patchNow();status.patch=patch;
  const ledgers=byHero(h=>dayLedger(dayDir,h)),rows=byHero(h=>listCases(heroDir(root,patch.name,h)));
  const known=byHero(h=>new Set(rows[h].map(r=>r.matchId)));
  const verifiedFolder=(h:Hero)=>path.join(heroDir(root,patch.name,h),'verified');
  const verified=byHero(h=>{const folder=verifiedFolder(h);
    const records=fs.existsSync(folder)?fs.readdirSync(folder).filter(f=>/^[1-9]\d{0,15}\.json$/.test(f)).map(f=>readJson(path.join(folder,f))):[];
    status.verifiedReplays[h]=new Set([...rows[h],...records].filter(v=>v.day===day).map(v=>v.matchId)).size;
    return new Set<number>([...known[h],...records.map(v=>v.matchId)]);});
  for(const h of heroKeys){status.completed[h]=rows[h].filter(c=>c.day===day).length;status.reviewSlotsToday[h]=ledgers[h].size();
    rebuildMemory(root,patch.name,h,rows[h]);}
  writeJson(path.join(root,'memory','current.json'),patch);
  const full=()=>heroKeys.every(h=>status.completed[h]>=TARGET);
  if(full())return;
  status.state='running';status.phase='checking-model';progress();
  let client:Awaited<ReturnType<typeof createReviewClient>>|undefined;
  const disableModel=(e:unknown)=>{client=undefined;const message=`Reviews deferred (verified replays still saved): ${msg(e)}`;issue(message);event('model-unavailable',{message});progress();};
  try{client=await createReviewClient(profile);assert(client.checkModel);
    await client.checkModel(AbortSignal.timeout(20_000));event('model-confirmed',{...profile});}
  catch(e){disableModel(e);}
  status.phase='discovery';progress();
  const {all,tiers,odRelease}=await discover(patch,root,run,message=>{issue(message);event('source-issue',{message});progress();});
  for(const h of heroKeys){status.candidateCount[h]=all[h].length;all[h]=all[h].filter(c=>!known[h].has(c.id));}
  event('catalog',{counts:status.candidateCount});progress();
  let checkedAt=Date.parse(patch.checkedAt),reviewFailures=0;
  const stillCurrent=async(force=false)=>{
    clock();if(!force && Date.now()-checkedAt<300_000)return;
    const p=await patchNow();if(p.name!==patch.name||p.from!==patch.from)throw new Stop(`Valve patch changed: ${patch.name} -> ${p.name}; no cross-patch publication`);
    checkedAt=Date.now();
  };
  const ranks=new Map<number,any>();
  async function study(c:Candidate){
    const h=c.hero,ledger=ledgers[h],answerFile=path.join(root,'evidence',patch.name,h,String(c.id),threatAnswerFile);
    if(known[h].has(c.id)||!verified[h].has(c.id)&&status.verifiedReplays[h]>=TARGET ||
      verified[h].has(c.id)&&!fs.existsSync(answerFile)&&(!client||ledger.has(c.id)||ledger.size()>=TARGET))return;
    await stillCurrent();status.current={hero:h,matchId:c.id};status.phase='qualifying';
    status.examinedThisRun[h]++;progress();
    const {api,bytes,existing}=await matchMetadata(c.id,root);
    let rank:any;
    if(c.kind==='rank-probe'){
      const account=api?.players?.find((p:any)=>p.hero_id===HEROES[h])?.account_id;
      if(!Number.isSafeInteger(account)||account<=0)throw Error('Anonymous/unranked focal player');
      if(!ranks.has(account))ranks.set(account,await ranking(account));
      rank=ranks.get(account);
    }
    const player=verifyMatch(c,api,{...patch,odRelease},Date.now()/1000,tiers.get(c.league??-1),rank);
    const disk=fs.statfsSync(root,{bigint:true});
    if(disk.bavail*disk.bsize<4n*1024n**3n)throw new Stop('Less than 4 GiB free on replay volume; no local-drive fallback');
    status.phase='reading-dem';progress();
    const {evidence,decisions,threats,provenance}=await replayEvidence(c,api,bytes,existing,patch,root);
    const input=fogReviewInput(decisions,evidence,threats),inputSha256=sha(input),instructionsSha256=sha(spellInstructions);
    await stillCurrent();
    const qualification={...c,leagueTier:api.league?.tier??tiers.get(c.league??-1),focalLeaderboardRank:rank?.leaderboard_rank,
      slot:player.player_slot,patchStart:patch.from,openDotaPatch:api.patch,odRelease,patchCheckedAt:new Date(checkedAt).toISOString(),catalog:path.relative(root,path.join(run,'catalog.json'))};
    if(!verified[h].has(c.id)){
      writeJson(path.join(verifiedFolder(h),`${c.id}.json`),{version:1,day,patch:patch.name,hero:h,matchId:c.id,qualification,provenance,
        evidenceFile:provenance.evidenceFile},true);
      verified[h].add(c.id);status.verifiedReplays[h]++;event('dem-verified',{hero:h,matchId:c.id});progress();
    }
    if(!fs.existsSync(answerFile)&&(!client||ledger.has(c.id)))return;
    const reserve=()=>{
      if(!ledger.reserve(c.id,TARGET,{matchId:c.id,hero:h,patch:patch.name,at:ts(),runId,inputSha256,...profile}))
        throw new Error('Daily quota already reserved');
      status.reviewSlotsToday[h]=ledger.size();
    };
    let answer;
    if(fs.existsSync(answerFile)){if(!ledger.has(c.id))reserve();answer=readJson(answerFile);}
    else{
      if(ledger.size()>=TARGET)return;
      status.phase='reviewing';progress();
      try{
        assert(client);const r=await client.generate(spellInstructions,input,AbortSignal.timeout(Math.max(1,Math.min(45*60_000,until-Date.now()))),spellSchema,()=>{
          clock();reserve();event('review-admitted',{...status.current,inputSha256,decisionCount:decisions.points.length});progress();},128000);
        assert(r?.status==='completed' && Array.isArray(r.output),'Incomplete model response');
        const text=r.output.flatMap((x:any)=>x.type==='message'&&Array.isArray(x.content)?x.content:[])
          .filter((x:any)=>x.type==='output_text').map((x:any)=>x.text).join('');
        const result=JSON.parse(text);checkedFogReview(result,decisions,evidence,threats);
        answer={format:THREAT_FORMAT,...profile,inputSha256,instructionsSha256,result,responseId:r.id,usage:r.usage,receivedAt:ts()};
        writeJson(answerFile,answer,true);reviewFailures=0;
      }catch(e){if(e instanceof ReviewTargetError||++reviewFailures>=3){disableModel(e);return;}
        throw e;}
    }
    assert(answer.format===THREAT_FORMAT && answer.provider===profile.provider && answer.model===profile.model && answer.reasoningEffort===profile.reasoningEffort &&
      answer.inputSha256===inputSha256 && answer.instructionsSha256===instructionsSha256,'Cached answer profile/evidence mismatch');
    const {observations,minuteNotes,decisionNotes}=checkedFogReview(answer.result,decisions,evidence,threats);
    await stillCurrent(true);clock();
    const record:Case={version:3,day,patch:patch.name,hero:h,matchId:c.id,completedAt:ts(),
      qualification,
      provenance:{...provenance,inputSha256,instructionsSha256,responseId:answer.responseId,answerFile:path.relative(root,answerFile)},model:profile,evidence,observations,minuteNotes,decisions,decisionNotes};
    writeJson(path.join(heroDir(root,patch.name,h),'matches',`${c.id}.json`),record,true);
    known[h].add(c.id);status.completed[h]++;rows[h].push(record);rows[h].sort((a,b)=>b.evidence.start-a.evidence.start||b.matchId-a.matchId);
    rebuildMemory(root,patch.name,h,rows[h]);event('learned',{hero:h,matchId:c.id,completed:status.completed[h]});progress();
  }
  // Round-robin, including rare heroes; a busy hero cannot exhaust another hero's quota.
  for(let i=0;i<Math.min(100,Math.max(...heroKeys.map(h=>all[h].length)));i++)for(const h of heroKeys){
    clock();const c=all[h][i];if(!c||status.completed[h]>=TARGET)continue;
    try{await study(c);}catch(e){
      if(e instanceof Stop || e instanceof ReviewTargetError || ['ENOSPC','EROFS','EACCES','ENODEV'].includes((e as NodeJS.ErrnoException).code??''))throw e;
      const message=msg(e);issue(`${h} ${c.id}: ${message}`);event('not-counted',{hero:h,matchId:c.id,phase:status.phase,message});progress();
    }
  }
}
progress();event('start');
try{
  await runDay();
  status.state=heroKeys.every(h=>status.completed[h]>=TARGET)?'complete':'partial';
  if(status.state==='partial')process.exitCode=2;
}catch(e){
  status.state='blocked';issue(msg(e));event('blocked',{message:msg(e)});process.exitCode=1;
}finally{
  status.current=null;status.phase='finished';progress();writeJson(path.join(run,'result.json'),status);
  event('end',{state:status.state,completed:status.completed});console.log(JSON.stringify(status));
}
