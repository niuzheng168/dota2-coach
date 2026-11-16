import { MAP } from './combat-data.ts';
import { estimateCamp, initialResources } from './clear-model.ts';
import { campCompositions } from './camp-compositions.ts';
import { distance, insideCamp, NAVIGATION, nextSpawn } from './map-data.ts';
import type { GridNavigation, WalkPath } from './map-data.ts';
import { clamp, RULES } from './rules.ts';
import { travelToCamp } from './travel.ts';
import type { Camp, JungleRoute, MapData, PlayerSnapshot, Point, Resources, RouteStep, SelfStats } from './types.ts';

export class CampTracker {
  readonly cleared = new Map<string, number>();
  calibration = 1;
  private visit: { camp: Camp; since: number; hits: number; lastKill: number; predicted: number | null } | null = null;
  private previousClock: number | null = null;
  private previousPosition: Point | null = null;
  readonly map: MapData;
  constructor(map: MapData = MAP) { this.map = map; }
  invalidateVisit(): void { this.visit = null; this.previousClock = null; this.previousPosition = null; }
  markCleared(id: string, clock: number): void {
    if (!this.map.camps.some(c => c.id === id)) throw new Error('未知营地');
    this.cleared.set(id, clock); this.visit = null;
  }
  ingest(player: PlayerSnapshot, stats: SelfStats): boolean {
    let changed = false;
    const discontinuity = this.previousClock !== null && (player.clock < this.previousClock || player.clock - this.previousClock > 4);
    if (discontinuity) this.visit = null;
    // A minute boundary crossed while our own hero is inside the spawn box blocks it.
    // Other players/wards are unknowable; a later spawn remains explicitly an assumption.
    if (player.alive && !discontinuity && this.previousClock !== null && nextSpawn(this.previousClock, this.map) <= player.clock) {
      for (const camp of this.map.camps) {
        if (this.cleared.has(camp.id) && insideCamp(player.position, camp) &&
            this.previousPosition && insideCamp(this.previousPosition, camp)) {
          this.cleared.set(camp.id, player.clock); changed = true;
        }
      }
    }
    this.previousClock = player.clock; this.previousPosition = player.position;
    if (!player.alive || !player.canAct || player.lastHits === null || discontinuity) { this.visit = null; return changed; }
    const camp = this.map.camps.find(c => insideCamp(player.position, c));
    if (this.visit && this.visit.camp.id !== camp?.id) {
      const visit = this.visit;
      // Conservative inference: require the largest initial pack count, not just gold income.
      const threshold = Math.max(...campCompositions(visit.camp.kind, visit.since, visit.camp)
        .map(c => Object.values(c.units).reduce((a, b) => a + b, 0)));
      if (visit.hits >= threshold && visit.lastKill > visit.since && player.clock - visit.since <= 90) {
        this.cleared.set(visit.camp.id, visit.lastKill); changed = true;
        if (visit.predicted && visit.predicted >= 3) {
          const ratio = clamp((visit.lastKill - visit.since) / visit.predicted, 0.5, 2);
          this.calibration = clamp(this.calibration * 0.8 + ratio * 0.2, 0.5, 2);
        }
      }
      this.visit = null;
    }
    if (camp && !this.visit) {
      const predicted = estimateCamp(player, stats, camp.kind, player.clock, initialResources(player), 1, { camp });
      this.visit = { camp, since: player.clock, hits: 0, lastKill: player.clock,
        predicted: predicted.feasible ? predicted.seconds : null };
    }
    return changed;
  }
  // Count only increments observed while remaining in the SAME camp, never a kill on departure.
  observeKills(previous: PlayerSnapshot | null, player: PlayerSnapshot): void {
    if (!this.visit || !previous || previous.lastHits === null || player.lastHits === null) return;
    if (!insideCamp(previous.position, this.visit.camp) || !insideCamp(player.position, this.visit.camp) ||
        player.clock - previous.clock > 4 || player.clock < previous.clock) return;
    const delta = player.lastHits - previous.lastHits;
    if (delta > 0 && delta <= 10) { this.visit.hits += delta; this.visit.lastKill = player.clock; }
    if (delta < 0) this.visit = null;
  }
}

