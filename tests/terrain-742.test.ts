import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import { test } from 'node:test';
import { inflateSync } from 'node:zlib';
import { decodeGnv742, decodeTrees742, estimateWalk742, loadTerrain742, openGnv742, Terrain742 } from '../src/tactics/terrain-742.ts';
import type { Blob742, TerrainInput742, XY742 } from '../src/tactics/terrain-742.ts';
import { surfaceGrid742 } from '../scripts/client/terrain-742-physics.ts';
import { decodeCapture742, validateTerrain742 } from '../scripts/client/terrain-742-validation.ts';

const read = (file: string) => JSON.parse(fs.readFileSync(new URL(`../data/${file}`, import.meta.url), 'utf8'));
const artifact = read('farming-742-terrain.json');
const unpack = (r: Blob742) => {
  const b = inflateSync(Buffer.from(r.zlib, 'base64'), { maxOutputLength: r.bytes });
  assert.equal(createHash('sha256').update(b).digest('hex'), r.sha256); assert.equal(b.length, r.bytes); return b;
};
const xy = (x: number, y: number) => ({ x, y });
const center = (x: number, y: number) => xy(x * 64 + 32, y * 64 + 32);
const small = (width: number, height: number): TerrainInput742 => ({
  width, height, cellSize: 64, x0: 0, y0: 0, flags: new Uint8Array(width * height).fill(1),
  clips: new Uint8Array(width * height), zQuarter: new Int16Array(width * height), trees: [], structures: [],
});

test('7.42 GNV is a new client member, not the 7.41f mask; dimension, offset, endian and flag regressions', () => {
  const b = unpack(artifact.gnv), g = decodeGnv742(b);
  assert.equal(artifact.sources.mapVpk.sha256, '7fc24ff13fcbe6840e5b9d76beb23f681368ecd2aa8b32a1e31e3cda959bd304');
  assert.equal(artifact.gnv.sha256, '5451e230fb49372a3dde42588cc3a330caaad8ceef280d0c4093ae73cae64588');
  assert.notEqual(artifact.gnv.sha256, artifact.changeFrom741f.priorGnvSha256);
  assert.deepEqual([g.width, g.height, g.cellSize, g.x0, g.y0, b.length], [320, 328, 64, -10240, -10752, 104992]);
  assert.deepEqual([1, 17, 25, 0, 4, 5, 13, 20, 3].map(openGnv742), [true, true, true, false, false, false, false, false, false]);
  const old = read('dota2-map-reference.json').rawNavigation;
  let opened = 0, closed = 0;
  for (let i = 0; i < g.flags.length; i++) {
    const was = old.rows[Math.floor(i / g.width)][i % g.width] === '1', now = openGnv742(g.flags[i]);
    if (now && !was) opened++; if (was && !now) closed++;
  }
  assert.deepEqual([opened, closed], [351, 967]);
  assert.throws(() => decodeGnv742(b.subarray(0, b.length - 1)));
  for (const pos of [0, 4, 8, 12, 16]) {
    const bad = Buffer.from(b); bad.writeUInt32LE(0, pos); assert.throws(() => decodeGnv742(bad));
  }
});

test('TRM layer multiplicity is preserved; nonentity trees do not acquire made-up Z', () => {
  const b = unpack(artifact.trm), { trees, names } = decodeTrees742(b), zs = unpack(artifact.entityTreeZQ4);
  assert.deepEqual(names, ['world_layer_radiant_base', 'world_layer_dire_base', '']);
  assert.deepEqual(names.map((_, i) => trees.filter(t => t.layer === i).length), [103, 77, 2223]);
  assert.equal(trees.length, 2403); assert.equal(new Set(trees.map(p => `${p.x},${p.y}`)).size, 2402);
  assert.equal(zs.length, 2 * trees.length);
  assert.equal(trees.filter((_, i) => zs.readInt16LE(i * 2) === -32768).length, 9);
  assert.equal(artifact.sources.lumps.reduce((s: number, l: any) => s + l.exactTreeXYMatches, 0), 2394);
  assert.throws(() => decodeTrees742(b.subarray(0, b.length - 1)));
  const bad = Buffer.from(b); bad.writeUInt32LE(3, bad.length - 4); assert.throws(() => decodeTrees742(bad));
});

