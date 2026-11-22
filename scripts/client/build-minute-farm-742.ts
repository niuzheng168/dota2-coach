/** Build-time only, Node 24. From the repo root:
 *   node scripts/client/build-minute-farm-742.ts [--check]
 * To also reproduce the small additional KV/linked-lane/overview extract from the
 * exact local client (no live process, replay, network or runtime Node dependency):
 *   node scripts/client/build-minute-farm-742.ts --client "C:/Steam/steamapps/common/dota 2 beta" ".tmp/client-audit/vrf-21.1/Source2Viewer-CLI.exe" [--check]
 */
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { gzipSync, gunzipSync } from 'node:zlib';
import { deriveLanePaths, entityValues } from '../../src/replays/source2-map.ts';
import { estimateCamp742, neutral742 } from '../../src/tactics/camp-742.ts';
import { loadTerrain742, openGnv742, POLICY742 } from '../../src/tactics/terrain-742.ts';
import { kvObject as kv, parseKeyValues } from '../lib/valve-kv.ts';

const root = path.resolve(import.meta.dirname, '../..');
const json = (name: string) => JSON.parse(fs.readFileSync(path.join(root, 'data', name), 'utf8'));
const hash = (b: Buffer) => createHash('sha256').update(b).digest('hex');
const terrain = json('farming-742-terrain.json'), routes = json('farming-742-routes.json');
const catalog = json('farming-742-camp-catalog.json'), units = json('farming-742-camp-units.json');
const mapHash = '7fc24ff13fcbe6840e5b9d76beb23f681368ecd2aa8b32a1e31e3cda959bd304';
const overviewHash = 'fc31a7979950863f262afd2f0c8d7a8bf5ec6ffbdc48c465aa5495cd41d4cc1d';
assert(terrain.serverBuild === 6989 && terrain.sources.mapVpk.sha256 === mapHash && routes.patch.version === '7.42');
assert(units.installedBuild === 6989 && catalog.patch === '7.42');
type XY = [number, number];
const xy = (v: number[]): XY => [v[0], v[1]];
const xyObj = ([x, y]: XY) => ({ x, y });
const cmp = (a: string, b: string) => a < b ? -1 : a > b ? 1 : 0;

