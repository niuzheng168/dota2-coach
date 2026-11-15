import { distance, insideCamp, nextSpawn } from './map-data.ts';
import type { WalkPath } from './map-data.ts';
import type { Camp, MapData, Point } from './types.ts';

export interface CampTravel {
  arrival: number;
  travelSeconds: number;
  waitSeconds: number;
  waitAt?: Point;
  availableAt: number[];
}

// The polyline includes a real outside waypoint, including for an out-and-back visit.
// Wait BEFORE entering the box, not at its center with a fictitious zero-time refresh.
export function travelToCamp(
  path: WalkPath, camp: Camp, campIndex: number, camps: Camp[], availableAt: readonly number[],
  clock: number, moveSpeed: number, deadline: number, map: MapData,
): CampTravel | null {
  const lengths = [0];
  for (let i = 1; i < path.points.length; i++) lengths.push(lengths[i - 1] + distance(path.points[i - 1], path.points[i]));
  const length = lengths.at(-1)!;
  const turnSeconds = path.distance > 100 ? 0.5 : 0;
  const secondsPerUnit = length > 0 ? path.distance / length / moveSpeed : 0;
  const travelSeconds = path.distance / moveSpeed + turnSeconds;
  const outside = path.points.findLastIndex(point => !insideCamp(point, camp));
  let waitAt: Point | undefined, prefixDistance = length;
  if (outside >= 0) {
    waitAt = path.points[outside]; prefixDistance = lengths[outside];
    const inside = path.points[outside + 1];
    if (inside) {
      // Even a sparse polyline must stage near the target, not wait for a minute
      // back at the source camp and accidentally block that camp's refresh.
      let enter = 0;
      for (const axis of ['x', 'y'] as const) {
        const delta = inside[axis] - waitAt[axis];
        if (waitAt[axis] < camp.bounds.min[axis]) enter = Math.max(enter, (camp.bounds.min[axis] - waitAt[axis]) / delta);
        if (waitAt[axis] > camp.bounds.max[axis]) enter = Math.max(enter, (camp.bounds.max[axis] - waitAt[axis]) / delta);
      }
      const segment = distance(waitAt, inside);
      const ratio = Math.max(0, enter - 32 / segment);
      prefixDistance += ratio * segment;
      waitAt = { x: waitAt.x + (inside.x - waitAt.x) * ratio, y: waitAt.y + (inside.y - waitAt.y) * ratio };
    }
  }
  const prefixSeconds = turnSeconds + prefixDistance * secondsPerUnit;
  const readiness = [...availableAt];
  let waitSeconds = 0, arrival = 0;
  function positionAt(at: number): Point {
    const elapsed = at - clock;
    if (elapsed <= turnSeconds) return path.points[0];
    if (waitSeconds > 0 && elapsed >= prefixSeconds && elapsed <= prefixSeconds + waitSeconds) return waitAt!;
    const walking = elapsed - turnSeconds - (elapsed > prefixSeconds ? waitSeconds : 0);
    const along = secondsPerUnit > 0 ? walking / secondsPerUnit : 0;
    let i = 1;
    while (i < lengths.length && lengths[i] < along) i++;
    if (i >= lengths.length) return path.points.at(-1)!;
    const ratio = (along - lengths[i - 1]) / (lengths[i] - lengths[i - 1] || 1);
    return {
      x: path.points[i - 1].x + (path.points[i].x - path.points[i - 1].x) * ratio,
      y: path.points[i - 1].y + (path.points[i].y - path.points[i - 1].y) * ratio,
    };
  }
  // A delayed source-box exit may miss a refresh and require another minute.
  // Each retry advances a spawn, so the finite horizon bounds this loop.
  for (let tries = 0; tries <= Math.ceil((deadline - clock) / map.spawn.interval) + 1; tries++) {
    if (outside < 0 && readiness[campIndex] > clock) return null;
    waitSeconds = outside < 0 ? 0 : Math.max(0, readiness[campIndex] - clock - prefixSeconds);
    arrival = clock + travelSeconds + waitSeconds;
    if (arrival >= deadline) return null;
    let delayedTarget = false;
    for (let i = 0; i < camps.length; i++) {
      // Already available camps contain a pack, so walking through at another minute
      // does not remove that pack. Only an outstanding respawn can be blocked here.
      while (readiness[i] > clock && readiness[i] <= arrival &&
          insideCamp(positionAt(readiness[i]), camps[i])) {
        readiness[i] = nextSpawn(readiness[i], map);
        if (i === campIndex) delayedTarget = true;
      }
    }
    if (!delayedTarget) return {
      arrival, travelSeconds, waitSeconds, availableAt: readiness,
      ...(waitSeconds > 0 ? { waitAt } : {}),
    };
  }
  return null;
}
