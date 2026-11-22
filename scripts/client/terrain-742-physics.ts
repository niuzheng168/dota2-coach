/** Rasterise the *default solid* world triangle mesh from a Source2Viewer
 * PHYS dump. Not a visibility/overview image, water surface or engine call. */
import { createHash } from 'node:crypto';

type Grid = { width: number; height: number; cellSize: number; x0: number; y0: number };
export const UNKNOWN_Z = -32768;
export const sha742 = (bytes: Buffer | string) => createHash('sha256').update(bytes).digest('hex');
export const CLIP_RISE_RESERVE = 32; // planning envelope, not engine step height

type V3 = [number, number, number];
/** Clip a triangle (including projected vertical faces) to a grid column.
 * Preserve interpolated Z: a distant high vertex must not close every tile
 * in the triangle's XY bounding box. */
function halfPlane(poly: V3[], axis: 0 | 1, edge: number, sign: number): V3[] {
  if (!poly.length) return [];
  const out: V3[] = [];
  let a = poly.at(-1)!, da = (a[axis] - edge) * sign;
  for (const b of poly) {
    const db = (b[axis] - edge) * sign;
    if ((da >= 0) !== (db >= 0)) {
      const q = da / (da - db);
      out.push([a[0] + q * (b[0] - a[0]), a[1] + q * (b[1] - a[1]), a[2] + q * (b[2] - a[2])]);
    }
    if (db >= 0) out.push(b);
    a = b; da = db;
  }
  return out;
}

function blob(mesh: string, key: string) {
  const header = new RegExp(`\\b${key}\\s*=\\s*#\\[`).exec(mesh);
  if (!header) throw new Error(`Missing ${key} byte array`);
  const begin = header.index + header[0].length, end = mesh.indexOf(']', begin);
  if (end < 0) throw new Error('Truncated PHYS');
  const hex = mesh.slice(begin, end).replace(/\s+/g, '');
  if (!hex.length || hex.length % 24 || /[^a-f0-9]/i.test(hex)) throw new Error(`Invalid ${key} triples`);
  return Buffer.from(hex, 'hex');
}