test('PHYS vertical intersection uses the highest solid hit; clip shadow is not water or a wall below the ground', () => {
  const bytes = (rows: number[][], float = false) => {
    const b = Buffer.alloc(rows.length * 12);
    rows.flat().forEach((n, i) => float ? b.writeFloatLE(n, 4 * i) : b.writeUInt32LE(n, 4 * i));
    return b.toString('hex');
  };
  const mesh = (index: number, v: number[][], f: number[][]) => `m_nCollisionAttributeIndex = ${index}
    m_Mesh = { m_Vertices = #[${bytes(v, true)}] m_Triangles = #[${bytes(f)}] }`;
  const ground = mesh(0, [[0, 0, 0], [192, 0, 192], [0, 192, 0], [0, 0, 128], [64, 0, 128], [0, 64, 128]],
    [[0, 1, 2], [5, 4, 3]]);
  const clips = mesh(1, [[65, 1, 250], [120, 1, 250], [65, 60, 250], [1, 65, -20], [60, 65, -20], [1, 120, -20]],
    [[0, 1, 2], [3, 4, 5]]);
  const liquid = [[0, 0, 3000], [192, 0, 3000], [0, 192, 3000]];
  const dump = [ground, clips, mesh(2, liquid, [[0, 1, 2]]), mesh(3, liquid, [[0, 1, 2]]),
    'm_collisionAttributes = m_CollisionGroupString = "default"',
    ...['"npcclip","playerclip"', '"water"', '"window"'].map(v => `m_CollisionGroupString = "conditionallysolid" ${v}`)].join('\n');
  const out = surfaceGrid742(dump, small(3, 3));
  assert.equal(out.bytes.readInt16LE(0), 4 * 128); assert.equal(out.bytes.readInt16LE(2), 4 * 96);
  assert.equal(out.bytes.readInt16LE(16), -32768);
  assert.equal(out.clip[1], 3); assert.equal(out.clip[3], 1); assert.equal(out.clip[8], 0);
  assert.throws(() => surfaceGrid742(dump.replace('"npcclip"', '"other"'), small(3, 3)));
});

test('A* and smoothing detour around a real barrier, preserve off-centre endpoints, and sum actual segments', () => {
  const data = small(9, 9);
  for (let y = 0; y < 7; y++) data.flags[y * 9 + 4] = 5;
  const nav = new Terrain742(data), a = xy(101, 90), b = xy(481, 105);
  assert.equal(nav.segmentIsOpen(a, b), false);
  const route = nav.walk(a, b, 370); assert(route.ok);
  assert.deepEqual(route.points[0], a); assert.deepEqual(route.points.at(-1), b);
  assert(route.points.length >= 4); assert(route.distance > 2 * Math.hypot(a.x - b.x, a.y - b.y));
  let d = 0;
  for (let i = 1; i < route.points.length; i++) {
    const p: XY742 = route.points[i - 1], q: XY742 = route.points[i], len = Math.hypot(q.x - p.x, q.y - p.y); d += len;
    assert(nav.segmentIsOpen(q, p));
    // Independent dense check against the known wall, not another A* run.
    const n = Math.ceil(2 * len);
    for (let j = 0; j <= n; j++) {
      const x = p.x + (q.x - p.x) * j / n, y = p.y + (q.y - p.y) * j / n;
      assert(Math.max(4 * 64 - x, x - 5 * 64, y - 7 * 64, -y) > 24);
    }
  }
  assert(Math.abs(route.distance - d) < 1e-9); assert.equal(route.seconds, d / 370);
  const faster = nav.walk(a, b, 405); assert(faster.ok); assert.deepEqual(faster.points, route.points); assert.equal(faster.seconds, d / 405);
  for (let y = 7; y < 9; y++) data.flags[y * 9 + 4] = 0;
  assert.deepEqual(new Terrain742(data).walk(a, b, 370), { ok: false, reason: 'no-static-route' });
});

test('rejects obstructed endpoints without snapping, clipped corners, diagonal squeezing and an unsupported speed', () => {
  const d = small(6, 6); d.flags[2 * 6 + 2] = 0;
  const nav = new Terrain742(d);
  assert.equal(nav.inspect(xy(127, 153))?.state, 'open'); // open centre, hull crosses into blocked neighbour
  assert.deepEqual(nav.walk(xy(127, 153), center(4, 4), 325), { ok: false, reason: 'start-blocked' });
  assert.deepEqual(nav.walk(center(4, 4), center(2, 2), 325), { ok: false, reason: 'end-blocked' });
  // Centre line misses the wall; the swept footprint doesn't.
  const a = xy(96, 108), b = xy(223, 108);
  assert(nav.segmentIsOpen(a, a) && nav.segmentIsOpen(b, b)); assert.equal(nav.segmentIsOpen(a, b), false);
  assert.deepEqual(nav.walk(xy(NaN, 0), b, 325), { ok: false, reason: 'invalid-position' });
  assert.equal(nav.segmentIsOpen(xy(-1, -1), b), false);
  for (const speed of [NaN, Infinity, -10, 0]) assert.deepEqual(nav.walk(a, b, speed), { ok: false, reason: 'invalid-effective-speed' });
  const corner = small(4, 4); corner.flags.fill(0); corner.flags[5] = corner.flags[10] = 1;
  assert.deepEqual(new Terrain742(corner).walk(center(1, 1), center(2, 2), 325), { ok: false, reason: 'no-static-route' });
  const zero = nav.walk(a, a, 325); assert(zero.ok && zero.seconds === 0 && zero.distance === 0);
});

