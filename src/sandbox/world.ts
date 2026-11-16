import fs from 'node:fs';
import { COMBAT, COMPOSITIONS, MAP } from '../tactics/combat-data.ts';
import { distance, insideCamp, NAVIGATION } from '../tactics/map-data.ts';
import { calculateSelfStats } from '../tactics/self-stats.ts';
import { initialResources } from '../tactics/clear-model.ts';
import { burstAbility } from '../tactics/skills.ts';
import { clamp, levelValue, physicalMultiplier } from '../tactics/rules.ts';
import type { Camp, PlayerSnapshot, Point, Team } from '../tactics/types.ts';
import { SANDBOX_RECOMMENDATION_SCOPE } from './recommendation-scope.ts';

const FIXTURE = JSON.parse(fs.readFileSync(new URL('../../tests/fixtures/luna-741f.json', import.meta.url), 'utf8'));
export const LEGACY_SANDBOX_RULES = {
  version: 'luna-sandbox-v1', sourcePatch: MAP.patch, tickSeconds: 0.1, horizonSeconds: 180,
  heroAttackRange: 330, beamRange: 800, neutralAttackRange: 450,
  waveEverySeconds: 30, waveMelee: 3, waveRanged: 1, laneMoveSpeed: 325,
  laneMelee: { health: 550, damage: 20, gold: 38, range: 160, attackTime: 1 },
  laneRanged: { health: 300, damage: 24, gold: 48, range: 500, attackTime: 1.5 },
  assumptions: [
    '简化实验环境，不是 Dota 2 客户端；模拟金币/GPM 不等于实战收益。',
    '营地位置和英雄/普通野怪基础数值取自固定 7.41f 数据；所有范围、兵线参数和路径近似均属沙盒机制。',
    '全图可见；固定等级装备；普通营地组合按种子均匀抽样，并非真实刷新概率；水生营地暂按普通组合。',
    '三路是示意折线；小兵自动行军、互攻；没有塔、英雄对手、战争迷雾、工资、购买或升级。',
    '野怪原地攻击，不追逐；不实现引野、堆野、水生进化、野怪升级/回复/技能/光环；疯脸暂不加移速。',
    '月刃即时弹射、优先未命中目标；月光只有伤害和施法前摇，不模拟眩晕；环月和月蚀不支持。',
    '英雄击杀才计金币；兵线与野怪分账；死亡结束实验，未使用时间不从 180 秒分母里扣除。',
  ],
};
const LEGACY_LANES: { id: string; points: Point[] }[] = [
  { id: 'top', points: [{ x: -6600, y: -6200 }, { x: -6600, y: 5600 }, { x: 6200, y: 6000 }] },
  { id: 'mid', points: [{ x: -6600, y: -6200 }, { x: 0, y: 0 }, { x: 6200, y: 6000 }] },
  { id: 'bot', points: [{ x: -6600, y: -6200 }, { x: 6200, y: -6200 }, { x: 6200, y: 6000 }] },
];
export const SANDBOX_RULES = {
  ...LEGACY_SANDBOX_RULES, version: 'luna-sandbox-v2-lane-pull',
  physicsRevision: 'walkable-hero-chase-1',
  objective: 'neutral and enemy-lane last-hit gold over 180 seconds',
  neutralAttackRange: 150, neutralMoveSpeed: 300, neutralAggroRange: 500,
  neutralChaseSeconds: 7, laneAggroRange: 450, laneChaseSeconds: 6,
  laneFarmRadius: 1100, laneWarmupSeconds: 120, stackRewardMultiplier: 0.8,
  assumptions: [
    ...LEGACY_SANDBOX_RULES.assumptions.filter(s => !s.startsWith('野怪原地') && !s.startsWith('三路是')),
    '目标为线野补刀总金币；兵线与野怪分账。不因偏好下半区而硬编码路线或奖励。',
    '兵线在起点前预演 120 秒，包含已有交战兵线；三路几何仍是示意，所有追逐半径/速度/时限属未校准参数。',
    '野怪受攻击后追逐，脱战步行归位；英雄将野怪引离刷新框才可能堆野。拉到兵线附近可使兵野互攻，不直接发放拉野金币。',
    '堆叠单位金币/经验乘 0.8，仅应用一次，不发自行堆野奖励；标记范围/时机是沙盒简化，非完整客户端复刻。',
    '原地回复、野怪升级/技能/光环、水生进化、环月、月蚀、动态碰撞仍缺失；追逐是直线近似。',
    'farm_lane 的 push / last_hit 是模型指定的低层攻击执行模式，不会选择另一条兵线或营地。',
    '己方可活动区域尚未核验，路线建议已禁用。营地阵营标签、没有死亡或没有敌方野怪击杀，都不能证明路径安全。',
  ],
};
/** Geometry helpers expose legal destinations, not ranked farming candidates. */
export function walkablePoint(point: Point): Point | null {
  const g = MAP.grid, x = Math.floor((point.x - g.origin.x) / g.cellSize), y = Math.floor((point.y - g.origin.y) / g.cellSize);
  for (let r = 0; r <= 5; r++) for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
    if (Math.max(Math.abs(dx), Math.abs(dy)) !== r || g.rows[y + dy]?.[x + dx] !== '1') continue;
    return { x: g.origin.x + (x + dx + 0.5) * g.cellSize, y: g.origin.y + (y + dy + 0.5) * g.cellSize };
  }
  return null;
}
export const LANES = [
  { id: 'top', points: [{ x: -6600, y: -6200 }, { x: -5700, y: -2400 }, { x: -4600, y: 800 }, { x: -1200, y: 4800 }, { x: 6200, y: 6000 }] },
  { id: 'mid', points: [{ x: -6600, y: -6200 }, { x: -2600, y: -2300 }, { x: 0, y: 0 }, { x: 2600, y: 2300 }, { x: 6200, y: 6000 }] },
  { id: 'bot', points: [{ x: -6600, y: -6200 }, { x: -4300, y: -5600 }, { x: -1200, y: -5000 }, { x: 2100, y: -3800 }, { x: 4600, y: -800 }, { x: 6200, y: 6000 }] },
].map(lane => ({ ...lane, points: lane.points.map(p => walkablePoint(p) || p) }));
export const CAMP_EXITS = MAP.camps.map(camp => ({
  campId: camp.id,
  exits: [
    { direction: 'west', position: { x: camp.bounds.min.x - 280, y: camp.y } },
    { direction: 'east', position: { x: camp.bounds.max.x + 280, y: camp.y } },
    { direction: 'south', position: { x: camp.x, y: camp.bounds.min.y - 280 } },
    { direction: 'north', position: { x: camp.x, y: camp.bounds.max.y + 280 } },
  ].map(e => ({ ...e, position: walkablePoint(e.position) })).filter(e => e.position && !insideCamp(e.position, camp)),
}));
export interface Unit extends Point {
  id: string; name: string; team: Team | 'neutral'; campId?: string; laneId?: string;
  hp: number; maxHp: number; damage: number; armor: number; magicResistance: number;
  gold: number; xp: number; attackTime: number; nextAttack: number; range: number;
  waypoints: Point[];
  home?: Point; aggroTarget?: string; aggroUntil?: number; returning?: boolean;
  stacked?: boolean; lastAggroByHero?: boolean; packId?: string;
}
export type Order = { type: 'stop' } | { type: 'move'; position: Point } |
  { type: 'attack'; targetId: string } | { type: 'farm'; campId: string; targetPriority: string[] } |
  { type: 'farm_lane'; laneId: string; position: Point; targetPriority: string[]; attackMode: 'push' | 'last_hit' } |
  { type: 'pull'; campId: string; position: Point };
