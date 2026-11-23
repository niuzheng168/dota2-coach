/** Pure selection, replay evidence and coaching-memory contracts. No network or live game input. */
import assert from 'node:assert/strict';
import { farmingMinutes, gameClockSecond, MINUTE_FORMAT, type FarmSample } from './daily-minutes.ts';

export const HEROES = { luna: 48, troll_warlord: 95, sven: 18 } as const;
export type Hero = keyof typeof HEROES;
export const heroKeys = Object.keys(HEROES) as Hero[];
export const TARGET = 20, MIN_MMR = 8000, TOP_RANK = 200;
export const validPatch = (p: unknown): p is string => typeof p === 'string' && /^\d{1,2}\.\d{1,3}[a-z]?$/.test(p);
export const hkDay = (now = new Date()) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Hong_Kong',
  year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
const positive = (n: any): n is number => Number.isSafeInteger(n) && n > 0;
const topRank = (n: any) => positive(n) && n <= TOP_RANK;
export const officialTier = (tier: unknown) => tier === 'professional' || tier === 'premium';
export type Patch = { name: string; from: number; odRelease?:{id:number;from:number;name:string} };
export function majorRelease(rows:any,patch:Patch) {
  const name=patch.name.replace(/[a-z]$/,''), matches=Array.isArray(rows)?rows.filter(r=>r?.name===name):[];
  if(matches.length!==1)return undefined;
  const id=matches[0].id,from=Math.floor(Date.parse(matches[0].date)/1000);
  return positive(id)&&positive(from)?{name,id,from}:undefined;
}
export function currentPatch(payload: any, now: number): Patch {
  assert(payload?.success && Array.isArray(payload.patches), 'Invalid Valve patch list');
  const entries = payload.patches.filter((p: any) => positive(p.patch_timestamp) && p.patch_timestamp <= now)
    .sort((a: any, b: any) => b.patch_timestamp - a.patch_timestamp);
  // Fail closed on an unknown latest label, not back to the most recent parseable patch.
  assert(validPatch(entries[0]?.patch_number) && entries.every((e: any) =>
    e.patch_timestamp !== entries[0].patch_timestamp || e.patch_number === entries[0].patch_number), 'Unknown/ambiguous current Valve patch');
  return { name: entries[0].patch_number, from: entries[0].patch_timestamp };
}
export type Candidate = { id: number; hero: Hero; start: number; source: string; kind: 'ranked' | 'league' | 'rank-probe';
  mmr?: number; rank?: number; account?: number; radiant?: boolean; league?: number; patchLabel?:string };
const highLobby=(n:any)=>positive(n)&&n>=MIN_MMR&&n<30000; // exclude the 50000 tournament sentinel
export function eligibleTime(t: unknown, patch: Patch, now: number): t is number {
  return positive(t) && t >= patch.from && t < now;
}
export function d2ptCandidates(rows: any, hero: Hero, patch: Patch, now: number, source: string): Candidate[] {
  assert(Array.isArray(rows), 'Invalid D2PT catalog');
  return rows.flatMap((raw: any) => {
    const x = typeof raw?.data === 'string' ? JSON.parse(raw.data) : raw?.data ?? raw;
    if (x?.hero_id !== HEROES[hero] || !positive(x.match_id) || x.patch !== patch.name || x.lobby_type !== 7 ||
      x.position !== 'pos 1' || !positive(x.account_id) || ![0, 1, false, true].includes(x.is_radiant) ||
      !eligibleTime(x.activate_time, patch, now) || !(highLobby(x.mmr) || topRank(x.leaderboard_rank))) return [];
    return [{ id: x.match_id, hero, start: x.activate_time, source, kind: 'ranked', account: x.account_id,
      radiant: !!x.is_radiant, mmr: highLobby(x.mmr) ? x.mmr : undefined, rank: topRank(x.leaderboard_rank) ? x.leaderboard_rank : undefined,patchLabel:x.patch }];
  });
}
/** MMR is a MATCH average. Other pos-1 heroes explicitly identified in the same lobby are eligible too;
 * another player's leaderboard rank or pro flag never transfers to them. */
