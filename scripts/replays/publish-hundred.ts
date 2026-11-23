import { replayArea } from '../../src/replays/storage.ts';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { parseArgs } from 'node:util';
import { farmingWindow } from '../../src/replays/farming-routes.ts';
import { historicalReviews, matchesReviewProfile, reviewDirectory, reviewProfile } from './review-model.ts';
const replayDir=replayArea();
const read=(p:string)=>JSON.parse(fs.readFileSync(p,'utf8'));
const {values}=parseArgs({options:{legacy:{type:'boolean'}}});
const profile=values.legacy?null:reviewProfile();
const reviewRoot=profile?reviewDirectory(profile,replayDir):historicalReviews(replayDir);
const provenance=profile?{provider:profile.provider,reasoningEffort:profile.reasoningEffort}:{};
const progress=read(`${replayDir}/hundred-progress.json`),catalog=read(`${replayDir}/hundred-candidates.json`);
if(!progress.complete||progress.completed.length!==100||new Set(progress.completed.map((m:any)=>m.matchId)).size!==100)
  throw new Error('Exactly 100 validated unique replays are required before publishing');
const matches=[],reviews=[],windows=[];
const sourceOrder=new Map<number,number>(catalog.matches.map((m:any,i:number)=>[m.matchId,i]));
for(const selected of progress.completed.toSorted((a:any,b:any)=>sourceOrder.get(a.matchId)!-sourceOrder.get(b.matchId)!)){
  const root=`${replayDir}/study/${selected.matchId}`;
  const full=read(root+'.route.json'),review=read(`${reviewRoot}/${selected.matchId}.json`);
  const expected=profile ? matchesReviewProfile(review,profile) : review.version===1 && review.model==='gpt-6-astra';
  if(!expected)throw new Error('Review does not belong to the selected publication profile');
  if(!full.complete||!full.validation.lastHitParity||full.validation.camps!==28||full.validation.towers!==22||
    review.matchId!==full.matchId)throw new Error('Invalid published match');
  const nonempty=(v:any)=>typeof v==='string'&&v.trim().length>0;
  if(!nonempty(review.result.summary)||review.result.observations.length<4||
    review.result.observations.some((o:any)=>!nonempty(o.fact)||!nonempty(o.interpretation)||!nonempty(o.coachQuestion))||
    review.result.counterexamples.some((o:any)=>!nonempty(o.fact))||
    review.result.unknowns.some((s:any)=>!nonempty(s)))throw new Error('Empty model study fields');
  const frames=[...new Set([0,300,600,900,1200,full.duration].map(t=>full.frames.find((f:any)=>f.time>=t)||full.frames.at(-1)))];
  let kept=-Infinity,pendingBreak=true;const trace=[];
  for(const point of full.trace){
    if(point[4])pendingBreak=true;
    if(point[0]-kept>=5||pendingBreak){trace.push([point[0],point[1],point[2],point[3],Number(pendingBreak)]);kept=point[0];pendingBreak=false;}
  }
  const {kills,casts,loadouts,frames:_frames,trace:_trace,...meta}=full;
  const stackingCase=selected.matchId===9089477184?read('data/luna-stacking-case.json'):undefined;
  matches.push({...meta,frames,trace,review:review.result,stackingCase,
    neutralKillEvents:kills.filter((k:any)=>k.kind==='neutral').length});
  reviews.push(review);
  // Common-window comparisons require actual coverage; short games aren't
  // extrapolated to 20 minutes or mixed with full-window rates.
  if(full.duration>=1200)windows.push({matchId:full.matchId,player:full.player,team:full.team,won:full.won,
    providerPro:full.providerPro,tournament:full.tournament,mmr:full.matchMmr,...farmingWindow(full.frames,600,1200)});
}
const quantile=(a:number[],q:number)=>{const s=a.toSorted((a,b)=>a-b),p=(s.length-1)*q,lo=Math.floor(p);return s[lo]+(s[Math.ceil(p)]-s[lo])*(p-lo);};
const usage=reviews.reduce((a:any,r:any)=>({input:a.input+(r.usage?.input_tokens??0),output:a.output+(r.usage?.output_tokens??0),
  total:a.total+(r.usage?.total_tokens??0)}),{input:0,output:0,total:0});