test('GNV-open cells can still have a cliff, missing Z, a raised clip, an offset tree or standing structure', () => {
  const d = small(8, 8);
  for (let y = 0; y < 8; y++) for (let x = 4; x < 8; x++) d.zQuarter[y * 8 + x] = 4 * 128;
  const nav = new Terrain742(d);
  assert.deepEqual(nav.walk(center(2, 4), center(5, 4), 325), { ok: false, reason: 'no-static-route' });
  d.zQuarter[5 * 8 + 4] = 4 * 64; // permission via an actual intermediate height
  assert(new Terrain742(d).walk(center(2, 4), center(5, 4), 325).ok);
  d.zQuarter[5 * 8 + 4] = -32768; d.clips[8 + 4] = 3; d.clips[8 + 6] = 1;
  d.trees.push({ x: 128, y: 128, layer: 2 }); d.structures.push({ id: 'tower', xy: [416, 416], reserve: 40, hull: 'TOWER' });
  const n = new Terrain742(d);
  assert.equal(n.inspect(center(4, 5))?.state, 'no-solid-height'); assert.equal(n.inspect(center(4, 1))?.state, 'raised-clip-reserve');
  assert.equal(n.inspect(center(6, 1))?.state, 'open'); assert.equal(n.inspect(center(1, 2))?.state, 'standing-tree-reserve');
  assert.equal(n.inspect(center(6, 6))?.state, 'structure-reserve');
});

test('7.42 camp timings use the swept route around the original gate high ground, not air distance', () => {
  const a = xy(4520, -8336), b = xy(6712, -7632), n = loadTerrain742();
  assert.equal(n.inspect(a)?.centerSurfaceZ, 0); assert.equal(n.inspect(b)?.centerSurfaceZ, 256);
  assert.equal(n.segmentIsOpen(a, b), false);
  const r = estimateWalk742(a, b, 370); assert(r.ok);
  assert(Math.abs(r.distance - 2495.192) < .01); assert(Math.abs(r.seconds - 6.744) < .001);
  assert(r.distance > Math.hypot(a.x - b.x, a.y - b.y) + 190);
  assert(r.points.slice(1).every((q, i) => n.segmentIsOpen(r.points[i], q)));
  assert.deepEqual(artifact.replayLandmarkCheck.maxAbsAxisDeltaVsRoundedReplay, { tower: .442, spawner: .481, boxEdge: .5 });
  assert.deepEqual(artifact.speedFields.heroBase, { luna: 325, troll_warlord: 290 });
  assert.deepEqual([artifact.speedFields.treadsFlatRanged, artifact.speedFields.treadsFlatMelee], [45, 55]);
  assert.equal(artifact.structures.length, 74); assert.equal(artifact.speedPaths.length, 8); assert.equal(artifact.physics.rawOpenWithoutHeight, 0);
  assert.equal(artifact.camps.length, 28);
  for (const c of artifact.camps) { const p = xy(c.origin[0], c.origin[1]); assert(n.segmentIsOpen(p, p), c.id); }
});

test('all 28 camp centres connect both ways; every returned segment also clears the envelope backwards', () => {
  const map = loadTerrain742(), points: XY742[] = artifact.camps.map((c: {origin: number[]}) => xy(c.origin[0], c.origin[1]));
  let count = 0, deniedChords = 0;
  for (const a of points) for (const b of points) {
    if (a === b) continue;
    const leg = map.walk(a, b, 370); assert(leg.ok);
    assert.deepEqual(leg.points[0], a); assert.deepEqual(leg.points.at(-1), b);
    assert(leg.points.slice(1).every((p, i) => map.segmentIsOpen(p, leg.points[i])));
    count++; deniedChords += Number(!map.segmentIsOpen(a, b));
  }
  assert.deepEqual([count, deniedChords], [756, 752]);
});

test('entire holdout and refusal counts reproduce offline; clock pauses, real Z errors and poor cases stay visible', () => {
  const bundle = read('farming-742-terrain-traces.json'), report = read('farming-742-terrain-validation.json');
  assert.deepEqual(validateTerrain742(bundle.captures), report);
  assert.equal(report.geometry.alive, 19619); assert.equal(report.geometry.rawGnvOpen, 19619);
  assert.equal(report.geometry.repeatedClockDiscardedInCapture, 259); assert(report.geometry.centerZVsNetworkZ.maxAbs > 300);
  assert.equal(report.uninterrupted.n, 137); assert.equal(report.uninterrupted.meanAbsS, .561); assert(report.uninterrupted.maxAbsS > 6);
  assert.deepEqual(report.uninterrupted.refusals, { 'start-blocked': 5, 'end-blocked': 5 });
  assert.equal(report.uninterrupted.blockedChord.n, 105); assert(report.uninterrupted.observedPolylineSupported < report.uninterrupted.n);
  assert.equal(Object.values(report.scheduled.categories as Record<string, number>).reduce((a, b) => a + b, 0), 270);
  assert(report.scheduled.mixed.meanAbsS > 2); assert.notEqual(report.worst.prefixSpeed, report.worst.recordedBase);
  assert.throws(() => decodeCapture742({ ...bundle.captures[0], samplesSha256: 'not-the-trace' }));
  assert.throws(() => validateTerrain742(bundle.captures.slice(1)));
});