export function planJungleRoute(
  player: PlayerSnapshot, stats: SelfStats, tracker: CampTracker,
  navigation: Pick<GridNavigation, 'path'> = NAVIGATION,
  estimate: typeof estimateCamp = estimateCamp,
): JungleRoute {
  const map = tracker.map;
  const warnings = [
    `数据快照 ${map.patch}，无法从 GSI 核实客户端补丁；不同版本请勿信任坐标和数值。`,
    '营地按假设有野处理，无法知道别人清野、封野、堆野或敌人位置。',
    '水生营地按推定出生时刻、无历史封野假设枚举进化组合；初始出生时刻、升级顺序及概率未知，不能用于实际收益标定。',
    '营地组合为待实机核验的代表样本，按等概率估算掉落金钱；经验仅展示；耗时、血蓝取样本保守包络，不是保证。',
    '地形使用静态寻路网格与树木；不计砍树、跳跃、传送、动态阻挡和引野走位。',
    '未模拟野怪技能、光环、召唤、攻击类型特殊修正；承伤另加 15% 余量，仍可能低估。',
    '按窗口内单只死亡赏金选优，包含最后未清完营地；次数和经验不参与评分。限宽搜索不保证全局最优，窗口内固定等级装备。',
    ...stats.warnings,
  ];
  const base: JungleRoute = {
    plannedAt: player.clock, horizonSeconds: RULES.horizon, validUntil: player.clock + RULES.horizon,
    steps: [], gold: 0, xp: 0, seconds: 0,
    score: 0, nextAction: '暂无可验证的刷野路线。', status: 'unavailable', warnings,
  };
  if (!player.alive || !player.canAct) return { ...base, nextAction: '等待英雄存活且可行动后重新规划。' };
  if (player.health < player.maxHealth * RULES.minHealthFraction) {
    return { ...base, status: 'recover', nextAction: '血量偏低，先补给或回到安全位置，不建议继续刷野。' };
  }
  const initial = map.camps.filter(c => c.team === player.team)
    .map(camp => ({ camp, path: navigation.path(player.position, camp) }))
    .filter(c => c.path !== null).sort((a, b) => a.camp.id.localeCompare(b.camp.id));
  const candidates = initial.map(c => c.camp);
  const deadline = player.clock + RULES.horizon;
  // Cache walking legs once per plan; a long route should not rerun path extraction
  // for every search branch. -1 represents the current hero position.
  const legs = new Map<string, WalkPath | null>(initial.map((c, i) => [`-1:${i}`, c.path]));
  const escapes = new Map<number, WalkPath | null>();
  function leg(from: number, to: number): WalkPath | null {
    const key = `${from}:${to}`;
    if (legs.has(key)) return legs.get(key)!;
    const path = navigation.path(candidates[from], candidates[to]);
    legs.set(key, path);
    return path;
  }
  function escape(index: number): WalkPath | null {
    if (escapes.has(index)) return escapes.get(index)!;
    const c = candidates[index];
    // Walking to a reachable point just outside the real spawn box and back
    // also permits a one-camp wait/respawn loop without standing in the box.
    const points: Point[] = [
      { x: c.bounds.min.x - 96, y: c.y }, { x: c.bounds.max.x + 96, y: c.y },
      { x: c.x, y: c.bounds.min.y - 96 }, { x: c.x, y: c.bounds.max.y + 96 },
    ].sort((a, b) => distance(c, a) - distance(c, b));
    for (const point of points) {
      const path = navigation.path(c, point);
      if (!path) continue;
      const loop = { distance: path.distance * 2, points: [...path.points, ...path.points.slice(0, -1).reverse()] };
      escapes.set(index, loop); return loop;
    }
    escapes.set(index, null); return null;
  }
  interface SearchNode {
    at: number;
    clock: number;
    resources: Resources;
    availableAt: number[];
    steps: RouteStep[];
    gold: number;
    xp: number;
    key: string;
  }
  // Rank both the beam and the final route by expected gold in the same window.
  // XP and visit count are display-only, including on ties. Do not normalize by
  // elapsed time: a short high-rate route can still earn less over the full window.
  const compare = (a: SearchNode, b: SearchNode) => b.gold - a.gold ||
    a.clock - b.clock || b.resources.health - a.resources.health ||
    b.resources.mana - a.resources.mana || a.key.localeCompare(b.key);
  const buckets: SearchNode[][] = Array.from({ length: Math.ceil(RULES.horizon / RULES.routeBucketSeconds) }, () => []);
  buckets[0].push({
    at: -1, clock: player.clock, resources: initialResources(player),
    availableAt: candidates.map(c => tracker.cleared.has(c.id) ? nextSpawn(tracker.cleared.get(c.id)!, map) : map.spawn.first),
    steps: [], gold: 0, xp: 0, key: '',
  });
  let bestNode: SearchNode | null = null;
  let expandedNodes = 0, evaluated = 0;
  // Time bins keep slow and fast branches from competing at an arbitrary fixed
  // depth. Positive clear time + the horizon/beam bound terminate fast-hero loops.
  for (let bucket = 0; bucket < buckets.length; bucket++) {
    for (let expanded = 0; buckets[bucket].length && expanded < RULES.routeBeamWidth; expanded++) {
      buckets[bucket].sort(compare);
      const node = buckets[bucket].shift()!;
      expandedNodes++;
      for (let i = 0; i < candidates.length; i++) {
        const c = candidates[i];
        let path = leg(node.at, i);
        if (!path) continue;
        if (node.at === i || (node.availableAt[i] > node.clock && path.points.every(p => insideCamp(p, c)))) {
          const loop = escape(i);
          if (!loop) continue;
          path = { distance: path.distance + loop.distance, points: [...path.points, ...loop.points.slice(1)] };
        }
        const travel = travelToCamp(path, c, i, candidates, node.availableAt,
          node.clock, stats.moveSpeed, deadline, map);
        if (!travel) continue;
        const elapsed = travel.arrival - node.clock;
        evaluated++;
        const result = estimate(player, stats, c.kind, travel.arrival, {
          health: Math.min(player.maxHealth, node.resources.health + stats.healthRegen * elapsed),
          mana: Math.min(player.maxMana, node.resources.mana + stats.manaRegen * elapsed),
          readyAt: node.resources.readyAt,
          activeUntil: node.resources.activeUntil,
        }, tracker.calibration, { camp: c, limitSeconds: deadline - travel.arrival,
          spawnAt: tracker.cleared.has(c.id) || node.steps.some(s => s.campId === c.id)
            ? travel.availableAt[i] : Math.max(map.spawn.first, Math.floor(travel.arrival / map.spawn.interval) * map.spawn.interval) });
        const finish = travel.arrival + result.seconds;
        if (!result.feasible || !(result.seconds > 0) || result.healthAfter < player.maxHealth * RULES.minHealthFraction ||
            finish > deadline + 1e-8 || result.gold <= 0) continue;
        const spawnAt = travel.availableAt[i];
        const availability = tracker.cleared.has(c.id) || node.steps.some(s => s.campId === c.id) || spawnAt > map.spawn.first
          ? 'respawn-assumed' : 'assumed';
        const step: RouteStep = {
          ...result, campId: c.id, label: c.label, kind: c.kind, position: { x: c.x, y: c.y },
          arrival: travel.arrival, finish, travelSeconds: travel.travelSeconds, waitSeconds: travel.waitSeconds,
          ...(travel.waitAt ? { waitAt: travel.waitAt } : {}),
          spawnAt: availability === 'respawn-assumed' ? spawnAt : null, availability, path: path.points,
        };
        const availableAt = travel.availableAt; availableAt[i] = nextSpawn(finish, map);
        const gold = node.gold + result.gold, xp = node.xp + result.xp;
        const child: SearchNode = {
          at: i, clock: finish, availableAt,
          resources: { health: result.healthAfter, mana: result.manaAfter, readyAt: result.readyAt, activeUntil: result.activeUntil },
          steps: [...node.steps, step], gold, xp, key: `${node.key}/${c.id}`,
        };
        if (!bestNode || compare(child, bestNode) < 0) bestNode = child;
        const targetBucket = Math.floor((finish - player.clock) / RULES.routeBucketSeconds);
        // A partial pack is terminal: no invented respawn, no second reward for its survivors.
        if (result.complete !== false && targetBucket < buckets.length) {
          const queue = buckets[targetBucket];
          queue.push(child); queue.sort(compare);
          if (queue.length > RULES.routeBeamWidth) queue.length = RULES.routeBeamWidth;
        }
      }
    }
    // Dropped alternatives need not retain long paths after their bucket has passed.
    buckets[bucket] = [];
  }
  const best: JungleRoute | null = bestNode ? {
    ...base, steps: bestNode.steps, seconds: bestNode.clock - player.clock,
    score: bestNode.gold, gold: bestNode.gold, xp: bestNode.xp, validUntil: bestNode.clock, status: 'ready',
    search: { engine: 'time-bucket-beam', expanded: expandedNodes, evaluated, beamWidth: RULES.routeBeamWidth, optimal: false },
  } : null;
  if (!best) return {
    ...base, status: candidates.length ? 'recover' : 'unavailable',
    nextAction: candidates.length ? '保守估计下没有安全刷野路线，先补给或回到安全位置。' : '当前位置没有静态网格可验证的步行路线，请移动到开阔地后重试。',
  };
  const first = best.steps[0];
  best.nextAction = `下一个：${first.label}，约 ${Math.ceil(first.arrival - player.clock)} 秒到。` +
    (first.waitSeconds ? `先在刷新框外等约 ${Math.ceil(first.waitSeconds)} 秒，再进入营地。` : '') +
    `清野约 ${Math.ceil(first.clearRange[0])}–${Math.ceil(first.clearRange[1])} 秒。`;
  return best;
}
