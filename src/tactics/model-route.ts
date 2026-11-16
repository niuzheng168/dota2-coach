import { COMBAT, MAP } from './combat-data.ts';
import { estimateCamp, initialResources } from './clear-model.ts';
import { insideCamp, NAVIGATION, nextSpawn } from './map-data.ts';
import type { WalkPath } from './map-data.ts';
import { RULES } from './rules.ts';
import { burstAbility } from './skills.ts';
import { travelToCamp } from './travel.ts';
import type { Camp, JungleRoute, MapData, PlayerSnapshot, Point, SelfStats } from './types.ts';

export interface ModelVisit {
  campId: string;
  casts: { name: string; seconds: number }[];
  targetPriority: string[];
}
export interface PlanningInput {
  player: PlayerSnapshot;
  stats: SelfStats;
  cleared: Map<string, number>;
  recent: { clock: number; position: Point; health: number; mana: number; lastHits: number | null }[];
}
export function parseModelVisits(value: unknown): ModelVisit[] {
  if (!Array.isArray(value) || !value.length || value.length > 40) throw new Error('路线须有 1–40 个模型选择的营地');
  return value.map(v => {
    if (!v || typeof v.campId !== 'string' || v.campId.length > 80 ||
        !Array.isArray(v.casts) || v.casts.length > 24 || !Array.isArray(v.targetPriority) || v.targetPriority.length > 16)
      throw new Error('路线动作格式不正确');
    const casts = v.casts.map((c: any) => {
      if (!c || typeof c.name !== 'string' || c.name.length > 100 ||
          !Number.isFinite(c.seconds) || c.seconds < 0 || c.seconds > 90) throw new Error('施法计划无效');
      return { name: c.name as string, seconds: c.seconds as number };
    });
    if (v.targetPriority.some((name: unknown) => typeof name !== 'string' || name.length > 100))
      throw new Error('目标顺序无效');
    return { campId: v.campId, casts, targetPriority: [...v.targetPriority] };
  });
}

