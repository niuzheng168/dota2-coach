import { MAP } from '../tactics/combat-data.ts';
import { parseModelVisits } from '../tactics/model-route.ts';
import type { ModelVisit } from '../tactics/model-route.ts';
import type { JungleRoute, RouteStep } from '../tactics/types.ts';
import { SANDBOX_RULES, SandboxWorld } from './world.ts';

/** The same runner drives visible playback and isolated API trials. No route search lives here. */
export class PlanRunner {
  readonly world: SandboxWorld;
  readonly visits: ModelVisit[];
  readonly steps: RouteStep[] = [];
  readonly rejectedCasts: { clock: number; campId: string; name: string; error: string }[] = [];
  index = 0;
  minHealth: number;
  private active: { started: number; arrival: number | null; rewards: number; events: number; damageTaken: number;
    path: RouteStep['path']; attempted: Set<number> } | null = null;
  constructor(world: SandboxWorld, visits: unknown) {
    this.world = world;
    this.visits = parseModelVisits(visits);
    for (const visit of this.visits) {
      if (!MAP.camps.some(c => c.id === visit.campId && c.team === world.player.team))
        throw new Error('规划实验只接受本方营地；手动行动 API 可探索全图');
      if (visit.casts.some(c => !['luna_lucent_beam', 'item_mask_of_madness'].includes(c.name)))
        throw new Error('unsupported：沙盒路线目前只支持月光和疯脸主动');
    }
    this.minHealth = world.player.health;
  }
  get finished(): boolean { return this.index >= this.visits.length || this.world.done; }
  advance(seconds: number): void {
    const ticks = Math.round(seconds * 10);
    if (!Number.isFinite(seconds) || seconds <= 0 || seconds > 180 || Math.abs(ticks / 10 - seconds) > 1e-8)
      throw new Error('推进时间须为 0.1–180 秒且是 0.1 的整数倍');
    for (let n = 0; n < ticks && !this.world.done; n++) {
      const visit = this.visits[this.index];
      if (visit && !this.active) {
        this.world.setOrder({ type: 'farm', campId: visit.campId, targetPriority: visit.targetPriority });
        this.active = { started: this.world.clock, arrival: null, rewards: this.world.rewards.length, damageTaken: this.world.damageTaken,
          events: this.world.events.length, path: structuredClone([this.world.player.position, ...this.world.path]),
          attempted: new Set() };
      }
      if (visit && this.active && this.world.arrivedAt !== null) {
        this.active.arrival = this.world.arrivedAt;
        for (const [i, cast] of visit.casts.entries()) {
          if (this.active.attempted.has(i) || this.world.clock - this.active.arrival + 1e-8 < cast.seconds) continue;
          this.active.attempted.add(i);
          try { this.world.cast(cast.name); }
          catch (error) { this.rejectedCasts.push({ clock: this.world.clock, campId: visit.campId,
            name: cast.name, error: error instanceof Error ? error.message : '施法失败' }); }
        }
      }
      this.world.advance(0.1);
      this.minHealth = Math.min(this.minHealth, this.world.player.health);
      if (this.active && (this.world.done || (this.world.arrivedAt !== null && !this.world.campUnits(visit.campId).length)))
        this.finishStep();
    }
  }
  private finishStep(): void {
    const active = this.active!, visit = this.visits[this.index];
    const camp = MAP.camps.find(c => c.id === visit.campId)!;
    const state = this.world.snapshot(), arrival = active.arrival ?? this.world.arrivedAt ?? this.world.clock;
    const kills = this.world.rewards.slice(active.rewards).filter(r => r.source === 'neutral');
    const casts: Record<string, number> = {};
    for (const e of this.world.events.slice(active.events)) if (e.type === 'cast' && e.detail)
      casts[e.detail] = (casts[e.detail] || 0) + 1;
    const seconds = this.world.clock - arrival;
    this.steps.push({
      campId: camp.id, label: camp.label, kind: camp.kind, position: { x: camp.x, y: camp.y },
      arrival, finish: this.world.clock, seconds, travelSeconds: arrival - active.started, waitSeconds: 0,
      spawnAt: null, availability: 'assumed', path: active.path, clearRange: [seconds, seconds],
      gold: kills.reduce((s, k) => s + k.gold, 0), xp: kills.reduce((s, k) => s + k.xp, 0),
      healthAfter: state.player.health, manaAfter: state.player.mana,
      readyAt: state.readyAt, activeUntil: state.maskUntil === null ? {} : { item_mask_of_madness: state.maskUntil },
      damageTaken: this.world.damageTaken - active.damageTaken,
      casts, complete: !this.world.campUnits(camp.id).length,
      feasible: state.player.alive && this.minHealth >= state.player.maxHealth * 0.3,
      kills: kills.map(k => ({ seconds: k.clock - arrival, unit: k.name, gold: k.gold, xp: k.xp })),
    });
    this.index++; this.active = null;
    if (!this.world.done) this.world.setOrder({ type: 'stop' });
  }
  route(): JungleRoute {
    const state = this.world.snapshot();
    const gold = state.score.neutralGold, seconds = (this.steps.at(-1)?.finish ?? this.world.startClock) - this.world.startClock;
    const resourcesEnough = this.minHealth >= this.world.player.maxHealth * 0.3;
    const violatesTerritory = this.world.version === 'v2' && this.world.territoryAudit().status === 'violated';
    return {
      plannedAt: this.world.startClock, horizonSeconds: 180, validUntil: this.world.startClock + seconds,
      steps: structuredClone(this.steps), seconds, gold, xp: this.world.rewards.filter(r => r.source === 'neutral').reduce((s, r) => s + r.xp, 0),
      score: gold, status: gold > 0 && resourcesEnough && !violatesTerritory && this.world.player.alive ? 'ready' : 'unavailable',
      nextAction: violatesTerritory ? '沙盒接触了对方营地，该实验方案不合格' :
        !resourcesEnough ? '沙盒实验低于 30% 最低血量约束或死亡，请修改方案' : gold > 0 ? '沙盒已执行模型指定路线；不代表实战安全' : '本次方案没有获得纯野金币',
      warnings: [...this.world.rules.assumptions, `最低血量 ${this.minHealth.toFixed(1)}；失败施法 ${this.rejectedCasts.length} 次。`,
        ...this.rejectedCasts.slice(0, 12).map(c => `${c.campId} @${c.clock.toFixed(1)} ${c.name}: ${c.error}`)],
      search: { engine: 'sandbox-action-replay', expanded: 0, evaluated: 1, beamWidth: 0, optimal: false },
    };
  }
}
export function evaluateRoute(seed: number, visits: unknown, version: 'v1' | 'v2' = 'v2') {
  const world = new SandboxWorld(seed, version), runner = new PlanRunner(world, visits);
  runner.advance(180);
  return { route: runner.route(), finalState: world.snapshot(), rejectedCasts: runner.rejectedCasts,
    minimumHealth: runner.minHealth, measuredInGame: false };
}
