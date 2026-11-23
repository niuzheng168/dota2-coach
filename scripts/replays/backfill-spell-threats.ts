/** Existing DEMs only. No model, downloads, previous answer rewrites, or daily admissions. */
import assert from 'node:assert/strict';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { pathToFileURL } from 'node:url';
import { heroKeys,validPatch,type Hero } from '../../src/replays/daily-study.ts';
import { fogEvidenceFor,heroDir,listCases,rebuildMemory,threatsFor,type Case } from '../../src/replays/daily-memory.ts';
import { replayArea } from '../../src/replays/storage.ts';
import { archivedInputs } from './backfill-daily-minutes.ts';
import { backfillFog } from './backfill-fog-decisions.ts';
import { threatLayer } from './daily-evidence.ts';

export async function backfillSpells(root:string,c:Case){
  await backfillFog(root,c);
  const dir=heroDir(root,c.patch,c.hero),d=fogEvidenceFor(dir,c);assert(d);
  if(threatsFor(dir,c,d))return false;
  const {facts}=archivedInputs(root,c),pid=facts.heroPath[0].playerIdField,slot=c.qualification.slot;
  assert(Number.isInteger(slot)&&pid===Number(slot)%128+(Number(slot)<128?0:5),'Spell focal slot mismatch');
  const {demSha256,metadataSha256,factsSha256}=c.provenance;
  assert(typeof demSha256==='string'&&typeof metadataSha256==='string'&&typeof factsSha256==='string');
  await threatLayer(root,d,path.join(root,'evidence',c.patch,c.hero,String(c.matchId)),facts,pid,{demSha256,metadataSha256,factsSha256});return true;
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  const {values:{patch,hero,match}}=parseArgs({options:{patch:{type:'string'},hero:{type:'string'},match:{type:'string'}}});
  assert(validPatch(patch)&&(!hero||heroKeys.includes(hero as Hero))&&(!match||(hero&&/^[1-9]\d{0,15}$/.test(match))),'Specify archived --patch [--hero HERO [--match ID]]');
  const root=replayArea('daily-study');
  for(const h of hero?[hero as Hero]:heroKeys){
    const rows=listCases(heroDir(root,patch,h)),selected=rows.filter(c=>!match||c.matchId===Number(match));if(match)assert(selected.length===1);
    for(const c of selected)try{console.log(JSON.stringify({hero:h,match:c.matchId,created:await backfillSpells(root,c)}));}
    catch(e){process.exitCode=1;console.error(`${h} ${c.matchId}: ${(e as Error).message}`);}
    const m=rebuildMemory(root,patch,h,rows);console.log(JSON.stringify({hero:h,spellEvidenceCases:m.spellEvidenceCases,spellDecisionPoints:m.spellDecisionPoints,spellReviewedCases:m.spellReviewedCases}));
  }
}
