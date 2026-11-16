import { MAP } from '../tactics/combat-data.ts';
import { distance } from '../tactics/map-data.ts';
import type { Point } from '../tactics/types.ts';
import { LANES, parseOrder, SANDBOX_RULES, SandboxWorld } from './world.ts';
import type { Order } from './world.ts';
import { SANDBOX_RECOMMENDATION_SCOPE } from './recommendation-scope.ts';

export interface EconomyStep {
  kind: 'camp' | 'lane' | 'pull' | 'move' | 'wait';
  campId: string | null;
  laneId: string | null;
  position: Point | null;
  duration: number;
  untilClock: number | null;
  targetPriority: string[];
  attackMode: 'push' | 'last_hit';
  casts: { name: string; seconds: number }[];
}
export function stepOrder(step: EconomyStep): Order {
  if (step.kind === 'wait') return { type: 'stop' };
  if (step.kind === 'move') return parseOrder({ type: 'move', position: step.position });
  if (step.kind === 'pull') return parseOrder({ type: 'pull', campId: step.campId, position: step.position });
  if (step.kind === 'lane') return parseOrder({ type: 'farm_lane', laneId: step.laneId,
    position: step.position, attackMode: step.attackMode, targetPriority: step.targetPriority });
  return parseOrder({ type: 'farm', campId: step.campId, targetPriority: step.targetPriority });
}
export function parseEconomySteps(value: unknown): EconomyStep[] {
  if (!Array.isArray(value) || !value.length || value.length > 40) throw new Error('线野计划需要 1–40 个行动');
  return value.map(s => {
    if (!s || !['camp', 'lane', 'pull', 'move', 'wait'].includes(s.kind) ||
        !Number.isFinite(s.duration) || s.duration < 0.1 || s.duration > 180 ||
        Math.abs(Math.round(s.duration * 10) - s.duration * 10) > 1e-7 ||
        (s.untilClock !== null && (!Number.isFinite(s.untilClock) || s.untilClock < 600 || s.untilClock > 780)) ||
        !['push', 'last_hit'].includes(s.attackMode) || !Array.isArray(s.targetPriority) || s.targetPriority.length > 16 ||
        s.targetPriority.some((n: unknown) => typeof n !== 'string' || n.length > 100) ||
        !Array.isArray(s.casts) || s.casts.length > 24) throw new Error('线野行动字段或时间范围无效');
    const casts = s.casts.map((c: any) => {
      if (!c || !['luna_lucent_beam', 'item_mask_of_madness'].includes(c.name) ||
          !Number.isFinite(c.seconds) || c.seconds < 0 || c.seconds > 180) throw new Error('unsupported：技能或施法时间无效');
      return { name: c.name as string, seconds: c.seconds as number };
    });
    const step: EconomyStep = { kind: s.kind, campId: s.campId, laneId: s.laneId,
      position: s.position === null ? null : { x: s.position?.x, y: s.position?.y },
      duration: s.duration, untilClock: s.untilClock, attackMode: s.attackMode, targetPriority: [...s.targetPriority], casts };
    if ((step.kind === 'camp' || step.kind === 'pull') && !MAP.camps.some(c => c.id === step.campId && c.team === 'radiant'))
      throw new Error('该固定天辉场景只接受本方营地计划');
    if (step.kind === 'lane' && !LANES.some(l => l.id === step.laneId)) throw new Error('未知兵线');
    stepOrder(step); // Validate coordinates and low-level order before any world is changed.
    return step;
  });
}
export interface EconomyStepResult {
  kind: EconomyStep['kind']; label: string; position: Point; path: Point[];
  started: number; arrival: number | null; finish: number; completed: boolean;
  neutralGold: number; laneGold: number; totalGold: number; health: number; mana: number;
  nearbyEnemyWaves: { laneId: string; position: Point; count: number; gold: number }[];
}
export class EconomyPlanRunner {
  readonly world: SandboxWorld;
  readonly plan: EconomyStep[];
  readonly results: EconomyStepResult[] = [];
  readonly rejectedCasts: { clock: number; step: number; name: string; error: string }[] = [];
  index = 0;
  minHealth: number;
  private active: { started: number; arrival: number | null; rewardIndex: number; path: Point[]; casts: Set<number>; sawLane: boolean } | null = null;
  constructor(world: SandboxWorld, steps: unknown) {
    if (world.version !== 'v2') throw new Error('线野计划需要 v2 环境');
    this.world = world; this.plan = parseEconomySteps(steps); this.minHealth = world.player.health;
  }
  get finished(): boolean { return this.index >= this.plan.length || this.world.done; }
  advance(seconds: number): void {
    const ticks = Math.round(seconds * 10);
    if (!Number.isFinite(seconds) || seconds <= 0 || seconds > 180 || Math.abs(ticks / 10 - seconds) > 1e-8)
      throw new Error('推进时间须为 0.1–180 秒且是 0.1 的整数倍');
    for (let n = 0; n < ticks && !this.world.done; n++) {
      const step = this.plan[this.index];
      if (step && !this.active) {
        try { this.world.setOrder(stepOrder(step)); }
        catch (error) {
          throw new Error(`第 ${this.index + 1} 步 ${step.kind} ${step.campId || step.laneId || ''}，起点 (${this.world.player.position.x.toFixed(0)},${this.world.player.position.y.toFixed(0)})：${error instanceof Error ? error.message : '行动无效'}`);
        }
        this.active = { started: this.world.clock, arrival: step.kind === 'wait' ? this.world.clock : null,
          rewardIndex: this.world.rewards.length, path: structuredClone([this.world.player.position, ...this.world.path]), casts: new Set(), sawLane: false };
      }
      if (step && this.active) {
        if (step.kind === 'lane' && this.world.units.some(u => u.laneId === step.laneId && u.team !== this.world.player.team &&
            distance(u, step.position!) <= SANDBOX_RULES.laneFarmRadius)) this.active.sawLane = true;
        if (this.active.arrival === null && this.world.arrivedAt !== null) this.active.arrival = this.world.arrivedAt;
        if (this.active.arrival !== null) for (const [i, cast] of step.casts.entries()) {
          if (this.active.casts.has(i) || this.world.clock - this.active.arrival + 1e-8 < cast.seconds) continue;
          this.active.casts.add(i);
          try { this.world.cast(cast.name); }
          catch (error) { this.rejectedCasts.push({ clock: this.world.clock, step: this.index, name: cast.name,
            error: error instanceof Error ? error.message : '施法失败' }); }
        }
      }
      this.world.advance(0.1); this.minHealth = Math.min(this.minHealth, this.world.player.health);
      if (this.active && step) {
        const laneClear = step.kind === 'lane' && this.active.sawLane && this.world.arrivedAt !== null &&
          !this.world.units.some(u => u.laneId === step.laneId && u.team !== this.world.player.team && distance(u, step.position!) <= SANDBOX_RULES.laneFarmRadius);
        const completed = step.kind === 'camp' ? this.world.arrivedAt !== null && !this.world.campUnits(step.campId!).length :
          laneClear ? true :
          step.kind === 'move' ? this.world.arrivedAt !== null : step.kind === 'pull' ? this.world.pullPhase === 'done' : false;
        if (completed || this.world.done || this.world.clock - this.active.started + 1e-8 >= step.duration ||
            (step.untilClock !== null && this.world.clock + 1e-8 >= step.untilClock)) this.finishStep(completed);
      }
    }
  }
  private finishStep(completed: boolean): void {
    const active = this.active!, step = this.plan[this.index], rewards = this.world.rewards.slice(active.rewardIndex);
    const sum = (source: string) => rewards.filter(r => r.source === source).reduce((s, r) => s + r.gold, 0);
    const neutralGold = sum('neutral'), laneGold = sum('lane');
    const camp = MAP.camps.find(c => c.id === step.campId);
    const label = step.kind === 'camp' ? camp!.label : step.kind === 'pull' ? `引野 ${camp!.label}` :
      step.kind === 'lane' ? `${step.laneId} 兵线 · ${step.attackMode}` : step.kind === 'wait' ? '等待刷新/兵线' : '移动';
    this.results.push({ kind: step.kind, label, position: { ...this.world.player.position }, path: active.path,
      started: active.started, arrival: active.arrival ?? this.world.arrivedAt, finish: this.world.clock, completed,
      neutralGold, laneGold, totalGold: neutralGold + laneGold, health: this.world.player.health, mana: this.world.player.mana,
      nearbyEnemyWaves: this.world.laneSummary().flatMap(l => l.waves.filter(w => distance(w.position, this.world.player.position) < 2200)
        .map(w => ({ laneId: l.laneId, ...w }))) });
    this.active = null; this.index++;
    if (!this.world.done) this.world.setOrder({ type: 'stop' });
  }
  summary() {
    const state = this.world.snapshot();
    const territoryAudit = this.world.territoryAudit();
    const interesting = this.world.events.filter(e => ['stack_spawn', 'lane_pull', 'spawn_blocked', 'hero_death'].includes(e.type));
    return {
      objective: 'lane-plus-neutral-gold', version: SANDBOX_RULES.version, seed: this.world.seed,
      // "ready" is experimental eligibility only, NEVER a verified own-side route.
      status: this.world.player.alive && this.minHealth >= this.world.player.maxHealth * 0.3 &&
        state.score.totalGold > 0 && territoryAudit.status !== 'violated' ? 'ready' : 'unavailable',
      territoryAudit, recommendationScope: SANDBOX_RECOMMENDATION_SCOPE,
      score: state.score, minimumHealth: this.minHealth, steps: structuredClone(this.results),
      elapsedSeconds: this.world.elapsed, idleAfterPlanSeconds: this.finished ? this.world.clock - (this.results.at(-1)?.finish ?? this.world.startClock) : 0,
      travelled: this.world.travelled, laneGoldLostToOtherUnits: this.world.laneGoldLostToOtherUnits,
      rejectedCasts: this.rejectedCasts, mechanicEvents: interesting,
      warnings: SANDBOX_RULES.assumptions, measuredInGame: false,
    };
  }
}
export function evaluateEconomyPlan(seed: number, steps: unknown) {
  const world = new SandboxWorld(seed, 'v2'), runner = new EconomyPlanRunner(world, steps);
  runner.advance(180);
  return { ...runner.summary(), finalState: world.snapshot() };
}
