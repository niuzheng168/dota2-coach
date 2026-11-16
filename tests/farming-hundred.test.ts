import assert from 'node:assert/strict';
import fs from 'node:fs';
import {createHash} from 'node:crypto';
import {test} from 'node:test';
test('published hundred-case study has unique complete validated cases and anchored model reviews',()=>{
  const read=(p:string)=>JSON.parse(fs.readFileSync(p,'utf8'));
  const index=read('data/luna-hundred-index.json'),audit=read('data/luna-hundred-audit.json'),analysis=read('data/luna-hundred-analysis.json');
  assert.equal(index.matches.length,100);assert.equal(new Set(index.matches.map((m:any)=>m.matchId)).size,100);
  assert.equal(audit.progress.complete,true);assert.equal(audit.progress.completed.length,100);
  assert.equal(index.informationScope,'omniscient-offline-replay-only');assert.equal(index.stats.modelReviews,100);
  const ids=new Set(audit.catalog.matches.map((m:any)=>m.matchId));
  for(const b of audit.catalog.patchEvidence.builds)
    assert.equal(createHash('sha256').update(b.text).digest('hex'),b.sha256);
  for(const m of index.matches){
    assert.ok(ids.has(m.matchId));assert.ok(m.complete&&m.validation.lastHitParity);
    assert.equal(m.validation.camps,28);assert.equal(m.validation.towers,22);
    assert.ok(m.startTime>=audit.catalog.patchEvidence.from);
    assert.ok(audit.catalog.patchEvidence.builds.some((b:any)=>b.build===m.gameBuild));
    assert.ok(m.review.observations.length>=4);
    for(const o of [...m.review.observations,...m.review.counterexamples])for(const id of o.evidenceIds){
      assert.ok(id.startsWith(`${m.matchId}:w`));assert.ok(Number(id.split(':w')[1])*30<m.duration);
    }
    assert.equal(createHash('sha256').update(JSON.stringify(m.review)).digest('hex'),
      audit.reviewHashes.find((r:any)=>r.matchId===m.matchId).resultSha256);
  }
  assert.equal(index.stats.professionalLabelled,index.matches.filter((m:any)=>m.providerPro).length);
  assert.equal(index.stats.full10to20Windows,index.matches.filter((m:any)=>m.duration>=1200).length);
  assert.equal(analysis.rows.length,100);assert.equal(analysis.distribution.fullWindowN,index.stats.full10to20Windows);
});
