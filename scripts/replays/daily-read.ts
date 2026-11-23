import fs from 'node:fs';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { readCoachMemory, readCoachMinute, readJson } from '../../src/replays/daily-memory.ts';
import { hkDay } from '../../src/replays/daily-study.ts';
import { replayArea } from '../../src/replays/storage.ts';
import { fogSituations, type FogSituation } from '../../src/replays/fog-decisions.ts';

const {values:{status,patch,hero,match,minute,situation}}=parseArgs({options:{status:{type:'boolean'},patch:{type:'string'},hero:{type:'string'},
  match:{type:'string'},minute:{type:'string'},situation:{type:'string'}}});
if(status){
  const p=path.join(replayArea('daily-study'),'runs',hkDay(),'status.json');
  if(!fs.existsSync(p)){console.error('今天没有运行记录。');process.exitCode=1;}
  else console.log(JSON.stringify(readJson(p),null,2));
}else if(!patch||!hero){
  console.error('指定 --patch <实际补丁号> --hero luna|troll_warlord|sven，或用 --status 查看今日进度。');process.exitCode=1;
}else if(situation!==undefined){
  if(match!==undefined||minute!==undefined||!fogSituations.includes(situation as FogSituation))throw Error('指定单个有效的 --situation，不与 --minute 混用');
  const m=readCoachMemory(patch,hero);
  if(!m?.situations){console.error('这个补丁/英雄没有己方视野索引。');process.exitCode=1;}
  else console.log(JSON.stringify({patch,hero,situation,fogEvidenceCases:m.fogEvidenceCases,...m.situations[situation]},null,2));
}else if(match!==undefined||minute!==undefined){
  if(!/^[1-9]\d{0,15}$/.test(match??'')||!/^(0|[1-9]\d*)$/.test(minute??''))throw Error('需要 --match <对局ID> 和 --minute <从0开始的分钟>');
  const row=readCoachMinute(patch,hero,Number(match),Number(minute));
  if(!row){console.error('该对局没有此分钟的实录。');process.exitCode=1;}else console.log(JSON.stringify(row,null,2));
}else {
  const m=readCoachMemory(patch,hero);
  if(!m){console.error('此补丁和英雄尚无记忆；不回退到旧版本。');process.exitCode=1;}
  else console.log(JSON.stringify(m,null,2));
}
