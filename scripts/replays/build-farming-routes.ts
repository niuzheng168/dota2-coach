import { replayArea } from '../../src/replays/storage.ts';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { farmingMatch } from '../../src/replays/farming-routes.ts';

const replayDir=replayArea();
const read=(file:string)=>JSON.parse(fs.readFileSync(file,'utf8'));
const sha=(data:Buffer|string)=>createHash('sha256').update(data).digest('hex');
const catalog=read('data/luna-farming-source-catalog.json');
const reference=read('data/dota2-map-reference.json'),evidence=catalog.patchEvidence;
for(const build of evidence.builds){
  if(sha(build.text)!==build.sha256||Number(build.text.match(/^ServerVersion=(\d+)/m)?.[1])!==build.build||
    Date.parse(build.recordedAt)/1000<evidence.from)throw new Error('Invalid patch/build evidence');
}
const matches=[];
for(const source of catalog.matches){
  const root=`${replayDir}/${source.matchId}`,factsBytes=fs.readFileSync(root+'.facts.json'),apiBytes=fs.readFileSync(root+'.opendota.json');
  const facts=JSON.parse(factsBytes.toString()),api=JSON.parse(apiBytes.toString()),acquisition=read(root+'.acquisition.json');
  if(facts.demSha256!==acquisition.demSha256||sha(apiBytes)!==acquisition.metadataSha256||
    !evidence.builds.some((b:any)=>b.build===facts.gameBuild)||api.start_time<evidence.from||
    (evidence.until!==null&&api.start_time>=evidence.until))throw new Error('Replay hash/build/patch-window mismatch: '+source.matchId);
  const match=farmingMatch(facts,api,reference,source);
  matches.push({...match,provenance:{acquisition,factsSha256:sha(factsBytes),parser:facts.parser}});
  console.log(JSON.stringify({id:match.matchId,player:match.player,build:match.gameBuild,validation:match.validation,windows:match.windows}));
}
const result={version:1,patch:'7.41f',generatedFrom:catalog,
  informationScope:'omniscient-offline-replay-only',
  notes:[
    '实际 Luna 本体坐标，不是模拟生成路线。轨迹以至少 0.5 秒间隔保留真实采样，未插值；死亡、大位移与数据缺口断线。',
    '收入采用录像中的兵线/野怪分项累计计数器；含受控单位与幻象，不是本体独占收益，不包括工资/英雄/建筑收入。',
    '营地击杀只按同 tick、同单位名、唯一出生器归属关联；歧义保留未知，不按距离猜。经过营地不等于完成清野。',
    'D2PT MMR 是对局平均值，不是该职业选手的个人 MMR；职业及 smurf 名称是网站标识，未独立验证账号所有权。',
    '7.41f 由官方补丁时间窗口、客户端 build 来源和回放 build 交叉核对；6974 与 6975 都位于同一子补丁窗口。',
    '录像可能包含控图后进入敌区的行动，不代表实战可安全照搬；没有向实时规划器启用任何路线。',
  ],matches};
const output='data/luna-farming-routes.json',text=JSON.stringify(result)+'\n';
if(process.argv.includes('--check')){if(fs.readFileSync(output,'utf8')!==text)throw new Error('Farming corpus differs');}
else fs.writeFileSync(output,text);