const stats={matches:100,professionalLabelled:matches.filter(m=>m.providerPro).length,
  tournaments:matches.filter(m=>m.tournament).length,pubs:matches.filter(m=>!m.tournament).length,
  reused:progress.completed.filter((m:any)=>m.reused).length,
  extraValidatedNotSelected:(progress.superseded||[]).length,
  radiant:matches.filter(m=>m.team==='radiant').length,wins:matches.filter(m=>m.won).length,
  knownAccounts:new Set(matches.filter(m=>m.accountId!=null).map(m=>m.accountId)).size,
  anonymousAccountMatches:matches.filter(m=>m.accountId==null).length,
  matchesWithoutNeutralKillAttribution:matches.filter(m=>m.neutralKillEvents>0&&m.validation.unresolvedNeutralKills===m.neutralKillEvents).length,
  totalReplayHours:matches.reduce((n,m)=>n+m.duration,0)/3600,
  full10to20Windows:windows.length,medianFarmPerMinute:quantile(windows.map(w=>w.farmPerMinute),.5),
  q25FarmPerMinute:quantile(windows.map(w=>w.farmPerMinute),.25),q75FarmPerMinute:quantile(windows.map(w=>w.farmPerMinute),.75),
  pooledLaneFraction:windows.reduce((n,w)=>n+w.laneGold,0)/windows.reduce((n,w)=>n+w.farmGold,0),
  windowsWithOwnStacks:windows.filter(w=>w.stacks>0).length,modelReviews:reviews.length,model:reviews[0].model,...provenance,usage};
const notes=[
  '100 场不同 match ID；职业筛选标识优先，再按目录对局平均 MMR 选择。不是全球最高 100 场，也不是随机代表性样本。',
  '每场完整解析原始 replay，并核对 7.41f 官方时间窗口、客户端 build、28 野点、22 塔和 10 人终局补刀。',
  '轨迹来自 Luna 本体实录；图库用 5 秒真实采样，选中对局加载更细轨迹。没有规则生成路线。',
  '分项金币来自实录累计计数器，含受控单位与幻象；线野金/分不等于整局 GPM 或最优收益。',
  '部分旧 build 的单位名称没有成功解出，营地击杀关联可能全部未知；活怪计数仍按出生器句柄关联，不把未知击杀猜到最近野点。',
  '职业名及 smurf 是 D2PT 标签，未独立认证账号；MMR 为对局平均值。PRO 表示赛事，不编造赛事 MMR。',
  '每局已由独立配置的大模型复盘，结论有窗口证据索引；模型文字仍是解释，不是因果证明。',
  '世界快照含全知回放信息，没有视野掩码；仅用于离线研究，未进入实战规划器。叠野计数不能证明拉野动作。',
  '短于 20 分钟的比赛不计入完整 10–20 分钟横向统计；查看时明确使用实际结束时刻。',
  '实战建议仍默认关闭。现有教练缺少已验收的兵线/敌方可见信息识别及允许活动区域校验。',
];
const result={version:1,patch:'7.41f',mode:'indexed-study',generatedAt:new Date().toISOString(),
  informationScope:'omniscient-offline-replay-only',stats,notes,matches};
fs.writeFileSync('data/luna-hundred-index.json',JSON.stringify(result)+'\n');
const recoveryResults=read(`${replayDir}/hundred-recovered.json`);
fs.writeFileSync('data/luna-hundred-audit.json',JSON.stringify({version:1,catalog,progress,recoveryResults,stats,windows,
  reviewHashes:reviews.map(r=>({matchId:r.matchId,inputSha256:r.inputSha256,
    resultSha256:createHash('sha256').update(JSON.stringify(r.result)).digest('hex'),model:r.model,...provenance,usage:r.usage}))},null,2)+'\n');
console.log(JSON.stringify(stats,null,2));
