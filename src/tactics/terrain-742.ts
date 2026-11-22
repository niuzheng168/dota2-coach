/** Offline, initial-map ground walking only. No dependency on the 7.41f
 * simulator, live game, replay, credentials or network. World units, +Y north. */
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { inflateSync } from 'node:zlib';

export type XY742 = Readonly<{ x: number; y: number }>;
export type Tree742 = XY742 & { layer: number };
export type Structure742 = { id: string; xy: [number, number]; reserve: number; hull: string };
export type Grid742 = { width: number; height: number; cellSize: number; x0: number; y0: number; flags: Uint8Array };
export type TerrainInput742 = Grid742 & { zQuarter: Int16Array; clips: Uint8Array; trees: Tree742[]; structures: Structure742[] };
export type Blob742 = { bytes: number; sha256: string; zlib: string };
export type Walk742 =
  | { ok: true; model: '7.42-static-grid'; points: XY742[]; distance: number; seconds: number; effectiveMoveSpeed: number }
  | { ok: false; reason: 'invalid-effective-speed' | 'invalid-position' | 'start-blocked' | 'end-blocked' | 'no-static-route' };

const SURFACE_MISSING = -32768;
// Model policies, not a claim of continuous engine hull/terrain physics.
export const POLICY742 = Object.freeze({
  halfHull: 24, treeReserve: 32, maxCenterGrade: 1, shortcutCells: 12,
});
const states = ['open', 'gnv-blocked', 'no-solid-height', 'standing-tree-reserve', 'structure-reserve', 'raised-clip-reserve'] as const;
const valid = (p: XY742) => p != null && Number.isFinite(p.x) && Number.isFinite(p.y);
const distance = (a: XY742, b: XY742) => Math.hypot(a.x - b.x, a.y - b.y);
export const openGnv742 = (flag: number) => (flag & 1) !== 0 && (flag & 6) === 0;

export function decodeGnv742(b: Buffer): Grid742 {
  if (b.length < 32 || b.readUInt32LE(0) !== 0xfadebead) throw new Error('Bad GNV header');
  const cellSize = b.readFloatLE(4), width = b.readInt32LE(16), height = b.readInt32LE(20);
  if (cellSize !== 64 || b.readFloatLE(8) !== 32 || b.readFloatLE(12) !== 32 || width < 1 || height < 1 ||
      width * height > 2_000_000 || b.length !== 32 + width * height) throw new Error('Unsupported GNV layout');
  return { width, height, cellSize, x0: b.readInt32LE(24) * cellSize, y0: b.readInt32LE(28) * cellSize, flags: b.subarray(32) };
}

export function decodeTrees742(b: Buffer) {
  if (b.length < 64 || b.toString('ascii', 0, 4) !== 'trmp' || b.readUInt32LE(4) !== 1 ||
      b.readUInt32LE(8) !== 64 || b.subarray(20, 64).some(v => v !== 0)) throw new Error('Bad TRM v1 header');
  const layers = b.readUInt32LE(12), count = b.readUInt32LE(16), start = b.length - 12 * count;
  if (layers !== 3 || start < 64 || start % 4) throw new Error('Invalid TRM size');
  let offset = 64;
  const names: string[] = [];
  for (let i = 0; i < layers; i++) {
    const end = b.indexOf(0, offset);
    if (end < offset || end >= start) throw new Error('Missing TRM layer name');
    names.push(b.toString('ascii', offset, end)); offset = end + 1;
  }
  if (names.join('|') !== 'world_layer_radiant_base|world_layer_dire_base|' ||
      Math.ceil(offset / 4) * 4 !== start || b.subarray(offset, start).some(v => v)) throw new Error('Unknown TRM layers');
  const trees: Tree742[] = [];
  for (let i = start; i < b.length; i += 12) {
    const x = b.readInt32LE(i), y = b.readInt32LE(i + 4), layer = b.readUInt32LE(i + 8);
    if (layer >= layers || x % 64 || y % 64) throw new Error('Unknown TRM record');
    trees.push({ x, y, layer });
  }
  return { names, trees };
}

