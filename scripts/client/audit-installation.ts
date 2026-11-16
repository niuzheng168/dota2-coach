import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { entityValues, extractSpeedPaths, deriveLanePaths, validateEngineMapCapture } from '../../src/replays/source2-map.ts';
import { parseKeyValues, kvObject } from '../lib/valve-kv.ts';

const install = path.resolve(process.argv[2] || 'C:/Steam/steamapps/common/dota 2 beta');
const cli = path.resolve(process.argv[3] || '.tmp/client-audit/vrf-21.1/Source2Viewer-CLI.exe');
const hash = (bytes: Buffer | string) => createHash('sha256').update(bytes).digest('hex');
const read = (f: string) => JSON.parse(fs.readFileSync(f, 'utf8'));
const pin = read('data/dota2-client-tool-pin.json');
if (hash(fs.readFileSync(cli)) !== pin.executableSha256) throw new Error('Source2Viewer CLI executable pin mismatch');
const inf = fs.readFileSync(path.join(install, 'game/dota/steam.inf'), 'utf8');
const build = Number(inf.match(/^ServerVersion=(\d+)/m)?.[1]);
if (build !== read('data/dota2-replay-source-pins.json').steamInf.serverBuild) throw new Error('Client/replay build mismatch');
const mapFile = path.join(install, 'game/dota/maps/dota.vpk'), mapHash = hash(fs.readFileSync(mapFile));
const cache = path.resolve('.tmp/client-audit', `installed-${mapHash.slice(0, 12)}`);
fs.mkdirSync(cache, { recursive: true });
const command = (args: string[]) => execFileSync(cli, args, { encoding: 'utf8', windowsHide: true,
  timeout: 90_000, maxBuffer: 64 * 1024 * 1024 });
const verification = command(['-i', mapFile, '--vpk_verify']);
if (!verification.includes('Verified the package.')) throw new Error('Map package verification did not complete');
fs.writeFileSync(path.join(cache, 'vpk-verification.txt'), verification);
const raw = path.join(cache, 'raw');
command(['-i', mapFile, '-f', 'maps/dota.gnv,maps/dota/entities/', '-o', raw, '-q']);
command(['-i', path.join(install, 'game/dota/pak01_dir.vpk'), '-f',
  'scripts/npc/npc_units.txt,scripts/npc/npc_abilities.txt,scripts/npc/items.txt,scripts/npc/heroes/npc_dota_hero_luna.txt,scripts/npc/heroes/npc_dota_hero_base.txt,resource/overviews/dota.txt',
  '-o', raw, '-q']);
const mapReference = read('data/dota2-map-reference.json');
const sourceHashes = read('data/dota2-jungle-data-hashes.json').sha256;
const sources = ['npc_units.txt','npc_abilities.txt','items.txt','heroes/npc_dota_hero_luna.txt','heroes/npc_dota_hero_base.txt'].map(name => {
  const bytes = fs.readFileSync(path.join(raw, 'scripts/npc', name));
  const normalized = bytes.toString('utf8').replace(/\r\n/g, '\n');
  return { name, bytes: bytes.length, rawSha256: hash(bytes), normalizedLfSha256: hash(normalized),
    pinnedSha256: sourceHashes[name], matchesAfterOnlyCrlfToLf: hash(normalized) === sourceHashes[name] };
});
if (sources.some(s => !s.matchesAfterOnlyCrlfToLf)) throw new Error('Installed game numeric source differs from pin');
const gnv = fs.readFileSync(path.join(raw, 'maps/dota.gnv'));
if (hash(gnv) !== mapReference.sources.files.find((f: any) => f.name === 'gridnav.gnv').sha256)
  throw new Error('Installed navigation differs from reference');
