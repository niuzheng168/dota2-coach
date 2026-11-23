/** Local-only migration of previously reviewed cases. No downloader, model client, scheduler or daily ledger. */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { pathToFileURL } from 'node:url';
import { compactEvidence, heroKeys, validPatch, type Candidate } from '../../src/replays/daily-study.ts';
import { evidenceDigest, heroDir, listCases, minuteEvidenceFor, minuteSidecar, rebuildMemory, writeJson, type Case } from '../../src/replays/daily-memory.ts';
import { replayArea } from '../../src/replays/storage.ts';

function verifiedJson(file:string,hash:unknown,limit:number){
  assert(typeof hash==='string'&&/^[a-f0-9]{64}$/.test(hash)&&fs.statSync(file).size<=limit,'Missing digest or oversized archive input');
  const bytes=fs.readFileSync(file);assert.equal(createHash('sha256').update(bytes).digest('hex'),hash,`Source hash mismatch: ${path.basename(file)}`);
  return JSON.parse(bytes.toString('utf8'));
}
export function archivedInputs(root:string,c:Case){
  const {matchId:id,provenance:p,qualification:q}=c;
  assert(q.id===id&&q.hero===c.hero&&typeof q.start==='number'&&q.start===c.evidence.start&&['ranked','league','rank-probe'].includes(String(q.kind))&&
    typeof q.patchStart==='number'&&q.start>=q.patchStart,'Unverified original match qualification');
  const api=verifiedJson(path.join(root,'raw',String(id),`${id}.opendota.json`),p.metadataSha256,8*1024**2);
  const facts=verifiedJson(path.join(root,'evidence',c.patch,c.hero,String(id),'facts.json'),p.factsSha256,512*1024**2);
  assert(api.start_time===q.start&&facts.gameBuild===c.evidence.build&&typeof p.demSha256==='string','Mismatched archived match/build');
  const evidence=compactEvidence(q as Candidate,api,facts,p.demSha256,{name:c.patch,from:q.patchStart});
  // This is a rebucketing of recorded counters, not a division of a 5-minute total by five.
  for(const key of ['lhGain','laneGold','neutralGold'] as const){
    assert.equal(evidence.windows.reduce((s,w)=>s+w[key],0),c.evidence.windows.reduce((s,w)=>s+w[key],0),`Whole-game ${key} changed`);
  }
  return {evidence,facts};
}
export function backfillCase(root:string,c:Case){
  const dir=heroDir(root,c.patch,c.hero);
  if(minuteEvidenceFor(dir,c))return 'existing';
  assert(c.version===1);
  const {matchId:id,provenance:p}=c,{evidence}=archivedInputs(root,c);
  writeJson(minuteSidecar(dir,id),{source:'verified-facts-backfill',factsSha256:p.factsSha256,metadataSha256:p.metadataSha256,
    demSha256:p.demSha256,evidenceSha256:evidenceDigest(evidence),evidence},true);
  return 'created';
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  const {values:{patch}}=parseArgs({options:{patch:{type:'string'}}});
  assert(validPatch(patch),'Specify the archived patch explicitly with --patch');
  const root=replayArea('daily-study');assert(fs.existsSync(path.join(root,'memory',patch)),'No archived memory for this patch');
  for(const h of heroKeys){
    const rows=listCases(heroDir(root,patch,h));let created=0;
    for(const c of rows){
      try{if(backfillCase(root,c)==='created')created++;}
      catch(e){console.error(`${patch} ${h} ${c.matchId}: ${(e as Error).message}`);process.exitCode=1;}
    }
    const m=rebuildMemory(root,patch,h,rows);
    console.log(JSON.stringify({patch,hero:h,cases:rows.length,created,minuteEvidenceCases:m.minuteEvidenceCases,minuteModelReviews:m.minuteReviewedCases}));
  }
}
