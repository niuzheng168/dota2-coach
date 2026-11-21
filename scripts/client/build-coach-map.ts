import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { entityValues, deriveLanePaths } from '../../src/replays/source2-map.ts';

// The coach's map of the installed client (7.42 since build 6983): neutral camps with their 7.42 home-jungle pools,
// towers, buildings, objectives and no-ward zones, read from maps/dota.vpk with the pinned Source2Viewer CLI.
// Used by the coach client for place names, minimap marks and ward guesses; read-only, the game files stay untouched.
//   node scripts/client/build-coach-map.ts [install] [Source2Viewer-CLI.exe] [--check]
const install = path.resolve(process.argv[2] && !process.argv[2].startsWith('--') ? process.argv[2] : 'C:/Steam/steamapps/common/dota 2 beta');
const cli = path.resolve(process.argv[3] && !process.argv[3].startsWith('--') ? process.argv[3] : '.tmp/client-audit/vrf-21.1/Source2Viewer-CLI.exe');
const output = 'data/dota2-coach-map.json';
const hash = (bytes: Buffer | string) => createHash('sha256').update(bytes).digest('hex');
const pin = JSON.parse(fs.readFileSync('data/dota2-client-tool-pin.json', 'utf8'));
if (hash(fs.readFileSync(cli)) !== pin.executableSha256) throw new Error('Source2Viewer CLI executable pin mismatch');
const game = path.join(install, 'game/dota');
const build = Number(fs.readFileSync(path.join(game, 'steam.inf'), 'utf8').match(/^ServerVersion=(\d+)/m)?.[1]);
if (!(build >= 6983)) throw new Error(`Client build ${build} predates 7.42 (6983)`);
const mapFile = path.join(game, 'maps/dota.vpk'), mapSha256 = hash(fs.readFileSync(mapFile));
const raw = path.resolve('.tmp/coach-map', `${build}-${mapSha256.slice(0, 12)}`);
const run = (args: string[]) => execFileSync(cli, args, { encoding: 'utf8', windowsHide: true, timeout: 120_000, maxBuffer: 64 << 20 });
if (!fs.existsSync(path.join(raw, 'maps/dota/entities/default_ents.vents_c'))) {
  run(['-i', mapFile, '-f', 'maps/dota/entities/', '-o', raw, '-q']);
  run(['-i', path.join(game, 'pak01_dir.vpk'), '-f',
    'resource/overviews/dota.txt,resource/localization/abilities_schinese.txt', '-o', raw, '-q']);
}
const entitiesFile = path.join(raw, 'maps/dota/entities/default_ents.vents_c');
const entities = entityValues(run(['-i', entitiesFile, '-b', 'DATA']));
const zh = Object.fromEntries([...fs.readFileSync(path.join(raw, 'resource/localization/abilities_schinese.txt'), 'utf8')
  .matchAll(/^\s*"(npc_dota_[a-z0-9_]+)"\s+"([^"]*)"/gm)].map(m => [m[1], m[2]]));

const of = (cls: string) => entities.filter(e => e.classname === cls);
const name = (e: any) => String(e.targetname || '').replace(/^\[PR#\]/, '');
const xy = (e: any) => [Math.round(e.origin[0]), Math.round(e.origin[1])] as [number, number];
const dist = (a: number[], b: number[]) => Math.hypot(a[0] - b[0], a[1] - b[1]);
/** Bounds of every PHYS hull of a trigger's model, in model space. */
function hulls(e: any) {
  if (e.scales.some((v: number) => v !== 1) || e.angles[0] !== 0 || e.angles[2] !== 0)
    throw new Error(`Only unscaled triggers turned about the vertical axis are supported (${e.model})`);
  const dump = run(['-i', path.join(raw, e.model + '_c'), '-b', 'PHYS']);
  const vectors = (key: string) => [...dump.matchAll(new RegExp(`\\b${key}\\s*=\\s*\\[([^\\]]+)\\]`, 'g'))]
    .map(m => m[1].split(',').map(Number));
  const mins = vectors('m_vMinBounds'), maxs = vectors('m_vMaxBounds');
  if (!mins.length || mins.length !== maxs.length || ![...mins, ...maxs].flat().every(Number.isFinite)) throw new Error('Unreadable hull bounds');
  return mins.map((min, i) => ({ min, max: maxs[i] }));
}
/** World AABB of an unrotated trigger (camp spawn boxes). */
function box(e: any) {
  if (e.angles[1] !== 0) throw new Error(`Rotated camp box ${e.model}`);
  const h = hulls(e);
  return { min: [0, 1].map(i => Math.round(Math.min(...h.map(v => v.min[i])) + e.origin[i])),
           max: [0, 1].map(i => Math.round(Math.max(...h.map(v => v.max[i])) + e.origin[i])) };
}
/** A trigger as world polygons, one rectangle per hull turned by the entity's yaw (no-ward zones are turned). */
function polygons(e: any) {
  const yaw = e.angles[1] * Math.PI / 180, c = Math.cos(yaw), s = Math.sin(yaw);
  return hulls(e).map(({ min, max }) => [[min[0], min[1]], [max[0], min[1]], [max[0], max[1]], [min[0], max[1]]]
    .map(([x, y]) => [Math.round(e.origin[0] + x * c - y * s), Math.round(e.origin[1] + x * s + y * c)]));
}

const overviewText = fs.readFileSync(path.join(raw, 'resource/overviews/dota.txt'), 'utf8');
const ov = (k: string) => Number(overviewText.match(new RegExp(`\\b${k}\\s+(-?[\\d.]+)`))?.[1]);
const overview = { posX: ov('pos_x'), posY: ov('pos_y'), scale: ov('scale'), coordinateSpan: ov('scale') * 1024 };
if (!Object.values(overview).every(Number.isFinite)) throw new Error('Unreadable overview');

const lanes = deriveLanePaths(entities).map(l => ({ id: l.id, lane: l.id.split('_').at(-1) as string,
  team: l.id.includes('_good_') ? 'radiant' : 'dire', points: l.points.map((p: number[]) => [Math.round(p[0]), Math.round(p[1])]) }));
if (lanes.length !== 6) throw new Error('Expected six lane paths');
const LANE_ZH: Record<string, string> = { top: '上路', mid: '中路', bot: '下路' };

// 7.42: each non-amphibian, non-ancient camp spawns from its side's own pool ("home jungle"); amphibian camps keep the frog line.
// From the 7.42 patch notes (https://www.dota2.com/datafeed/patchnotes?version=7.42). Unit lists name the kinds of creep only.
const unit = (n: string) => ({ unit: `npc_dota_neutral_${n}`, name: zh[`npc_dota_neutral_${n}`] ?? n });
const campTypes = {
  radiant: {
    small: [{ id: 'kobolds', units: ['kobold', 'kobold_tunneler', 'kobold_taskmaster'] }, { id: 'trolls-kobolds', units: ['forest_troll_berserker', 'kobold'] },
      { id: 'trolls', units: ['forest_troll_berserker', 'forest_troll_high_priest'] }],
    medium: [{ id: 'wolves', units: ['alpha_wolf', 'giant_wolf'] }, { id: 'ogres', units: ['ogre_magi', 'ogre_mauler'] }, { id: 'radigulls', units: ['radigull'] }],
    large: [{ id: 'centaurs', units: ['centaur_khan', 'centaur_outrunner'] }, { id: 'wildwings', units: ['enraged_wildkin', 'wildkin'] },
      { id: 'eimermoles', units: ['eimermole_foreman', 'eimermole'] }],
  },
  dire: {
    small: [{ id: 'vhouls', units: ['gnoll_assassin'] }, { id: 'ghosts', units: ['ghost', 'fel_beast'] }, { id: 'harpies', units: ['harpy_storm', 'harpy_scout'] }],
    medium: [{ id: 'satyrs', units: ['satyr_soulstealer', 'satyr_trickster'] }, { id: 'mud-golems', units: ['mud_golem'] },
      { id: 'odobenus', units: ['odobenus_frostmage', 'odobenus_footman'] }],
    large: [{ id: 'large-satyrs', units: ['satyr_hellcaller', 'satyr_soulstealer'] }, { id: 'hellbears', units: ['polar_furbolg_ursa_warrior', 'polar_furbolg_champion'] },
      { id: 'dark-trolls', units: ['dark_troll_warlord', 'dark_troll'] }],
  },
  ancient: [{ id: 'dragons', units: ['black_dragon', 'black_drake'] }, { id: 'granite', units: ['granite_golem', 'rock_golem'] },
    { id: 'ice', units: ['ice_shaman', 'frostbitten_golem'] }, { id: 'ogre-seals', units: ['ogre_seal', 'ogre_seal_pup'] }],
  amphibian: [{ id: 'frogs', units: ['tadpole', 'froglet', 'grown_frog', 'ancient_frog', 'froglet_mage', 'grown_frog_mage', 'ancient_frog_mage'] }],
};
const named = (list: { id: string; units: string[] }[]) => list.map(t => ({ id: t.id, units: t.units.map(unit) }));
const camptypes = {
  radiant: Object.fromEntries(Object.entries(campTypes.radiant).map(([k, v]) => [k, named(v)])),
  dire: Object.fromEntries(Object.entries(campTypes.dire).map(([k, v]) => [k, named(v)])),
  ancient: named(campTypes.ancient), amphibian: named(campTypes.amphibian),
};
for (const list of [camptypes.ancient, camptypes.amphibian, ...Object.values(camptypes.radiant), ...Object.values(camptypes.dire)])
  for (const t of list) for (const u of t.units) if (u.name === u.unit.replace('npc_dota_neutral_', '')) throw new Error(`No Chinese name for ${u.unit}`);

const KINDS = ['small', 'medium', 'large', 'ancient'] as const;
// Names without the side ("天辉"/"夜魇", or "我方"/"敌方" from the player's side), written for these 7.42 positions: a camp
// that moved more than 300 units stops the build, so a later patch gets its names reviewed instead of silently reused.
const CAMP_NAMES: Record<string, [string, number, number]> = {
  neutralcamp_good_1: ['下路一塔小野', 3978, -5027], neutralcamp_good_2: ['下路一塔大野', 4649, -3699],
  neutralcamp_good_15: ['下路神符中野', 1922, -3975], neutralcamp_good_9: ['下路二塔中野', -1983, -4815],
  neutralcamp_good_12: ['下路边缘中野', -2415, -8402], neutralcamp_good_20: ['下路高台中野', 6712, -7632],
  neutralcamp_good_4: ['下路水生中野', 186, -5197], neutralcamp_good_11: ['下路边缘水生中野', -720, -7696],
  neutralcamp_good_14: ['下路边缘水生小野', 4520, -8336], neutralcamp_good_5: ['中路大野', -1454, -3356],
  neutralcamp_good_7: ['上路中野', -4013, 992], neutralcamp_good_8: ['远古野', -5024, -96],
  neutralcamp_good_13: ['上路边缘大野', -8313, -553], neutralcamp_good_16: ['上路边缘小野', -8023, -1838],
  neutralcamp_evil_2: ['上路一塔小野', -3911, 4829], neutralcamp_evil_1: ['上路一塔大野', -4824, 3915],
  neutralcamp_evil_15: ['上路神符中野', -2596, 3850], neutralcamp_evil_4: ['上路二塔中野', 1224, 4176],
  neutralcamp_evil_12: ['上路边缘中野', 2016, 7896], neutralcamp_evil_20: ['上路高台中野', -6408, 7816],
  neutralcamp_evil_6: ['上路水生中野', -852, 4940], neutralcamp_evil_14: ['上路边缘水生中野', 336, 7696],
  neutralcamp_evil_11: ['上路边缘水生小野', -4228, 8140], neutralcamp_evil_5: ['中路大野', 1064, 2580],
  neutralcamp_evil_9: ['下路中野', 3392, -1408], neutralcamp_evil_8: ['远古野', 4352, 48],
  neutralcamp_evil_7: ['下路边缘大野', 7928, -120], neutralcamp_evil_13: ['下路边缘小野', 8430, 1263],
};
const SIDE_ZH = { radiant: '天辉', dire: '夜魇' };
const campBoxes = new Map(entities.filter(e => e.classname === 'trigger_multiple' && /^\[PR#\]neutralcamp_/.test(e.targetname))
  .map(e => [name(e), box(e)]));
const camps = of('npc_dota_neutral_spawner').map(e => {
  const id = String(e.VolumeName).replace(/^\[PR#\]/, '');
  const team = id.startsWith('neutralcamp_good_') ? 'radiant' : id.startsWith('neutralcamp_evil_') ? 'dire' : null;
  const kind = KINDS[Number(e.NeutralType)];
  if (!team || !kind || !campBoxes.has(id) || !CAMP_NAMES[id]) throw new Error(`Unexpected camp ${id}`);
  const min = Number(e.MinSpawnType), max = Number(e.MaxSpawnType);
  // Spawn types: 1–3 Radiant pool, 4–6 Dire pool, 7 amphibian (flooded camps); ancients draw from the shared ancient list.
  const pool = kind === 'ancient' ? 'ancient' : min === 7 && max === 7 ? 'amphibian' : max <= 3 ? 'radiant' : min >= 4 ? 'dire' : null;
  if (!pool) throw new Error(`Unreadable camp pool ${id} ${min}-${max}`);
  const p = xy(e), [named, nx, ny] = CAMP_NAMES[id];
  if (dist(p, [nx, ny]) > 300) throw new Error(`Camp ${id} moved; review its name`);
  return { id, team, kind, pool, x: p[0], y: p[1], box: campBoxes.get(id)!, name: named, label: SIDE_ZH[team] + named };
});
if (camps.length !== 28) throw new Error(`Expected 28 camps, found ${camps.length}`);

const towers = of('npc_dota_tower').map(e => {
  const m = String(e.MapUnitName).match(/^npc_dota_(goodguys|badguys)_tower([1-4])(?:_(top|mid|bot))?$/);
  if (!m) throw new Error(`Unexpected tower ${e.MapUnitName}`);
  const team = m[1] === 'goodguys' ? 'radiant' : 'dire', [x, y] = xy(e);
  const short = `${m[3] ? LANE_ZH[m[3]] : '基地'}${'一二三四'[Number(m[2]) - 1]}塔`;
  return { name: e.MapUnitName, team, tier: Number(m[2]), lane: m[3] ?? null, position: [x, y], short, label: SIDE_ZH[team] + short };
});
if (towers.length !== 22) throw new Error('Expected 22 towers');
const buildings = [
  ...of('npc_dota_fort').map(e => ({ kind: 'ancient', team: e.teamnumber === '2' ? 'radiant' : 'dire', position: xy(e) })),
  ...of('ent_dota_fountain').map(e => ({ kind: 'fountain', team: e.teamnumber === '2' ? 'radiant' : 'dire', position: xy(e) })),
  ...of('npc_dota_barracks').map(e => ({ kind: 'barracks', team: /goodguys/.test(e.MapUnitName) ? 'radiant' : 'dire', name: e.MapUnitName, position: xy(e) })),
];
// Objectives in a side's jungle carry that side (shown as 我方/敌方); the others are named by their half of the map.
const sideOf = (e: any) => e.origin[0] + e.origin[1] < 0 ? 'radiant' : 'dire';
const half = (e: any) => e.origin[1] - e.origin[0] > 0 ? '上路' : '下路';
const river = (e: any) => e.origin[1] > 0 ? '上河道' : '下河道';
const objective = (kind: string, team: string | null, short: string, e: any) =>
  ({ kind, team, position: xy(e), short, label: (team ? SIDE_ZH[team as 'radiant' | 'dire'] : '') + short });
const objectives = [
  ...of('npc_dota_watch_tower').map(e => objective('outpost', sideOf(e), zh.npc_dota_watch_tower, e)),
  ...of('dota_item_rune_spawner_bounty').map(e => objective('bounty-rune', sideOf(e), '赏金神符', e)),
  ...of('npc_dota_unit_twin_gate').map(e => objective('twin-gate', null, half(e) + zh.npc_dota_unit_twin_gate, e)),
  ...of('npc_dota_lotus_pool').map(e => objective('lotus-pool', null, half(e) + zh.npc_dota_lotus_pool, e)),
  ...of('npc_dota_xp_fountain').map(e => objective('wisdom-shrine', null, half(e) + zh.npc_dota_xp_fountain, e)),
  ...of('dota_item_rune_spawner_powerup').map(e => objective('power-rune', null, river(e) + '神符', e)),
  ...of('info_player_start_dota').filter(e => /^roshan_location_\d$/.test(name(e)))
    .map(e => objective('roshan', null, river(e) + zh.npc_dota_roshan, e)),
  ...of('info_player_start_dota').filter(e => /^miniboss_location_\d$/.test(name(e)))
    .map(e => objective('tormentor', null, half(e) + zh.npc_dota_miniboss, e)),
  ...of('npc_dota_lantern').map(e => objective('watcher', null, zh.npc_dota_lantern, e)),
];
const noWards = of('trigger_no_wards').flatMap(polygons);
const sources = {
  client: { build, steamInf: 'game/dota/steam.inf' },
  map: { file: 'game/dota/maps/dota.vpk', sha256: mapSha256, entitiesSha256: hash(fs.readFileSync(entitiesFile)) },
  tool: { name: pin.name, version: pin.version, executableSha256: pin.executableSha256 },
  campPools: 'https://www.dota2.com/datafeed/patchnotes?version=7.42&language=english (neutral_creeps)',
};
const result = {
  version: 'coach-map-742-1', patch: '7.42', sources, overview,
  note: 'World coordinates of the installed client (build ' + build + '). Camp boxes are spawn-box AABBs; camp kinds and pools from the spawner ' +
    '(NeutralType, Min/MaxSpawnType); possible camp types from the 7.42 patch notes. No live state: camps may be empty, stacked or upgraded.',
  camps, campTypes: camptypes, towers, buildings, objectives, noWards, lanes,
};
const text = JSON.stringify(result, null, 1) + '\n';
if (process.argv.includes('--check')) {
  const old = JSON.parse(fs.readFileSync(output, 'utf8'));
  if (JSON.stringify({ ...old, sources: undefined }) !== JSON.stringify({ ...result, sources: undefined }))
    throw new Error('Installed client map differs from data/dota2-coach-map.json');
  console.log('Coach map matches the installed client.');
} else {
  fs.writeFileSync(output, text);
  console.log(`Coach map ${result.patch} (build ${build}): ${camps.length} camps, ${towers.length} towers, ${objectives.length} objectives, ${noWards.length} no-ward zones.`);
}
