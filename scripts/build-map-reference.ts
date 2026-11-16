import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { MAP } from '../src/tactics/combat-data.ts';

const pins = JSON.parse(await fs.readFile('data/dota2-map-reference-sources.json', 'utf8'));
const cache = path.resolve('.tmp/map-reference');
await fs.mkdir(cache, { recursive: true });
async function read(name: string): Promise<Buffer> {
  const entry = pins.files.find((f: any) => f.name === name);
  if (!entry) throw new Error('Unpinned input');
  const file = path.join(cache, name);
  let bytes = await fs.readFile(file).catch(() => null);
  if (!bytes) {
    const url = `https://raw.githubusercontent.com/Reino-Doce/dota-maps-data/${pins.commit}/${entry.path}`;
    const response = await fetch(url, { signal: AbortSignal.timeout(30_000), redirect: 'error' });
    if (!response.ok) throw new Error(`Source HTTP ${response.status}`);
    bytes = Buffer.from(await response.arrayBuffer());
  }
  if (bytes.length !== entry.bytes || createHash('sha256').update(bytes).digest('hex') !== entry.sha256 ||
      createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex') !== entry.gitBlob)
    throw new Error(`Pinned source mismatch: ${name}`);
  await fs.writeFile(file, bytes);
  return bytes;
}
const json = async (name: string) => JSON.parse((await read(name)).toString('utf8'));
const raw = (await json('mapdata.json')).data, terrain = await json('terrain_grid.json');
const navigation = await read('gridnav.gnv'), paths = await json('path_corner.json');
const spawns = await json('npc_dota_spawner.json'), buildings = await json('building_vision.json');
const overview = await json('overview.json');
await read('dota-map-741f.png'); // Reference art is cached locally, not redistributed in Git.
if (buildings.source.gridnavSha256 !== pins.files.find((f: any) => f.name === 'gridnav.gnv').sha256)
  throw new Error('Building geometry/navigation provenance mismatch');
if (navigation.readUInt32LE(0) !== 0xfadebead || terrain.width !== MAP.grid.width || terrain.height !== MAP.grid.height)
  throw new Error('Map grid mismatch');
const reference = {
  version: 'map-reference-741f-1', patch: '7.41f', sources: pins,
  status: 'partial-cross-check-not-a-simulator',
  bounds: { min: MAP.grid.origin, max: { x: MAP.grid.origin.x + MAP.grid.width * MAP.grid.cellSize,
    y: MAP.grid.origin.y + MAP.grid.height * MAP.grid.cellSize } },
  camps: MAP.camps,
  trees: raw.ent_dota_tree.map((t: any) => [t.x, t.y, t.z]),
  towers: buildings.buildings.filter((b: any) => b.unitClass === 'npc_dota_tower'),
  lanePaths: paths.features.map((f: any) => ({
    id: f.id, lane: f.id.split('_').at(-1), team: f.id.includes('_good_') ? 'radiant' : 'dire',
    spawn: spawns.features.find((s: any) => s.id === f.id)?.geometry.coordinates,
    points: f.geometry.coordinates,
  })),
  overview: { ...overview, coordinateSpan: overview.scale * 1024,
    note: 'Valve overview scale uses a 1024 reference, not the texture pixel width; image is visual reference only, not river physics.' },
  rawNavigation: { ...MAP.grid, rows: Array.from({ length: MAP.grid.height }, (_, y) =>
    Array.from({ length: MAP.grid.width }, (_, x) => {
      const flag = navigation[32 + y * MAP.grid.width + x];
      return flag & 1 && !(flag & 6) ? '1' : '0';
    }).join('')) },
  limitations: [
    '底图河道仅是固定版本参考图，不提供水流方向、加速区域或物理边界。',
    '树林为静态原始坐标；没有复原砍树/长树时序，不能据此宣称动态通路正确。',
    '兵线路径是地图 path_corner，不是小兵实际每一帧的行进轨迹。',
    '回放单位来自真实实体快照；没有运行模拟出兵、刷野、塔攻击或碰撞。',
    '本页不生成打钱建议，不向实时规划器发送回放全图信息。',
  ],
};
if (reference.camps.length !== 28 || reference.towers.length !== 22 || reference.lanePaths.length !== 6)
  throw new Error('Unexpected reference coverage; review rather than silently accepting');
const output = JSON.stringify(reference) + '\n';
if (process.argv.includes('--check')) {
  if (await fs.readFile('data/dota2-map-reference.json', 'utf8') !== output) throw new Error('Reference build differs');
} else await fs.writeFile('data/dota2-map-reference.json', output);
console.log(`Map reference: ${reference.camps.length} camps, ${reference.towers.length} towers, ${reference.trees.length} static trees, 6 directional lane paths. NOT full physics validation.`);