function extract(install: string, cli: string) {
  const game = path.resolve(install, 'game/dota'), raw = path.join(root, '.tmp/minute-farm-742-build');
  const map = path.join(game, 'maps/dota.vpk'), steam = fs.readFileSync(path.join(game, 'steam.inf'));
  const lump = 'maps/dota/entities/default_ents.vents_c', overview = 'resource/overviews/dota.txt';
  assert.equal(hash(steam), terrain.sources.numericFiles.find((f: any) => f.name === 'steam.inf').rawSha256);
  assert.equal(hash(fs.readFileSync(map)), mapHash);
  cli = path.resolve(cli);
  assert.equal(hash(fs.readFileSync(cli)), json('dota2-client-tool-pin.json').executableSha256);
  const run = (...args: string[]) => execFileSync(cli, args, { windowsHide: true, encoding: 'utf8', maxBuffer: 64 << 20 });
  fs.mkdirSync(raw, { recursive: true });
  run('-i', map, '-f', lump, '-o', raw, '-q');
  run('-i', path.join(game, 'pak01_dir.vpk'), '-f', `${overview},scripts/npc/heroes/`, '-o', raw, '-q');
  const bytes = (name: string) => fs.readFileSync(path.join(raw, name));
  const lumpSha256 = hash(bytes(lump));
  assert.equal(lumpSha256, terrain.sources.lumps.find((l: any) => l.name === 'default_ents').sha256);
  const entities = entityValues(run('-i', path.join(raw, lump), '-b', 'DATA'));
  assert.equal(hash(bytes(overview)), overviewHash);
  const ov = kv(parseKeyValues(bytes(overview).toString()).dota);
  const overviewValues = [Number(ov.pos_x), Number(ov.pos_y), Number(ov.scale) * 1024];
  const bounds = entities.filter(e => e.classname === 'dota_minimap_boundary').map(e => xy(e.origin))
    .sort(([a], [b]) => a - b);
  const [left, top, span] = overviewValues;
  assert.deepEqual(bounds, [[left, top - span], [left + span, top]]);
  const lanes = deriveLanePaths(entities).map(l => ({ id: l.id, points: [xy(l.spawn), ...l.points.map(xy)] }))
    .sort((a, b) => cmp(a.id, b.id));
  const runes = entities.filter(e => e.classname === 'dota_item_rune_spawner_powerup').map(e => xy(e.origin)).sort(([a], [b]) => a - b);
  const spawners = entities.filter(e => e.classname === 'npc_dota_neutral_spawner').map(e => {
    const id = e.VolumeName.replace(/^\[PR#\]neutralcamp_/, '');
    const t = terrain.camps.find((c: any) => c.id === id), r = routes.map.camps.find((c: any) => c.id === id);
    assert.deepEqual(t.origin, e.origin); assert.equal(Number(e.NeutralType), r.initialTier);
    return { id, tier: r.initialTier, water: Number(e.MinSpawnType) === 7 && Number(e.MaxSpawnType) === 7,
      upgrades: Number(e.MaxUpgradeCount) };
  }).sort((a, b) => cmp(a.id, b.id));
  const dir = 'scripts/npc/heroes/', all = fs.readdirSync(path.join(raw, dir)).filter(f => /^npc_dota_hero_\w+\.txt$/.test(f)).sort();
  const keys = (f: string) => kv(kv(parseKeyValues(bytes(dir + f).toString()).DOTAHeroes)[f.slice(0, -4)]);
  const base = keys('npc_dota_hero_base.txt'), filesDigest = createHash('sha256');
  const heroes: Record<string, number[]> = {};
  for (const f of all) {
    filesDigest.update(f + '\0').update(bytes(dir + f));
    const h = { ...base, ...keys(f) };
    if (h.Enabled !== '1') continue;
    assert(h.MovementCapabilities === 'DOTA_UNIT_CAP_MOVE_GROUND' &&
      (h.BoundsHullName === 'DOTA_HULL_SIZE_HERO' || h.BoundsHullName === 'DOTA_HULL_SIZE_SMALL'));
    const attr = { DOTA_ATTRIBUTE_AGILITY: 'Agility', DOTA_ATTRIBUTE_STRENGTH: 'Strength',
      DOTA_ATTRIBUTE_INTELLECT: 'Intelligence', DOTA_ATTRIBUTE_ALL: 'all' }[String(h.AttributePrimary)];
    assert(attr);
    // Universal: deliberately credit NO universal attribute damage in the fallback; do not guess a patch multiplier.
    const profile = [Number(h.MovementSpeed), Number(h.AttackRate), Number(h.AttackDamageMin),
      Number(h.AttackDamageMax) - Number(h.AttackDamageMin), Number(h.BaseAttackSpeed), Number(h.AttributeBaseAgility),
      Number(h.AttributeAgilityGain), attr === 'all' ? -1 : Number(h['AttributeBase' + attr]),
      attr === 'all' ? 0 : Number(h['Attribute' + attr + 'Gain'])];
    assert(profile.every(Number.isFinite) && profile[0] > 0 && profile[1] > 0 && profile[3] >= 0);
    heroes[f.slice(0, -4)] = profile;
  }
  assert(Object.keys(heroes).length === 127 && lanes.length === 6 && runes.length === 2 && spawners.length === 28);
  return { build: 6989, mapHash, lumpSha256, overviewHash, heroFilesSha256: filesDigest.digest('hex'),
    heroColumns: 'move, BAT, weaponMin, rollWidth, baseIAS, agi, agiGain, primary (-1: universal omitted), primaryGain',
    overview: overviewValues, runes, lanes, spawners, heroes };
}

// Descriptors refer to fixed *locations*, not standing buildings or the current tier/roster of a water camp.
const names: Record<string, string> = {
  good_1: '下路一塔西北小野', good_2: '下路一塔北侧大野', good_4: '下路二塔东北水营', good_5: '中路一塔南侧大野',
  good_7: '上路内侧北中野', good_8: '上路内侧远古营', good_9: '下路二塔西北中野', good_11: '南缘中段水营',
  good_12: '南缘西段中野', good_13: '西缘北段大野', good_14: '南缘东段水营', good_15: '下路内侧东中野',
  good_16: '西缘南段小野', good_20: '东南角高台中野',
  evil_1: '上路一塔东南大野', evil_2: '上路一塔东南小野', evil_4: '上路二塔东南中野', evil_5: '中路一塔北侧大野',
  evil_6: '上路二塔西南水营', evil_7: '东缘南段大野', evil_8: '下路内侧远古营', evil_9: '远古营西南中野',
  evil_11: '北缘西段水营', evil_12: '北缘东段中野', evil_13: '东缘北段小野', evil_14: '北缘中段水营',
  evil_15: '上路内侧西中野', evil_20: '西北角高台中野',
};

function compile(source: ReturnType<typeof extract>) {
  assert(source.build === 6989 && source.overviewHash === overviewHash && source.mapHash === mapHash);
  assert.equal(source.lumpSha256, terrain.sources.lumps.find((l: any) => l.name === 'default_ents').sha256);
  assert.equal(source.heroFilesSha256, 'eee21d8e7d5b8b446b7e42bc215d1434c4808db372385ae3b1ce7ee0f91fd458');
  for (const name of ['luna', 'troll_warlord']) {
    const h = units.heroes[name];
    assert.deepEqual(source.heroes['npc_dota_hero_' + name], [terrain.speedFields.heroBase[name], h.bat, h.baseAttack[0],
      h.baseAttack[1] - h.baseAttack[0], h.baseAttackSpeed, h.agility, h.agilityGain, h.agility, h.agilityGain]);
  }
  const map = loadTerrain742(), { width: w, height: h, cellSize: size, x0, y0 } = map.grid, n = w * h;
  assert.deepEqual([w, h, size, POLICY742.halfHull], [320, 328, 64, 24]);
  const center = (i: number): XY => [x0 + (i % w + .5) * size, y0 + (Math.floor(i / w) + .5) * size];
  const cell = ([x, y]: XY) => Math.floor((y - y0) / size) * w + Math.floor((x - x0) / size);
  const state = Array.from({ length: n }, (_, i) => map.inspect(xyObj(center(i)))!);
  const low = state.map(s => openGnv742(s.flag) && s.centerSurfaceZ !== null && s.centerSurfaceZ <= 64);
  // Trace only the *connected* central lowland containing both actual power runes.
  // Outer flooded jungles at the same Z are not the river. Per column forbid the
  // ENTIRE min..max strip plus 256 units in X/Y. Beyond measured columns permit only
  // land behind that side's own outer-lane T1 latitude. This is a conservative
  // home interior, NOT a guessed diagonal, a visibility/control map, or a safety rating.
  const river = [cell(source.runes[0])];
  assert(low[river[0]]); low[river[0]] = false;
  const min = new Array<number>(w).fill(Infinity), max = new Array<number>(w).fill(-Infinity);
  for (let k = 0; k < river.length; k++) {
    const i = river[k], x = i % w, y = center(i)[1];
    min[x] = Math.min(min[x], y - size / 2); max[x] = Math.max(max[x], y + size / 2);
    for (const j of [i - w, i + w, ...(x ? [i - 1] : []), ...(x < w - 1 ? [i + 1] : [])])
      if (low[j]) { low[j] = false; river.push(j); }
  }
  assert(river.includes(cell(source.runes[1])) && river.length === 3875);
  const west = min.findIndex(Number.isFinite), east = min.findLastIndex(Number.isFinite);
  for (let x = 0; x < w; x++) {
    if (x >= west && x <= east) { assert(Number.isFinite(min[x])); continue; }
    const lane = x < west ? 'top' : 'bot';
    const t1 = (side: string) => routes.map.towers.find((t: any) => t.id === `dota_${side}guys_tower1_${lane}`).y;
    min[x] = t1('good'); max[x] = t1('bad');
  }
  const sides = Buffer.alloc(n), edges = Buffer.alloc(n);
  for (let i = 0; i < n; i++) if (state[i].state === 'open') {
    const x = i % w, y = center(i)[1], l = Math.max(0, x - 4), r = Math.min(w, x + 5);
    if (y + size / 2 <= Math.min(...min.slice(l, r)) - 256) sides[i] = 1;
    if (y - size / 2 >= Math.max(...max.slice(l, r)) + 256) { assert(!sides[i]); sides[i] = 2; }
  }
  const directions = [[1, 0], [1, 1], [0, 1], [-1, 1]];
  for (let i = 0; i < n; i++) if (sides[i]) {
    const a = xyObj(center(i));
    directions.forEach(([dx, dy], d) => {
      const j = i + dx + w * dy;
      if (i % w + dx < 0 || i % w + dx >= w || sides[j] !== sides[i]) return;
      if (dx && dy && (sides[i + dx] !== sides[i] || sides[i + w * dy] !== sides[i])) return;
      const b = xyObj(center(j));
      if (map.segmentIsOpen(a, b) && map.segmentIsOpen(b, a)) {
        edges[i] |= 1 << d; edges[j] |= 1 << (d + 4);
      }
    });
  }
  // Off-centre endpoint -> its own cell centre (never snap to an adjacent open tile).
  const fits = (p: XY, side: number) => {
    const i = cell(p), c = center(i), a = xyObj(p), b = xyObj(c);
    if (!edges[i] || sides[i] !== side || !map.segmentIsOpen(a, b) || !map.segmentIsOpen(b, a)) return false;
    for (let oy = -1; oy <= 1; oy++) for (let ox = -1; ox <= 1; ox++) {
      if (Math.abs(p[0] - c[0] - ox * size) <= size / 2 + POLICY742.halfHull &&
          Math.abs(p[1] - c[1] - oy * size) <= size / 2 + POLICY742.halfHull && sides[i + ox + w * oy] !== side) return false;
    }
    return true;
  };
  const rosters = catalog.compositions.map((c: { spawnerTypes: number[]; units: string[] }) => {
    assert(c.spawnerTypes.length === 1);
    // Use the TS estimator's own refusal policy (splits/summons/defensive auras).
    // The synthetic attacker is ONLY a capability probe, never live timing input.
    const eligible = estimateCamp742({ patch: '7.42', clock: 120, scope: 'full-unstacked', units: c.units,
      attacker: { hero: 'generic', damage: [400, 400], attackSpeed: 200, bat: 1.7, mode: 'ranged', weapon: 'none' },
      policy: 'uninterrupted-attacks' }).status === 'scenario';
    return { tier: c.spawnerTypes[0], water: c.units.some(n => /frog|tadpole/.test(n)), eligible,
      pack: c.units.map(n => { const u = neutral742(n)!; return [u.hp, u.armor, u.regen]; }) };
  }) as { tier: number; water: boolean; eligible: boolean; pack: number[][] }[];
  const sites: { id: string; label: string; side: number; x: number; y: number; group: string | null }[] = [];
  const groups: Record<string, { catalog: number; packs: number[][][] }> = {};
  for (const s of source.spawners) {
    const side = s.id.startsWith('good_') ? 1 : 2, p = xy(terrain.camps.find((c: any) => c.id === s.id).origin);
    assert(names[s.id] && sides[cell(p)] !== 3 - side, 'Domain must not include an opponent spawner');
    if (!fits(p, side)) continue; // Never move an untrusted endpoint to the nearest open square.
    const group = `${s.water ? 'water' : 'land'}${s.tier}-${s.tier + s.upgrades}`;
    const possible = rosters.filter(r => r.water === s.water && r.tier >= s.tier && r.tier <= s.tier + s.upgrades);
    groups[group] = { catalog: possible.length, packs: possible.filter(r => r.eligible).map(r => r.pack) };
    sites.push({ id: s.id, label: names[s.id], side, x: p[0], y: p[1], group });
  }
  for (const lane of source.lanes) {
    const [, faction, road] = /^npc_dota_spawner_(good|bad)_(top|mid|bot)$/.exec(lane.id)!;
    const side = faction === 'good' ? 1 : 2, name = ({ top: '上路', mid: '中路', bot: '下路' })[road];
    const tower = routes.map.towers.find((t: any) => t.id === `dota_${faction}guys_tower1_${road}`);
    // Interpolate on the actual linked home lane, stopping at the projection of its
    // outer tower. Towers are only an extent reference, never a walk destination.
    const samples: { p: XY; s: number }[] = [];
    let along = 0;
    for (let i = 1; i < lane.points.length; i++) {
      const a = lane.points[i - 1], b = lane.points[i], dx = b[0] - a[0], dy = b[1] - a[1], len = Math.hypot(dx, dy);
      const steps = Math.max(1, Math.ceil(len / 48));
      for (let k = 0; k < steps; k++) samples.push({
        p: [Math.round(a[0] + dx * k / steps), Math.round(a[1] + dy * k / steps)], s: along + len * k / steps });
      along += len;
    }
    const towerDist = (p: XY) => Math.hypot(p[0] - tower.x, p[1] - tower.y);
    const near = samples.reduce((a, b) => towerDist(a.p) < towerDist(b.p) ? a : b);
    assert(towerDist(near.p) < 720);
    for (const [k, suffix] of ['内段', '中段', '外段'].entries()) {
      const bin = samples.filter(({ p, s }) => s >= near.s * k / 3 && s < near.s * (k + 1) / 3 &&
        fits(p, side) && routes.map.towers.every((t: any) => Math.hypot(p[0] - t.x, p[1] - t.y) >= 384));
      if (!bin.length) continue;
      const target = near.s * (k + .5) / 3;
      const { p } = bin.reduce((a, b) => Math.abs(a.s - target) < Math.abs(b.s - target) ? a : b);
      sites.push({ id: `lane_${faction}_${road}_${k + 1}`, label: `${name}${suffix}回线点`, side, x: p[0], y: p[1], group: null });
    }
  }
  assert(sites.filter(s => !s.group).length >= 12 && sites.filter(s => s.group).length >= 20);
  assert(new Set(sites.map(s => s.id)).size === sites.length);
  const up = units.mechanics.neutral_upgrade;
  return { format: 1, build: 6989, overview: source.overview, width: w, height: h, x0, y0, cellSize: size,
    hull: POLICY742.halfHull, sides: sides.toString('base64'), edges: edges.toString('base64'), sites, groups,
    heroes: source.heroes, upgrade: [Number(up.increase_time), Number(up.increase_health), Number(up.increase_armor)] };
}

const args = process.argv.slice(2), check = args.includes('--check'), other = args.filter(v => v !== '--check');
assert(other.length === 0 || (other.length === 3 && other[0] === '--client'), 'See usage at top of script');
const supplement = path.join(root, 'data/minute-farm-742-client.json');
if (other.length) {
  const source = JSON.stringify(extract(other[1], other[2])) + '\n';
  if (check) assert.deepEqual(JSON.parse(fs.readFileSync(supplement, 'utf8')), JSON.parse(source));
  else fs.writeFileSync(supplement, source);
}
const out = path.join(root, 'desktop/DotaCoach.TwoPc.Core/Assets/minute-farm-742.json.gz');
const artifact = compile(JSON.parse(fs.readFileSync(supplement, 'utf8'))), text = Buffer.from(JSON.stringify(artifact));
if (check) assert.deepEqual(gunzipSync(fs.readFileSync(out)), text);
else { fs.mkdirSync(path.dirname(out), { recursive: true }); fs.writeFileSync(out, gzipSync(text, { level: 9 })); }
console.log(`6989: ${artifact.sites.filter(s => s.group).length} admitted camps, ${artifact.sites.filter(s => !s.group).length} lane anchors, ` +
  `${Object.keys(artifact.heroes).length} hero profiles, ${fs.statSync(out).size} embedded bytes (${check ? 'verified' : 'generated'}).`);
if (check) {
  // Independent, reproducible comparators for MinuteFarmSimulationTests. These first-hit-to-last-death
  // times are exact ONLY within the repository's explicit physical scenarios; not actual camp/hero panels.
  const cases = [[114, 180, 0, 6, 15.111], [114, 180, 2, 3, 18.889], [294, 330, 2, 3, 4.121]];
  for (const [damage, ias, tier, count, worst] of cases) {
    const durations = catalog.compositions.filter((c: any) => c.spawnerTypes.includes(tier) &&
      !c.units.some((u: string) => /frog|tadpole/.test(u))).flatMap((c: any) => {
      const exact = estimateCamp742({ patch: '7.42', clock: 610, scope: 'full-unstacked', units: c.units,
        attacker: { hero: 'generic', bat: 1.7, mode: 'ranged', damage: [damage, damage], attackSpeed: ias, weapon: 'none' },
        policy: 'uninterrupted-attacks' });
      return exact.timing ? [exact.timing.firstHitToLastDeathSeconds[1]] : [];
    });
    assert.equal(durations.length, count); assert.equal(Math.max(...durations), worst);
  }
  const map = loadTerrain742();
  const point = (id: string) => xyObj(xy(terrain.camps.find((s: any) => s.id === id).origin));
  const gate = map.walk(point('good_14'), point('good_20'), 370), unrestricted = map.walk(point('good_1'), point('good_20'), 370);
  assert(gate.ok && Math.abs(gate.distance - 2495.192) < .001 && unrestricted.ok && Math.round(unrestricted.distance) === 4069);
  console.log('TS comparison: smoothed gate walk 2495.192 wu; longest covered first-hit scenarios 15.111 / 18.889 / 4.121 s (see native fixtures).');
}
