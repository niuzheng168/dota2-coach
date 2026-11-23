import { replayArea } from '../../src/replays/storage.ts';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { farmingMatch } from '../../src/replays/farming-routes.ts';
import { studyEvidence } from '../../src/replays/study-evidence.ts';
const replayDir=replayArea();
const read=(p:string)=>JSON.parse(fs.readFileSync(p,'utf8'));
const sha=(b:Buffer)=>createHash('sha256').update(b).digest('hex');
const id=Number(process.argv[2]),root=`${replayDir}/${id}`,catalog=read(`${replayDir}/hundred-candidates.json`);
const source=catalog.matches.find((m:any)=>m.matchId===id);
if(!source)throw new Error('ID not in observed candidate catalog');
const factsBytes=fs.readFileSync(root+'.facts.json'),apiBytes=fs.readFileSync(root+'.opendota.json');
const facts=JSON.parse(factsBytes.toString()),api=JSON.parse(apiBytes.toString()),acq=read(root+'.acquisition.json');
const patch=catalog.patchEvidence;
if(facts.demSha256!==acq.demSha256||sha(apiBytes)!==acq.metadataSha256||
  api.start_time<patch.from||(patch.until!==null&&api.start_time>=patch.until)||
  !patch.builds.some((b:any)=>b.build===facts.gameBuild))throw new Error('Hash/patch/build mismatch');
const match=farmingMatch(facts,api,read('data/dota2-map-reference.json'),source);
const provenance={factsSha256:sha(factsBytes),demSha256:facts.demSha256,archiveSha256:acq.archiveSha256,metadataSha256:acq.metadataSha256,parser:facts.parser};
fs.mkdirSync(`${replayDir}/study`,{recursive:true});
fs.writeFileSync(`${replayDir}/study/${id}.route.json`,JSON.stringify({...match,provenance})+'\n');
fs.writeFileSync(`${replayDir}/study/${id}.evidence.json`,JSON.stringify({...studyEvidence(facts,match),provenance})+'\n');
console.log(JSON.stringify({matchId:id,build:match.gameBuild,validation:match.validation,duration:match.duration}));
