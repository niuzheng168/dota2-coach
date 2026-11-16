import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { balancedEnd, entityValues, parseArrayData, deriveLanePaths, extractSpeedPaths,validateEngineMapCapture } from '../src/replays/source2-map.ts';

const read=(f:string)=>JSON.parse(fs.readFileSync(new URL(`../data/${f}`,import.meta.url),'utf8'));
const report=read('dota2-client-map-validation.json'),map=read('dota2-client-map.json');

test('Source2 entity values keep multiline data and resource names separate without evaluating code',()=>{
  const input=`keyValues3Data = {
    version = 1
    values = {
      classname = "dota_movespeed_modifier_path"
      model = resource_name:"maps/dota/entities/test.vmdl"
      origin = [ 0, -1.5, 2e2, ]
      local.angles = [ 0, 0, 0 ]
      note = "braces { are data }"
      pathNodes = """[ [0,0,0,0,0,0,1,2,0,], ]"""
    }
    attributes = {}
  }`;
  const e=entityValues(input)[0];
  assert.equal(e.model,'maps/dota/entities/test.vmdl');
  assert.deepEqual(e.origin,[0,-1.5,200]);
  assert.deepEqual(parseArrayData(e.pathNodes),[[0,0,0,0,0,0,1,2,0]]);
  assert.equal(e.note,'braces { are data }');
  assert.throws(()=>parseArrayData('[process.exit()]'));
});
test('Malformed/duplicate entity data and unclosed strings fail closed',()=>{
  assert.throws(()=>entityValues('keyValues3Data = { values={ a=1 a=2 } }'),/Duplicate/);
  assert.throws(()=>entityValues('keyValues3Data = { values={ a=NaN } }'));
  assert.throws(()=>balancedEnd('{ a="unterminated }',0));
  assert.throws(()=>balancedEnd('{ a="""bad }',0));
  assert.throws(()=>entityValues('not an entity resource'));
});
test('Lane paths follow explicit links, accept a self-terminal and reject real cycles',()=>{
  const entities=[
    {classname:'npc_dota_spawner_good_bot',NPCFirstWaypoint:'a',origin:[0,0,0]},
    {classname:'path_corner',targetname:'a',target:'b',origin:[1,2,3]},
    {classname:'path_corner',targetname:'b',target:'b',origin:[4,5,6]},
  ];
  assert.deepEqual(deriveLanePaths(entities)[0].points,[[1,2,3],[4,5,6]]);
  assert.throws(()=>deriveLanePaths(entities.map(e=>e.targetname==='b'?{...e,target:'a'}:e)),/Cyclic/);
  assert.throws(()=>deriveLanePaths([{...entities[0],NPCFirstWaypoint:'missing'},...entities.slice(1)]));
});
test('Speed-path shapes require all control arrays; extraction does not infer river boundaries',()=>{
  const e={classname:'dota_movespeed_modifier_path',hammerUniqueId:'test',origin:[0,0,0],
    angles:[0,0,0],scales:[1,1,1],pathNodes:'[[0,0,0,0,0,0,1,2,3,],]',
    pathNodeRadiusScales:'[100,]',pathNodeRadiusHeightScales:'[100,]',pathNodeMoveSpeedTypes:'["1",]'};
  const p=extractSpeedPaths([e])[0];
  assert.equal(p.nodes.length,1);assert.match(p.interpretation,/NOT inferred/);
  assert.throws(()=>extractSpeedPaths([{...e,pathNodeRadiusScales:'[]'}]));
});
test('Engine scans reject null heights, invalid bit rows and duplicate trees instead of coercing missing data',()=>{
  const capture={map:'dota',width:2,height:1,rows:[{index:0,heights:[0,128],traversable:'11',
    blocked:'00',nearbyTree0:'01',nearbyTree24:'01'}],trees:[{index:1,position:[0,0,0]}],
    entities:[],completed:{trees:1,towers:0}};
  assert.doesNotThrow(()=>validateEngineMapCapture(capture));
  const bad:any=structuredClone(capture);bad.rows[0].heights[0]=null;
  assert.throws(()=>validateEngineMapCapture(bad));
  assert.throws(()=>validateEngineMapCapture({...capture,rows:[{...capture.rows[0],blocked:'0?'}]}));
  assert.throws(()=>validateEngineMapCapture({...capture,trees:[...capture.trees,...capture.trees],completed:{trees:2,towers:0}}));
});
test('Installed numeric source hashes match only after documented newline normalization',()=>{
  assert.equal(report.client.serverBuild,6975);
  assert.equal(report.navigation.matchesPinnedSource,true);
  assert.equal(report.staticSources.length,5);
  for(const s of report.staticSources){
    assert.equal(s.normalizedLfSha256,s.pinnedSha256);
    assert.equal(s.matchesAfterOnlyCrlfToLf,true);
    assert.notEqual(s.rawSha256,s.normalizedLfSha256);
  }
  assert.equal(report.fullMapPhysicsCertified,false);
});
test('Full engine map scan validates heights and all prefab/layer trees, not main-lump-only counts',()=>{
  assert.equal(report.terrain.cells,104960);
  assert.equal(report.terrain.changedCellsAbove001,0);
  assert.ok(report.terrain.maxHeightDelta<0.01);
  assert.equal(report.trees.runtimeCount,2475);assert.equal(report.trees.positionsMatch,true);
  assert.equal(report.compiledEntities.baseLumpTrees,2306);
  assert.notEqual(report.compiledEntities.sha256,report.compiledEntities.upstreamEntityCaptureSha256);
  assert.equal(report.campBoxes.length,28);assert.equal(report.spawners.length,28);
  assert.ok(report.campBoxes.every((c:any)=>c.referenceError<0.01));
  assert.equal(report.towers.length,22);assert.equal(report.lanePaths.length,6);assert.equal(report.speedPaths.length,5);
  assert.ok(report.towers.every((t:any)=>t.positionError<0.01&&t.hull===144));
});
test('Initial engine point grid records all 18 old-grid differences without altering historical map',()=>{
  const old=read('dota2-map.json'),changes=[];
  for(let y=0;y<map.grid.height;y++)for(let x=0;x<map.grid.width;x++)
    if(map.grid.rows[y][x]!==old.grid.rows[y][x])changes.push({x:map.grid.origin.x+(x+.5)*64,y:map.grid.origin.y+(y+.5)*64});
  assert.equal(changes.length,18);
  assert.deepEqual(changes,report.pointNavigation.oldGridDifferences.map(({x,y}:any)=>({x,y})));
  assert.equal(map.fullMapPhysicsCertified,false);
  assert.match(report.pointNavigation.note,/not continuous/);
});
test('Engine-only local mechanics probes verify actual mask speed and tree collision transitions',()=>{
  const m=report.mechanics,s=(label:string)=>m.samples.find((x:any)=>x.label===label);
  assert.equal(m.completed.completed,true);
  assert.equal(m.completed.fullPhysicsCertified,false);
  assert.equal(s('bare-luna').idealSpeed,325);assert.equal(s('treads-mask-inactive').idealSpeed,370);
  assert.ok(Math.abs(s('mask-requested').idealSpeed-392.2)<0.001);
  assert.ok(s('mask-requested').modifiers.includes('modifier_item_mask_of_madness_berserk'));
  assert.ok(s('mask-cast-state').cooldown>0);
  assert.equal(s('tree-before').nearby,true);assert.equal(s('tree-cut').nearby,false);assert.equal(s('tree-regrown').nearby,true);
  assert.equal(m.completed.naturalTreeRegrowthTimerTested,false);
  assert.ok(m.samples.some((s:any)=>s.modifiers?.includes('modifier_path_movespeed')));
  const script=fs.readFileSync(new URL('../scripts/client/engine-mechanics-probe.lua',import.meta.url));
  assert.equal(m.scriptSha256,createHash('sha256').update(script).digest('hex'));
  assert.equal(m.client.mapPackageSha256,report.client.mapPackageSha256);
});
