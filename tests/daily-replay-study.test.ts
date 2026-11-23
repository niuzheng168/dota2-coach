import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import fs from 'node:fs';
import http from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test, type TestContext } from 'node:test';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import { createSandboxServer } from '../src/sandbox/server.ts';
import { checkedReview, compactEvidence, currentPatch, d2ptCandidates, d2ptLineups, hkDay, majorRelease, playerCandidates, proCandidates,
  pubCandidates, verifyMatch, type Candidate } from '../src/replays/daily-study.ts';
import { dayLedger, fogSidecar, heroDir, listCases, readCoachMemory, readCoachMinute, rebuildMemory, writeJson, type Case } from '../src/replays/daily-memory.ts';
import { minuteAnswerFile } from '../src/replays/daily-minutes.ts';
import { decisionEvidence, fogAnswerFile, type TeamView } from '../src/replays/fog-decisions.ts';
import { baseScenarios,checkedSpellTape, spellThreats, burst5, stunUnion, threatAnswerFile, type SpellTape } from '../src/replays/spell-threats.ts';
import { digestJson, loadRules, rulesFile, type Rules } from '../src/replays/spell-rules.ts';
import { spellRule } from '../scripts/replays/prepare-spell-rules.ts';
import { checkedFogReview, fogReviewInput } from '../src/replays/fog-review.ts';
import { backfillFog } from '../scripts/replays/backfill-fog-decisions.ts';
import { backfillSpells } from '../scripts/replays/backfill-spell-threats.ts';
import { backfillCase } from '../scripts/replays/backfill-daily-minutes.ts';

const day=86400, patch={name:'7.42b',from:20000*day},now=patch.from+4*day,start=now-day;
const c:Candidate={id:45,hero:'sven',start,kind:'ranked',mmr:8100,radiant:true,account:19,source:'fixture',patchLabel:patch.name};
const api={match_id:45,start_time:start,duration:600,lobby_type:7,radiant_win:true,
  players:[0,1,2,3,4,128,129,130,131,132].map((p,i)=>({player_slot:p,hero_id:i===0?18:100+i,account_id:i===0?19:200+i,last_hits:i===0?90:i}))};
const listing={hero_id:18,match_id:45,lobby_type:7,patch:patch.name,position:'pos 1',account_id:19,is_radiant:1,mmr:8100,activate_time:start};
const h=(t:number)=>({name:'npc_dota_hero_sven',team:2,playerIdField:0,lifeState:0,position:{x:2*t,y:t},
  economy:{m_iLastHitCount:Math.round(90*t/600),m_iNetWorth:t*10,m_iCreepKillGold:t*3,m_iNeutralKillGold:t*2},
  items:[{slot:0,name:'item_branches'},...(t>=300?[{slot:1,name:'item_mask_of_madness'}]:[]),...(t>=600?[{slot:2,name:'item_blink'}]:[])]});
const hash='a'.repeat(64);
function facts(){return {schemaVersion:4,complete:true,matchId:45,parser:{name:'gem-dota',version:'0.14.0'},gameBuild:9000,
  demSha256:hash,postGameTick:18100,durationSeconds:600,heroPath:[h(0)],frames:Array.from({length:601},(_,t)=>({tick:t*30+100,gameTime:t,
    heroes:[h(t),...(t===600?api.players.slice(1).map((p,i)=>({name:`other_${p.hero_id}`,team:i<4?2:3,playerIdField:i+1,
      economy:{m_iLastHitCount:p.last_hits}})):[])]}))};}
function shortReplay(end:number){
  const f=facts(),frames=f.frames.filter(v=>v.gameTime<end),final=f.frames.at(-1)!;
  const last=h(end),tick=Math.round(end*30)+100;
  frames.push({tick,gameTime:end,heroes:[last,...final.heroes.slice(1)]});
  return {f:{...f,durationSeconds:end,postGameTick:tick,frames},
    meta:{...api,duration:Math.round(end),players:[{...api.players[0],last_hits:last.economy.m_iLastHitCount},...api.players.slice(1)]}};
}
function temp(t:TestContext){
  const base=fs.realpathSync(tmpdir()),root=fs.mkdtempSync(path.join(base,'dota-daily-test-'));
  t.after(()=>{const actual=fs.realpathSync(root);assert.equal(path.dirname(actual),base);
    assert.match(path.basename(actual),/^dota-daily-test-[a-zA-Z0-9]{6}$/);fs.rmSync(actual,{recursive:true});});return root;
}
const roster=Array.from({length:5},(_,i)=>({slot:i+5,hero:`npc_dota_hero_${['axe','puck','drow_ranger','luna','rubick'][i]}`}));
const markers=(x:number)=>[{xy:[x,20] as [number,number],alive:true}];
function fakeMap(t:number):TeamView['frames'][number]{
  return {tick:100+t*30,bodyXY:[0,0],enemies:roster.map((r,i)=>({...r,state:i===0?'visible':'hidden',markers:i===0?markers(6500):[]})),
    allies:[{hero:'npc_dota_hero_lion',xy:[6400,20]}],farmCoverage:'sampled-visible-only',lane:{xy:[400,20],
      straightDistance:400,units:[['npc_dota_creep_badguys_melee',95,450]],listedGross:[31,38],nearestAllyDistance:6000,nearestAlliedTowerDistance:4400},neutral:null};
}
const fogResponse=(cards:{key:string}[])=>({decisionNotes:cards.map(c=>({key:c.key,preferred:'retreat',plan:'先向己方塔侧退，再看先手位。',
  alternative:'lane',alternativePlan:'仅收已在射程内的残兵，不能多跟一波。',mapRead:'线外的关键控制手没有新视野。',tradeoff:'少一刀换不先过河暴露。',
  exitSignal:'对面先手明确出现在远端再接线。',confidence:'low'})),observations:['items','farming'].map(topic=>({topic,fact:'实录',interpretation:'条件解释',coachQuestion:'谁还能到？',keys:[cards[0].key]}))});