export function d2ptLineups(rows:any,hero:Hero,patch:Patch,now:number,source:string):Candidate[]{
  assert(Array.isArray(rows),'Invalid D2PT roster');
  return rows.flatMap(raw=>{
    const x=typeof raw?.data==='string'?JSON.parse(raw.data):raw?.data??raw;
    if(!positive(x?.match_id)||x.patch!==patch.name||x.lobby_type!==7||!highLobby(x.mmr)||
      !eligibleTime(x.activate_time,patch,now)||!Array.isArray(x.players)||x.players.length!==10)return [];
    const ps=x.players.filter((p:any)=>p.hero_npc===hero && p.position==='pos 1' && positive(p.account_id) && [0,1,false,true].includes(p.is_radiant));
    if(ps.length!==1)return [];
    return [{id:x.match_id,hero,start:x.activate_time,source,kind:'ranked',account:ps[0].account_id,
      radiant:!!ps[0].is_radiant,mmr:x.mmr,patchLabel:x.patch}];
  });
}
export function playerCandidates(rows:any,account:number,hero:Hero,patch:Patch,now:number,source:string):Candidate[]{
  assert(Array.isArray(rows)&&positive(account),'Invalid ranked player listing');
  return rows.filter(x=>positive(x?.match_id)&&x.hero_id===HEROES[hero]&&x.lobby_type===7&&eligibleTime(x.start_time,patch,now))
    .map(x=>({id:x.match_id,hero,start:x.start_time,source,kind:'rank-probe',account}));
}
export function proCandidates(rows: any, hero: Hero, patch: Patch, now: number, source: string): Candidate[] {
  assert(Array.isArray(rows), 'Invalid professional matches catalog');
  return rows.filter(x => positive(x?.match_id) && positive(x.leagueid) && eligibleTime(x.start_time, patch, now))
    .map(x => ({ id: x.match_id, hero, source, start: x.start_time, kind: 'league', league: x.leagueid }));
}
export function pubCandidates(rows: any, hero: Hero, patch: Patch, now: number, source: string): Candidate[] {
  assert(Array.isArray(rows), 'Invalid public matches catalog');
  return rows.filter(x => positive(x?.match_id) && x.lobby_type === 7 && x.avg_rank_tier >= 80 && x.num_rank_tier >= 8 &&
    eligibleTime(x.start_time, patch, now) && [x.radiant_team, x.dire_team].some(s => Array.isArray(s) && s.includes(HEROES[hero])))
    .map(x => ({ id: x.match_id, hero, source, start: x.start_time, kind: 'rank-probe' }));
}
export function verifyMatch(c: Candidate, api: any, patch: Patch, now: number, leagueTier?: string, leaderboard?: any) {
  assert(api?.match_id === c.id && Array.isArray(api.players) && api.players.length === 10 && typeof api.radiant_win === 'boolean' &&
    eligibleTime(api.start_time, patch, now) && api.start_time === c.start && positive(api.duration) &&
    api.start_time + api.duration <= now, 'Incomplete match, wrong ID, or start outside current patch');
  assert(c.patchLabel===undefined||c.patchLabel===patch.name,'Conflicting match patch label');
  if(patch.odRelease)assert(api.start_time>=patch.odRelease.from && (api.patch==null||api.patch===patch.odRelease.id),
    'OpenDota major patch or rollout time mismatch');
  assert(new Set(api.players.map((p:any)=>p.player_slot)).size===10 &&
    api.players.every((p:any)=>[0,1,2,3,4,128,129,130,131,132].includes(p.player_slot)), 'Invalid ten-player roster');
  const ps = api.players.filter((p: any) => p.hero_id === HEROES[c.hero]);
  assert(ps.length === 1 && [...Array(5).keys(),128,129,130,131,132].includes(ps[0].player_slot), 'Focal hero/slot not unique');
  const p = ps[0];
  // Valve's patch list can give a release date at 00:00 UTC rather than the deployment minute.
  // On that date only accept a source which explicitly tagged this exact (sub)patch.
  if (c.patchLabel !== patch.name && patch.from % 86400 === 0)
    assert(api.start_time >= patch.from + 86400, 'Release-day match lacks an exact subpatch label');
  if (c.account && p.account_id != null) assert(p.account_id === c.account, 'Account mismatch');
  if (c.radiant !== undefined) assert(c.radiant === (p.player_slot < 128), 'Team mismatch');
  if (c.kind === 'league') {
    assert(api.lobby_type === 1 && positive(c.league) && api.leagueid === c.league &&
      officialTier(api.league?.tier ?? leagueTier), 'Not a confirmed professional/premium league match');
  } else {
    assert(api.lobby_type === 7, 'Not a ranked public match');
    if (c.kind === 'rank-probe') assert(positive(p.account_id) && leaderboard?.profile?.account_id === p.account_id &&
      topRank(leaderboard.leaderboard_rank), 'No verified top-200 focal player');
    else assert(highLobby(c.mmr) || topRank(c.rank), 'Unqualified MMR/rank');
  }
  return p;
}

