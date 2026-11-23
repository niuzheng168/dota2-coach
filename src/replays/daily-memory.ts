import fs from 'node:fs';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { HEROES, validPatch, type Hero, type Evidence, type Observation, type MinuteNote } from './daily-study.ts';
import { MINUTE_FORMAT, type MinuteWindow } from './daily-minutes.ts';
import { FOG_FORMAT, fogSituations, situationAt, type Decisions, type FogSituation } from './fog-decisions.ts';
import type { DecisionNote } from './fog-review.ts';
import { THREAT_FORMAT, baseScenarios, type Threats } from './spell-threats.ts';
import { replayArea } from './storage.ts';

type LegacyEvidence=Omit<Evidence,'format'|'windowSeconds'|'halfSeconds'|'windows'|'role'> & {role?:string;
  windows:(Pick<MinuteWindow,'id'|'from'|'to'|'lh'|'lhGain'|'laneGold'|'neutralGold'|'netWorth'|'items'>&{path:number[][]})[]};
type CaseHeader = { day:string; patch:string; hero:Hero; matchId:number; completedAt:string;
  qualification:Record<string,unknown>; provenance:Record<string,unknown>; model:{provider:string;model:string;reasoningEffort:string};
  observations:Observation[] };
export type Case = CaseHeader & ({version:1;evidence:LegacyEvidence}|{version:2;evidence:Evidence;minuteNotes:MinuteNote[]}|
  {version:3;evidence:Evidence;minuteNotes:MinuteNote[];decisions:Decisions;decisionNotes:DecisionNote[]});
