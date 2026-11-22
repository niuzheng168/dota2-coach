/** Read-only, build-pinned VPK extraction. The only executable invoked is the
 * pinned file decoder. Do not run a server/client or reuse its runtime grid. */
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { crc32, deflateSync, inflateSync } from 'node:zlib';
import { entityValues, extractSpeedPaths } from '../../src/replays/source2-map.ts';
import { decodeGnv742, decodeTrees742, openGnv742, POLICY742 } from '../../src/tactics/terrain-742.ts';
import type { Blob742, Structure742 } from '../../src/tactics/terrain-742.ts';
import { kvObject, numbers, parseKeyValues } from '../lib/valve-kv.ts';
import { CLIP_RISE_RESERVE, sha742 as sha, surfaceGrid742 } from './terrain-742-physics.ts';

const root = path.resolve(import.meta.dirname, '../..'), work = path.join(root, '.tmp/farm742-terrain');
const file = (p: string) => path.join(root, p);
const json = (p: string) => JSON.parse(fs.readFileSync(file(p), 'utf8'));
const mapHash = '7fc24ff13fcbe6840e5b9d76beb23f681368ecd2aa8b32a1e31e3cda959bd304';
const commit = '76ab89451c1384c2e106b676258c6e9f72787e11';
// Raw public files at this exact GameTracking-Dota2 commit. Local VPK member
// bytes may differ by CRLF only; no loose numeric equivalence.
const publicPins: Record<string, string> = {
  'steam.inf': 'e5a14b290a771c54148039dd4ca7d2e23c730cbd4a08b972c6e01cea24b03843',
  'scripts/npc/heroes/npc_dota_hero_luna.txt': '82ebaf8770330f0f3ddfd2b25924d53f26e3679272529cbc32ae8342b07b46a8',
  'scripts/npc/heroes/npc_dota_hero_troll_warlord.txt': '8d9a261be8cb387c5ef29e19506facf39003b0c62a9c79c0f6c765a2d4400611',
  'scripts/npc/items.txt': '3aa970ea2b1e5fa025731a98748329b4ebfb3a40db63bf63c7b928f5110345af',
  'scripts/npc/npc_units.txt': '1a8e4f01480630cfb9aa1daa2eb1bc348d7793c0ae4ced6869f1e552b63d6596',
};
const pack = (b: Buffer): Blob742 => ({ bytes: b.length, sha256: sha(b), zlib: deflateSync(b, { level: 9 }).toString('base64') });
const sorted = (a: string, b: string) => a < b ? -1 : a > b ? 1 : 0;
const round3 = (n: number) => Math.round(n * 1000) / 1000;

