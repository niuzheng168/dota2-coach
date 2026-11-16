import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { test } from 'node:test';
import { createSandboxServer } from '../src/sandbox/server.ts';

const {drawCampIcon,drawHeroIcon,heroMarkerSize,facingRotation}=await import(new URL('../public/simulator-icons.js',import.meta.url).href);
const manifest=JSON.parse(fs.readFileSync('public/minimap/manifest.json','utf8'));
const sha=(data:Buffer)=>createHash('sha256').update(data).digest('hex');
const icons={manifest,sheet:'atlas',heroes:{npc_dota_hero_luna:'luna'},camps:{occupied:'gold',empty:'grey',blocked:'orange'}};
function recording(){
  const images:any[]=[],rotations:number[]=[],texts:string[]=[],stack:any[]=[];
  let angle=0;
  const ctx={
    globalAlpha:1,filter:'none',strokeStyle:'',fillStyle:'',font:'',textAlign:'',lineWidth:1,
    save(){stack.push({angle,alpha:this.globalAlpha,filter:this.filter});},
    restore(){const s=stack.pop();angle=s.angle;this.globalAlpha=s.alpha;this.filter=s.filter;},
    translate(){},rotate(value:number){angle+=value;rotations.push(value);},
    drawImage(...args:any[]){images.push({args,angle,alpha:this.globalAlpha,filter:this.filter});},
    strokeRect(){},fillText(text:string){texts.push(text);},beginPath(){},moveTo(){},lineTo(){},stroke(){},
  };
  return {ctx,images,rotations,texts};
}
test('checked-in client sprites have matching hashes, dimensions, build and Valve attribution',()=>{
  const definitions=JSON.parse(fs.readFileSync('data/dota2-simulator-definitions.json','utf8'));
  assert.equal(manifest.serverBuild,definitions.serverBuild);assert.equal(manifest.patch,definitions.patch);
  assert.match(manifest.attribution,/Valve/);
  for(const asset of [manifest.sheet,...Object.values(manifest.heroes)] as any[]){
    const bytes=fs.readFileSync('public'+asset.url);
    assert.equal(sha(bytes),asset.sha256);
    assert.deepEqual([...bytes.subarray(0,8)],[137,80,78,71,13,10,26,10]);
    assert.equal(bytes.readUInt32BE(16),asset.width);assert.equal(bytes.readUInt32BE(20),asset.height);
  }
  assert.equal(manifest.heroes.npc_dota_hero_luna.source,'panorama/images/heroes/icons/npc_dota_hero_luna_png.vtex_c');
  assert.ok(manifest.sources.some((s:any)=>s.file==='scripts/mod_textures.txt'&&s.sha256.length===64));
});
test('all four camp kinds use their distinct client atlas rectangles, not circular placeholders',()=>{
  const rects={small:[0,32,32,32],medium:[0,416,32,32],large:[32,384,32,32],ancient:[0,384,32,32]};
  for(const [kind,rect]of Object.entries(rects)){
    const r=recording();
    drawCampIcon(r.ctx,icons,kind,{x:100,y:200},16,{hasNeutrals:true,spawnBlocked:true});
    assert.deepEqual(r.images[0].args,['gold',...rect,92,192,16,16]);assert.equal(r.images[0].alpha,1);
    assert.deepEqual(r.texts,[]);
  }
});
test('empty and blocked camps keep the same native sprite with visual state tint and opacity',()=>{
  for(const [status,image,alpha]of [
    [{hasNeutrals:false,spawnBlocked:false},'grey',.45],
    [{hasNeutrals:false,spawnBlocked:true},'orange',.85],
  ] as const){
    const r=recording();drawCampIcon(r.ctx,icons,'large',{x:0,y:0},18,status);
    assert.equal(r.images[0].args[0],image);assert.equal(r.images[0].alpha,alpha);
    assert.deepEqual(r.images[0].args.slice(1,5),[32,384,32,32]);assert.equal(r.ctx.globalAlpha,1);
  }
});
test('hero portrait stays upright; only the client arrow rotates, with world Y inverted on canvas',()=>{
  for(const facing of [0,Math.PI/2,Math.PI,-Math.PI/2,.394613889651819]){
    const r=recording();drawHeroIcon(r.ctx,icons,{name:'npc_dota_hero_luna',hp:100,facing},{x:100,y:200},28);
    assert.equal(r.images.length,2);assert.equal(r.images[0].args[0],'atlas');
    assert.deepEqual(r.images[0].args.slice(1,5),[128,576,128,128]);
    assert.ok(Math.abs(r.images[0].angle+facing)<1e-10);assert.equal(facingRotation(facing),-facing);
    assert.deepEqual(r.images[1].args,['luna',86,186,28,28]);assert.equal(r.images[1].angle,0);
    assert.deepEqual(r.rotations,[-facing]);
  }
  assert.equal(heroMarkerSize(1),28);assert.equal(heroMarkerSize(6),34);
});
test('dead hero uses dimmed portrait with no live facing arrow; missing assets are explicit placeholders',()=>{
  const dead=recording();drawHeroIcon(dead.ctx,icons,{name:'npc_dota_hero_luna',hp:0,facing:1},{x:0,y:0},28);
  assert.equal(dead.images.length,1);assert.equal(dead.images[0].filter,'grayscale(1)');
  assert.equal(dead.images[0].alpha,.5);assert.equal(dead.rotations.length,0);
  for(const name of ['npc_dota_hero_missing','npc_dota_hero_luna']){
    const r=recording();drawHeroIcon(r.ctx,name.includes('missing')?icons:null,{name,hp:100,facing:0},{x:0,y:0},28);
    assert.equal(r.images.length,0);assert.deepEqual(r.texts,['?']);
  }
});
test('local server serves only allowlisted minimap PNGs and module, without opening arbitrary file access',async()=>{
  const {server}=createSandboxServer();server.listen(0,'127.0.0.1');await once(server,'listening');
  const address=server.address();assert.ok(address&&typeof address==='object');
  const origin=`http://127.0.0.1:${address.port}`;
  try{
    for(const asset of [manifest.sheet,manifest.heroes.npc_dota_hero_luna]){
      const response=await fetch(origin+asset.url);assert.equal(response.status,200);
      assert.equal(response.headers.get('content-type'),'image/png');
      assert.equal(sha(Buffer.from(await response.arrayBuffer())),asset.sha256);
    }
    assert.equal((await fetch(origin+'/simulator-icons.js')).status,200);
    assert.deepEqual(await(await fetch(origin+'/minimap/manifest.json')).json(),manifest);
    for(const suffix of ['/minimap/missing.png','/minimap/../../.env']){
      assert.notEqual((await fetch(origin+suffix)).status,200);
    }
    assert.equal((await fetch(origin+'/minimap/luna.png',{headers:{Origin:'https://example.com'}})).status,403);
  }finally{await new Promise<void>(resolve=>server.close(()=>resolve()));}
});