export const readJson = (p:string) => JSON.parse(fs.readFileSync(p,'utf8'));
// A short-lived read handle on the Windows share can deny an atomic rename. Retry that operation,
// never unlink/truncate the last good index. Permanent errors and existing immutable records still fail.
const renameWait=new Int32Array(new SharedArrayBuffer(4));
function retrySharingViolation(op:()=>void){
  const delays=[15,30,60,120,240,480];
  for(let i=0;;i++)try{return op();}catch(err){
    const code=(err as NodeJS.ErrnoException).code;
    if(i>=delays.length||!(code==='EBUSY'||(process.platform==='win32'&&(code==='EPERM'||code==='EACCES'))))throw err;
    Atomics.wait(renameWait,0,0,delays[i]);
  }
}
/** Same-volume atomic publication. Immutable case records are linked without replacing earlier evidence. */
export function writeBytes(p:string, bytes:string|Buffer, immutable=false) {
  fs.mkdirSync(path.dirname(p),{recursive:true});
  const tmp = `${p}.${randomUUID()}.tmp`;
  try {
    fs.writeFileSync(tmp,bytes,{flag:'wx'});
    if (immutable) fs.linkSync(tmp,p); else retrySharingViolation(()=>fs.renameSync(tmp,p));
  } finally { if (fs.existsSync(tmp)) retrySharingViolation(()=>fs.unlinkSync(tmp)); }
}
export const writeJson=(p:string,v:unknown,immutable=false)=>writeBytes(p,JSON.stringify(v)+'\n',immutable);
export function heroDir(root:string, patch:string, hero:string) {
  if (!validPatch(patch) || !Object.hasOwn(HEROES,hero)) throw Error('Invalid memory patch/hero');
  return path.join(root,'memory',patch,hero);
}
function readCase(dir:string,file:string):Case{
  const c:Case=readJson(path.join(dir,'matches',file));
  if (![1,2,3].includes(c.version) || path.basename(dir)!==c.hero || path.basename(path.dirname(dir))!==c.patch ||
    `${c.matchId}.json`!==file || c.evidence.matchId!==c.matchId || c.evidence.hero!==c.hero || c.evidence.patch!==c.patch)
    throw Error('Memory case identity mismatch');
  if(c.version!==1 && (c.evidence.format!==MINUTE_FORMAT||c.minuteNotes.length!==c.evidence.windows.length||
    !c.minuteNotes.every((n,i)=>n.minute===i&&n.evidenceId===c.evidence.windows[i].id)))throw Error('Invalid persisted per-minute review');
  if(c.version===3&&(c.decisions.format!==FOG_FORMAT||c.decisions.matchId!==c.matchId||c.decisions.hero!==c.hero||c.decisions.patch!==c.patch||
    digest(c.decisions)!==c.provenance.decisionsSha256||c.decisionNotes.length!==c.decisions.points.length||
    !c.decisionNotes.every((n,i)=>n.id===c.decisions.points[i].id)))throw Error('Invalid persisted team-view review');
  return c;
}
export function listCases(dir:string):Case[] {
  const folder = path.join(dir,'matches');
  if (!fs.existsSync(folder)) return [];
  return fs.readdirSync(folder).filter(n=>/^[1-9]\d{0,15}\.json$/.test(n)).map(file=>readCase(dir,file))
    .sort((a,b)=>b.evidence.start-a.evidence.start || b.matchId-a.matchId);
}
export const minuteSidecar=(dir:string,id:number)=>path.join(dir,MINUTE_FORMAT,`${id}.json`);
const digest=(e:unknown)=>createHash('sha256').update(JSON.stringify(e)).digest('hex');
export const evidenceDigest=(e:Evidence)=>digest(e);
/** V1 interpretations remain anchored to :wN. Only immutable, source-checked sidecars can add numeric minutes to them. */
export function minuteEvidenceFor(dir:string,c:Case):Evidence|null{
  if(c.version!==1)return c.evidence;
  const file=minuteSidecar(dir,c.matchId);if(!fs.existsSync(file))return null;
  const s=readJson(file),e:Evidence=s.evidence;
  if(s.source!=='verified-facts-backfill'||e?.format!==MINUTE_FORMAT||e.patch!==c.patch||e.hero!==c.hero||e.matchId!==c.matchId||
    !['demSha256','factsSha256','metadataSha256'].every(k=>typeof s[k]==='string' && /^[a-f0-9]{64}$/.test(s[k]) && s[k]===c.provenance[k])||
    evidenceDigest(e)!==s.evidenceSha256)throw Error('Mismatched backfilled minute evidence');
  return e;
}
export const fogSidecar=(dir:string,id:number)=>path.join(dir,FOG_FORMAT,`${id}.json`);
export function fogEvidenceFor(dir:string,c:Case):Decisions|null{
  if(c.version===3)return c.decisions;
  const file=fogSidecar(dir,c.matchId);if(!fs.existsSync(file))return null;
  const s=readJson(file),d:Decisions=s.decisions;
  if(s.source!=='team-dem-backfill'||d?.format!==FOG_FORMAT||d.matchId!==c.matchId||d.hero!==c.hero||d.patch!==c.patch||
    !['demSha256','factsSha256','metadataSha256'].every(k=>/^[a-f0-9]{64}$/.test(s[k])&&s[k]===c.provenance[k])||
    !/^[a-f0-9]{64}$/.test(s.teamViewSha256)||digest(d)!==s.decisionsSha256)throw Error('Mismatched team-view sidecar');
  return d;
}
export function threatsFor(dir:string,c:Case,d:Decisions|null):Threats|null{
  const file=path.join(dir,THREAT_FORMAT,`${c.matchId}.json`);
  if(!fs.existsSync(file)){if(c.provenance.threatsSha256)throw Error('Missing published spell evidence');return null;}
  const s=readJson(file),v:Threats=s.threats;
  if(!d||s.source!=='team-spell-witnesses'||v?.format!==THREAT_FORMAT||v.hero!==c.hero||v.patch!==c.patch||v.matchId!==c.matchId||
    v.build!==c.evidence.build||v.demSha256!==c.provenance.demSha256||s.decisionsSha256!==digest(d)||digest(v)!==s.threatsSha256||
    v.points.length!==d.points.length||!v.points.every((p,i)=>p.id===d.points[i].id&&p.key===d.points[i].key)||
    !['demSha256','factsSha256','metadataSha256'].every(k=>/^[a-f0-9]{64}$/.test(s[k])&&c.provenance[k]===s[k])||
    !/^[a-f0-9]{64}$/.test(s.spellTapeSha256)||c.provenance.threatsSha256&&
      (s.threatsSha256!==c.provenance.threatsSha256||s.spellTapeSha256!==c.provenance.spellTapeSha256))throw Error('Mismatched spell witnesses');
  return v;
}
type ThreatLesson={id:string;assessment:string;changeIf:string};
function spellLessons(dir:string,c:Case,v:Threats|null):ThreatLesson[]{
  const file=path.join(dir,'spell-field-notes',`${c.matchId}.json`);if(!v||!fs.existsSync(file))return [];
  const s=readJson(file),ids=new Set(v.points.map(p=>p.id));
  if(s.source!=='codex-offline-spell-case-study'||s.threatsSha256!==digest(v)||!Array.isArray(s.notes)||
    !s.notes.every((n:any)=>ids.has(n.id)&&[n.assessment,n.changeIf].every(x=>typeof x==='string'&&x.trim())))
    throw Error('Unanchored spell case study');
  return s.notes;
}
/** Explicitly authored case-study notes (not paid model answers, not additional reviewed matches). */
type FieldNote={id:string;mapRead:string;assessment:string;changeIf:string};
function fieldNotes(dir:string,c:Case,d:Decisions|null):FieldNote[]{
  const file=path.join(dir,'field-notes',`${c.matchId}.json`);if(!d||!fs.existsSync(file))return [];
  const s=readJson(file),ids=new Set(d.points.map(p=>p.id));
  if(s.source!=='codex-offline-case-study'||s.decisionsSha256!==digest(d)||!Array.isArray(s.notes)||
    !s.notes.every((n:any)=>ids.has(n.id)&&[n.mapRead,n.assessment,n.changeIf].every(t=>typeof t==='string'&&t.trim()))||
    new Set(s.notes.map((n:any)=>n.id)).size!==s.notes.length)throw Error('Unanchored offline case study');
  return s.notes;
}
export function readCoachMinute(patch:string,hero:string,id:number,minute:number,root=replayArea('daily-study')){
  if(!Number.isSafeInteger(id)||id<=0||!Number.isSafeInteger(minute)||minute<0)throw Error('Invalid match/minute');
  const dir=heroDir(root,patch,hero),name=`${id}.json`;
  if(!fs.existsSync(path.join(dir,'matches',name)))return null;
  const c=readCase(dir,name),e=minuteEvidenceFor(dir,c),w=e?.windows[minute];if(!e||!w)return null;
  const d=fogEvidenceFor(dir,c),notes=fieldNotes(dir,c,d),th=threatsFor(dir,c,d),spellNotes=spellLessons(dir,c,th);
  return {patch,hero,matchId:id,source:c.version===3?'team-view-decision-review':c.version===2?'minute-model-review':'verified-facts-backfill',window:w,
    firstMainSlot:e.firstMainSlot.filter(i=>i.t>=w.from&&(i.t<w.to||minute===e.windows.length-1&&i.t<=w.to)),
    modelReading:c.version!==1?c.minuteNotes[minute].reading:null,
    decisionSource:c.version===3?'team-view-decision-review':d?'team-dem-backfill':null,
    spellRules:th?.definitions??null,spellSource:th?'team-spell-witnesses':null,
    decisions:d?.points.filter(p=>p.minute===minute).map(p=>{
      const s=th?.points.find(x=>x.id===p.id);
      return {...p,modelJudgement:c.version===3?c.decisionNotes.find(n=>n.id===p.id):null,offlineCaseNote:notes.find(n=>n.id===p.id)??null,
        spellThreatBefore:s?.before??null,spellBaseScenarios:th&&s?baseScenarios(th,s,p.before.own?.hp??null,p.before.own?.maxHp??null):null,
        castRankReplayAudit:s?.auditRanks??null,spellCaseNote:spellNotes.find(n=>n.id===p.id)??null};
    })??null};
}
const median=(v:number[])=>{const a=v.toSorted((x,y)=>x-y); return a.length?(a[(a.length-1)>>1]+a[a.length>>1])/2:null;};
export function rebuildMemory(root:string,patch:string,hero:Hero, rows=listCases(heroDir(root,patch,hero))) {
  const dir=heroDir(root,patch,hero),itemTimes=new Map<string,number[]>(),periods=new Map<number,MinuteWindow[]>();
  let minuteEvidenceCases=0,minuteReviewedCases=0,fogEvidenceCases=0,decisionPoints=0,reviewedDecisionPoints=0;
  let spellEvidenceCases=0,spellDecisionPoints=0,spellReviewedCases=0;
  const spellExamples:{matchId:number;minute:number;source:string;id:string;assessment:string;changeIf:string}[]=[];
  const decisionExamples:{matchId:number;id:string;minute:number;source:string;mapRead:string;assessment:string;changeIf:string}[]=[];
  type Example={matchId:number;id:string;minute:number;at:number;source:'case-study'|'model'|'evidence-only';judgement:string|null;changeIf:string|null};
  const situations=Object.fromEntries(fogSituations.map(s=>[s,{matches:0,points:0,examples:[] as Example[]}])) as
    Record<FogSituation,{matches:number;points:number;examples:Example[]}>;
  const pairs=new Map<string,{from:string;to:string;times:number[]}>();
  for (const c of rows) {
    for(const {item,t} of c.evidence.firstMainSlot) {const a=itemTimes.get(item)??[];a.push(t);itemTimes.set(item,a);}
    // A reproducible progression, not a hand-coded item meta: consider items still held at the end.
    const terminal=new Set(c.evidence.windows.at(-1)?.items), retained=c.evidence.firstMainSlot.filter(i=>terminal.has(i.item)&&i.t>=180);
    for(let i=1;i<retained.length;i++){
      const a=retained[i-1],b=retained[i];if(a.t>=b.t)continue;
      const key=`${a.item}:${b.item}`,pair=pairs.get(key)??{from:a.item,to:b.item,times:[]};
      pair.times.push(b.t);pairs.set(key,pair);
    }
    const e=minuteEvidenceFor(dir,c);
    if(e)minuteEvidenceCases++;if(c.version!==1)minuteReviewedCases++;
    const d=fogEvidenceFor(dir,c);
    if(d){
      fogEvidenceCases++;decisionPoints+=d.points.length;
      if(c.version===3)reviewedDecisionPoints+=c.decisionNotes.length;
      const byId=new Map(d.points.map(p=>[p.id,p.minute]));
      const th=threatsFor(dir,c,d);
      if(th){
        spellEvidenceCases++;spellDecisionPoints+=th.points.length;
        for(const n of spellLessons(dir,c,th))if(spellExamples.length<15)
          spellExamples.push({...n,matchId:c.matchId,minute:byId.get(n.id)!,source:'codex-offline-spell-case-study'});
        if(c.version===3&&c.provenance.threatsSha256){
          if(!c.decisionNotes.every(n=>n.threatRead?.trim()&&n.threatRefs?.length&&Object.hasOwn(n,'threatMetric')))
            throw Error('Incomplete persisted spell judgements');
          spellReviewedCases++;
          const picks=[c.decisionNotes.find(n=>n.threatMetric?.ref==='burst'),c.decisionNotes.find(n=>n.nextFarmComparison==='different-family'),c.decisionNotes[0]];
          for(const n of new Set(picks))if(n?.threatRead&&spellExamples.length<15)spellExamples.push({id:n.id,matchId:c.matchId,minute:byId.get(n.id)!,
            source:'model-spell-review',assessment:n.threatRead+' '+n.plan,changeIf:n.exitSignal});
        }
      }
      const manual=fieldNotes(dir,c,d),studied=manual.map(n=>({...n,source:'codex-offline-case-study'}));
      for(const s of fogSituations){
        const bucket=situations[s],hits=d.points.filter(p=>situationAt(p,s));
        if(!hits.length)continue;bucket.matches++;bucket.points+=hits.length;
        const examples:Example[]=hits.map(p=>{
          const field=manual.find(n=>n.id===p.id),note=c.version===3?c.decisionNotes.find(n=>n.id===p.id):null;
          return {matchId:c.matchId,id:p.id,minute:p.minute,at:p.before.at,source:field?'case-study':note?'model':'evidence-only',
            judgement:field?.assessment??note?.plan??null,changeIf:field?.changeIf??note?.exitSignal??null};
        });
        const order={'case-study':0,'model':1,'evidence-only':2};
        bucket.examples=[...bucket.examples,...examples.sort((a,b)=>order[a.source]-order[b.source]).slice(0,3)]
          .sort((a,b)=>order[a.source]-order[b.source]).slice(0,12);
      }
      if(c.version===3){
        // Surface conditional/disagreeing examples too, not a vote for the pro's actual next hit.
        const examples=[c.decisionNotes.find(n=>n.nextFarmComparison==='different-family'),c.decisionNotes.find(n=>n.preferred==='retreat'||n.preferred==='hold'),c.decisionNotes[0]];
        for(const n of new Set(examples))if(n)studied.push({id:n.id,source:'team-view-decision-review',mapRead:n.mapRead,assessment:n.plan+' '+n.tradeoff,changeIf:n.exitSignal});
      }
      for(const n of studied)if(decisionExamples.length<15)decisionExamples.push({matchId:c.matchId,minute:byId.get(n.id)!,...n});
    }
    for(const w of e?.windows??[]){
      if(w.partial)continue;
      const a=periods.get(w.minute)??[];a.push(w);periods.set(w.minute,a);
    }
  }
  const income=(xs:{lhGain:number;laneGold:number;neutralGold:number}[])=>({medianLhGain:median(xs.map(x=>x.lhGain)),
    medianLaneGoldInWindow:median(xs.map(x=>x.laneGold)),medianNeutralGoldInWindow:median(xs.map(x=>x.neutralGold))});
  const memory={version:2,patch,hero,samples:rows.length,windowSeconds:60,halfSeconds:30,minuteEvidenceCases,minuteReviewedCases,
    fogEvidenceCases,decisionPoints,reviewedDecisionPoints,decisionExamples,situations,spellEvidenceCases,spellDecisionPoints,spellReviewedCases,spellExamples,updatedAt:new Date().toISOString(),
    confirmedCarry:rows.filter(c=>c.evidence.role==='recorded-pos-1'||(!c.evidence.role&&c.qualification.kind==='ranked')).length,
    scope:'按局计算的历史离线样本。主栏首见不是购买时间；出装频率和收益相关性不是推荐或因果。不得当作当前地图情报。',
    items:[...itemTimes].map(([item,ts])=>({item,heldInMatches:ts.length,totalMatches:rows.length,medianFirstHeldSeconds:median(ts)}))
      .sort((a,b)=>b.heldInMatches-a.heldInMatches || a.item.localeCompare(b.item)),
    retainedItemProgressions:[...pairs.values()].map(p=>({from:p.from,to:p.to,matches:p.times.length,medianSecondHeldAt:median(p.times)}))
      .sort((a,b)=>b.matches-a.matches||a.from.localeCompare(b.from)||a.to.localeCompare(b.to)),
    farming:[...periods].sort(([a],[b])=>a-b).map(([minute,ws])=>({minute,endMinute:minute+1,n:ws.length,
      medianLastHits:median(ws.map(w=>w.lh)),...income(ws),
      halves:[0,1].map(i=>({offset:i*30,n:ws.length,...income(ws.map(w=>w.halves[i]))}))})),
    lessons:rows.slice(0,6).flatMap(c=>(['items','farming'] as const).flatMap(topic=>{
      const o=c.observations.find(r=>r.topic===topic);
      return o?[{matchId:c.matchId,topic,reviewWindowSeconds:c.version===1?300:60,reviewUnit:c.version===3?'decision':c.version===2?'minute':'five-minutes',evidenceIds:o.evidenceIds,fact:o.fact,interpretation:o.interpretation,coachQuestion:o.coachQuestion}]:[];
    })), caseDirectory:'matches/'};
  writeJson(path.join(heroDir(root,patch,hero),'coach.json'),memory);return memory;
}
/** Deliberately requires a patch: a new patch never silently inherits an older hero's lessons. */
export function readCoachMemory(patch:string, hero:string,root=replayArea('daily-study')) {
  const file=path.join(heroDir(root,patch,hero),'coach.json');
  if(!fs.existsSync(file))return null;
  const m=readJson(file);
  if((m.version!==1&&m.version!==2)||m.patch!==patch||m.hero!==hero||!Array.isArray(m.lessons))throw Error('Invalid coach memory');
  return m.version===1?{...m,windowSeconds:300}:m;
}

/** The OS mutex serializes admissions; exclusive records also make crash/retry billing conservative. */
export function dayLedger(dayDir:string, hero:Hero) {
  const dir=path.join(dayDir,'review-slots',hero);
  fs.mkdirSync(dir,{recursive:true});
  const ids=()=>fs.readdirSync(dir).filter(f=>/^[1-9]\d{0,15}\.json$/.test(f));
  const has=(id:number)=>fs.existsSync(path.join(dir,`${id}.json`));
  return {has,size:()=>ids().length,reserve(id:number,limit:number,details:unknown){
    if(!Number.isSafeInteger(id)||id<=0||has(id)||ids().length>=limit)return false;
    try{writeJson(path.join(dir,`${id}.json`),details,true);return true;}
    catch(e){if((e as NodeJS.ErrnoException).code==='EEXIST')return false;throw e;}
  }};
}