function extract(install: string, tool: string) {
  fs.mkdirSync(work, { recursive: true });
  const toolPin = json('data/dota2-client-tool-pin.json');
  assert.equal(sha(fs.readFileSync(tool)), toolPin.executableSha256, 'Unpinned Source2Viewer CLI');
  const run = (args: string[]) => execFileSync(tool, args, { cwd: work, windowsHide: true, encoding: 'utf8',
    timeout: 120_000, maxBuffer: 100 * 1024 * 1024 });
  const game = path.join(install, 'game/dota'), vpk = path.join(game, 'maps/dota.vpk'), steam = path.join(game, 'steam.inf');
  const inf = fs.readFileSync(steam);
  assert.match(inf.toString(), /^ClientVersion=6989\r?$/m); assert.match(inf.toString(), /^ServerVersion=6989\r?$/m);
  assert.equal(sha(fs.readFileSync(vpk)), mapHash, 'Do not silently upgrade the 7.42 snapshot');
  const verify = run(['-i', vpk, '--vpk_verify']);
  assert(verify.includes('Verified the package.') && verify.includes('not signed') && verify.includes('Verified the package checksums.'));
  const raw = path.join(work, 'raw'), bytes = (p: string) => fs.readFileSync(path.join(raw, p));
  const members = ['maps/dota.gnv', 'maps/dota.trm', 'maps/dota/world_physics.vmdl_c', 'maps/dota/entities/'];
  run(['-i', vpk, '-f', members.join(','), '-o', raw, '-q']);
  const listing = run(['-i', vpk, '-f', members.join(','), '--vpk_list']);
  const checked = new Set<string>();
  for (const line of listing.trim().split(/\r?\n/)) {
    const row = /^([\w./-]+) CRC:([a-f0-9]+) size:(\d+)$/i.exec(line);
    assert(row && !row[1].split('/').includes('..') && row[1].startsWith('maps/'), 'Unexpected VPK directory record');
    const b = bytes(row[1]); assert.equal(crc32(b), Number.parseInt(row[2], 16)); assert.equal(b.length, Number(row[3]));
    checked.add(row[1]);
  }
  assert(members.slice(0, 3).every(p => checked.has(p)));
  const scripts = Object.keys(publicPins).filter(p => p !== 'steam.inf');
  run(['-i', path.join(game, 'pak01_dir.vpk'), '-f', scripts.join(','), '-o', raw, '-q']);
  const numericFiles = Object.entries(publicPins).map(([name, publicSha256]) => {
    const b = name === 'steam.inf' ? inf : bytes(name);
    assert.equal(sha(b.toString('utf8').replace(/\r\n/g, '\n')), publicSha256, `Public version differs: ${name}`);
    return { name, rawSha256: sha(b), lfSha256: publicSha256 };
  });
  const gnv = bytes(members[0]), trm = bytes(members[1]), g = decodeGnv742(gnv), t = decodeTrees742(trm);
  const surface = surfaceGrid742(run(['-i', path.join(raw, members[2]), '-b', 'PHYS']), g);
  const entityFiles = ['default_ents', 'world_layer_dire_base', 'world_layer_dire_destruction',
    'world_layer_radiant_base', 'world_layer_radiant_destruction'];
  const layers = entityFiles.map(name => {
    const p = `maps/dota/entities/${name}.vents_c`;
    assert(checked.has(p));
    const ents = entityValues(run(['-i', path.join(raw, p), '-b', 'DATA']));
    return { name, sha256: sha(bytes(p)), ents };
  });
  const main = layers[0].ents, mapLayers = main.filter(e => e.classname === 'info_world_layer');
  assert.deepEqual(mapLayers.map(e => e.layerName).sort(), entityFiles.slice(1));
  assert(mapLayers.every(e => e.spawnflags === Number(e.layerName.endsWith('_base'))));
  const xy = (x: number, y: number) => `${x},${y}`;
  const locations = new Set(t.trees.map(p => xy(p.x, p.y)));
  const treeRecords = new Map<string, number[]>();
  const treeZ = Buffer.alloc(2 * t.trees.length);
  for (const [i, p] of t.trees.entries()) {
    const key = `${p.layer}:${xy(p.x, p.y)}`;
    const slots = treeRecords.get(key) ?? []; slots.push(i); treeRecords.set(key, slots);
    treeZ.writeInt16LE(-32768, 2 * i);
  }
  const lumps = layers.map(l => {
    const treeRows = l.ents.filter(e => e.classname === 'ent_dota_tree');
    const index = t.names.indexOf(l.name === 'default_ents' ? '' : l.name);
    for (const e of treeRows) {
      const key = `${index}:${xy(e.origin[0], e.origin[1])}`, slot = treeRecords.get(key)?.shift();
      assert(slot !== undefined, `Map TRM and active entity layer disagree: ${key}`);
      const q = Math.round(e.origin[2] * 4); assert(Number.isFinite(q) && q > -32768 && q <= 32767);
      treeZ.writeInt16LE(q, 2 * slot);
    }
    return { name: l.name, sha256: l.sha256, entities: l.ents.length, exactTreeXYMatches: treeRows.length };
  });
  const readKV = (p: string) => parseKeyValues(bytes(p).toString('utf8'));
  const units = kvObject(readKV('scripts/npc/npc_units.txt').DOTAUnits);
  const items = kvObject(readKV('scripts/npc/items.txt').DOTAAbilities);
  const item = (n: string) => kvObject(kvObject(items[`item_${n}`]).AbilityValues);
  const hero = (n: string) => {
    const k = `npc_dota_hero_${n}`;
    return kvObject(kvObject(readKV(`scripts/npc/heroes/${k}.txt`).DOTAHeroes)[k]);
  };
  const luna = hero('luna'), troll = hero('troll_warlord');
  const rage = kvObject(kvObject(kvObject(troll.AbilityDefinitions).troll_warlord_berserkers_rage).AbilityValues);
  assert.equal(luna.BoundsHullName, 'DOTA_HULL_SIZE_HERO'); assert.equal(troll.BoundsHullName, luna.BoundsHullName);
  const speedFields = { heroBase: { luna: Number(luna.MovementSpeed), troll_warlord: Number(troll.MovementSpeed) },
    bootsFlat: Number(item('boots').bonus_movement_speed), treadsFlatRanged: Number(item('power_treads').bonus_movement_speed_ranged),
    treadsFlatMelee: Number(item('power_treads').bonus_movement_speed_melee), meleeRageByLevel: numbers(rage.bonus_move_speed),
    phaseActivePctRanged: Number(item('phase_boots').phase_movement_speed_range), phaseActivePctMelee: Number(item('phase_boots').phase_movement_speed),
    madnessActivePctRanged: Number(item('mask_of_madness').berserk_bonus_movement_speed),
    madnessActivePctMelee: Number(item('mask_of_madness').berserk_bonus_movement_speed_melee) };

  // Reserve envelopes, NOT engine hull radii. Leave space around objects whose
  // actual blocking behaviour changes with state. The tower target/range is not a hull.
  const reserves: Record<string, number> = { SMALL: 48, FILLER: 144, TOWER: 160, BUILDING: 320, BARRACKS: 320, UNSPECIFIED: 320 };
  const buildings = main.filter(e => ['npc_dota_tower', 'npc_dota_barracks', 'npc_dota_fort', 'npc_dota_building',
    'npc_dota_base_blocker', 'npc_dota_unit_twin_gate', 'npc_dota_lotus_pool', 'npc_dota_watch_tower',
    'npc_dota_lantern', 'npc_dota_xp_fountain', 'ent_dota_fountain'].includes(e.classname));
  const structures = buildings.map((e): Structure742 => {
    const hull = String(kvObject(units[e.MapUnitName]).BoundsHullName || 'UNSPECIFIED').replace(/^DOTA_HULL_SIZE_/, '');
    assert(Object.hasOwn(reserves, hull));
    return { id: `${e.classname}:${e.hammerUniqueId}`, xy: [e.origin[0], e.origin[1]], hull, reserve: reserves[hull] };
  }).sort((a, b) => sorted(a.id, b.id));

  const corpus = json('data/farming-742-routes.json');
  assert(corpus.patch.version === '7.42' && corpus.builds.some((b: any) => b.build === 6989 && b.commit === commit && b.sha256 === publicPins['steam.inf']));
  const towers = main.filter(e => e.classname === 'npc_dota_tower');
  assert.equal(towers.length, 22);
  const delta = (a: number[], b: number[]) => {
    assert.equal(a.length, b.length);
    assert([...a, ...b].every(Number.isFinite)); return Math.max(...a.map((x, i) => Math.abs(x - b[i])));
  };
  const towerError = Math.max(...towers.map(e => {
    const t = corpus.map.towers.find((r: any) => r.id === e.targetname.slice(5));
    assert(t && t.team === Number(e.teamnumber)); return delta(e.origin.slice(0, 2), [t.x, t.y]);
  }));
  let centerError = 0, boxError = 0;
  const camps = main.filter(e => e.classname === 'npc_dota_neutral_spawner').map(e => {
    const name = String(e.VolumeName), id = name.replace(/^\[PR#\]neutralcamp_/, '');
    const c = corpus.map.camps.find((c: any) => c.id === id), boxes = main.filter(e => e.classname === 'trigger_multiple' && e.targetname === name);
    assert(c && boxes.length === 1 && Number(e.NeutralType) === c.initialTier);
    const box = boxes[0]; assert.deepEqual(box.angles, [0, 0, 0]); assert.deepEqual(box.scales, [1, 1, 1]);
    assert(/^maps\/dota\/entities\/neutralcamp_\w+\.vmdl$/.test(box.model));
    const model = `${box.model}_c`; assert(checked.has(model));
    const phys = run(['-i', path.join(raw, model), '-b', 'PHYS']);
    assert([...phys.matchAll(/\bm_Hull\s*=/g)].length === 1 && /\bm_meshes\s*=\s*\[\s*\]/.test(phys));
    const bounds = ['m_vMinBounds', 'm_vMaxBounds'].flatMap(k => {
      const m = [...phys.matchAll(new RegExp(`\\b${k}\\s*=\\s*\\[([^\\]]+)\\]`, 'g'))];
      assert.equal(m.length, 1);
      const v = m[0][1].split(',').map(Number); assert(v.length === 3 && v.every(Number.isFinite));
      return [v[0] + box.origin[0], v[1] + box.origin[1]];
    });
    centerError = Math.max(centerError, delta(e.origin.slice(0, 2), [c.x, c.y]));
    boxError = Math.max(boxError, delta(bounds, c.box));
    return { id, origin: e.origin as number[], bounds: bounds.map(round3), hullSha256: sha(bytes(model)) };
  }).sort((a, b) => sorted(a.id, b.id));
  assert.equal(new Set(camps.map(c => c.id)).size, 28);
  assert(Math.max(towerError, centerError, boxError) < 0.53, 'Not the 7.42 replay landmarks (rounded to units)');
  const paths = extractSpeedPaths(main).map(({ interpretation: _, ...raw }) => raw);
  const old = json('data/dota2-map-reference.json');
  assert.equal(old.patch, '7.41f');
  const oldGrid = old.rawNavigation;
  assert.deepEqual([oldGrid.width, oldGrid.height, oldGrid.cellSize, oldGrid.origin.x, oldGrid.origin.y], [g.width, g.height, 64, g.x0, g.y0]);
  const oldTrees = new Set<string>(old.trees.map(([x, y]: [number, number]) => xy(Math.round(x), Math.round(y))));
  let opened = 0, closed = 0, rawOpen = 0, openWithoutHeight = 0;
  for (const [i, f] of g.flags.entries()) {
    const now = openGnv742(f), before = oldGrid.rows[Math.floor(i / g.width)][i % g.width] === '1';
    if (now && !before) opened++;
    if (!now && before) closed++;
    if (now) { rawOpen++; if (surface.bytes.readInt16LE(2 * i) === -32768) openWithoutHeight++; }
  }
  assert.equal(sha(fs.readFileSync(vpk)), mapHash); assert.deepEqual(inf, fs.readFileSync(steam));
  return { version: 1, patch: '7.42', serverBuild: 6989, map: 'dota', sourceRevision: 11264029,
    format: { scope: 'Offline static foot navigation; not an engine path, dynamic collision proof or farm-cycle ETA.',
      blobs: 'zlib/base64; bytes and SHA256 refer to uncompressed payloads',
      gnv: 'Original VPK member incl. header; ascending Y rows; (flags & 1) && !(flags & 6)',
      trm: 'Original v1 tree table incl. layer names, duplicate retained; records are LE int32 X,Y,layer (NOT tree Z)',
      entityTreeZQ4: 'TRM order, int16LE / 4, -32768 if not matched to a compiled tree entity',
      solidSurfaceQ4: 'GNV order, highest default PHYS surface at each cell center, int16LE / 4, -32768 if no hit',
      clipMask: 'GNV order, byte bit0=projected npcclip/playerclip geometry, bit1=raised reserve',
      speed: 'Required actual/estimated effective XY units/s for the whole leg; heroBase is NOT a substitute.',
      routingPolicy: { ...POLICY742, structureReserves: reserves, notEngineRadii: true } },
    sources: { mapVpk: { sha256: mapHash, bytes: fs.statSync(vpk).size, verifiedChecksums: true, signed: false },
      cli: { version: toolPin.version, executableSha256: toolPin.executableSha256 }, publicGameTrackingCommit: commit,
      numericFiles, lumps, worldPhysicsSha256: sha(bytes(members[2])) },
    grid: { width: g.width, height: g.height, cellSize: g.cellSize, origin: [g.x0, g.y0], rawOpen },
    gnv: pack(gnv), trm: pack(trm), entityTreeZQ4: pack(treeZ), solidSurfaceQ4: pack(surface.bytes), clipMask: pack(surface.clip),
    physics: { group: 'default', vertices: surface.vertexCount, triangles: surface.triangleCount,
      nonverticalTriangles: surface.projected, centerHits: surface.hits, rawOpenWithoutHeight: openWithoutHeight,
      clipTriangles: surface.clipTriangles, clipReserveRise: CLIP_RISE_RESERVE,
      clipXYCells: surface.clip.reduce((n, c) => n + Number(!!c), 0),
      raisedClipRawOpen: surface.clip.reduce((n, c, i) => n + Number(!!(c & 2) && openGnv742(g.flags[i])), 0) },
    trees: { trmRecords: t.trees.length, uniqueXY: locations.size,
      onlyTrmRecords: [...treeRecords].filter(([, slots]) => slots.length).map(([key, slots]) => [key, slots.length]),
      entityXYMatches: lumps.reduce((a, l) => a + l.exactTreeXYMatches, 0) },
    changeFrom741f: { priorMapCommit: old.sources.commit, priorGnvSha256: old.sources.files.find((f: any) => f.name === 'gridnav.gnv').sha256,
      gnvOpened: opened, gnvClosed: closed, treeXYAdded: [...locations].filter(p => !oldTrees.has(p)).length,
      treeXYRemoved: [...oldTrees].filter(p => !locations.has(p)).length },
    structures, camps, speedFields, speedPaths: paths,
    replayLandmarkCheck: { matches: corpus.matches.map((m: any) => m.id), towers: towers.length, camps: camps.length,
      maxAbsAxisDeltaVsRoundedReplay: { tower: round3(towerError), spawner: round3(centerError), boxEdge: round3(boxError) } } };
}

if (process.argv[1] && path.resolve(process.argv[1]) === import.meta.filename) {
  const args = process.argv.slice(2), positional = args.filter(a => a !== '--check');
  if (positional.length > 2 || positional.some(a => a.startsWith('--'))) throw new Error('Usage: terrain-742-extract.ts [install] [CLI] [--check]');
  const out = file('data/farming-742-terrain.json');
  const result = extract(path.resolve(positional[0] || 'C:/Steam/steamapps/common/dota 2 beta'),
    path.resolve(positional[1] || file('.tmp/client-audit/vrf-21.1/Source2Viewer-CLI.exe')));
  if (args.includes('--check')) {
    const saved = JSON.parse(fs.readFileSync(out, 'utf8'));
    // Different zlib implementations may encode the same payload differently.
    for (const k of ['gnv', 'trm', 'entityTreeZQ4', 'solidSurfaceQ4', 'clipMask'] as const) {
      const limit = { maxOutputLength: result[k].bytes };
      assert.deepEqual(inflateSync(Buffer.from(saved[k].zlib, 'base64'), limit), inflateSync(Buffer.from(result[k].zlib, 'base64'), limit));
      saved[k].zlib = result[k].zlib;
    }
    // KV3 spline tangents include signed zero; published JSON represents -0
    // as 0. Compare the same public representation, never float tolerances.
    assert.deepEqual(saved, JSON.parse(JSON.stringify(result)));
  } else fs.writeFileSync(out, JSON.stringify(result) + '\n');
  console.log(JSON.stringify({ verified: out, grid: result.grid, trees: result.trees, surface: result.physics,
    comparedWith741f: result.changeFrom741f, replay: result.replayLandmarkCheck.maxAbsAxisDeltaVsRoundedReplay }, null, 2));
}
