import { MAP } from './combat-data.ts';
import type { Camp, MapData, Point } from './types.ts';

export function insideCamp(point: Point, camp: Camp): boolean {
  return point.x >= camp.bounds.min.x && point.x <= camp.bounds.max.x &&
    point.y >= camp.bounds.min.y && point.y <= camp.bounds.max.y;
}
export const distance = (a: Point, b: Point) => Math.hypot(a.x - b.x, a.y - b.y);
export function nextSpawn(after: number, map: MapData = MAP): number {
  // Treat sub-microsecond rounding at a minute as that minute, not a free extra
  // pack immediately after a simulated clear ending at 659.9999999999999.
  const at = after + 1e-6;
  return at < map.spawn.first ? map.spawn.first
    : map.spawn.first + (Math.floor((at - map.spawn.first) / map.spawn.interval) + 1) * map.spawn.interval;
}
class Heap {
  data: [number, number][] = [];
  push(node: number, cost: number): void {
    let i = this.data.length;
    this.data.push([node, cost]);
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (this.data[p][1] <= cost) break;
      this.data[i] = this.data[p]; i = p;
    }
    this.data[i] = [node, cost];
  }
  pop(): [number, number] | undefined {
    const first = this.data[0], last = this.data.pop();
    if (!first || !last || !this.data.length) return first;
    let i = 0;
    while (i * 2 + 1 < this.data.length) {
      let c = i * 2 + 1;
      if (c + 1 < this.data.length && this.data[c + 1][1] < this.data[c][1]) c++;
      if (this.data[c][1] >= last[1]) break;
      this.data[i] = this.data[c]; i = c;
    }
    this.data[i] = last;
    return first;
  }
}
export interface WalkPath { distance: number; points: Point[] }
export class GridNavigation {
  readonly map: MapData;
  private fields = new Map<number, Float64Array>();
  constructor(map: MapData = MAP) { this.map = map; }
  private walk(x: number, y: number): boolean { return this.map.grid.rows[y]?.[x] === '1'; }
  private point(index: number): Point {
    const g = this.map.grid;
    return { x: g.origin.x + (index % g.width + 0.5) * g.cellSize,
      y: g.origin.y + (Math.floor(index / g.width) + 0.5) * g.cellSize };
  }
  private cell(point: Point): number | null {
    const g = this.map.grid, x = Math.floor((point.x - g.origin.x) / g.cellSize), y = Math.floor((point.y - g.origin.y) / g.cellSize);
    // Do not snap across cliffs/trees. A blocked source has no verified walking route.
    return this.walk(x, y) ? y * g.width + x : null;
  }
  private neighbors(index: number): [number, number][] {
    const g = this.map.grid, x = index % g.width, y = Math.floor(index / g.width);
    const result: [number, number][] = [];
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      if ((!dx && !dy) || !this.walk(x + dx, y + dy)) continue;
      if (dx && dy && (!this.walk(x + dx, y) || !this.walk(x, y + dy))) continue;
      result.push([(y + dy) * g.width + x + dx, g.cellSize * (dx && dy ? Math.SQRT2 : 1)]);
    }
    return result;
  }
  path(from: Point, to: Point): WalkPath | null {
    const start = this.cell(from), end = this.cell(to);
    if (start === null || end === null) return null;
    let field = this.fields.get(end);
    if (!field) {
      const g = this.map.grid;
      field = new Float64Array(g.width * g.height).fill(Infinity); field[end] = 0;
      const heap = new Heap(); heap.push(end, 0);
      for (let pair = heap.pop(); pair; pair = heap.pop()) {
        const [at, cost] = pair;
        if (cost > field[at]) continue;
        for (const [neighbor, edge] of this.neighbors(at)) {
          if (cost + edge < field[neighbor]) {
            field[neighbor] = cost + edge; heap.push(neighbor, cost + edge);
          }
        }
      }
      if (this.fields.size >= 32) this.fields.delete(this.fields.keys().next().value!);
      this.fields.set(end, field);
    }
    if (!Number.isFinite(field[start])) return null;
    if (start === end) return { distance: distance(from, to), points: [from, to] };
    const points: Point[] = [from, this.point(start)];
    let at = start;
    while (at !== end) {
      const next = this.neighbors(at).sort((a, b) => field![a[0]] + a[1] - field![b[0]] - b[1])[0]?.[0];
      if (next === undefined || field[next] >= field[at]) return null;
      at = next; points.push(this.point(at));
    }
    points.push(to);
    return { distance: field[start] + distance(from, this.point(start)) + distance(to, this.point(end)), points };
  }
}
// Immutable static fields can be shared across rooms; player/camp state cannot.
export const NAVIGATION = new GridNavigation();
