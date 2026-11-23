import assert from 'node:assert/strict';
import type { Decisions } from './fog-decisions.ts';
import type { Evidence, MinuteNote, Observation } from './daily-study.ts';
import { digestJson } from './spell-rules.ts';
import { baseScenarios,hasMeasure, measure, threatRefs, type Threats, type ThreatMetric } from './spell-threats.ts';

// Keep reviewer input a positive projection. Neither the extraction trigger nor the next hit,
// future economy, score, qualification, match ID or an omniscient unit position belongs here.
export function fogReviewInput(d:Decisions,e:Evidence,th?:Threats){
  assert(d.matchId===e.matchId&&d.hero===e.hero&&d.patch===e.patch);
  if(th)assert(th.matchId===d.matchId&&th.hero===d.hero&&th.patch===d.patch&&th.build===e.build&&
    th.points.length===d.points.length&&th.points.every((p,i)=>p.id===d.points[i].id),'Wrong threat evidence for fog review');
  const bank:Record<string,unknown>={};
  function ref(v:unknown){const k=digestJson(v).slice(0,24);bank[k]=v;return k;}
  function spellCard(v:Threats,p:Threats['points'][number],own:Decisions['points'][number]['before']['own']){
    // No auditRanks, enemy hidden skill levels/CDs, rank of a prior cast, or events later than this card.
    const {ownDefence,ownSampleAt,strongestObserved5sInLast90s:burst,opponents}=p.before;
    const impact=(v:typeof opponents[number]['spells'][number]['lastTeamImpact'])=>v&&[v.at,v.target,v.amount,v.damageType,v.confirmedFocalBody];
    return {ownDefence,ownSampleAt,baseScenarios:baseScenarios(v,p,own?.hp??null,own?.maxHp??null),observedBurstRef:burst?ref(burst):null,opponents:opponents.map(({spells,...f})=>({...f,
      seen:spells.map(s=>[s.spell,ref({lastCast:s.lastVisibleCast&&[s.lastVisibleCast.at,s.lastVisibleCast.sideSource],
        lastTeamHit:impact(s.lastTeamImpact),lastBodyHit:impact(s.lastHitToMyBody)})])}))};
  }
  const halves=e.windows.flatMap(w=>w.halves.map(h=>({closes:Math.min(w.to,w.from+h.offset+30),from:h.sampledFrom,to:h.sampledTo,
    lastHits:h.lhGain,laneGold:h.laneGold,neutralGold:h.neutralGold,stacks:h.stacks,recordedBodyOrder:h.kills?.bodyOrder??null})));
  const cards=d.points.map(({key,before},i)=>({key,...before,...(th&&{threat:spellCard(th,th.points[i],before.own)}),
      pastIncome:halves.filter(h=>h.closes<=before.at&&h.to<=before.at&&before.at-h.closes<120),
      recentFirstMainSlot:e.firstMainSlot.filter(i=>i.t<=before.at&&before.at-i.t<120)})).sort((a,b)=>a.key.localeCompare(b.key));
  return '请按指定 JSON 结构复盘以下乱序局面。\n'+JSON.stringify({patch:d.patch,hero:d.hero,
    ...(th&&{build:th.build,publicRules:th.definitions,witnessBank:Object.fromEntries(Object.entries(bank).sort(([a],[b])=>a.localeCompare(b)))}),cards});
}
export const fogInstructions=`你是独立的 Dota 2 一号位教练，用中文为每张暂停卡判断下一步，而不是猜高手实际怎么选。
这些卡来自同一场的己方团队视角，打乱了顺序。对每张卡只用它自己的数据；不从别的卡的时间、装备或位置往回推。没有录像后果、排名、胜负或玩家眼动。卡中坐标为地图单位。整分和半分是时间参照，不保证刷新、遇敌或收到一波线。
读图：逐一交代能改变选择的敌方控制、切入、支援英雄。visible 的 markers 是我方视野里这个英雄外观的标记，可能有幻象/分身，不告诉你哪个是真身；now 仅在只有一个标记时给出。alive=false 的可见标记不是活人露头。hidden 是采样时没看到这个外观，不是死亡/开雾/在附近；unknown 是缺证。lastSeen 是最后一次单一标记的采样位置、当时的状态和已过去秒数；不是现在的位置，旧死亡信息不能排除复活/买活。最近可见兵的消失、队友能否接线/掩护、己塔和退路都要考虑；团队能读到不等于选手真的看到了。
localHeroExchanges 是过去15秒内己方可见的英雄类单位之间的伤害/死亡记录，双方此前最近1.5秒的采样相距不超过1500，多个同名敌方标记不强行定位。它不保证交手的都是本体。区分谁跟谁打、打点相对这条线有多远、过去了几秒、还有谁没露头；看到几人扎堆不是开团，过去在打不等于还在打，更不证明对手不能TP。没有交手证据也不能断言没有团战。lane/neutral 只列最近可见的一组：null 是没有可报价的可见目标，不是无兵/空营。直线距离不是走路耗时，listedGross 是所列实体毛赏金区间，不是预期可拿、英雄到账或整波/整营。lastLocalLane 是旧线索。自己的装备/技能冷却字段不是无条件脱身能力。
pastIncome 仅是该卡之前已结束的两分钟内的半分钟分项，含受控单位；recordedBodyOrder 只表示本体补刀先后，不代表步行路线。recentFirstMainSlot 是过去两分钟内装备主栏首见，不是购买时刻；结合装备能力与危险变化解释为什么值得换区，不要只列出装次序。
每张卡比较两个不同的可执行选项：比如接塔前残兵就退 vs 再送一波上坡；推线逼回防 vs 就近刷野留TP；等关键控制再露头 vs 立即露头。把动作、能拿/可能丢的线野资源、路程和时间机会成本、技能/装备窗口、暴露和退路说具体。未知而足以翻转判断时可给 unknown 或低信心，但仍给可执行的保守备选。明确何时应停止往深处走、什么新信号让你改判，别用“看情况”。远处打起来可创造带线窗口，但不自动等于能上高地；全员从图上消失不等于塔下的一刀也不能补。不要为了反对而反对，也不要为了职业选手找理由。不要附会真实意图、精确击杀概率、未观测的眼/TP/CD/地形、假想路线的实得金币。
每张输入卡均返回一次 decisionNotes，key 原样。preferred 和 alternative 为行动类别（可同类但必须是不同执行方案，例如都接线但停止线不同）；plan 和 alternativePlan 必须是具体不同动作，alternative 不得为 unknown。本人死亡时不写可以马上刷钱，比较复活后的条件方案。没有地图采样或任何敌方席位为 unknown 时 confidence 不得为 high。mapRead 写当时能知道与不能知道的威胁，tradeoff 比较两个方案，exitSignal 给退出/改判的可观测条件。给能核对的依据，不为字数省略关键分歧，不输出内在思维过程。observations 至少各有一条 items/farming，结合可重复或冲突的局面，分别给事实、有限解释、下次教练要问的问题，keys 只引用对应卡。`;
const options=['lane','neutral','hold','retreat','join','unknown'] as const,confidence=['low','medium','high'];
type Action=typeof options[number];
const string={type:'string'};
const obj=(properties:Record<string,unknown>)=>({type:'object',additionalProperties:false,required:Object.keys(properties),properties});
const arr=(items:unknown)=>({type:'array',items});
const schema=(spells:boolean)=>({name:spells?'farm_spell_threats_v2':'farm_team_view_decisions_v1',schema:obj({
  decisionNotes:arr(obj({key:string,preferred:{type:'string',enum:options},plan:string,alternative:{type:'string',enum:options},alternativePlan:string,
    mapRead:string,tradeoff:string,exitSignal:string,confidence:{type:'string',enum:confidence},...(spells&&{threatRead:string,threatRefs:arr(string),
      threatMetric:{anyOf:[{type:'null'},obj({ref:string,field:string,value:{type:'number'}})]}})})),
  observations:arr(obj({topic:{type:'string',enum:['items','farming']},fact:string,interpretation:string,coachQuestion:string,keys:arr(string)}))})});