const entityFile = path.join(raw, 'maps/dota/entities/default_ents.vents_c');
const text = command(['-i', entityFile, '-b', 'DATA']);
fs.writeFileSync(path.join(cache, 'entities.txt'), text);
const entities = entityValues(text), speedPaths = extractSpeedPaths(entities), lanes = deriveLanePaths(entities);
const maxDelta = (a: number[], b: number[]) => {
  if (a.length !== b.length || ![...a, ...b].every(Number.isFinite)) throw new Error('Geometry vector mismatch');
  return Math.max(...a.map((n, i) => Math.abs(n - b[i])));
};
const campModels = entities.filter(e => e.classname === 'trigger_multiple' && /^\[PR#\]neutralcamp_/.test(e.targetname));
const boxRows = campModels.map(e => {
  if (e.angles.some((v: number) => v !== 0) || e.scales.some((v: number) => v !== 1))
    throw new Error('Only identity rotation/scale camp hulls currently supported; do not guess transforms');
  if (!/^maps\/dota\/entities\/neutralcamp_[a-z0-9_]+\.vmdl$/.test(e.model)) throw new Error('Unexpected camp model path');
  const modelFile = path.join(raw, e.model + '_c'), dump = command(['-i', modelFile, '-b', 'PHYS']);
  if ([...dump.matchAll(/\bm_Hull\s*=/g)].length !== 1 || !/\bm_meshes\s*=\s*\[\s*\]/.test(dump))
    throw new Error('Unsupported camp physics shape');
  const vector = (name: string) => {
    const matches = [...dump.matchAll(new RegExp(`\\b${name}\\s*=\\s*\\[([^\\]]+)\\]`, 'g'))];
    if (matches.length !== 1) throw new Error('Missing/ambiguous hull bounds');
    const value = matches[0][1].split(',').map(Number);
    if (value.length !== 3 || !value.every(Number.isFinite)) throw new Error('Invalid hull bound');
    return value;
  };
  const min = vector('m_vMinBounds').map((v, i) => v + e.origin[i]);
  const max = vector('m_vMaxBounds').map((v, i) => v + e.origin[i]);
  const id = e.targetname.slice(5), ref = mapReference.camps.find((c: any) => c.id === id);
  if (!ref) throw new Error('Uncatalogued client camp');
  return { id, min, max, modelSha256: hash(fs.readFileSync(modelFile)),
    referenceError: maxDelta([min[0],min[1],max[0],max[1]], [ref.bounds.min.x,ref.bounds.min.y,ref.bounds.max.x,ref.bounds.max.y]) };
});
if (boxRows.length !== 28 || boxRows.some(c => c.referenceError > 0.01)) throw new Error('Camp hull bounds mismatch');
const spawnerRows = entities.filter(e=>e.classname==='npc_dota_neutral_spawner').map(e=>{
  const id=String(e.VolumeName).replace(/^\[PR#\]/,'');
  const ref=mapReference.camps.find((c:any)=>c.id===id);
  if(!ref)throw new Error('Uncatalogued neutral spawner');
  return {id,position:e.origin,type:Number(e.NeutralType),referenceType:['small','medium','large','ancient'].indexOf(ref.kind),
    positionError:maxDelta(e.origin.slice(0,2),[ref.x,ref.y]),minSpawnType:e.MinSpawnType,maxSpawnType:e.MaxSpawnType,
    maxUpgradeCount:e.MaxUpgradeCount,pullType:e.PullType,aggroType:e.AggroType};
});
if(spawnerRows.length!==28||spawnerRows.some(s=>s.positionError>.01||s.type!==s.referenceType))throw new Error('Compiled spawner mismatch');
const laneRows = lanes.map(l => {
  const ref = mapReference.lanePaths.find((p: any) => p.id === l.id);
  if (!ref || ref.points.length !== l.points.length) throw new Error('Lane chain coverage mismatch');
  const error = maxDelta(l.points.flatMap(p => p.slice(0,2)), ref.points.flat());
  const spawnError = maxDelta(l.spawn.slice(0,2), ref.spawn);
  return { ...l, error, spawnError };
});
if (laneRows.some(l => l.error > 0.01 || l.spawnError > 0.01)) throw new Error('Compiled lane paths differ');
const units = kvObject(parseKeyValues(fs.readFileSync(path.join(raw, 'scripts/npc/npc_units.txt'),'utf8')).DOTAUnits);
const unitBase = kvObject(units.npc_dota_units_base);
const unitFields = ['MovementSpeed','MovementTurnRate','BoundsHullName','AttackRange','AttackRate','StatusHealth',
  'ArmorPhysical','AttackDamageMin','AttackDamageMax','BountyGoldMin','BountyGoldMax','VisionDaytimeRange','VisionNighttimeRange'];
const unitSpecs = Object.entries(units).filter(([name]) => /^npc_dota_(neutral_|creep_(goodguys|badguys)|.*tower)/.test(name))
  .map(([name, value]) => {
    const resolved = { ...unitBase, ...kvObject(value) };
    return { name, ...Object.fromEntries(unitFields.map(k => [k, resolved[k] ?? null])) };
  });
const runtimeFile = '.tmp/client-audit/runtime-map-tree-query.json';
const runtime = read(runtimeFile), terrain = read('.tmp/map-reference/terrain_grid.json');
validateEngineMapCapture(runtime);
if(runtime.client?.serverBuild!==build||runtime.client?.mapPackageSha256!==mapHash||
  runtime.scriptSha256!==hash(fs.readFileSync('scripts/client/engine-map-probe.lua')))throw new Error('Runtime scan does not match installed map/current probe');
if (runtime.rows.length !== terrain.height || runtime.map !== 'dota' || runtime.completed.trees !== runtime.trees.length)
  throw new Error('Incomplete runtime map scan');
let changedHeights = 0, maxHeightDelta = 0, navOnlyDifferences = 0;
const old = read('data/dota2-map.json');
const cellDifferences: any[] = [];
const engineGrid: string[] = [];
for (let y=0;y<terrain.height;y++) {
  const row = runtime.rows[y]; let cells = '';
  for (let x=0;x<terrain.width;x++) {
    const delta = Math.abs(row.heights[x]-terrain.heights[y][x]); maxHeightDelta = Math.max(maxHeightDelta, delta);
    if (delta > 0.01) changedHeights++;
    const flag = gnv[32+y*terrain.width+x], rawOpen = !!(flag&1) && !(flag&6);
    const navOpen = row.traversable[x] === '1' && row.blocked[x] === '0';
    if (rawOpen !== navOpen) navOnlyDifferences++;
    const open = navOpen && row.nearbyTree24[x] === '0';
    cells += open ? '1' : '0';
    if ((old.grid.rows[y][x] === '1') !== open) cellDifferences.push({
      x: terrain.origin[0]+(x+0.5)*64,y: terrain.origin[1]+(y+0.5)*64,
      oldOpen: old.grid.rows[y][x]==='1',enginePointQueryOpen: open });
  }
  engineGrid.push(cells);
}
const key = (p: number[]) => p.map(Math.fround).join(',');
const refTrees = new Set(terrain.trees.map(key));
const treeMatches = runtime.trees.length === refTrees.size && runtime.trees.every((t: any) => refTrees.has(key(t.position)));
const towers = runtime.entities.filter((e: any) => e.class === 'npc_dota_tower');
const towerRows = towers.map((t: any) => {
  const ref = mapReference.towers.find((r: any) => Math.hypot(r.position[0]-t.position[0],r.position[1]-t.position[1])<0.01);
  if (!ref) throw new Error('Runtime tower missing from reference');
  return { ...t, referenceId: ref.id, positionError: maxDelta(t.position,ref.position) };
});
const mechanicsFile = '.tmp/client-audit/runtime-mechanics.json';
const mechanics = fs.existsSync(mechanicsFile) ? read(mechanicsFile) : null;
if(mechanics && mechanics.completed.completed!==true)throw new Error('Do not publish a failed mechanics probe as validated');
if(mechanics && (mechanics.client?.mapPackageSha256!==mapHash||mechanics.scriptSha256!==hash(fs.readFileSync('scripts/client/engine-mechanics-probe.lua'))))
  throw new Error('Mechanics capture does not match the current probe/map');
const report = {
  version: 'installed-map-audit-1', date: '2026-11-15', fullMapPhysicsCertified: false,
  client: { serverBuild: build, steamInfSha256: hash(inf), mapPackageSha256: mapHash,
    packageVerification: 'checksums verified; package not signed', tool: pin },
  staticSources: sources, navigation: { bytes: gnv.length,sha256: hash(gnv),matchesPinnedSource:true,
    runtimeNavOnlyDifferences: navOnlyDifferences, note:'Point traversability alone excludes tree collision. Query IsNearbyTree separately.' },
  compiledEntities: { sha256:hash(fs.readFileSync(entityFile)), upstreamEntityCaptureSha256:read('.tmp/map-reference/building_vision.json').source.mapEntitiesSha256,
    entityCount:entities.length,baseLumpTrees:entities.filter(e=>e.classname==='ent_dota_tree').length,
    note:'Compiled lump hash differs from upstream capture; semantic and runtime checks below are authoritative for these fields, not a claim of whole-file equality.' },
  campBoxes:boxRows,spawners:spawnerRows,lanePaths:laneRows,towers:towerRows,
  terrain:{cells:terrain.width*terrain.height,changedCellsAbove001:changedHeights,maxHeightDelta,
    runtimeSha256:hash(fs.readFileSync(runtimeFile)),scriptSha256:runtime.scriptSha256},
  trees:{runtimeCount:runtime.trees.length,referenceCount:terrain.trees.length,positionsMatch:treeMatches,
    note:'All runtime trees include prefab/layer trees absent from the 2306-tree main lump.'},
  pointNavigation:{status:'measured-initial-state-only',hullQueryRadius:24,oldGridDifferences:cellDifferences,
    note:'No player state or tree mutations in this scan. Point-query grid is not continuous swept-hull collision or dynamic path proof.'},
  speedPaths, unitSpecs,mechanics,
  outstanding:['Exact spline interpolation/volume edge membership','Natural tree regrowth delay and live update integration',
    'Continuous collision and turn-rate execution','Tower target/aggro/damage execution in simulator','Neutral AI and full spawn composition/upgrade mechanics'],
};
if (!treeMatches || changedHeights || towerRows.length!==22) throw new Error('Runtime map/reference mismatch');
const mapOutput = {version:1,serverBuild:build,gameMap:'dota',mapPackageSha256:mapHash,
  sourceRuntimeSha256:report.terrain.runtimeSha256,grid:{...old.grid,rows:engineGrid},
  trees:runtime.trees.map((t:any)=>t.position),towers:towerRows,speedPaths,
  limitations:report.outstanding,fullMapPhysicsCertified:false};
function save(file: string,value: unknown) {
  const text=JSON.stringify(value)+'\n';
  if(process.argv.includes('--check')) {if(fs.readFileSync(file,'utf8')!==text)throw new Error('Installed audit differs: '+file);}
  else fs.writeFileSync(file,text);
}
save('data/dota2-client-map-validation.json',report);save('data/dota2-client-map.json',mapOutput);
console.log(JSON.stringify({build,gridBytesMatch:true,terrainCells:report.terrain.cells,changedHeights,trees:report.trees,
  camps:boxRows.length,towers:towerRows.length,lanes:laneRows.length,oldGridDifferences:cellDifferences.length,
  speedPaths:speedPaths.length,fullMapPhysicsCertified:false},null,2));