const nonnegative = (n: any): n is number => Number.isSafeInteger(n) && n >= 0;
export function compactEvidence(c: Candidate, api: any, facts: any, demHash: string, patch: Patch) {
  const npc = `npc_dota_hero_${c.hero}`, player = api.players.find((p: any) => p.hero_id === HEROES[c.hero]);
  const team = player.player_slot < 128 ? 2 : 3, pid = (player.player_slot % 128) + (team === 2 ? 0 : 5);
  const final = facts.frames?.at(-1);
  assert(facts.complete === true && facts.schemaVersion === 4 && facts.parser?.name === 'gem-dota' &&
    facts.parser.version === '0.14.0' && facts.matchId === api.match_id && facts.matchId === c.id &&
    facts.demSha256 === demHash && /^[a-f0-9]{64}$/.test(demHash) && positive(facts.gameBuild) &&
    final?.tick === facts.postGameTick && positive(final.tick) && final.heroes.length === 10 &&
    Math.abs(facts.durationSeconds - api.duration) <= 3 && Math.abs(final.gameTime - facts.durationSeconds) < 0.01,
    'Incomplete or mismatched DEM/build/terminal clock');
  assert(Array.isArray(facts.heroPath) && facts.heroPath.length && facts.heroPath.every((h:any) =>
    h.name === npc && h.team === team && h.playerIdField === pid), 'Replay extraction focused on a different player/hero');
  for (const p of api.players) {
    const h = final.heroes.find((x: any) => x.playerIdField === p.player_slot % 128 + (p.player_slot < 128 ? 0 : 5));
    assert(h && nonnegative(p.last_hits) && h.economy?.m_iLastHitCount === p.last_hits, 'Terminal ten-player last-hit mismatch');
  }
  const firstHeld = new Map<string, number>();
  const samples:FarmSample[]=[];
  for (const f of facts.frames) {
    if (!Number.isFinite(f.gameTime) || f.gameTime < 0) continue;
    const h = f.heroes.find((x: any) => x.name === npc && x.team === team && x.playerIdField === pid);
    if (!h || !Array.isArray(h.items)) continue;
    const e = h.economy, t = gameClockSecond(f.gameTime), items: string[] = [];
    for (const i of h.items) {
      if (i.slot < 0 || i.slot > 5 || !/^item_[a-z0-9_]{1,65}$/.test(i.name ?? '')) continue;
      items.push(i.name); if (!firstHeld.has(i.name)) firstHeld.set(i.name, Math.floor(t*1000)/1000);
    }
    if (!e || ![e.m_iLastHitCount,e.m_iCreepKillGold,e.m_iNeutralKillGold].every(nonnegative)) continue;
    const pos = h.position;
    samples.push({ t, lh: e.m_iLastHitCount, lane: e.m_iCreepKillGold, neutral: e.m_iNeutralKillGold,
      nw: nonnegative(e.m_iNetWorth) ? e.m_iNetWorth : null, stacks:nonnegative(e.m_iCampsStacked)?e.m_iCampsStacked:null,
      items, xy: h.lifeState===0 && Number.isFinite(pos?.x) && Number.isFinite(pos?.y)
        ? [Math.round(pos.x),Math.round(pos.y)] : null });
  }
  assert(samples.length >= 2 && samples[0].t < 10 && samples.at(-1)!.lh === player.last_hits &&
    Math.abs(samples.at(-1)!.t - api.duration) <= 3 && firstHeld.size, 'Missing focal hero, inventory or full economy samples');
  assert(samples.every((s,i)=>!i || (s.t>=samples[i-1].t && s.t-samples[i-1].t<=5)),'Missing continuous focal inventory/economy samples');
  const windows=farmingMinutes(samples,facts,c.hero,team,`${c.id}:${c.hero}`);
  return { format:MINUTE_FORMAT, windowSeconds:60 as const, halfSeconds:30 as const,
    patch:patch.name, hero:c.hero, matchId:c.id, start:api.start_time, duration:api.duration, build:facts.gameBuild,
    role:c.kind==='ranked'?'recorded-pos-1':'unverified-position',won:(team===2)===api.radiant_win,
    team:team===2?'radiant':'dire', firstMainSlot:[...firstHeld].map(([item,t])=>({item,t})),
    windows, scope:'Offline game clock. Half-minute income is a counter delta, including controlled units, not wave ownership. Recorded body last-hit order is not a walking route. No inferred camp spawns/clears or player vision. Main-slot first held is not purchase time.' };
}
export type Evidence = ReturnType<typeof compactEvidence>;
/** Pack the FULL minute sequence into one request. Inventories repeat often; encode them once by index. */
export function minuteReviewInput(e:Evidence){
  const {windows,...match}=e, loadouts:string[][]=[],indices=new Map<string,number>();
  const minutes=windows.map(w=>{
    const sig=JSON.stringify(w.items);if(!indices.has(sig)){indices.set(sig,loadouts.length);loadouts.push(w.items);}
    const halves=w.halves.map(h=>{
      const k=h.kills;
      return [h.sampledFrom,h.sampledTo,h.lhGain,h.laneGold,h.neutralGold,h.stacks,h.fromXY,h.toXY,
        k?[k.body.lane,k.body.neutral,k.illusion.lane,k.illusion.neutral,k.unknown.lane,k.unknown.neutral,k.bodyOrder]:null];
    });
    return {id:w.id,minute:w.minute,end:Math.round(w.to*1000)/1000,partial:w.partial,lhEnd:w.lh,nwEnd:w.netWorth,loadout:indices.get(sig),halves};
  });
  return '请依据下列录像证据返回指定的 JSON：\n'+JSON.stringify({...match,
    halfColumns:['sampleFromSeconds','sampleToSeconds','LHgain','laneGold','neutralGold','stackCounterGain','bodyXYfrom','bodyXYto','recordedKills'],
    killColumns:['bodyLane','bodyNeutral','illusionLane','illusionNeutral','unknownActorLane','unknownActorNeutral','bodyOrder(L=lane,N=neutral,L+N=same tick)'],
    loadouts,minutes});
}
export const instructions = `你是离线 Dota 2 发育录像研究员。按游戏时钟逐分钟分析全场，不分摊五分钟总量。m0 是 0:00–1:00，m14 是 14:00–15:00。每个分钟的 halves 依次是 :00–:30、:30–下个整分；末段不足时只分析实录。累计收益含受控单位，读 recordedKills 区分本体、幻象和未知；null 是未知。整分是野怪刷新检查点，半分是出兵节奏的参照，不保证营地真的刷新或英雄吃到对应波次；出兵不是到线或击杀时间。bodyOrder 只是本体补刀类型的先后；不能从坐标、收入或叠野计数推出清了哪个营、完整行走路线、玩家视野或主观意图。主栏首见不是购买时间。
minuteNotes 按输入顺序逐一覆盖所有 minute（含不满一分钟的结尾），每个 minute 一个 reading，用一句简短中文说明该分钟两段的线野取舍或变化。没有证据就说明未观察到什么；不要把零收益写成挂机，不必重复全部数字。结合相邻分钟和装备首见另写 2–6 条 observations，出装 items 和打钱 farming 各至少一条，给出可核对事实、有限解释和下次在相似局势要问的教练问题。每条引用实际分钟 id，可加 :a 或 :b 指定实有半段。相关不等于因果。仅返回所给 JSON 结构，不输出隐藏思维。`;
export type Observation = {topic:'items'|'farming';fact:string;interpretation:string;coachQuestion:string;evidenceIds:string[]};
export type MinuteNote={minute:number;evidenceId:string;reading:string};
export const reviewSchema={name:'daily_replay_minutes_v2',schema:{type:'object',additionalProperties:false,
  required:['matchId','hero','minuteNotes','observations'],properties:{matchId:{type:'integer'},hero:{type:'string',enum:heroKeys},
    minuteNotes:{type:'array',items:{type:'object',additionalProperties:false,required:['minute','reading'],
      properties:{minute:{type:'integer'},reading:{type:'string'}}}},
    observations:{type:'array',items:{type:'object',additionalProperties:false,
      required:['topic','fact','interpretation','coachQuestion','evidenceIds'],properties:{topic:{type:'string',enum:['items','farming']},
        fact:{type:'string'},interpretation:{type:'string'},coachQuestion:{type:'string'},evidenceIds:{type:'array',items:{type:'string'}}}}}}}};