test('current means the latest released exact Valve subpatch and Hong Kong calendar day',()=>{
  const input={success:true,patches:[{patch_number:'7.42',patch_timestamp:patch.from-1},
    {patch_number:'7.42c',patch_timestamp:now+1},{patch_number:patch.name,patch_timestamp:patch.from}]};
  assert.deepEqual(currentPatch(input,now),patch);
  assert.throws(()=>currentPatch({...input,patches:[...input.patches,{patch_number:'unknown',patch_timestamp:now-1}]},now));
  assert.throws(()=>currentPatch({success:false,patches:[]},now));
  assert.equal(hkDay(new Date('2026-11-22T16:00:00Z')),'2026-11-23');
  assert.deepEqual(majorRelease([{name:'7.42',id:61,date:new Date(patch.from*1000).toISOString()}],patch),{name:'7.42',id:61,from:patch.from});
});
test('ranked carry directories exclude prior patches, placeholders and pro flags; lineup transfers lobby MMR, not individual ranks',()=>{
  const rows=[listing,{...listing,patch:'7.42'},{...listing,activate_time:patch.from-1},{...listing,mmr:6500,is_pro:1},
    {...listing,mmr:50000},{...listing,mmr:7290,leaderboard_rank:200,match_id:46}];
  assert.deepEqual(d2ptCandidates(rows,'sven',patch,now,'fixture').map(c=>c.id),[45,46]);
  const players=Array.from({length:10},(_,i)=>({hero_npc:i===1?'sven':'other',position:'pos 1',account_id:19+i,is_radiant:0}));
  const luna={...listing,hero_id:48,players,leaderboard_rank:1};
  assert.equal(d2ptLineups([luna],'sven',patch,now,'fixture')[0].account,20);
  assert.deepEqual(d2ptLineups([{...luna,mmr:6700}],'sven',patch,now,'fixture'),[]);
  // Older than a week is still eligible if a real DEM is available in this same patch.
  assert.equal(d2ptCandidates([{...listing,activate_time:now-8*day}],'sven',{...patch,from:now-20*day},now,'fixture').length,1);
});
test('OpenDota requires independent event tier or top-200 focal account, verifies both clocks, ID, roster, side and exact patch',()=>{
  assert.equal(verifyMatch(c,api,patch,now).hero_id,18);
  assert.throws(()=>verifyMatch(c,{...api,patch:60},{...patch,odRelease:{id:61,name:'7.42',from:patch.from}},now));
  for(const bad of [{...c,patchLabel:'7.42a'},{...c,start:start-1},{...c,account:29},{...c,radiant:false}])assert.throws(()=>verifyMatch(bad,api,patch,now));
  const p=proCandidates([{match_id:45,start_time:start,leagueid:8}],'sven',patch,now,'pro')[0];
  assert.throws(()=>verifyMatch(p,{...api,leagueid:8,lobby_type:1},patch,now,'amateur'));
  assert.throws(()=>verifyMatch(p,{...api,leagueid:8,lobby_type:1,league:{tier:'amateur'}},patch,now,'premium'));
  assert.equal(verifyMatch(p,{...api,leagueid:8,lobby_type:1},patch,now,'professional').hero_id,18);
  const pub=pubCandidates([{match_id:45,start_time:start,radiant_team:[18],dire_team:[],avg_rank_tier:80,num_rank_tier:10,lobby_type:7}],'sven',patch,now,'pub')[0];
  assert.throws(()=>verifyMatch(pub,api,patch,now));assert.throws(()=>verifyMatch(pub,api,patch,now,undefined,{profile:{account_id:6},leaderboard_rank:1}));
  assert.equal(verifyMatch(pub,api,patch,now,undefined,{profile:{account_id:19},leaderboard_rank:200}).account_id,19);
  assert.throws(()=>verifyMatch({...pub,start:patch.from+100},{...api,start_time:patch.from+100},patch,now,undefined,{profile:{account_id:19},leaderboard_rank:2}));
  assert.equal(playerCandidates([{match_id:45,start_time:start,hero_id:18,lobby_type:7}],19,'sven',patch,now,'players')[0].kind,'rank-probe');
});
test('a real complete focal extraction and all ten last hits are required, model can cite only this case and both topics',()=>{
  const f=facts(), e=compactEvidence(c,api,f,hash,patch);
  assert.equal(e.windows.length,10);
  assert.ok(e.windows.every((w,i)=>w.minute===i&&!w.partial&&w.lhGain===9&&w.laneGold===180&&w.neutralGold===120&&w.halves.length===2));
  assert.deepEqual(e.windows[0].halves.map(h=>[h.offset,h.lhGain,h.laneGold,h.neutralGold,h.kills]),[[0,5,90,60,null],[30,4,90,60,null]]);
  assert.equal(e.firstMainSlot.find(i=>i.item==='item_blink')?.t,600);
  assert.throws(()=>compactEvidence(c,api,{...f,heroPath:[{...h(0),name:'npc_dota_hero_luna'}]},hash,patch));
  assert.throws(()=>compactEvidence(c,api,{...f,complete:false},hash,patch));
  assert.throws(()=>compactEvidence(c,api,{...f,frames:f.frames.filter((_,i)=>i<10||i>40)},hash,patch));
  assert.throws(()=>compactEvidence(c,api,f,'b'.repeat(64),patch));
  assert.throws(()=>compactEvidence(c,{...api,players:api.players.map((p,i)=>({...p,last_hits:p.last_hits+(i===8?1:0)}))},f,hash,patch));
  const observations=['items','farming'].map(topic=>({topic,fact:'可核查事实',interpretation:'可能解释',coachQuestion:'条件是否相同？',evidenceIds:[e.windows[0].id]}));
  const good={matchId:c.id,hero:c.hero,observations,minuteNotes:e.windows.map(w=>({minute:w.minute,reading:'实录线野比较'}))};
  assert.equal(checkedReview(good,e).observations.length,2);assert.equal(checkedReview(good,e).minuteNotes[0].evidenceId,'45:sven:m0');
  assert.throws(()=>checkedReview({...good,minuteNotes:good.minuteNotes.slice(1)},e));
  assert.throws(()=>checkedReview({...good,minuteNotes:good.minuteNotes.map(()=>good.minuteNotes[0])},e));
  assert.throws(()=>checkedReview({...good,observations:observations.map(o=>({...o,evidenceIds:['45:sven:w0']}))},e));
  assert.throws(()=>checkedReview({...good,observations:[observations[0],observations[0]]},e));
  assert.throws(()=>checkedReview({...good,hero:'luna'},e));
  assert.throws(()=>checkedReview({...good,observations:[observations[0],{...observations[1],evidenceIds:['fake']}]},e));
});
test('clock-aligned half-minutes keep partial tails and exact terminal totals; no rounding a real tick into the next period',()=>{
  for(const [end,minutes,halves,partial] of [[59.97,1,2,true],[60,1,2,false],[60.00001,1,2,false],
    [60.04,2,1,true],[90,2,1,true],[90.04,2,2,true],[120,2,2,false]] as const){
    const {f,meta}=shortReplay(end);
    // Economy fields are integer-valued in real DEMs, including the terminal frame.
    for(const fr of f.frames){const e=fr.heroes[0].economy as ReturnType<typeof h>['economy'];e.m_iCreepKillGold=Math.round(e.m_iCreepKillGold);e.m_iNeutralKillGold=Math.round(e.m_iNeutralKillGold);}
    const ws=compactEvidence(c,meta,f,hash,patch).windows,tail=ws.at(-1)!;
    assert.equal(ws.length,minutes);assert.equal(tail.halves.length,halves);assert.equal(tail.partial,partial);
    assert.equal(ws.reduce((s,w)=>s+w.laneGold,0),Math.round(end*3));assert.equal(ws.reduce((s,w)=>s+w.lhGain,0),meta.players[0].last_hits);
    assert.ok(ws.every(w=>w.neutralGold===w.halves.reduce((s,h)=>s+h.neutralGold,0)));
  }
});
test('kill events use game clock, :30 / :00 boundary ownership and actual actors rather than timer-inferred waves or camps',()=>{
  const {f,meta}=shortReplay(75),at=(t:number)=>100+t*30+(t>=15?90:0);
  const enemy='npc_dota_creep_badguys_melee',neutral='npc_dota_neutral_kobold',own='npc_dota_creep_goodguys_melee';
  const kill=(t:number,target:string,team:number,known=true,illusion=false)=>({log_type:'DEATH',tick:at(t),attacker_name:'npc_dota_hero_sven',
    attacker_team:2,target_name:target,target_team:team,attacker_is_illusion_present:known,attacker_is_illusion:illusion});
  const source={...f,postGameTick:at(75),frames:f.frames.map(fr=>({...fr,tick:at(fr.gameTime)})),clock:{game_start_tick:100,pauses:[{start_tick:550,end_tick:640}]},
    svenCombatLog:[kill(75,neutral,4),kill(30,enemy,3),kill(30,neutral,4),kill(29,neutral,4),kill(33,neutral,4,true,true),
      kill(43,enemy,3,false),kill(50,own,2),kill(54,neutral,2),kill(60,enemy,3),kill(76,neutral,4)]};
  const e=compactEvidence(c,meta,source,hash,patch),[a,b]=e.windows[0].halves,tail=e.windows[1];
  assert.equal(a.kills?.bodyOrder,'N');assert.equal(b.kills?.bodyOrder,'L+N');assert.equal(b.kills?.illusion.neutral,1);
  assert.deepEqual(b.kills?.unknown,{lane:1,neutral:0});assert.deepEqual(b.kills?.body,{lane:1,neutral:1});
  assert.equal(tail.halves[0].kills?.bodyOrder,'L>N');assert.equal(tail.halves.length,1);
  const r={matchId:45,hero:'sven',minuteNotes:e.windows.map(w=>({minute:w.minute,reading:'r'})),
    observations:['items','farming'].map(topic=>({topic,fact:'f',interpretation:'i',coachQuestion:'q',evidenceIds:[tail.id+':b']}))};
  assert.throws(()=>checkedReview(r,e));for(const o of r.observations)o.evidenceIds=[tail.id+':a'];assert.doesNotThrow(()=>checkedReview(r,e));
});
test('legacy five-minute reviews stay immutable; source-verified minute and team-map backfill add no paid reviews or quotas',async t=>{
  const root=temp(t),dir=heroDir(root,patch.name,'sven'),{f,meta}=shortReplay(75),raw=path.join(root,'raw','45'),
    ev=path.join(root,'evidence',patch.name,'sven','45');
  const file=path.join(dir,'matches','45.json'),apiFile=path.join(raw,'45.opendota.json'),factsFile=path.join(ev,'facts.json');
  const digest=(p:string)=>createHash('sha256').update(fs.readFileSync(p)).digest('hex');
  const ff={...f,clock:{game_start_tick:100,pauses:[]}};
  writeJson(apiFile,meta);writeJson(factsFile,ff);
  const record:Case={version:1,day:'2026-11-23',patch:patch.name,hero:'sven',matchId:45,completedAt:'2026-11-22T16:30:00Z',
    qualification:{...c,slot:0,patchStart:patch.from},provenance:{demSha256:hash,metadataSha256:digest(apiFile),factsSha256:digest(factsFile)},
    model:{provider:'fixture',model:'fixture',reasoningEffort:'fixture'},observations:[{topic:'farming',fact:'old',interpretation:'i',coachQuestion:'q',evidenceIds:['45:sven:w0']}],
    evidence:{patch:patch.name,hero:'sven',matchId:45,start:meta.start_time,duration:75,build:f.gameBuild,team:'radiant',won:true,scope:'legacy',firstMainSlot:[],
      windows:[{id:'45:sven:w0',from:0,to:75,lh:11,lhGain:11,netWorth:750,laneGold:225,neutralGold:150,items:[],path:[]}]}};
  writeJson(file,record,true);const oldHash=digest(file);
  const before=rebuildMemory(root,patch.name,'sven');assert.equal(before.farming.length,0);assert.equal(before.samples,1);
  assert.equal(readCoachMinute(patch.name,'sven',45,1,root),null);
  fs.appendFileSync(factsFile,' ');assert.throws(()=>backfillCase(root,record),/hash mismatch/);writeJson(factsFile,ff);
  assert.equal(backfillCase(root,record),'created');assert.equal(backfillCase(root,record),'existing');
  const after=rebuildMemory(root,patch.name,'sven');assert.equal(digest(file),oldHash);assert.equal(after.samples,1);
  assert.equal(after.minuteEvidenceCases,1);assert.equal(after.minuteReviewedCases,0);assert.equal(after.farming.length,1);
  assert.equal(after.lessons[0].reviewWindowSeconds,300);assert.deepEqual(after.lessons[0].evidenceIds,['45:sven:w0']);
  const tail=readCoachMinute(patch.name,'sven',45,1,root)!;assert.equal(tail.window.halves[0].partial,true);
  assert.equal(tail.modelReading,null);assert.equal(tail.source,'verified-facts-backfill');assert.ok(!fs.existsSync(path.join(root,'runs')));
  const viewfile=path.join(ev,'team-map-v2.json'),fsamples=Array.from({length:76},(_,s)=>fakeMap(s));
  fsamples[60].enemies[0]={...fsamples[60].enemies[0],state:'hidden',markers:[]};
  const v={format:'team-map-v2',hero:'sven',pid:0,team:2,matchId:45,build:9000,demSha256:hash,postGameTick:ff.postGameTick,frames:fsamples,combat:[]};
  writeJson(viewfile,{...v,team:3});await assert.rejects(backfillFog(root,record),/mismatch/);
  writeJson(viewfile,v);assert.equal(await backfillFog(root,record),true);assert.equal(await backfillFog(root,record),false);
  const row=readCoachMinute(patch.name,'sven',45,1,root)!,point=row.decisions![0];
  assert.equal(row.decisionSource,'team-dem-backfill');assert.equal(point.modelJudgement,null);assert.equal(point.offlineCaseNote,null);
  assert.ok(point.triggers.includes('all-off-map'));const s=JSON.parse(fs.readFileSync(fogSidecar(dir,45),'utf8'));
  writeJson(path.join(dir,'field-notes','45.json'),{source:'codex-offline-case-study',decisionsSha256:s.decisionsSha256,
    notes:[{id:point.id,mapRead:'先手消失',assessment:'让线',changeIf:'交手时再评估'}]},true);
  const revised=rebuildMemory(root,patch.name,'sven');
  assert.equal(digest(file),oldHash);assert.equal(revised.fogEvidenceCases,1);assert.equal(revised.reviewedDecisionPoints,0);
  assert.equal(revised.situations['all-off-map'].examples[0].judgement,'让线');assert.equal(revised.decisionExamples[0].mapRead,'先手消失');
  assert.equal(readCoachMinute(patch.name,'sven',45,1,root)!.decisions![0].modelJudgement,null);
  assert.ok(!fs.existsSync(path.join(root,'runs')));
  writeJson(fogSidecar(dir,45),{...s,demSha256:'b'.repeat(64)});assert.throws(()=>readCoachMinute(patch.name,'sven',45,1,root),/Mismatched team-view/);
});
test('publishing a refreshed minute summary survives a sharing violation without removing the previous summary',t=>{
  const root=temp(t),p=path.join(root,'coach.json');writeJson(p,{n:1});
  let tries=0;const rename=fs.renameSync;
  t.mock.method(fs,'renameSync',(from:fs.PathLike,to:fs.PathLike)=>{
    if(to===p && ++tries===1){assert.deepEqual(JSON.parse(fs.readFileSync(p,'utf8')),{n:1});throw Object.assign(Error('share busy'),{code:'EBUSY'});}
    rename(from,to);
  });
  writeJson(p,{n:2});assert.equal(tries,2);assert.deepEqual(JSON.parse(fs.readFileSync(p,'utf8')),{n:2});
});
test('fog decisions separate hidden/unknown/ambiguous markers, prefix map evidence and after-the-fact farm outcomes',()=>{
  const {f,meta}=shortReplay(75),clock={game_start_tick:100,pauses:[]},tick=(s:number)=>100+s*30;
  const kill=(t:number,lane:boolean,illusion=false)=>({log_type:'DEATH',tick:tick(t),attacker_name:'npc_dota_hero_sven',attacker_team:2,
    attacker_is_illusion_present:true,attacker_is_illusion:illusion,target_team:lane?3:4,target_name:lane?'npc_dota_creep_badguys_melee':'npc_dota_neutral_kobold'});
  const src={...f,clock,svenCombatLog:[kill(4,false),kill(7,true,true),kill(9,true),kill(10,false),kill(10,true),kill(14,false),kill(17,true),kill(18,false)]};
  const team:TeamView={format:'team-map-v2',matchId:45,build:9000,demSha256:hash,postGameTick:tick(75),hero:'sven',team:2,pid:0,
    frames:Array.from({length:76},(_,s)=>fakeMap(s)),combat:[[tick(4),'npc_dota_hero_axe','npc_dota_hero_lion','DAMAGE'],
      [tick(12),'npc_dota_hero_puck','npc_dota_hero_lion','DAMAGE']]};
  team.frames[14].enemies[0].state='hidden';team.frames[14].enemies[0].markers=[];
  team.frames[17].enemies[0].state='unknown';team.frames[17].enemies[0].markers=[];
  team.frames[60].enemies[0].markers.push(...markers(6300));
  const e=compactEvidence(c,meta,src,hash,patch),d=decisionEvidence(e,src,team,0),get=(t:number)=>d.points.find(p=>p.before.at===t)!;
  assert.deepEqual(d.points.filter(p=>p.triggers.includes('between-farm-types')).map(p=>p.before.at),[4,14,17]); // no ordered L/N at one tick
  const dark=get(14);assert.ok(dark.triggers.includes('all-off-map'));assert.equal(dark.before.map!.enemies[0].now,null);
  assert.equal(dark.before.map!.enemies[0].lastSeen?.secondsAgo,1);assert.deepEqual(dark.before.map!.enemies[0].lastSeen?.xy,[6500,20]);
  assert.deepEqual(dark.before.map!.localHeroExchanges.map(x=>[x.enemy,x.lastSecondsAgo,x.events]),[['npc_dota_hero_axe',10,1]]);
  assert.equal(dark.after.nextBodyLastHit?.kind,'lane');assert.ok(!get(17).triggers.includes('all-off-map'));
  assert.equal(get(60).before.map!.enemies[0].now,null);assert.equal(get(60).before.map!.enemies[0].markers.length,2); // cannot select real body
  const input=JSON.parse(fogReviewInput(d,e).split('\n')[1]);assert.ok(!['matchId','won','role','mmr','minutes'].some(k=>k in input));
  assert.ok(input.cards.every((x:any)=>!('after' in x)&&!('triggers' in x)&&!('nextBodyLastHit' in x)&&!('laneGold' in x)));
  assert.ok(input.cards.every((x:any)=>x.pastIncome.every((h:any)=>h.closes<=x.at&&h.to<=x.at)&&x.recentFirstMainSlot.every((i:any)=>i.t<=x.at)));
  const future=structuredClone(team);future.frames[15].enemies[0].markers=markers(9999);
  const variant=decisionEvidence({...e,won:!e.won},src,future,0);assert.deepEqual(variant.points.find(p=>p.id===dark.id)!.before,dark.before);
  const invalid=structuredClone(team);(invalid.frames[14].enemies[0] as any).xy=[555,444];
  assert.throws(()=>decisionEvidence(e,src,invalid,0),/unmasked/);
  assert.throws(()=>decisionEvidence(e,src,{...team,team:3},0),/mismatch/);
  assert.throws(()=>decisionEvidence(e,src,{...team,frames:team.frames.slice(0,30)},0),/terminal/);
  const good=fogResponse(input.cards);good.decisionNotes.reverse();assert.equal(checkedFogReview(good,d,e).minuteNotes.length,2);
  assert.throws(()=>checkedFogReview({...good,decisionNotes:good.decisionNotes.slice(1)},d,e),/coverage/);
  assert.throws(()=>checkedFogReview({...good,decisionNotes:good.decisionNotes.map(()=>good.decisionNotes[0])},d,e));
  assert.throws(()=>checkedFogReview({...good,observations:good.observations.map(o=>({...o,keys:['other']}))},d,e));
  const n=good.decisionNotes.find(n=>n.key===get(17).key)!;n.confidence='high';assert.throws(()=>checkedFogReview(good,d,e),/confidence/);
});
test('damage, observed controls, enemy capability and self-defence use only the right body, tick and build; rank audit is not sent',t=>{
  const {f,meta}=shortReplay(75),src={...f,clock:{game_start_tick:100,pauses:[]}},at=(s:number)=>100+s*30;
  const e=compactEvidence(c,meta,src,hash,patch),team:TeamView={format:'team-map-v2',hero:'sven',pid:0,team:2,matchId:45,build:9000,demSha256:hash,
    postGameTick:at(75),frames:Array.from({length:76},(_,s)=>fakeMap(s)),combat:[]},d=decisionEvidence(e,src,team,0);
  const a='npc_dota_hero_axe',me='npc_dota_hero_sven',skill='axe_fixture';
  const v:SpellTape={...team,format:'team-spells-v2',frames:team.frames.map(f=>({tick:f.tick,ownItems:[[1,'item_magic_wand',12,0]],
    publicSheets:f.tick===at(54)?[[a,5,156]]:[]})),combat:[
    ['hit',at(61),a,skill,me,900,'pure',true],['cast',at(50),a,skill,2,'roster-label'],['hit',at(51),a,skill,me,58,'magical',true],
    ['hit',at(51),a,skill,me,700,'magical',false],['hit',at(52),a,skill,'npc_dota_hero_lion',275,'magical',false],
    ['hit',at(54),a,'',me,33,'physical',true],['add',at(50),a,'modifier_a',2],['remove',at(52),a,'modifier_a',2],
    ['add',at(51),a,'modifier_b',2],['remove',at(53),a,'modifier_b',2]]};
  const rule=spellRule({AbilityUnitDamageType:'DAMAGE_TYPE_MAGICAL',AbilityDamage:'80 160',AbilityValues:{duration:{value:'1.2 1.5',special_bonus_fixture:'+0.2'},
    conditional_damage:{special_bonus_scepter:'=100'}}},1);
  assert.deepEqual(rule.values.conditional_damage,{base:[],conditional:{special_bonus_scepter:'=100'}});
  const rules:Rules={format:'valve-spell-rules-v1',patch:patch.name,build:9000,steamInfSha256:hash,commit:'a'.repeat(40),
    heroesTree:'b'.repeat(40),itemsBlob:'c'.repeat(40),heroes:{[a]:{blob:'d'.repeat(40),spells:{[skill]:rule}}},
    items:{item_magic_wand:spellRule({AbilityValues:{restore_per_charge:'15'}},null)}};
  const th=spellThreats(d,src,v,0,rules),sp=th.points.find(p=>p.at===60)!,b=sp.before.strongestObserved5sInLast90s!;
  assert.equal(b.loggedDamage,91);assert.equal(b.nonOverlappingStunSeconds,3);assert.equal(sp.before.ownDefence[0].ifActivatedHP,180);
  const foe=sp.before.opponents.find(o=>o.hero===a)!;
  assert.deepEqual(foe.singleAppearanceSheet,{age:6,levelAtSighting:5,manaAtSighting:156});assert.equal(foe.spells[0].lastTeamImpact!.amount,275);
  assert.equal(foe.spells[0].lastHitToMyBody!.amount,58);assert.equal(sp.auditRanks[0].engineRankAtCast,2);
  const input=fogReviewInput(d,e,th);assert.doesNotMatch(input,/auditRanks|engineRankAtCast|900,"pure"/);
  const variant=structuredClone(v);(variant.combat[1] as any)[4]=3;assert.equal(input,fogReviewInput(d,e,spellThreats(d,src,variant,0,rules)));
  const raw=fogResponse(th.points),checked={...raw,decisionNotes:raw.decisionNotes.map(n=>({...n,threatRead:'先比控制和伤害，再决定是否接线。',
    threatRefs:[`rule/${a}/${skill}`],threatMetric:{ref:`rule/${a}/${skill}`,field:'AbilityDamage@2',value:160}}))};
  assert.ok(checkedFogReview(checked,d,e,th).decisionNotes.every(n=>n.threatMetric?.value===160));
  assert.throws(()=>checkedFogReview(raw,d,e,th),/threat assessment/);
  let n=checked.decisionNotes.find(n=>n.key===sp.key)!;n.threatRefs=[`seen/${a}/${skill}`];n.threatMetric={ref:n.threatRefs[0],field:'bodyDamage',value:275};
  assert.throws(()=>checkedFogReview(checked,d,e,th),/number does not match/);n.threatMetric.value=58;assert.doesNotThrow(()=>checkedFogReview(checked,d,e,th));
  checked.decisionNotes[0]={...n,key:checked.decisionNotes[0].key};assert.throws(()=>checkedFogReview(checked,d,e,th),/ungrounded/);
  assert.throws(()=>checkedSpellTape({...v,team:3},src,'sven',0),/do not match/);
  assert.throws(()=>spellThreats(d,src,v,0,{...rules,patch:'7.41f'}),/Cross-patch/);
  assert.equal(spellThreats(d,src,v,0,null).points.find(p=>p.at===60)!.before.ownDefence[0].ifActivatedHP,null);
  const root=temp(t);writeJson(rulesFile(root,patch.name,9000),{rules,sha256:digestJson(rules)});
  assert.equal(loadRules(root,patch.name,9000)?.commit,rules.commit);assert.equal(loadRules(root,'7.41f',9000),null);
  writeJson(rulesFile(root,patch.name,9000),{rules,sha256:hash});assert.throws(()=>loadRules(root,patch.name,9000),/Corrupt/);
  const receipt=(at:number,value:number)=>({tick:at*30,at,value,hero:a,spell:skill,type:'magical'});
  assert.equal(burst5([receipt(0,90),receipt(5.1,99)])!.loggedDamage,99);assert.equal(stunUnion([{from:0,to:3},{from:2,to:4}]),4);
  const more=structuredClone(th),mp=more.points.find(x=>x.at===60)!,kit=more.definitions!.heroes[a].spells;
  kit[skill].values.AbilityCooldown={base:[14,12]};kit[skill].values.AbilityManaCost={base:[100,125]};
  kit.axe_second=spellRule({AbilityManaCost:'85'},2);
  mp.before.opponents[0].spells.push({...mp.before.opponents[0].spells[0],spell:'axe_second'});
  mp.before.strongestObserved5sInLast90s!.components.push({...b.components[0],spell:'axe_second'});
  const scenarios=baseScenarios(more,mp,419,500);assert.deepEqual(scenarios.baseCooldownComparisons[0].remainingFromThatCastByBaseRank,[4,2]);
  assert.deepEqual(scenarios.previousBurstSpellPairs[0],{hero:a,spells:[skill,'axe_second'],minimumListedManaForTwoNewCasts:185,lastSeenMana:156,sheetAge:6,shortfallAtSighting:29});
  assert.equal(scenarios.knownActiveHealIfUsed,180);assert.equal(scenarios.hpIfThoseHealsUsedBeforeControl,500);
  kit[skill].values.AbilityCharges={base:[2]};assert.equal(baseScenarios(more,mp,419,500).baseCooldownComparisons.length,0);
});
test('independent daily quotas, immutable cases, cross-day dedup and patch-isolated coach retrieval',async t=>{
  const root=temp(t),dayDir=path.join(root,'runs','2026-11-23'),ledger=dayLedger(dayDir,'sven');
  for(let i=1;i<=20;i++)assert.ok(ledger.reserve(i,20,{i}));
  assert.equal(dayLedger(dayDir,'sven').reserve(21,20,{}),false);assert.equal(ledger.reserve(1,20,{}),false);
  assert.ok(dayLedger(dayDir,'luna').reserve(1,20,{}));assert.ok(dayLedger(path.join(root,'runs','2026-11-24'),'sven').reserve(21,20,{}));
  const e=compactEvidence(c,api,facts(),hash,patch),review=checkedReview({matchId:45,hero:'sven',minuteNotes:e.windows.map(w=>({minute:w.minute,reading:'r'})),observations:['items','farming']
    .map(topic=>({topic,fact:'f',interpretation:'i',coachQuestion:'q',evidenceIds:[e.windows[0].id]}))},e);
  const record:Case={version:2,day:'2026-11-23',patch:patch.name,hero:'sven',matchId:45,completedAt:'2026-11-22T16:30:00Z',
    qualification:{},provenance:{},model:{provider:'fixture',model:'fixture',reasoningEffort:'fixture'},evidence:e,...review};
  const dir=heroDir(root,patch.name,'sven'),file=path.join(dir,'matches','45.json');writeJson(file,record,true);
  assert.throws(()=>writeJson(file,{...record,day:'2026-11-24'},true));assert.equal(listCases(dir).length,1);
  const m=rebuildMemory(root,patch.name,'sven');assert.equal(m.samples,1);assert.equal(m.farming[0].medianLastHits,9);
  assert.equal(m.farming[0].halves[1].medianNeutralGoldInWindow,60);assert.equal(m.minuteReviewedCases,1);
  assert.equal(readCoachMinute(patch.name,'sven',45,3,root)?.modelReading,'r');assert.equal(readCoachMinute(patch.name,'sven',45,10,root),null);
  assert.deepEqual(m.retainedItemProgressions[0],{from:'item_mask_of_madness',to:'item_blink',matches:1,medianSecondHeldAt:600});
  assert.equal(m.lessons[1].evidenceIds[0],e.windows[0].id);assert.equal(readCoachMemory('7.42a','sven',root),null);
  assert.throws(()=>readCoachMemory('../x','sven',root));assert.throws(()=>readCoachMemory(patch.name,'../../x',root));
});
test('coach memory API is authenticated, read only and never silently returns another patch',async t=>{
  const root=temp(t),previous=process.env.DOTA_COACH_REPLAY_WORK_ROOT;process.env.DOTA_COACH_REPLAY_WORK_ROOT=root;
  t.after(()=>{if(previous===undefined)delete process.env.DOTA_COACH_REPLAY_WORK_ROOT;else process.env.DOTA_COACH_REPLAY_WORK_ROOT=previous;});
  rebuildMemory(path.join(root,'daily-study'),patch.name,'sven',[]);
  const {server,token}=createSandboxServer();server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(()=>{server.closeAllConnections();server.close();});const address=server.address();assert(address&&typeof address==='object');
  const url=`http://127.0.0.1:${address.port}/api/farming-memory`,headers={authorization:`Bearer ${token}`};
  assert.equal((await fetch(url)).status,401);assert.equal((await fetch(url,{headers})).status,400);
  assert.equal((await fetch(url,{method:'POST',headers})).status,405);assert.equal((await fetch(url+'?patch=7.42c&hero=sven',{headers})).status,404);
  const resp=await fetch(url+'?patch=7.42b&hero=sven',{headers});assert.equal(resp.status,200);assert.equal((await resp.json()).samples,0);
  assert.equal((await fetch(url+'?patch=7.42b&hero=sven&match=45',{headers})).status,400);
  assert.equal((await fetch(url+'?patch=7.42b&hero=sven&match=45&minute=0',{headers})).status,404);
});
test('offline CLI integration: source outage and unavailable model retain facts; next run learns once and resumes without another POST',async t=>{
  const root=temp(t),study=path.join(root,'daily-study'),cache=path.join(root,'farm742'),id=45;
  fs.mkdirSync(cache);const begin=Math.floor(Date.now()/1000)-86400, from=begin-3*86400;
  const replayUrl=`https://replay1.valve.net/570/${id}_2.dem.bz2`,sha=(s:string|Buffer)=>createHash('sha256').update(s).digest('hex');
  const json=JSON.stringify({...api,start_time:begin,patch:61,replay_url:replayUrl}), dem='PBDEMS2\0fixture',archive='BZhfixture';
  for(const [ext,s] of [['dem',dem],['archive',archive],['opendota.json',json]])fs.writeFileSync(path.join(cache,`${id}.${ext}`),s);
  writeJson(path.join(cache,`${id}.acquisition.json`),{matchId:id,metadataSha256:sha(json),replayUrl,demBytes:dem.length,archiveBytes:archive.length,
    demSha256:sha(dem),archiveSha256:sha(archive)});
  writeJson(path.join(cache,`${id}.facts.json`),{...facts(),clock:{game_start_tick:100,pauses:[]},demSha256:sha(dem)});
  writeJson(path.join(cache,'catalog-18.json'),[{...listing,activate_time:begin}]);
  const evidenceDir=path.join(study,'evidence',patch.name,'sven',String(id));
  writeJson(path.join(evidenceDir,'answer.json'),{oldFiveMinuteAnswer:true},true);
  writeJson(path.join(evidenceDir,minuteAnswerFile),{oldMinuteAnswer:true},true);
  writeJson(path.join(evidenceDir,fogAnswerFile),{oldFogAnswer:true},true);
  writeJson(path.join(evidenceDir,'team-map-v2.json'),{format:'team-map-v2',hero:'sven',pid:0,team:2,matchId:id,build:9000,
    demSha256:sha(dem),postGameTick:18100,frames:Array.from({length:601},(_,s)=>fakeMap(s)),combat:[]});
  writeJson(path.join(evidenceDir,'team-spells-v2.json'),{format:'team-spells-v2',hero:'sven',pid:0,team:2,matchId:id,build:9000,demSha256:sha(dem),
    postGameTick:18100,frames:Array.from({length:601},(_,s)=>({tick:100+s*30,publicSheets:[[roster[0].hero,4,80]],ownItems:[]})),combat:[]});
  const preload=path.join(root,'mock-public.mjs');
  fs.writeFileSync(preload,`globalThis.fetch=async url=>{
    if(url.includes('www.dota2.com/datafeed/'))return Response.json({success:true,patches:[{patch_number:'7.42b',patch_timestamp:${from}}]});
    if(url.includes('raw.githubusercontent.com/odota/dotaconstants/'))return Response.json([{id:61,name:'7.42',date:'2020-01-01T00:00:00.000Z'}]);
    return new Response(null,{status:403});
  };`);
  let enabled=false;const posts:any[]=[];
  const gateway=http.createServer(async(req,res)=>{
    res.setHeader('Content-Type','application/json');
    if(req.url==='/v1/models'){
      if(!enabled){res.writeHead(503);res.end('{}');return;}
      res.end(JSON.stringify({data:[{id:'gpt-6.2-sol',display_name:'Sol (github-copilot)',supported_endpoints:['/responses'],
        capabilities:{supports:{reasoning_effort:['xhigh']},limits:{max_output_tokens:96000}}}]}));return;
    }
    if(req.url!=='/v1/responses'){res.writeHead(404);res.end('{}');return;}
    let body='';for await(const b of req)body+=b;posts.push(JSON.parse(body));
    const payload=JSON.parse(posts.at(-1).input[0].content[0].text.split('\n').slice(1).join('\n'));
    const raw=fogResponse(payload.cards),ref='map/'+roster[0].hero;
    const result={...raw,decisionNotes:raw.decisionNotes.map(n=>({...n,threatRead:'只知道最后露头时有80蓝；没有己方受击证据。',threatRefs:[ref],
      threatMetric:{ref,field:'manaAtSighting',value:80}}))};
    res.end(JSON.stringify({id:'fixture-response',model:'gpt-6.2-sol',status:'completed',output:[{type:'message',content:[{type:'output_text',text:JSON.stringify(result)}]}]}));
  });
  gateway.listen(0,'127.0.0.1');await once(gateway,'listening');t.after(()=>{gateway.closeAllConnections();gateway.close();});
  const address=gateway.address();assert(address&&typeof address==='object');
  const env={...process.env,DOTA_COACH_REPLAY_WORK_ROOT:root,DOTA_COACH_COPILOT_BASE_URL:`http://127.0.0.1:${address.port}/v1`,
    CODEY_MODEL_API_KEY:'synthetic-fixture',TEMP:root,TMP:root,TMPDIR:root},dayDir=path.join(study,'runs',hkDay());
  const launch=async()=>{
    await assert.rejects(promisify(execFile)(process.execPath,['--import',pathToFileURL(preload).href,
      path.resolve(import.meta.dirname,'../scripts/replays/daily-run.ts')],{env,timeout:45000,windowsHide:true}),(e:any)=>e.code===2);
    return JSON.parse(fs.readFileSync(path.join(dayDir,'status.json'),'utf8'));
  };
  const first=await launch();assert.equal(first.verifiedReplays.sven,1);assert.equal(first.completed.sven,0);assert.equal(posts.length,0);
  assert.ok(fs.existsSync(path.join(study,'raw','45','45.dem')));assert.equal(dayLedger(dayDir,'sven').size(),0);
  enabled=true;const second=await launch();assert.deepEqual(second.completed,{luna:0,troll_warlord:0,sven:1},
    JSON.stringify({run:second.runId,phase:second.phase,posts:posts.length,issues:second.recentIssues}));assert.equal(posts.length,1);
  assert.equal(posts[0].text.format.type,'json_schema');assert.deepEqual(posts[0].reasoning,{effort:'xhigh'});assert.equal(posts[0].store,false);
  assert.equal(readCoachMemory('7.42b','sven',study).samples,1);assert.equal(dayLedger(dayDir,'sven').size(),1);
  assert.equal(posts[0].max_output_tokens,96000);const minute=readCoachMinute(patch.name,'sven',id,9,study)!;
  assert.equal(minute.source,'team-view-decision-review');assert.equal(minute.decisions![0].modelJudgement?.preferred,'retreat');
  assert.equal(minute.decisions![0].modelJudgement?.threatMetric?.value,80);assert.ok(fs.existsSync(path.join(evidenceDir,threatAnswerFile)));
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(evidenceDir,fogAnswerFile),'utf8')),{oldFogAnswer:true});
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(evidenceDir,minuteAnswerFile),'utf8')),{oldMinuteAnswer:true});
  assert.equal(await backfillSpells(study,listCases(heroDir(study,patch.name,'sven'))[0]),false);
  assert.equal(await backfillFog(study,listCases(heroDir(study,patch.name,'sven'))[0]),false);
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(evidenceDir,'answer.json'),'utf8')),{oldFiveMinuteAnswer:true});
  const third=await launch();assert.equal(third.completed.sven,1);assert.equal(posts.length,1);
});