/** Execute exactly the model's visits. No candidate generation, beam, ranking, or route substitution. */
export function replayModelRoute(
  input: PlanningInput, visits: ModelVisit[], map: MapData = MAP,
  navigation: { path(from: Point, to: Point): WalkPath | null } = NAVIGATION,
  estimate: typeof estimateCamp = estimateCamp,
): JungleRoute {
  const { player, stats } = input;
  if (!player.alive || !player.canAct || player.health < player.maxHealth * RULES.minHealthFraction)
    throw new Error('当前英雄状态不满足安全约束');
  const camps = map.camps.filter(c => c.team === player.team);
  for (const visit of visits) {
    if (!camps.some(c => c.id === visit.campId)) throw new Error(`未知或非己方营地：${visit.campId}`);
    for (const cast of visit.casts) {
      const ability = player.abilities.find(a => a.name === cast.name);
      const supported = cast.name === 'item_mask_of_madness'
        ? player.items.some(i => i.name === cast.name && i.cooldown !== undefined)
        : ability && burstAbility(ability, COMBAT.heroes[player.hero]?.abilities[cast.name]);
      if (!supported) throw new Error(`unsupported：${cast.name} 没有可复核的主动模型或当前状态`);
    }
    if (visit.targetPriority.some(name => !Object.hasOwn(COMBAT.neutrals, name)))
      throw new Error('未知中立单位，不能验证目标优先级');
  }
  const route: JungleRoute = {
    plannedAt: player.clock, horizonSeconds: 180, validUntil: player.clock, steps: [], seconds: 0, gold: 0, xp: 0, score: 0,
    status: 'unavailable', nextAction: '模型方案未获得窗口内收益。',
    warnings: [
      '路线及技能时机由大模型提出；这里只重放这一方案，不进行规则选路或替换。',
      '金钱来自未实机校准的数值工具，不是模型自报收益或实战最高收益保证。',
      '当前目标为 180 秒纯野；兵线、英雄、建筑收入未计，录像中的线野/团战不能直接迁移。',
      '出生、封野、水生进化和组合概率仍有假设；不模拟引野追逐、野怪技能光环、环月几何或升级。',
      ...stats.warnings,
    ],
    search: { engine: 'model-proposal-replay', expanded: 0, evaluated: 1, beamWidth: 0, optimal: false },
  };
  let at: Point = player.position, clock = player.clock, resources = initialResources(player);
  let availability = camps.map(c => input.cleared.has(c.id) ? nextSpawn(input.cleared.get(c.id)!, map) : map.spawn.first);
  const deadline = player.clock + 180;
  for (const visit of visits) {
    const index = camps.findIndex(c => c.id === visit.campId), camp = camps[index];
    let path = navigation.path(at, camp);
    if (!path) throw new Error(`不可达营地：${camp.id}`);
    if (availability[index] > clock && path.points.every(p => insideCamp(p, camp))) {
      const outside: Point[] = [
        { x: camp.bounds.min.x - 96, y: camp.y }, { x: camp.bounds.max.x + 96, y: camp.y },
        { x: camp.x, y: camp.bounds.min.y - 96 }, { x: camp.x, y: camp.bounds.max.y + 96 },
      ];
      let loop: WalkPath | null = null;
      for (const point of outside) {
        const out = navigation.path(camp, point);
        if (out) { loop = { distance: out.distance * 2, points: [...out.points, ...out.points.slice(0, -1).reverse()] }; break; }
      }
      if (!loop) throw new Error('无法验证框外刷新等待');
      path = { distance: path.distance + loop.distance, points: [...path.points, ...loop.points.slice(1)] };
    }
    const travel = travelToCamp(path, camp, index, camps, availability, clock, stats.moveSpeed, deadline, map);
    if (!travel) break;
    const elapsed = travel.arrival - clock;
    const knownRefresh = input.cleared.has(camp.id) || route.steps.some(s => s.campId === camp.id);
    const result = estimate(player, stats, camp.kind, travel.arrival, {
      ...resources,
      health: Math.min(player.maxHealth, resources.health + elapsed * stats.healthRegen),
      mana: Math.min(player.maxMana, resources.mana + elapsed * stats.manaRegen),
    }, 1, {
      camp, scheduledCasts: visit.casts, targetPriority: visit.targetPriority, policy: 'attacks-only',
      limitSeconds: deadline - travel.arrival,
      spawnAt: knownRefresh ? travel.availableAt[index] : Math.max(map.spawn.first, Math.floor(travel.arrival / 60) * 60),
    });
    if (!result.feasible || !Number.isFinite(result.seconds) || result.seconds <= 0 ||
        result.healthAfter < player.maxHealth * RULES.minHealthFraction)
      throw new Error(`模型方案在 ${camp.id} 未通过血量或战斗可行性复核`);
    clock = travel.arrival + result.seconds;
    if (clock > deadline + 1e-8) throw new Error('工具结果超过规划窗口');
    if (result.gold <= 0) break;
    route.steps.push({
      ...result, campId: camp.id, label: camp.label, kind: camp.kind, position: { x: camp.x, y: camp.y },
      arrival: travel.arrival, finish: clock, travelSeconds: travel.travelSeconds, waitSeconds: travel.waitSeconds,
      ...(travel.waitAt ? { waitAt: travel.waitAt } : {}), spawnAt: knownRefresh ? travel.availableAt[index] : null,
      availability: knownRefresh ? 'respawn-assumed' : 'assumed', path: path.points, policy: 'attacks-only',
    });
    route.gold += result.gold; route.xp += result.xp;
    resources = { health: result.healthAfter, mana: result.manaAfter, readyAt: result.readyAt, activeUntil: result.activeUntil };
    availability = travel.availableAt;
    at = camp;
    if (result.complete === false) break;
    availability[index] = nextSpawn(clock, map);
  }
  if (route.steps.length) {
    route.status = 'ready'; route.validUntil = clock; route.seconds = clock - player.clock; route.score = route.gold;
    route.nextAction = `大模型下一步：${route.steps[0].label}。`;
  }
  return route;
}