export function checkedReview(r: any, e: Evidence):{observations:Observation[];minuteNotes:MinuteNote[]} {
  assert(r?.matchId === e.matchId && r.hero === e.hero && Array.isArray(r.observations) &&
    r.observations.length >= 2 && r.observations.length <= 6, 'Invalid study review');
  assert(Array.isArray(r.minuteNotes)&&r.minuteNotes.length===e.windows.length,'Missing per-minute review coverage');
  const minuteNotes=e.windows.map((w,i)=>{
    const n=r.minuteNotes[i];
    assert(n?.minute===w.minute && typeof n.reading==='string' && n.reading.trim() && n.reading.length<=200,'Missing, duplicate or misordered minute reading');
    return {minute:w.minute,evidenceId:w.id,reading:n.reading.trim()};
  });
  const ids = new Set(e.windows.flatMap(w=>[w.id,...w.halves.map(h=>h.id)]));
  const obs = r.observations.map((o: any) => {
    assert(['items','farming'].includes(o?.topic) && [o.fact,o.interpretation,o.coachQuestion].every(v =>
      typeof v === 'string' && v.trim().length && v.length <= 800) && Array.isArray(o.evidenceIds) &&
      o.evidenceIds.length > 0 && o.evidenceIds.length <= 8 && o.evidenceIds.every((id: any) => ids.has(id)), 'Unanchored observation');
    return { topic:o.topic,fact:o.fact,interpretation:o.interpretation,coachQuestion:o.coachQuestion,evidenceIds:o.evidenceIds };
  });
  assert(new Set(obs.map((o: Observation)=>o.topic)).size === 2,'Must study both items and farming'); return {observations:obs,minuteNotes};
}