export interface WorldEvent {
  clock: number; type: string; unitId?: string; campId?: string; amount?: number; detail?: string;
}
export interface Reward {
  clock: number; unitId: string; name: string; campId?: string; source: 'neutral' | 'lane'; gold: number; xp: number;
}
export interface OpponentCampContact {
  clock: number; kind: 'hero_damage' | 'proximity_aggro';
  campId: string; unitId: string; heroPosition: Point; targetPosition: Point;
}
function approach(from: Point, to: Point, length: number): void {
  const d = distance(from, to), fraction = d ? Math.min(1, length / d) : 1;
  from.x += (to.x - from.x) * fraction; from.y += (to.y - from.y) * fraction;
}
function walk(point: Point, waypoints: Point[], length: number): void {
  while (waypoints.length && length > 0) {
    const d = distance(point, waypoints[0]);
    if (d <= length) { const next = waypoints.shift()!; point.x = next.x; point.y = next.y; length -= d; }
    else { approach(point, waypoints[0], length); break; }
  }
}
export function parseOrder(value: any): Order {
  if (value?.type === 'stop') return { type: 'stop' };
  if (value?.type === 'move' && Number.isFinite(value.position?.x) && Number.isFinite(value.position?.y) &&
      Math.abs(value.position.x) <= 10500 && Math.abs(value.position.y) <= 10500)
    return { type: 'move', position: { x: value.position.x, y: value.position.y } };
  if (value?.type === 'attack' && typeof value.targetId === 'string' && value.targetId.length <= 80)
    return { type: 'attack', targetId: value.targetId };
  if (value?.type === 'pull' && MAP.camps.some(c => c.id === value.campId)) {
    const position = parseOrder({ type: 'move', position: value.position });
    if (position.type === 'move') return { type: 'pull', campId: value.campId, position: position.position };
  }
  if (value?.type === 'farm_lane' && LANES.some(l => l.id === value.laneId) &&
      ['push', 'last_hit'].includes(value.attackMode) && Array.isArray(value.targetPriority) &&
      value.targetPriority.length <= 16 && value.targetPriority.every((n: unknown) => ['lane_melee', 'lane_ranged'].includes(n as string))) {
    const position = parseOrder({ type: 'move', position: value.position });
    if (position.type === 'move') return { type: 'farm_lane', laneId: value.laneId,
      position: position.position, targetPriority: [...value.targetPriority], attackMode: value.attackMode };
  }
  if (value?.type === 'farm' && MAP.camps.some(c => c.id === value.campId) &&
      Array.isArray(value.targetPriority) && value.targetPriority.length <= 16 &&
      value.targetPriority.every((name: unknown) => typeof name === 'string' && Object.hasOwn(COMBAT.neutrals, name)))
    return { type: 'farm', campId: value.campId, targetPriority: [...value.targetPriority] };
  throw new Error('无效行动；支持 stop / move / attack / farm / farm_lane / pull');
}

