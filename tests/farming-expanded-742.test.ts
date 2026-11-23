import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { compact, endpoints, type Hit742, type Row742 } from '../scripts/replays/study-742.ts';
import { packOne, publishable, summarizeExpanded, windowPoints, type ExpandedMatch } from '../scripts/replays/pack-expanded-742.ts';

const original = JSON.parse(fs.readFileSync('data/farming-742-routes.json', 'utf8'));
const sha = (s: string | Buffer) => createHash('sha256').update(s).digest('hex');
const pt = (t: number, lane = 100, neutral = 50, lh = 10) => ({ t, lane, neutral, lh });
const match = (override: Partial<ExpandedMatch> = {}): ExpandedMatch => ({
  ...original.matches[0], id: 1, hero: 'luna', team: 'radiant', mmr: 7800, accountId: 1, duration: 1400, finalLH: 200,
  startPoint: pt(.1,0,0,0), points: [pt(300.5,80,0,8), pt(600.25,200,100,30), pt(900.1,480,220,75), pt(1200.75,820,500,120)],
  endPoint: pt(1400,1300,850,200), trace: [], events: [], ...override,
});
const camps = { camps: [{ id: 'good_1', x: 100, y: 100 }, { id: 'good_2', x: 150, y: 150 }] };

test('reached milestones are not terminal observations in disguise; no fake 20:00, padded 5 minutes or divide by zero', () => {
  const long = match(), short = match({ id: 2, mmr: 7000, team: 'dire', points: long.points.slice(0,3),
    duration: 930.5, endPoint: pt(930.5,500,270,80), finalLH: 80 });
  const tiny = match({ id: 3, team: 'dire', mmr: 8100, points: [], duration: 180, endPoint: pt(180,0,0,0), finalLH: 0 });
  // An exact terminal sample can be a real milestone, but not the start of a subsequent nonempty window.
  const exact = match({ points: [long.points[0], pt(600)], duration: 600, endPoint: pt(600) });
  assert.equal(windowPoints(exact,300,600).coverage, 'full'); assert.equal(windowPoints(exact,600,900).coverage,'notReached');
  assert.equal(windowPoints(short,600,900).coverage, 'full'); assert.equal(windowPoints(short,900,1200).coverage,'truncated');
  assert.equal(windowPoints(tiny,0,300).coverage,'truncated'); assert.equal(windowPoints(tiny,300,600).coverage,'notReached');
  const groups = summarizeExpanded([long, short, tiny], camps).groups;
  const all = groups.find(g => g.hero === 'luna' && g.team === 'both' && g.tier === 'all')!;
  const high = groups.find(g => g.hero === 'luna' && g.team === 'both' && g.tier === 'high')!;
  assert.deepEqual(all.cumulativeLH.map(p => [p.n,p.median]), [[2,8],[2,30],[2,75],[1,120]]);
  assert.equal(all.accounts, 1); assert.equal(all.n, 3); assert.equal(high.n, 2);
  assert.deepEqual(all.windows.map(w => [w.full.n, w.truncated.n, w.notReached]), [[2,1,0],[2,0,1],[2,0,1],[1,1,1],[1,1,1]]);
  assert.deepEqual(high.windows.at(-1)!.full.n,1); assert.equal(high.windows.at(-1)!.truncated.n,0);
  const w = all.windows[3].truncated;
  assert.equal(w.medianFarmPerMinute, (20+50)*60/(930.5-900.1)); assert.equal(w.medianLaneShare,20/70);
  assert.equal(all.windows[0].truncated.laneShareN,0); assert.equal(all.windows[0].truncated.medianLaneShare,null);
  assert.equal(all.windows[0].truncated.medianFarmPerMinute,0); assert.equal(all.windows[0].full.routes,null);
  assert.equal(groups.find(g => g.hero === 'troll_warlord' && g.team === 'dire' && g.tier === 'all')!.windows[4].full.medianLHDelta, null);
});

type Trace = ExpandedMatch['trace'];
const hit = (t: number, site: string, actor: Hit742[2] = 'body', x: number | null = 100): Hit742 =>
  [t,site,actor,x,x,Math.round(t*30)];