export function surfaceGrid742(dump: string, g: Grid) {
  const attrs = dump.slice(dump.lastIndexOf('m_collisionAttributes ='));
  const groups = [...attrs.matchAll(/m_CollisionGroupString\s*=\s*"([^"]+)"/g)].map(m => m[1]);
  const meshes = dump.split(/\bm_nCollisionAttributeIndex\s*=\s*/).slice(1)
    .filter(m => /\bm_Mesh\s*=/.test(m));
  // Refuse a new or reordered interpretation; the last non-mesh index is the part.
  if (groups.join() !== 'default,conditionallysolid,conditionallysolid,conditionallysolid' ||
      !['"npcclip"', '"playerclip"', '"water"', '"window"'].every(s => attrs.includes(s)) ||
      meshes.length !== 4 || meshes.some((m, i) => Number.parseInt(m, 10) !== i)) throw new Error('Unknown PHYS mesh groups');
  const verts = blob(meshes[0], 'm_Vertices'), indices = blob(meshes[0], 'm_Triangles');
  const z = new Float64Array(g.width * g.height).fill(-Infinity);
  const vertexCount = verts.length / 12, triangleCount = indices.length / 12;
  const px = (x: number) => (x - g.x0) / g.cellSize - 0.5;
  const py = (y: number) => (y - g.y0) / g.cellSize - 0.5;
  let projected = 0;
  for (let i = 0; i < indices.length; i += 12) {
    const a = indices.readUInt32LE(i) * 12, b = indices.readUInt32LE(i + 4) * 12, c = indices.readUInt32LE(i + 8) * 12;
    if (Math.max(a, b, c) >= verts.length) throw new Error('PHYS vertex index outside buffer');
    const ax = verts.readFloatLE(a), ay = verts.readFloatLE(a + 4), az = verts.readFloatLE(a + 8);
    const bx = verts.readFloatLE(b), by = verts.readFloatLE(b + 4), bz = verts.readFloatLE(b + 8);
    const cx = verts.readFloatLE(c), cy = verts.readFloatLE(c + 4), cz = verts.readFloatLE(c + 8);
    if (![ax, ay, az, bx, by, bz, cx, cy, cz].every(Number.isFinite)) throw new Error('Nonfinite PHYS vertex');
    const ux = bx - ax, uy = by - ay, vx = cx - ax, vy = cy - ay, det = ux * vy - uy * vx;
    if (Math.abs(det) < 1e-7) continue; // vertical or degenerate; winding doesn't decide ground
    projected++;
    const left = Math.max(0, Math.ceil(px(Math.min(ax, bx, cx)) - 1e-8));
    const right = Math.min(g.width - 1, Math.floor(px(Math.max(ax, bx, cx)) + 1e-8));
    const bottom = Math.max(0, Math.ceil(py(Math.min(ay, by, cy)) - 1e-8));
    const top = Math.min(g.height - 1, Math.floor(py(Math.max(ay, by, cy)) + 1e-8));
    for (let y = bottom; y <= top; y++) for (let x = left; x <= right; x++) {
      const dx = g.x0 + (x + 0.5) * g.cellSize - ax, dy = g.y0 + (y + 0.5) * g.cellSize - ay;
      const u = (dx * vy - dy * vx) / det, v = (ux * dy - uy * dx) / det;
      if (u >= -1e-8 && v >= -1e-8 && u + v <= 1 + 1e-8) {
        const pos = y * g.width + x;
        z[pos] = Math.max(z[pos], az + u * (bz - az) + v * (cz - az));
      }
    }
  }
  let hits = 0;
  const bytes = Buffer.alloc(z.length * 2);
  for (const [i, v] of z.entries()) {
    if (Number.isFinite(v)) hits++;
    const q = Number.isFinite(v) ? Math.round(v * 4) : UNKNOWN_Z;
    if (Number.isFinite(v) && (q <= UNKNOWN_Z || q > 32767)) throw new Error('Surface height overflow');
    bytes.writeInt16LE(q, i * 2);
  }
  // The second mesh has npcclip/playerclip tags. Most of its surfaces are
  // level with or UNDER the ground; treating ALL their XY as walls is wrong.
  const cv = blob(meshes[1], 'm_Vertices'), ct = blob(meshes[1], 'm_Triangles');
  const clip = Buffer.alloc(z.length);
  for (let i = 0; i < ct.length; i += 12) {
    const v = [0, 4, 8].map(k => {
      const p = ct.readUInt32LE(i + k) * 12;
      return [cv.readFloatLE(p), cv.readFloatLE(p + 4), cv.readFloatLE(p + 8)] as V3;
    });
    if (!v.flat().every(Number.isFinite)) throw new Error('Nonfinite clip vertices');
    const xs = v.map(p => (p[0] - g.x0) / g.cellSize), ys = v.map(p => (p[1] - g.y0) / g.cellSize);
    for (let y = Math.max(0, Math.ceil(Math.min(...ys)) - 1); y <= Math.min(g.height - 1, Math.floor(Math.max(...ys))); y++)
      for (let x = Math.max(0, Math.ceil(Math.min(...xs)) - 1); x <= Math.min(g.width - 1, Math.floor(Math.max(...xs))); x++) {
        const left = g.x0 + x * g.cellSize, bottom = g.y0 + y * g.cellSize;
        const poly = halfPlane(halfPlane(halfPlane(halfPlane(v, 0, left, 1), 0, left + g.cellSize, -1),
          1, bottom, 1), 1, bottom + g.cellSize, -1);
        if (!poly.length) continue;
        const j = y * g.width + x;
        clip[j] |= 1;
        if (Number.isFinite(z[j]) && poly.some(p => p[2] > z[j] + CLIP_RISE_RESERVE)) clip[j] |= 2;
      }
  }
  return { bytes, vertexCount, triangleCount, projected, hits, clip, clipTriangles: ct.length / 12 };
}