function unpack(b: Blob742) {
  if (!Number.isSafeInteger(b.bytes) || b.bytes < 1 || b.bytes > 4_000_000) throw new Error('Invalid packed terrain size');
  const raw = inflateSync(Buffer.from(b.zlib, 'base64'), { maxOutputLength: b.bytes });
  if (raw.length !== b.bytes || createHash('sha256').update(raw).digest('hex') !== b.sha256) throw new Error('Terrain digest mismatch');
  return raw;
}

/** Inclusive slab-intersection: touching a reserved rectangle is not accepted. */
function intersectsBox(a: XY742, b: XY742, left: number, bottom: number, right: number, top: number) {
  let enter = 0, exit = 1;
  for (const [p, delta, lo, hi] of [[a.x, b.x - a.x, left, right], [a.y, b.y - a.y, bottom, top]]) {
    if (delta === 0) { if (p < lo || p > hi) return false; continue; }
    const u = (lo - p) / delta, v = (hi - p) / delta;
    enter = Math.max(enter, Math.min(u, v)); exit = Math.min(exit, Math.max(u, v));
    if (enter > exit) return false;
  }
  return true;
}

class Queue {
  a: [number, number, number][] = []; // estimate, travelled, grid index
  push(v: [number, number, number]) {
    let i = this.a.length;
    while (i > 0) {
      const j = (i - 1) >> 1;
      if (this.a[j][0] <= v[0]) break;
      this.a[i] = this.a[j]; i = j;
    }
    this.a[i] = v;
  }
  pop() {
    const v = this.a[0], last = this.a.pop()!;
    if (this.a.length) {
      let i = 0;
      while (2 * i + 1 < this.a.length) {
        let j = 2 * i + 1;
        if (j + 1 < this.a.length && this.a[j + 1][0] < this.a[j][0]) j++;
        if (this.a[j][0] >= last[0]) break;
        this.a[i] = this.a[j]; i = j;
      }
      this.a[i] = last;
    }
    return v;
  }
}