export const fogSchema=schema(false),spellSchema=schema(true);
export const spellInstructions=fogInstructions+`
每张卡必须补上 threatRead、threatRefs。先按这五名对手分工：谁能先手、谁接控或持续输出、谁最后在远处/没蓝/刚交关键技能；选择真正影响这一步的威胁解释，不机械罗列五套满级连招。比较“现在被单个技能打到”和“被控制留在持续伤害里且另一人能赶到”，结合退路、己方血量、反制与不去的收益代价。
publicRules 是该服务器 build 的基础技能/防具 KV，各等级表是公开规则，不是敌方已学等级；slot=null、gates、conditional（天赋/神杖/魔晶等）不能当成已经生效。分清一次伤害、每秒、每跳、普攻系数/额外物理、纯粹、条件触发和控制。比如沙尘暴的 sand_storm_damage 是每秒，不是每跳；穿刺的基础伤害不能当作目标实际掉血。字段语义或版本没核实就明确缺证，不默认25%魔抗、不把未知算零。优先引用对本体曾经实际记录的数值与时间，再报有依据的面板/条件区间。
同场乱序卡共用 witnessBank；只准解引用本卡 threat 引用的键，禁止从其他卡回填。seen 条目为[技能名,记录键]；lastCast=[施放秒,阵营依据]，roster-label 仅为可见事件署名与敌方阵容相符，不证明哪个是真身；lastTeamHit/lastBodyHit=[秒,受击者,日志伤害,类型,确认焦点本体]，unattributed 是未署技能，不断言必为普攻。比较距离、弹道/命中条件、最近可见施放距今与基础冷却的区间；命中不是重新施法，不能从没有记录断定技能正好可用/未用。singleAppearanceSheet 是最后可见单一外观的等级、蓝量及年龄，不是消失后的实时面板。检查多个技能的总蓝耗、先后手射程和是否有时间接后续，不默认为五人同时能到。
observedBurstRef 是前90秒内同一英雄本体真实承受的最大连续5游戏秒窗口，附每项日志伤害、笔数和伤害类型，绝不是预测、净扣血或斩杀线。completedStunsTouchingEncounter 是附近平衡配对的实际眩晕起止，重叠只算一次，未能配对不补猜。比较那次受击时的防具和现在的：雨滴单次触发阈值/格挡/冷却不是每段都挡，主动治疗只有能按、有效且没溢出才算，受控时不把魔棒或仙灵火当作已加血。说明哪个新信号会推翻判断。没有观察或不能定量时给出缺少的关键信息，不编概率或敌方私密面板。
baseScenarios 的剩余冷却只是假定从那次可见施放起算、按各等级基础冷却且没有调整/重置/之后再次施放，0不代表实际转好。对旧受击窗口里的同方两个主动技能只做“两招现在都要重新施放”的最低面板耗蓝核算；已在持续中/回复蓝量都可能改判。主动治疗估算已按最大血量封顶，不含治疗削减或来不及按的情况。
threatRefs 只写本卡有效的依据：burst（本卡有峰值）、defence（有己方防具记录）、seen/<敌方完整npc名称>/<本卡见过的技能名>、rule/<敌方完整npc名称>/<有公开定义的技能>、map/<敌方完整npc名称>；整卡无这些依据才写 unobserved。只要有技能定义、既往事件或峰值，至少引用一项相应依据，threatRead 用它解释判断。
threatMetric 选一个对选择重要的数值作机器核对：ref 必须也在 threatRefs；field 对 burst 用 loggedDamage 或 nonOverlappingStunSeconds，对 seen 用 bodyDamage、teamDamage 或 castAt（绝对游戏秒），对 rule 用原 KV 字段名@从1开始的下标（例如 impale_damage@2），对 map 用 manaAtSighting，对 defence 可用 item_magic_wand.ifActivatedHP 等。value 抄原值，不取整；没有任何可核对的数字才填 null。不要把引用某一档的规则解释为已知对手是该等级。`;
export type DecisionNote={id:string;preferred:Action;plan:string;alternative:Action;alternativePlan:string;mapRead:string;tradeoff:string;
  exitSignal:string;confidence:string;nextFarmComparison:'same-family'|'different-family'|'not-comparable';threatRead?:string;threatRefs?:string[];threatMetric?:ThreatMetric|null};