test('route support is per match and side/MMR tier; unknowns, mixed same-tick origins, deaths, remote kills and broken traces cannot bridge', () => {
  const trace: Trace = Array.from({ length: 151 }, (_,i) => [600+2*i,100,100,1,Number(!i)]);
  const pass = match({ trace, events: [hit(600,'lane:bot'),hit(606,'good_1'),hit(606,'good_1'),hit(608,'good_2','illusion'),
    hit(612,'lane:bot'), hit(616,'good_1'), hit(620,'lane:bot'), hit(900,'good_2')] });
  const cut = (id: number, e: Hit742[], tr = trace) => match({ id, mmr: 7250, team: 'dire', trace: tr, events: [
    hit(600,'lane:bot'), ...e, hit(612,'good_1')] });
  const dead = trace.map((p,i): Trace[number] => i === 3 ? [p[0],p[1],p[2],0,1] : p);
  const br = trace.map((p,i): Trace[number] => i === 3 ? [p[0],p[1],p[2],1,1] : p);
  const others = [cut(2,[hit(606,'?')]), cut(3,[hit(606,'good_2'),hit(606,'good_1')]), cut(4,[hit(606,'lane:bot','?')]),
    cut(5,[hit(606,'lane:?')]), cut(6,[hit(606,'good_2','body',null)]), cut(7,[hit(606,'good_2','body',5000)]),
    cut(8,[],dead), cut(9,[],br), cut(10,[],[]), match({ id: 11, team: 'dire', mmr: 7250, trace,
      events: [hit(600,'lane:bot'), hit(636,'good_1')] })];
  const gs = summarizeExpanded([pass,...others],camps).groups;
  const all = gs.find(r => r.hero === 'luna' && r.team === 'both' && r.tier === 'all')!.windows[2].full;
  assert.equal(all.n,11); assert.equal(all.routes!.matchesWithLink,1);
  assert.deepEqual(all.routes!.topTriples.find(r => r.sites.join('>') === 'lane:bot>good_1>lane:bot'),
    { sites:['lane:bot','good_1','lane:bot'], matches:1, example:{ id:1,times:[600,606,612],ticks:[18000,18180,18360] } });
  assert.equal(gs.find(r => r.hero === 'luna' && r.team === 'dire' && r.tier === 'all')!.windows[2].full.routes!.topLinks.length,0);
  const h = gs.find(r => r.hero === 'luna' && r.team === 'radiant' && r.tier === 'band-7500')!;
  assert.equal(h.windows[2].full.medianDistinctBodyCamps,1); // exclude the 15:00 kill and the illusion
  assert.equal(h.windows[3].full.medianDistinctBodyCamps,1);
  assert.equal(pass.events.filter(e => e[5] === 18180).length,2); // multiple kills kept, not two route votes
});