/** A deterministic toy world. It executes orders; it never chooses the next farming location. */
export class SandboxWorld {
  readonly seed: number;
  readonly startClock: number;
  readonly player: PlayerSnapshot;
  readonly stats;
  readonly version: 'v1' | 'v2';
  readonly rules;
  units: Unit[] = [];
  events: WorldEvent[] = [];
  rewards: Reward[] = [];
  readonly opponentCampContacts: OpponentCampContact[] = [];
  damageTaken = 0;
  laneGoldLostToOtherUnits = 0;
  travelled = 0;
  pullPhase: 'approach' | 'leave' | 'done' | null = null;
  order: Order = { type: 'stop' };
  path: Point[] = [];
  arrivedAt: number | null = null;
  private tick = 0;
  private rng: number;
  private unitSequence = 0;
  private nextAttack: number;
  private readyAt: Record<string, number>;
  private maskUntil = -Infinity;
  private pendingBeam: { at: number; targetId: string; damage: number; mana: number; cooldown: number } | null = null;
  private chasePath: Point[] = [];
  private chaseDestination: Point | null = null;
  private chaseTargetId: string | null = null;
  constructor(seed = 741, version: 'v1' | 'v2' = 'v2') {
    if (!Number.isInteger(seed) || seed < 0 || seed > 0xffffffff) throw new Error('seed 须为 0–4294967295 整数');
    this.seed = seed; this.rng = seed; this.version = version;
    this.rules = version === 'v1' ? LEGACY_SANDBOX_RULES : SANDBOX_RULES;
    this.player = structuredClone(FIXTURE.player);
    this.startClock = this.player.clock;
    this.stats = calculateSelfStats(this.player)!;
    this.readyAt = initialResources(this.player).readyAt;
    this.nextAttack = this.clock + 1 / this.stats.attacksPerSecond;
    if (version === 'v2') {
      // Construct an already-running lane instead of giving the model an empty 10:00 map.
      // No hero, gold, neutrals, or post-start rewards participate in this pre-roll.
      this.tick = -SANDBOX_RULES.laneWarmupSeconds * 10;
      while (this.tick < 0) {
        if (Math.round(this.clock * 10) % (SANDBOX_RULES.waveEverySeconds * 10) === 0) this.spawnWaves();
        this.tick++; this.updateCreepsV2(false);
      }
      this.events = []; this.laneGoldLostToOtherUnits = 0;
    }
    for (const camp of MAP.camps) this.spawnCamp(camp); // Initial packs are explicitly pre-existing.
    this.spawnWaves();
  }
  get clock(): number { return this.startClock + this.tick / 10; }
  get elapsed(): number { return this.tick / 10; }
  get done(): boolean { return !this.player.alive || this.elapsed >= SANDBOX_RULES.horizonSeconds; }
  private random(): number {
    this.rng = (Math.imul(1664525, this.rng) + 1013904223) >>> 0;
    return this.rng / 0x100000000;
  }
  private event(event: Omit<WorldEvent, 'clock'>): void {
    this.events.push({ clock: this.clock, ...event });
    if (this.events.length > 3000) this.events.shift();
  }
  campUnits(id: string): Unit[] { return this.units.filter(u => u.campId === id && u.hp > 0); }
  private auditOpponentCamp(unit: Unit, kind: OpponentCampContact['kind']): void {
    if (this.version !== 'v2' || !unit.campId ||
        !MAP.camps.some(c => c.id === unit.campId && c.team !== this.player.team)) return;
    // Retain the first contact of each kind per camp, independent of the bounded UI event log.
    if (this.opponentCampContacts.some(e => e.campId === unit.campId && e.kind === kind)) return;
    this.opponentCampContacts.push({ clock: this.clock, kind, campId: unit.campId, unitId: unit.id,
      heroPosition: { ...this.player.position }, targetPosition: { x: unit.x, y: unit.y } });
  }
  territoryAudit() {
    const rewards = this.rewards.filter(r => r.campId &&
      MAP.camps.some(c => c.id === r.campId && c.team !== this.player.team));
    return structuredClone({
      status: this.opponentCampContacts.length || rewards.length ? 'violated' : 'not-observed',
      realGameSafetyVerified: false,
      pathAreaVerified: false,
      opponentCampContacts: this.opponentCampContacts,
      opponentCampRewards: rewards,
      opponentCampGold: rewards.reduce((s, r) => s + r.gold, 0),
    });
  }
  private addNeutral(name: string, camp: Camp, ordinal: number, parent?: Unit): void {
    const d = COMBAT.neutrals[name], angle = ordinal * 2.399963;
    const at = this.version === 'v2' && parent ? parent : camp;
    const stacked = this.version === 'v2' && parent?.stacked;
    this.units.push({ id: `n${++this.unitSequence}`, name, team: 'neutral', campId: camp.id,
      x: at.x + 70 * Math.cos(angle), y: at.y + 70 * Math.sin(angle),
      hp: d.health, maxHp: d.health, damage: d.damage, armor: d.armor, magicResistance: d.magicResistance,
      gold: d.gold * (stacked ? SANDBOX_RULES.stackRewardMultiplier : 1),
      xp: d.xp * (stacked ? SANDBOX_RULES.stackRewardMultiplier : 1), attackTime: 100 * d.attackTime / d.attackSpeed,
      nextAttack: this.clock + 0.6, range: this.rules.neutralAttackRange, waypoints: [],
      ...(this.version === 'v2' ? { home: { x: camp.x, y: camp.y }, packId: parent?.packId || `${camp.id}@${this.clock}`,
        stacked: !!stacked, aggroTarget: parent?.aggroTarget, aggroUntil: parent?.aggroUntil } : {}) });
  }
  private spawnCamp(camp: Camp): void {
    const packs = COMPOSITIONS.filter(p => p.kind === camp.kind);
    const pack = packs[Math.floor(this.random() * packs.length)];
    let ordinal = 0;
    for (const [name, count] of Object.entries(pack.units))
      for (let i = 0; i < count; i++) this.addNeutral(name, camp, ordinal++);
    this.event({ type: 'neutral_spawn', campId: camp.id, detail: pack.id });
  }
  private spawnWaves(): void {
    for (const lane of this.version === 'v1' ? LEGACY_LANES : LANES) for (const team of ['radiant', 'dire'] as const) {
      const points = team === 'radiant' ? lane.points : [...lane.points].reverse();
      for (let i = 0; i < 4; i++) {
        const ranged = i === 3, d = ranged ? SANDBOX_RULES.laneRanged : SANDBOX_RULES.laneMelee;
        this.units.push({ id: `l${++this.unitSequence}`, name: ranged ? 'lane_ranged' : 'lane_melee',
          team, laneId: lane.id, x: points[0].x + i * 20, y: points[0].y - i * 20,
          hp: d.health, maxHp: d.health, damage: d.damage, armor: ranged ? 0 : 2, magicResistance: 0,
          gold: d.gold, xp: ranged ? 69 : 57, range: d.range, attackTime: d.attackTime,
          nextAttack: this.clock + 1, waypoints: structuredClone(points.slice(1)),
          ...(this.version === 'v2' ? { packId: `${lane.id}-${team}@${this.clock}` } : {}) });
      }
    }
    this.event({ type: 'lane_spawn', detail: '三路双方各 3 近战 + 1 远程' });
  }
  setOrder(value: unknown): void {
    if (this.done) throw new Error('实验已结束，请重置');
    const order = parseOrder(value);
    let path: Point[] = [];
    if (order.type === 'move' || order.type === 'farm' || order.type === 'farm_lane' || order.type === 'pull') {
      const target = order.type === 'move' || order.type === 'farm_lane' ? order.position : MAP.camps.find(c => c.id === order.campId)!;
      const route = NAVIGATION.path(this.player.position, target);
      if (!route) throw new Error('目标无法通过当前网格到达');
      path = structuredClone(route.points.slice(1));
      if (order.type === 'pull' && !NAVIGATION.path(target, order.position)) throw new Error('引野撤离点不可达');
    }
    if (order.type === 'attack' && !this.units.some(u => u.id === order.targetId && u.hp > 0 && u.team !== this.player.team))
      throw new Error('攻击目标不存在、已死亡或是友军');
    if (this.pendingBeam) this.event({ type: 'cast_cancelled', detail: '新移动/攻击命令取消前摇' });
    if (this.version === 'v1' && ['farm_lane', 'pull'].includes(order.type)) throw new Error('v1 不支持线野/引野');
    this.pendingBeam = null; this.order = order; this.path = path; this.arrivedAt = null;
    this.chasePath = []; this.chaseDestination = null; this.chaseTargetId = null;
    this.pullPhase = order.type === 'pull' ? 'approach' : null;
    this.event({ type: 'order', detail: JSON.stringify(order) });
  }
  currentTarget(): Unit | undefined {
    if (this.order.type === 'attack') {
      const id = this.order.targetId;
      return this.units.find(u => u.id === id && u.hp > 0);
    }
    if (this.order.type === 'farm_lane' && this.arrivedAt !== null) {
      const order = this.order;
      const targets = this.units.filter(u => u.hp > 0 && u.team !== this.player.team && u.laneId === order.laneId &&
        distance(u, order.position) <= SANDBOX_RULES.laneFarmRadius &&
        (order.attackMode === 'push' || u.hp <= (this.stats.damage + this.stats.creepDamage) * physicalMultiplier(u.armor)));
      const rank = (u: Unit) => { const n = order.targetPriority.indexOf(u.name); return n < 0 ? Infinity : n; };
      return targets.sort((a, b) => rank(a) - rank(b))[0];
    }
    if (this.order.type === 'pull' && this.pullPhase === 'approach' && this.arrivedAt !== null)
      return this.campUnits(this.order.campId)[0];
    if (this.order.type !== 'farm' || this.arrivedAt === null) return undefined;
    const order = this.order, targets = this.campUnits(order.campId);
    const rank = (u: Unit) => { const i = order.targetPriority.indexOf(u.name); return i < 0 ? Infinity : i; };
    return targets.sort((a, b) => rank(a) - rank(b))[0]; // User/model priority; stable spawn order otherwise.
  }
  cast(name: string, targetId = 'current'): void {
    if (this.done) throw new Error('实验已结束');
    if (this.pendingBeam) throw new Error('施法前摇尚未结束');
    if ((this.readyAt[name] ?? Infinity) > this.clock) throw new Error('技能/物品未学习、未装备或正在冷却');
    if (name === 'item_mask_of_madness') {
      const data = COMBAT.items[name], cost = levelValue(data.AbilityManaCost);
      if (this.player.mana < cost) throw new Error('魔法不足');
      this.player.mana -= cost;
      this.readyAt[name] = this.clock + levelValue(data.AbilityCooldown);
      this.maskUntil = this.clock + levelValue(data.berserk_duration);
      this.nextAttack = Math.min(this.nextAttack, this.clock + this.attackInterval());
      this.event({ type: 'cast', detail: name }); return;
    }
    if (name !== 'luna_lucent_beam') throw new Error('unsupported：沙盒尚未实现该技能');
    if (this.maskUntil > this.clock) throw new Error('疯脸沉默期间不能施放月光');
    const learned = this.player.abilities.find(a => a.name === name)!;
    const spell = burstAbility(learned, COMBAT.heroes[this.player.hero].abilities[name])!;
    const target = targetId === 'current' ? this.currentTarget() : this.units.find(u => u.id === targetId && u.hp > 0);
    if (!target || target.team === this.player.team || distance(target, this.player.position) > SANDBOX_RULES.beamRange)
      throw new Error('月光目标不存在、友方或超出施法范围');
    if (this.player.mana < spell.mana) throw new Error('魔法不足');
    this.pendingBeam = { at: this.clock + spell.castPoint, targetId: target.id,
      damage: spell.damage, mana: spell.mana, cooldown: spell.cooldown };
    this.event({ type: 'cast_start', unitId: target.id, detail: name });
  }
  private attackInterval(): number {
    const bonus = this.maskUntil > this.clock ? levelValue(COMBAT.items.item_mask_of_madness.berserk_bonus_attack_speed) : 0;
    return 100 * this.stats.attackTime! / clamp(this.stats.attackSpeed! + bonus, 20, 700);
  }
  private hurt(unit: Unit, damage: number, heroKill: boolean, attackerId?: string): number {
    if (unit.hp <= 0) return 0;
    if (heroKill && damage > 0) this.auditOpponentCamp(unit, 'hero_damage');
    const dealt = Math.min(unit.hp, Math.max(0, damage)); unit.hp -= dealt;
    if (this.version === 'v2' && unit.team === 'neutral' && (heroKill || attackerId)) {
      for (const member of [unit, ...this.campUnits(unit.campId!).filter(m => m.id !== unit.id && distance(m, unit) <= 700)]) {
        member.aggroTarget = heroKill ? 'hero' : attackerId;
        member.aggroUntil = this.clock + SANDBOX_RULES.neutralChaseSeconds;
        member.returning = false;
        if (heroKill) member.lastAggroByHero = true;
      }
    }
    if (unit.hp <= 1e-8) {
      unit.hp = 0;
      this.event({ type: 'death', unitId: unit.id, campId: unit.campId, detail: heroKill ? 'hero' : 'creep' });
      if (heroKill) {
        this.rewards.push({ clock: this.clock, unitId: unit.id, name: unit.name, campId: unit.campId,
          source: unit.team === 'neutral' ? 'neutral' : 'lane', gold: unit.gold, xp: unit.xp });
        this.player.lastHits = (this.player.lastHits || 0) + 1;
      } else if (unit.laneId && unit.team !== this.player.team) {
        this.laneGoldLostToOtherUnits += unit.gold;
      }
      if (unit.name === 'npc_dota_neutral_mud_golem') {
        const camp = MAP.camps.find(c => c.id === unit.campId)!;
        this.addNeutral('npc_dota_neutral_mud_golem_split', camp, 0, unit);
        this.addNeutral('npc_dota_neutral_mud_golem_split', camp, 1, unit);
      }
    }
    return dealt;
  }
  private heroAttack(target: Unit): void {
    const damage = this.stats.damage + this.stats.creepDamage;
    const dealt = this.hurt(target, damage * physicalMultiplier(target.armor), true);
    this.player.health = Math.min(this.player.maxHealth, this.player.health + dealt * (this.stats.lifesteal || 0));
    this.event({ type: 'attack', unitId: target.id, amount: dealt });
    const glaive = this.player.abilities.find(a => a.name === 'luna_moon_glaive')!;
    const values = COMBAT.heroes[this.player.hero].abilities[glaive.name].values;
    const talent = this.player.abilities.some(a => a.name === 'special_bonus_unique_luna_7' && a.level > 0) ? 5 : 0;
    const retention = 1 - (levelValue(values.damage_reduction_percent, glaive.level) - talent) / 100;
    const hit = new Set([target.id]); let from = target;
    for (let n = 1; n <= levelValue(values.bounces, glaive.level); n++) {
      const candidates = this.units.filter(u => u.hp > 0 && u.team !== this.player.team && u.id !== from.id &&
        distance(from, u) <= levelValue(values.range));
      candidates.sort((a, b) => Number(hit.has(a.id)) - Number(hit.has(b.id)) || distance(from, a) - distance(from, b));
      const next = candidates[0]; if (!next) break;
      this.hurt(next, damage * retention ** n * physicalMultiplier(next.armor), true);
      hit.add(next.id); from = next;
    }
    this.nextAttack = this.clock + this.attackInterval();
  }
  private hurtHero(unit: Unit): void {
    const armor = this.stats.armor - (this.maskUntil > this.clock ? levelValue(COMBAT.items.item_mask_of_madness.berserk_armor_reduction) : 0);
    const damage = Math.min(this.player.health, unit.damage * physicalMultiplier(armor));
    this.player.health -= damage; this.damageTaken += damage;
    if (this.player.health <= 0 && this.player.alive) {
      this.player.alive = false; this.player.canAct = false; this.pendingBeam = null;
      this.event({ type: 'hero_death' });
    }
  }
  private updateCreepsV2(heroEnabled: boolean): void {
    const entity = (id: string | undefined): Point | undefined => id === 'hero'
      ? heroEnabled && this.player.alive ? this.player.position : undefined
      : this.units.find(u => u.id === id && u.hp > 0);
    for (const unit of this.units) {
      if (unit.hp <= 0) continue;
      let target = entity(unit.aggroTarget);
      if (!target || (unit.aggroUntil ?? 0) <= this.clock) {
        if (unit.aggroTarget && unit.team === 'neutral') {
          unit.returning = true;
          this.event({ type: 'neutral_return', campId: unit.campId, unitId: unit.id });
        }
        unit.aggroTarget = undefined; target = undefined;
      }
      if (unit.team === 'neutral') {
        if (unit.returning) {
          approach(unit, unit.home!, SANDBOX_RULES.neutralMoveSpeed / 10);
          if (distance(unit, unit.home!) <= 30) {
            unit.returning = false; unit.aggroUntil = this.clock + 1;
          }
          continue;
        }
        if (!target && (unit.aggroUntil ?? 0) <= this.clock) {
          if (heroEnabled && distance(unit, this.player.position) <= SANDBOX_RULES.neutralAggroRange) {
            unit.aggroTarget = 'hero';
            this.auditOpponentCamp(unit, 'proximity_aggro');
          }
          target = entity(unit.aggroTarget);
          if (target) {
            unit.aggroUntil = this.clock + SANDBOX_RULES.neutralChaseSeconds;
            if (unit.aggroTarget === 'hero') unit.lastAggroByHero = true;
            this.event({ type: 'neutral_aggro', campId: unit.campId, unitId: unit.id, detail: unit.aggroTarget });
          }
        }
      } else if (!target) {
        // Lane creep combat/aggro is a world mechanism, not a farming-route policy.
        const opponent = this.units.find(u => u.hp > 0 && u.laneId === unit.laneId && u.team !== unit.team &&
          distance(unit, u) <= SANDBOX_RULES.laneAggroRange)
          || this.units.find(u => u.hp > 0 && u.team === 'neutral' && u.aggroTarget && !u.returning &&
            (u.aggroUntil ?? 0) > this.clock && distance(unit, u) <= SANDBOX_RULES.laneAggroRange);
        unit.aggroTarget = opponent?.id || (heroEnabled && unit.team !== this.player.team &&
          distance(unit, this.player.position) <= unit.range ? 'hero' : undefined);
        target = entity(unit.aggroTarget);
        if (target) {
          unit.aggroUntil = this.clock + SANDBOX_RULES.laneChaseSeconds;
          if (opponent?.team === 'neutral') this.event({ type: 'lane_pull', campId: opponent.campId,
            unitId: unit.id, detail: `${unit.team}/${unit.laneId}` });
        }
      }
      if (target) {
        if (distance(unit, target) > unit.range)
          approach(unit, target, (unit.team === 'neutral' ? SANDBOX_RULES.neutralMoveSpeed : SANDBOX_RULES.laneMoveSpeed) / 10);
        if (distance(unit, target) <= unit.range && unit.nextAttack <= this.clock + 1e-8) {
          unit.nextAttack = this.clock + unit.attackTime;
          if (unit.aggroTarget === 'hero') this.hurtHero(unit);
          else {
            const enemy = this.units.find(u => u.id === unit.aggroTarget && u.hp > 0);
            if (enemy) this.hurt(enemy, unit.damage * physicalMultiplier(enemy.armor), false, unit.id);
          }
        }
      } else if (unit.laneId) {
        walk(unit, unit.waypoints, SANDBOX_RULES.laneMoveSpeed / 10);
        if (!unit.waypoints.length) unit.hp = 0;
      }
      if (heroEnabled && !this.player.alive) break;
    }
    this.units = this.units.filter(u => u.hp > 0);
  }
  private leavePull(): void {
    if (this.order.type !== 'pull') return;
    const route = NAVIGATION.path(this.player.position, this.order.position);
    if (!route) throw new Error('实际引野撤离位置不可达');
    this.path = structuredClone(route.points.slice(1));
    this.pullPhase = 'leave'; this.arrivedAt = null;
    this.event({ type: 'pull_leave', campId: this.order.campId });
  }
  private chaseHero(target: Unit, budget: number): void {
    if (budget <= 0) return;
    const g = MAP.grid, cell = (p: Point) => ({
      x: Math.floor((p.x - g.origin.x) / g.cellSize), y: Math.floor((p.y - g.origin.y) / g.cellSize),
    });
    const next = { ...this.player.position }; approach(next, target, budget);
    const from = cell(this.player.position), to = cell(next);
    const walkable = (x: number, y: number) => g.rows[y]?.[x] === '1';
    const direct = walkable(to.x, to.y) && (from.x === to.x || from.y === to.y ||
      (walkable(from.x, to.y) && walkable(to.x, from.y)));
    if (this.chaseTargetId !== target.id || !this.chaseDestination || distance(this.chaseDestination, target) > 256) {
      this.chasePath = []; this.chaseDestination = { x: target.x, y: target.y }; this.chaseTargetId = target.id;
    }
    if (direct && !this.chasePath.length) {
      this.player.position.x = next.x; this.player.position.y = next.y; return;
    }
    if (!this.chasePath.length) {
      const destination = walkablePoint(target);
      const route = destination && NAVIGATION.path(this.player.position, destination);
      if (!route) return; // An unreachable target is not permission to walk through cliffs.
      this.chasePath = structuredClone(route.points.slice(1));
    }
    walk(this.player.position, this.chasePath, budget);
  }
  advance(seconds: number): void {
    const ticks = Math.round(seconds * 10);
    if (!Number.isFinite(seconds) || seconds <= 0 || seconds > 180 || Math.abs(ticks / 10 - seconds) > 1e-8)
      throw new Error('推进时间须为 0.1–180 秒且是 0.1 的整数倍');
    for (let n = 0; n < ticks && !this.done; n++) this.advanceTick();
  }
  private advanceTick(): void {
    this.tick++; this.player.clock = this.clock;
    this.player.health = Math.min(this.player.maxHealth, this.player.health + this.stats.healthRegen / 10);
    this.player.mana = Math.min(this.player.maxMana, this.player.mana + this.stats.manaRegen / 10);
    const beforeMove = { ...this.player.position };
    if (!this.pendingBeam) {
      walk(this.player.position, this.path, this.stats.moveSpeed / 10);
      if (['farm', 'move', 'farm_lane', 'pull'].includes(this.order.type) && !this.path.length && this.arrivedAt === null)
        this.arrivedAt = this.clock;
      if (this.order.type === 'pull' && this.pullPhase === 'leave' && !this.path.length) this.pullPhase = 'done';
      const target = this.currentTarget();
      if ((this.order.type === 'attack' || (this.version === 'v2' && ['farm', 'farm_lane'].includes(this.order.type))) &&
          target && distance(target, this.player.position) > SANDBOX_RULES.heroAttackRange) {
        const budget = Math.max(0, this.stats.moveSpeed / 10 - distance(beforeMove, this.player.position));
        if (this.version === 'v2') this.chaseHero(target, budget); else approach(this.player.position, target, budget);
      } else this.chasePath = [];
    }
    this.travelled += distance(beforeMove, this.player.position);
    if (this.version === 'v2') {
      this.updateCreepsV2(true);
      if (!this.player.alive) return;
    }
    // Boundary spawns precede kills at that same tick; occupying the box blocks a respawn.
    if (Math.round(this.clock * 10) % (MAP.spawn.interval * 10) === 0) {
      for (const camp of MAP.camps) if (this.version === 'v2' || !this.campUnits(camp.id).length) {
        if (insideCamp(this.player.position, camp) || this.units.some(u => u.hp > 0 && insideCamp(u, camp)))
          this.event({ type: 'spawn_blocked', campId: camp.id });
        else {
          const survivors = this.campUnits(camp.id);
          this.spawnCamp(camp);
          if (this.version === 'v2' && survivors.length) {
            for (const unit of this.campUnits(camp.id)) if (!unit.stacked) {
              unit.stacked = true; unit.gold *= SANDBOX_RULES.stackRewardMultiplier; unit.xp *= SANDBOX_RULES.stackRewardMultiplier;
            }
            this.event({ type: 'stack_spawn', campId: camp.id, amount: this.campUnits(camp.id).length, detail: '计数变化不是即时金币' });
          }
        }
      }
    }
    if (Math.round(this.clock * 10) % (SANDBOX_RULES.waveEverySeconds * 10) === 0) this.spawnWaves();
    if (this.pendingBeam && this.pendingBeam.at <= this.clock + 1e-8) {
      const beam = this.pendingBeam, target = this.units.find(u => u.id === beam.targetId && u.hp > 0);
      this.pendingBeam = null;
      if (target) {
        this.player.mana -= beam.mana; this.readyAt.luna_lucent_beam = this.clock + beam.cooldown;
        this.hurt(target, beam.damage * (1 - target.magicResistance), true);
        this.event({ type: 'cast', unitId: target.id, detail: 'luna_lucent_beam' });
      } else this.event({ type: 'cast_cancelled', detail: '月光目标在前摇期间死亡' });
    }
    const target = this.currentTarget();
    if (!this.pendingBeam && target && this.nextAttack <= this.clock + 1e-8 &&
        distance(target, this.player.position) <= SANDBOX_RULES.heroAttackRange) {
      this.heroAttack(target);
      if (this.order.type === 'pull' && this.pullPhase === 'approach') this.leavePull();
    } else if (this.order.type === 'pull' && this.pullPhase === 'approach' && this.arrivedAt !== null && !target) this.leavePull();
    if (this.version === 'v2') { this.units = this.units.filter(u => u.hp > 0); return; }
    for (const unit of this.units) {
      if (unit.hp <= 0) continue;
      const heroInRange = unit.team !== this.player.team && distance(unit, this.player.position) <= unit.range;
      const opponent = unit.laneId ? this.units.find(u => u.hp > 0 && u.laneId === unit.laneId &&
        u.team !== unit.team && distance(u, unit) <= unit.range) : undefined;
      if (unit.laneId && !heroInRange && !opponent) {
        walk(unit, unit.waypoints, SANDBOX_RULES.laneMoveSpeed / 10);
        if (!unit.waypoints.length) unit.hp = 0; // Reached an unmodeled base: despawn without reward.
      }
      if ((heroInRange || opponent) && unit.nextAttack <= this.clock + 1e-8) {
        unit.nextAttack = this.clock + unit.attackTime;
        if (heroInRange) {
          const armor = this.stats.armor - (this.maskUntil > this.clock ? levelValue(COMBAT.items.item_mask_of_madness.berserk_armor_reduction) : 0);
          const damage = Math.min(this.player.health, unit.damage * physicalMultiplier(armor));
          this.player.health -= damage; this.damageTaken += damage;
        } else this.hurt(opponent!, unit.damage * physicalMultiplier(opponent!.armor), false);
      }
      if (this.player.health <= 0) {
        this.player.alive = false; this.player.canAct = false; this.pendingBeam = null;
        this.event({ type: 'hero_death' }); break;
      }
    }
    this.units = this.units.filter(u => u.hp > 0);
  }
  snapshot() {
    const sum = (source: Reward['source']) => this.rewards.filter(r => r.source === source).reduce((s, r) => s + r.gold, 0);
    const neutralGold = sum('neutral'), laneGold = sum('lane');
    return structuredClone({
      version: this.rules.version, seed: this.seed, startClock: this.startClock, clock: this.clock,
      elapsedSeconds: this.elapsed, horizonSeconds: 180, done: this.done,
      endedBecause: !this.player.alive ? 'death' : this.done ? 'horizon' : null,
      player: this.player, stats: this.stats, order: this.order, path: this.path.length ? this.path : this.chasePath, arrivedAt: this.arrivedAt,
      readyAt: this.readyAt, maskUntil: Number.isFinite(this.maskUntil) ? this.maskUntil : null,
      targetId: this.currentTarget()?.id || null, units: this.units, damageTaken: this.damageTaken,
      pullPhase: this.pullPhase, travelled: this.travelled, laneGoldLostToOtherUnits: this.laneGoldLostToOtherUnits,
      ...(this.version === 'v2' ? { laneSummary: this.laneSummary(),
        recommendationScope: SANDBOX_RECOMMENDATION_SCOPE, territoryAudit: this.territoryAudit(),
        spawnState: MAP.camps.map(c => ({ campId: c.id,
          nextSpawnAt: (Math.floor(this.clock / 60) + 1) * 60, living: this.campUnits(c.id).length,
          outsideBox: this.campUnits(c.id).filter(u => !insideCamp(u, c)).length,
          stacked: this.campUnits(c.id).filter(u => u.stacked).length,
          blocked: insideCamp(this.player.position, c) || this.units.some(u => insideCamp(u, c)) })) } : {}),
      score: { neutralGold, laneGold, totalGold: neutralGold + laneGold,
        neutralGpmOver180Seconds: neutralGold / 3, totalGpmOver180Seconds: (neutralGold + laneGold) / 3,
        measuredInGame: false },
      events: this.events.slice(-60), rewards: this.rewards,
    });
  }
  laneSummary() {
    return LANES.map(l => {
      const enemies = this.units.filter(u => u.laneId === l.id && u.team !== this.player.team && u.hp > 0);
      const groups = new Map<string, Unit[]>();
      for (const unit of enemies) { const id = unit.packId || unit.id; groups.set(id, [...groups.get(id) || [], unit]); }
      return { laneId: l.id, nextWaveAt: (Math.floor(this.clock / 30) + 1) * 30,
        enemyGold: enemies.reduce((s, u) => s + u.gold, 0),
        waves: [...groups].map(([id, units]) => ({ id, count: units.length,
          position: { x: units.reduce((s, u) => s + u.x, 0) / units.length, y: units.reduce((s, u) => s + u.y, 0) / units.length },
          gold: units.reduce((s, u) => s + u.gold, 0), health: units.map(u => Math.round(u.hp)) })) };
    });
  }
}