export function checkedFogReview(raw:any,d:Decisions,e:Evidence,th?:Threats){
  const text=(s:any)=>typeof s==='string'&&s.trim().length>0&&s.length<=8192; // integrity limit, not an editorial length target
  assert(Array.isArray(raw?.decisionNotes)&&raw.decisionNotes.length===d.points.length,'Incomplete fog decision coverage');
  const byKey=new Map<string,any>(raw.decisionNotes.map((n:any)=>[n?.key,n]));
  const indexed=new Map(d.points.map(p=>[p.key,p]));
  const decisionNotes:DecisionNote[]=d.points.map((p,i)=>{
    const n=byKey.get(p.key);assert(n&&options.includes(n.preferred)&&options.includes(n.alternative)&&confidence.includes(n.confidence)&&
      [n.plan,n.alternativePlan,n.mapRead,n.tradeoff,n.exitSignal].every(text)&&n.plan.trim()!==n.alternativePlan.trim()&&n.alternative!=='unknown',
      'Missing, duplicate or invalid two-option judgement');
    const m=p.before.map;
    assert(n.confidence!=='high'||(m&&m.enemies.every(a=>a.state!=='unknown')),'Cannot claim high confidence with missing team vision');
    const actual=p.after.nextBodyLastHit?.kind;
    const comparison=actual&&(n.preferred==='lane'||n.preferred==='neutral')?n.preferred===actual?'same-family':'different-family':'not-comparable';
    if(th){const sp=th.points[i];assert(sp?.id===p.id&&sp.key===p.key,'Wrong spell checkpoint');
      const allowed=threatRefs(th,sp),numeric=(k:string)=>k==='burst'||k.startsWith('seen/')||k.startsWith('rule/');
      assert(text(n.threatRead)&&Array.isArray(n.threatRefs)&&n.threatRefs.length&&n.threatRefs.every((k:any)=>allowed.has(k))&&
        (![...allowed].some(numeric)||n.threatRefs.some(numeric)),'Missing or ungrounded spell threat assessment');
      const q=n.threatMetric;assert(q===null?!hasMeasure(th,sp):q&&n.threatRefs.includes(q.ref)&&typeof q.field==='string'&&
        Number.isFinite(q.value)&&measure(th,sp,q.ref,q.field)===q.value,'Spell number does not match this checkpoint/target/rank');}
    return {id:p.id,preferred:n.preferred,plan:n.plan,alternative:n.alternative,alternativePlan:n.alternativePlan,mapRead:n.mapRead,
      tradeoff:n.tradeoff,exitSignal:n.exitSignal,confidence:n.confidence,nextFarmComparison:comparison,...(th&&{threatRead:n.threatRead,threatRefs:n.threatRefs,threatMetric:n.threatMetric})};
  });
  const minuteNotes:MinuteNote[]=e.windows.map(w=>{
    const at=d.points.findIndex(p=>p.minute===w.minute&&p.triggers.includes('minute'));assert(at>=0);
    const n=decisionNotes[at];
    return {minute:w.minute,evidenceId:w.id,reading:`${n.mapRead} ${n.threatRead??''} ${n.plan} 对比：${n.alternativePlan}。${n.tradeoff} 改判条件：${n.exitSignal}`.replace('  ',' ')};
  });
  assert(Array.isArray(raw.observations)&&raw.observations.length>=2,'Missing coaching observations');
  const observations:Observation[]=raw.observations.map((o:any)=>{
    assert(['items','farming'].includes(o?.topic)&&[o.fact,o.interpretation,o.coachQuestion].every(text)&&Array.isArray(o.keys)&&
      o.keys.length>0&&o.keys.every((k:any)=>indexed.has(k)),'Unanchored fog coaching observation');
    return {topic:o.topic,fact:o.fact,interpretation:o.interpretation,coachQuestion:o.coachQuestion,
      evidenceIds:[...new Set<string>(o.keys.map((k:string)=>indexed.get(k)!.id))]};
  });
  assert(new Set(observations.map(o=>o.topic)).size===2,'Must cover both items and farming');
  return {decisionNotes,minuteNotes,observations};
}