export class Terrain742 {
  private readonly cellState: Uint8Array;
  readonly grid: Readonly<Omit<Grid742, 'flags'>>;
  private readonly flags: Uint8Array;
  private readonly z: Int16Array;
  constructor(input: TerrainInput742) {
    const { width, height, cellSize, x0, y0, flags, zQuarter, clips } = input;
    if (cellSize !== 64 || !Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1 ||
        width * height > 2_000_000 || flags.length !== width * height || flags.length !== zQuarter.length || flags.length !== clips.length ||
        !Number.isFinite(x0) || !Number.isFinite(y0)) throw new Error('Mismatched navigation/surface dimensions');
    this.grid = Object.freeze({ width, height, cellSize, x0, y0 });
    this.flags = Uint8Array.from(flags); this.z = Int16Array.from(zQuarter);
    this.cellState = flags.map((f, i) => !openGnv742(f) ? 1 : zQuarter[i] === SURFACE_MISSING ? 2 : clips[i] & 2 ? 5 : 0);
    const reserve = (cx: number, cy: number, r: number, state: number) => {
      if (![cx, cy, r].every(Number.isFinite) || !(r > 0) || r > 1024) throw new Error('Invalid obstacle reserve');
      for (let y = Math.max(0, Math.floor((cy - r - y0) / cellSize)); y <= Math.min(height - 1, Math.floor((cy + r - y0) / cellSize)); y++)
        for (let x = Math.max(0, Math.floor((cx - r - x0) / cellSize)); x <= Math.min(width - 1, Math.floor((cx + r - x0) / cellSize)); x++) {
          const l = x0 + x * cellSize, b = y0 + y * cellSize, i = y * width + x;
          if (!this.cellState[i] && Math.hypot(Math.max(l - cx, cx - l - cellSize, 0), Math.max(b - cy, cy - b - cellSize, 0)) < r)
            this.cellState[i] = state;
        }
    };
    for (const t of input.trees) reserve(t.x, t.y, POLICY742.treeReserve, 3);
    for (const b of input.structures) reserve(b.xy[0], b.xy[1], b.reserve, 4);
  }
  private cell(x: number, y: number) {
    return x >= 0 && x < this.grid.width && y >= 0 && y < this.grid.height ? y * this.grid.width + x : -1;
  }
  private coord(p: XY742) {
    return [Math.floor((p.x - this.grid.x0) / 64), Math.floor((p.y - this.grid.y0) / 64)] as const;
  }
  private center(i: number): XY742 {
    return { x: this.grid.x0 + ((i % this.grid.width) + .5) * 64, y: this.grid.y0 + (Math.floor(i / this.grid.width) + .5) * 64 };
  }
  inspect(p: XY742) {
    const i = valid(p) ? this.cell(...this.coord(p)) : -1;
    if (i < 0) return null;
    return { index: i, center: this.center(i), flag: this.flags[i], state: states[this.cellState[i]],
      centerSurfaceZ: this.z[i] === SURFACE_MISSING ? null : this.z[i] / 4 };
  }
  private step(x: number, y: number, nx: number, ny: number) {
    const a = this.cell(x, y), b = this.cell(nx, ny);
    if (a < 0 || b < 0 || this.cellState[a] || this.cellState[b]) return false;
    const diagonal = x !== nx && y !== ny, maxQuart = 4 * 64 * POLICY742.maxCenterGrade;
    if (Math.abs(this.z[a] - this.z[b]) > maxQuart * (diagonal ? Math.SQRT2 : 1)) return false;
    if (diagonal) {
      for (const side of [this.cell(x, ny), this.cell(nx, y)]) {
        if (this.cellState[side] || Math.abs(this.z[side] - this.z[a]) > maxQuart ||
            Math.abs(this.z[side] - this.z[b]) > maxQuart) return false;
      }
    }
    return true;
  }
  /** Traverse every crossed tile (DDA). Test blocked neighbour rectangles
   * against the whole swept 24-unit half-square, not just dots on the line. */
  segmentIsOpen(a: XY742, b: XY742): boolean {
    if (!valid(a) || !valid(b)) return false;
    let [x, y] = this.coord(a);
    const [ex, ey] = this.coord(b);
    if (this.cell(x, y) < 0 || this.cell(ex, ey) < 0) return false;
    const dx = b.x - a.x, dy = b.y - a.y, sx = Math.sign(dx), sy = Math.sign(dy);
    const tx = dx ? 64 / Math.abs(dx) : Infinity, ty = dy ? 64 / Math.abs(dy) : Infinity;
    let vx = dx ? (this.grid.x0 + (x + Number(sx > 0)) * 64 - a.x) / dx : Infinity;
    let vy = dy ? (this.grid.y0 + (y + Number(sy > 0)) * 64 - a.y) / dy : Infinity;
    const r = POLICY742.halfHull;
    for (;;) {
      if (this.cellState[this.cell(x, y)] !== 0) return false;
      for (let oy = -1; oy <= 1; oy++) for (let ox = -1; ox <= 1; ox++) {
        const ni = this.cell(x + ox, y + oy);
        if (ni >= 0 && !this.cellState[ni]) continue;
        const l = this.grid.x0 + (x + ox) * 64, d = this.grid.y0 + (y + oy) * 64;
        if (intersectsBox(a, b, l - r, d - r, l + 64 + r, d + 64 + r)) return false;
      }
      if (x === ex && y === ey) return true;
      const ux = x === ex ? Infinity : vx, uy = y === ey ? Infinity : vy;
      const crossX = ux <= uy + 1e-12, crossY = uy <= ux + 1e-12;
      const nx = crossX ? x + sx : x, ny = crossY ? y + sy : y;
      if (!this.step(x, y, nx, ny)) return false;
      if (crossX) vx += tx;
      if (crossY) vy += ty;
      x = nx; y = ny;
    }
  }
  walk(from: XY742, to: XY742, effectiveMoveSpeed: number): Walk742 {
    if (!Number.isFinite(effectiveMoveSpeed) || effectiveMoveSpeed <= 0) return { ok: false, reason: 'invalid-effective-speed' };
    if (!valid(from) || !valid(to)) return { ok: false, reason: 'invalid-position' };
    if (!this.segmentIsOpen(from, from)) return { ok: false, reason: 'start-blocked' };
    if (!this.segmentIsOpen(to, to)) return { ok: false, reason: 'end-blocked' };
    const finish = (points: XY742[]): Walk742 => {
      const d = points.slice(1).reduce((n, p, i) => n + distance(points[i], p), 0);
      const seconds = d / effectiveMoveSpeed;
      return Number.isFinite(seconds) ? { ok: true, model: '7.42-static-grid', points: points.map(({x, y}) => ({x, y})), distance: d, seconds, effectiveMoveSpeed }
        : { ok: false, reason: 'invalid-effective-speed' };
    };
    if (this.segmentIsOpen(from, to)) return finish([from, to]); // only after a full swept test
    const [ax, ay] = this.coord(from), [bx, by] = this.coord(to);
    const start = this.cell(ax, ay), goal = this.cell(bx, by), w = this.grid.width;
    if (!this.segmentIsOpen(from, this.center(start))) return { ok: false, reason: 'start-blocked' };
    if (!this.segmentIsOpen(this.center(goal), to)) return { ok: false, reason: 'end-blocked' };
    const n = this.cellState.length, g = new Float64Array(n).fill(Infinity), parent = new Int32Array(n);
    const q = new Queue(); q.push([0, 0, start]); g[start] = 0;
    while (q.a.length) {
      const [, len, i] = q.pop();
      if (len !== g[i]) continue;
      if (i === goal) break;
      const x = i % w, y = Math.floor(i / w);
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        if (!(dx || dy) || !this.step(x, y, x + dx, y + dy)) continue;
        const j = i + dy * w + dx, d = len + 64 * (dx && dy ? Math.SQRT2 : 1);
        if (d >= g[j]) continue;
        const hx = Math.abs(bx - x - dx), hy = Math.abs(by - y - dy);
        g[j] = d; parent[j] = i;
        q.push([d + 64 * (Math.max(hx, hy) + (Math.SQRT2 - 1) * Math.min(hx, hy)), d, j]);
      }
    }
    if (!Number.isFinite(g[goal])) return { ok: false, reason: 'no-static-route' };
    const backwards: XY742[] = [to];
    for (let j = goal; ; j = parent[j]) { backwards.push(this.center(j)); if (j === start) break; }
    const chain = [from, ...backwards.reverse()], points = [from];
    // Greedy bounded smoothing is NOT an assertion of continuous shortest path.
    for (let i = 0; i < chain.length - 1;) {
      let j = Math.min(i + POLICY742.shortcutCells, chain.length - 1);
      while (j > i && !this.segmentIsOpen(chain[i], chain[j])) j--;
      if (j === i) return { ok: false, reason: 'no-static-route' };
      if (distance(points.at(-1)!, chain[j]) > 0) points.push(chain[j]);
      i = j;
    }
    return finish(points);
  }
}

let cached: Terrain742 | undefined;
export function loadTerrain742(): Terrain742 {
  if (cached) return cached;
  const d = JSON.parse(readFileSync(new URL('../../data/farming-742-terrain.json', import.meta.url), 'utf8'));
  if (d.version !== 1 || d.patch !== '7.42' || d.serverBuild !== 6989 || d.map !== 'dota') throw new Error('Wrong terrain release');
  const g = decodeGnv742(unpack(d.gnv)), trm = decodeTrees742(unpack(d.trm)), z = unpack(d.solidSurfaceQ4);
  if (z.length !== g.width * g.height * 2) throw new Error('Corrupt terrain heights');
  const zQuarter = Int16Array.from({ length: z.length / 2 }, (_, i) => z.readInt16LE(i * 2));
  cached = new Terrain742({ ...g, clips: unpack(d.clipMask), trees: trm.trees, structures: d.structures, zQuarter });
  return cached;
}

/** The caller supplies present effective speed, never just m_iMoveSpeed.
 * Return a refusal (not a straight line or a snap) for a blocked endpoint. */
export function estimateWalk742(from: XY742, to: XY742, effectiveMoveSpeed: number): Walk742 {
  return loadTerrain742().walk(from, to, effectiveMoveSpeed);
}
