/** Offline only: no network/downloader, no model, no quota reservation. */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { pathToFileURL } from 'node:url';
import { heroKeys, validPatch, type Hero } from '../../src/replays/daily-study.ts';
import { fogEvidenceFor, fogSidecar, heroDir, listCases, rebuildMemory, writeJson, type Case } from '../../src/replays/daily-memory.ts';
import { decisionEvidence } from '../../src/replays/fog-decisions.ts';
import { replayArea } from '../../src/replays/storage.ts';
import { archivedInputs, backfillCase } from './backfill-daily-minutes.ts';
import { teamView } from './daily-evidence.ts';
import { sha } from './daily-sources.ts';

export async function backfillFog(root:string,c:Case){
  const dir=heroDir(root,c.patch,c.hero);
  if(fogEvidenceFor(dir,c))return false;
  backfillCase(root,c);
  const {evidence,facts}=archivedInputs(root,c),folder=path.join(root,'evidence',c.patch,c.hero,String(c.matchId));
  const pid=facts.heroPath[0].playerIdField,slot=c.qualification.slot;
  assert(Number.isInteger(slot)&&pid===Number(slot)%128+(Number(slot)<128?0:5),'Archived focal slot mismatch');
  const {view,teamViewSha256,teamViewFile}=await teamView(root,{id:c.matchId,hero:c.hero},folder,facts,pid);
  const decisions=decisionEvidence(evidence,facts,view,pid),{metadataSha256,factsSha256,demSha256}=c.provenance;
  writeJson(fogSidecar(dir,c.matchId),{source:'team-dem-backfill',metadataSha256,factsSha256,demSha256,teamViewSha256,teamViewFile,
    decisionsSha256:sha(JSON.stringify(decisions)),decisions},true);
  return true;
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  const {values:{patch,hero,match}}=parseArgs({options:{patch:{type:'string'},hero:{type:'string'},match:{type:'string'}}});
  assert(validPatch(patch)&&(!hero||heroKeys.includes(hero as Hero))&&(!match||(hero&&/^[1-9]\d{0,15}$/.test(match))),'Specify archived --patch [--hero HERO [--match ID]]');
  const root=replayArea('daily-study');assert(fs.existsSync(path.join(root,'memory',patch)),'No archived cases for patch');
  for(const h of hero? [hero as Hero]:heroKeys){
    const dir=heroDir(root,patch,h),rows=listCases(dir),selected=rows.filter(c=>!match||c.matchId===Number(match));
    if(match)assert.equal(selected.length,1,'Unknown archived match/hero');
    for(const c of selected)try{
      console.log(JSON.stringify({patch,hero:h,match:c.matchId,created:await backfillFog(root,c)}));
    }catch(e){process.exitCode=1;console.error(`${h} ${c.matchId}: ${(e as Error).message}`);}
    const m=rebuildMemory(root,patch,h,rows);
    console.log(JSON.stringify({hero:h,cases:m.samples,fogEvidenceCases:m.fogEvidenceCases,decisionPoints:m.decisionPoints,modelReviewed:m.reviewedDecisionPoints}));
  }
}