test('short route-core replay: target-only frames suffice; terminal ten-player parity, map, interior times and completeness remain mandatory', async t => {
  const dir = fs.mkdtempSync(path.join(tmpdir(), 'dota-pack-test-')), id = 1234567890;
  const name = (ext: string) => path.join(dir, `${id}.${ext}.json`);
  const write = (ext: string, v: unknown) => fs.writeFileSync(name(ext), JSON.stringify(v));
  try {
    const npc = 'npc_dota_hero_luna', duration = 1079.25, demSha256 = sha('fixture replay');
    const row: Row742 = { match_id:id, hero_id:48, name:'fixture', account_id:17, mmr:8001, is_radiant:1, won:1, lobby_type:7,
      patch:'7.42', position:'pos 1', duration:1079, activate_time:1795288240, last_hits:81 };
    const players = [0,1,2,3,4,128,129,130,131,132].map((s,i) => ({ player_slot:s, hero_id:i ? i : 48, account_id:i ? 100+i : 17, last_hits:i || 81 }));
    const meta = { match_id:id, start_time:row.activate_time, duration:1079, patch:61, radiant_win:true, lobby_type:7, players,
      replay_url:`http://replay1.valve.net/570/${id}_1.dem.bz2` };
    write('opendota',meta); write('acquisition',{ matchId:id, metadataSha256:sha(fs.readFileSync(name('opendota'))),
      archiveSha256:sha('fixture archive'), demSha256, replayUrl:meta.replay_url });
    const cs = original.map.camps.map((c: any,i: number) => ({ id:`neutralcamp_${c.id}`, spawnerIndex:1000+i, type:c.initialTier,
      min:c.box.slice(0,2),max:c.box.slice(2) }));
    const spawners = original.map.camps.map((c: any,i: number) => ({ index:1000+i, position:{x:c.x,y:c.y},types:[{type:c.initialTier}] }));
    const towers = original.map.towers.map((t: any) => ({ name:t.id,team:t.team,position:{x:t.x,y:t.y} }));
    const h = (i: number, lh: number) => ({ name:i ? 'other' : npc, team:i<5 ? 2 : 3, playerIdField:i, items:[],
      economy:{m_iLastHitCount:lh,m_iCreepKillGold:lh*3,m_iNeutralKillGold:lh*2} });
    const frames = [.1,300.1,600.2,900.3,duration].map((t,i) => ({ tick:Math.round(t*30), gameTime:t,
      heroes:t === duration ? players.map((p,k) => h(k,p.last_hits)) : [h(0,i*10)] }));
    const facts = { complete:true,schemaVersion:4,parser:{name:'gem-dota',version:'0.14.0'},matchId:id,gameBuild:6989,durationSeconds:duration,demSha256,postGameTick:frames.at(-1)!.tick,
      clock:{game_start_tick:0}, extractionScope:'route-core', frames, camps:cs,spawners,towers,creatures:[],lunaCombatLog:[],
      heroPath:[{...h(0,0),gameTime:900,position:{x:7,y:8},health:100,lifeState:0}] };
    write('facts',facts);
    const run = (allowShort = true) => compact(row, original.patch, original.builds, { directory:dir, allowShort });
    const out = run();
    assert.deepEqual(out.match.points.map(p => p.t),[300.1,600.2,900.3]);
    assert.equal(out.extractionScope,'route-core'); assert.equal(out.startPoint.t,.1);
    assert.deepEqual(out.endPoint,pt(1079.25,243,162,81)); assert.deepEqual(out.match.trace,[[900,7,8,1,1]]);
    assert.deepEqual(out.landmarks,original.map); assert.throws(() => run(false));
    await t.test('a tampered cache is rebuilt; byte changes in same-size same-name evidence invalidate warm results', () => {
      const cache = `${dir}/cache`, file = `${cache}/${id}.json`, archive = `${dir}/${id}.archive`, dem = `${dir}/${id}.dem`;
      const cached = () => packOne(row, dir, {patch:original.patch,builds:original.builds}, 'fixture-code', true, true, original.map, undefined, cache);
      fs.mkdirSync(cache); fs.writeFileSync(archive,'fixture archive'); fs.writeFileSync(dem,'fixture replay');
      try {
        const cold = cached(); assert.deepEqual(cached(),cold);
        const corrupt = JSON.parse(fs.readFileSync(file,'utf8')); corrupt.result.match.endPoint = {t:1200}; corrupt.result.match.trace = [];
        fs.writeFileSync(file,JSON.stringify(corrupt)); assert.deepEqual(cached(),cold);
        const times = fs.statSync(name('facts')), good = fs.readFileSync(name('facts'),'utf8');
        const bad = good.replace('"gameBuild":6989','"gameBuild":9999'); assert.notEqual(bad,good); assert.equal(bad.length,good.length);
        fs.writeFileSync(name('facts'),bad); fs.utimesSync(name('facts'),times.atime,times.mtime);
        assert.throws(cached,/build\/duration/); fs.writeFileSync(name('facts'),good);
        fs.writeFileSync(archive,'fixture archivX'); assert.throws(cached,/archiveSha256 vs acquisition/);
      } finally {
        for (const p of [file,archive,dem]) if (fs.existsSync(p)) fs.unlinkSync(p);
        fs.rmdirSync(cache);
      }
    });
    write('facts',{...facts,frames:frames.filter(f => f.gameTime !== 600.2)}); assert.throws(run,/boundary/);
    write('facts',{...facts,complete:false}); assert.throws(run,/completeness/);
    write('facts',{...facts,frames:frames.map(f => f.gameTime === duration ? {...f,heroes:f.heroes.slice(0,-1)} : f)});
    assert.throws(run,/Terminal LH mismatch/);
  } finally {
    for (const ext of ['facts','opendota','acquisition']) if (fs.existsSync(name(ext))) fs.unlinkSync(name(ext));
    fs.rmdirSync(dir);
  }
});

test('publication gate reconciles every candidate, terminal collection state and the original records', () => {
  type Entry = Parameters<typeof publishable>[0][number];
  const entry = (id: number, legacy16 = false): Entry => ({ row:{match_id:id} as Row742, legacy16, status:'verified', acquisition:{status:legacy16 ? 'reused' : 'parsed'} });
  const entries = [entry(1,true),entry(2)], progress = { eligible:2,updatedAt:'date',results:{ '1':{status:'reused'},'2':{status:'parsed'} } };
  assert.doesNotThrow(() => publishable(entries,progress));
  assert.throws(() => publishable(entries,null)); assert.throws(() => publishable(entries,{...progress,complete:false}));
  assert.throws(() => publishable(entries,{...progress,results:{'1':{status:'reused'}}}));
  assert.throws(() => publishable([entries[0],{...entries[1],acquisition:{status:'parsing'}}], progress));
  assert.throws(() => publishable([entries[0],{...entries[1],status:'pending'}],progress));
  assert.throws(() => publishable([{...entries[0],status:'rejected'},entries[1]],progress),/Lost original/);
  assert.throws(() => publishable([entries[0],{...entries[1],acquisition:{status:'failed'}}],progress),/Stale receipt/);
  assert.throws(() => publishable([entries[0],{...entries[1],acquisition:{status:'failed'}}],
    {...progress,results:{'1':{status:'reused'},'2':{status:'failed'}}}),/Contradictory/);
});
