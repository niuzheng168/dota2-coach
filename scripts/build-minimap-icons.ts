// Read-only extraction from the installed client; never writes into the game.
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { parseKeyValues, kvObject } from './lib/valve-kv.ts';

const args=process.argv.slice(2).filter(a=>a!=='--check');
const install=path.resolve(args[0]||'C:/Steam/steamapps/common/dota 2 beta');
const cli=path.resolve(args[1]||'.tmp/client-audit/vrf-21.1/Source2Viewer-CLI.exe');
const hash=(data:Buffer)=>createHash('sha256').update(data).digest('hex');
const pin=JSON.parse(fs.readFileSync('data/dota2-client-tool-pin.json','utf8'));
if(hash(fs.readFileSync(cli))!==pin.executableSha256)throw new Error('Source2Viewer executable pin mismatch');
const definitions=JSON.parse(fs.readFileSync('data/dota2-simulator-definitions.json','utf8'));
const inf=fs.readFileSync(path.join(install,'game/dota/steam.inf'),'utf8');
if(Number(inf.match(/^ServerVersion=(\d+)/m)?.[1])!==definitions.serverBuild)throw new Error('Client/simulator build mismatch');
const base=path.resolve('.tmp/minimap-icons/build'),raw=path.join(base,'raw'),decoded=path.join(base,'decoded');
const texture='materials/vgui/hud/minimap_sheet_psd_b65d6dd9.vtex_c';
const portrait='panorama/images/heroes/icons/npc_dota_hero_luna_png.vtex_c';
const sources=['scripts/mod_textures.txt','scripts/minimap_icons.txt','materials/vgui/hud/minimap_sheet.vmat_c',texture,portrait];
const run=(args:string[])=>execFileSync(cli,args,{encoding:'utf8',windowsHide:true,timeout:90_000,maxBuffer:8*1024*1024});
run(['-i',path.join(install,'game/dota/pak01_dir.vpk'),'-f',sources.join(','),'-o',raw,'-q']);
run(['-i',raw,'--recursive','-e','vtex_c','-d','-o',decoded,'-q']);
const sheetMaterial=run(['-i',path.join(raw,'materials/vgui/hud/minimap_sheet.vmat_c'),'-b','DATA']);
if(!sheetMaterial.includes(texture.slice(0,-2)))throw new Error('Minimap sheet material points to a different texture');
const textureData=kvObject(kvObject(parseKeyValues(fs.readFileSync(path.join(raw,sources[0]),'utf8'))['sprites/640_hud']).TextureData);
const icons=kvObject(parseKeyValues(fs.readFileSync(path.join(raw,sources[1]),'utf8')).minimap_icons);
if(icons['76']!=='minimap_heroicon_arrow_large')throw new Error('Minimap hero arrow mapping changed');
const png=(source:string)=>{
  const data=fs.readFileSync(path.join(decoded,source.replace(/\.vtex_c$/,'.png')));
  if(!data.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10])))throw new Error('Invalid decoded PNG');
  return {data,width:data.readUInt32BE(16),height:data.readUInt32BE(20)};
};
const sheet=png(texture),luna=png(portrait);
function rect(name:string){
  const values=kvObject(textureData[name]);
  const [x,y,width,height]=['x','y','width','height'].map(k=>Number(values[k]));
  if(values.file!=='materials/vgui/hud/minimap_sheet.vmat'||![x,y,width,height].every(Number.isInteger)||
    x<0||y<0||width<=0||height<=0||x+width>sheet.width||y+height>sheet.height)throw new Error('Invalid minimap sprite: '+name);
  return {name,x,y,width,height};
}
const manifest={
  version:1,patch:definitions.patch,serverBuild:definitions.serverBuild,
  attribution:'Dota 2 minimap artwork belongs to Valve. Original client textures, unmodified; rendering uses the client sprite rectangles.',
  tool:{name:pin.name,version:pin.version,executableSha256:pin.executableSha256},
  sources:sources.map(file=>({file,sha256:hash(fs.readFileSync(path.join(raw,file)))})),
  sheet:{url:'/minimap/minimap-sheet.png',width:sheet.width,height:sheet.height,sha256:hash(sheet.data)},
  heroes:{npc_dota_hero_luna:{url:'/minimap/luna.png',width:luna.width,height:luna.height,sha256:hash(luna.data),source:portrait}},
  camps:{small:rect('minimap_creepcamp'),medium:rect('minimap_creepcamp_mid'),large:rect('minimap_creepcamp_big'),ancient:rect('minimap_creepcamp_ancient')},
  heroArrow:{...rect('minimap_heroicon_arrow_large'),pointsAtZero:'east',worldFacingUnit:'radians',canvasRotation:'-worldFacing'},
};
fs.mkdirSync('public/minimap',{recursive:true});
for(const [file,data]of [
  ['minimap-sheet.png',sheet.data],['luna.png',luna.data],
  ['manifest.json',Buffer.from(JSON.stringify(manifest,null,2)+'\n')],
] as const){
  const destination=path.join('public/minimap',file);
  if(process.argv.includes('--check')){
    if(!fs.readFileSync(destination).equals(data))throw new Error('Minimap asset differs: '+destination);
  }else fs.writeFileSync(destination,data);
}
console.log(`Verified client minimap artwork: build ${manifest.serverBuild}, four camp sprites, Luna portrait and facing arrow.`);
