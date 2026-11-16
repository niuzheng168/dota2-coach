import { MAP } from './combat-data.ts';
import { randomUUID } from 'node:crypto';
import { CampTracker } from './jungle.ts';
import type { planJungleRoute } from './jungle.ts';
import { configuredReasoner } from './reasoning-planner.ts';
import type { ModelDecision, RouteReasoner } from './reasoning-planner.ts';
import { parseModelVisits, replayModelRoute } from './model-route.ts';
import type { PlanningInput } from './model-route.ts';
import { distance } from './map-data.ts';
import { calculateSelfStats, parsePlayerSnapshot } from './self-stats.ts';
import { RULES } from './rules.ts';
import type { JungleState, PlayerSnapshot, SelfStats } from './types.ts';

export class TacticalEngine {
  onChange?: () => void;
  private readonly reasoner: RouteReasoner | null;
  private readonly benchmarkPlanner?: typeof planJungleRoute;
  private readonly now: () => number;
  private pending: AbortController | null = null;
  private lastModelRequestAt = -Infinity;
  private generation = 0;
  private recent: PlanningInput['recent'] = [];
  private decision: { value: ModelDecision; clock: number; cursor: number; consumedThrough: number } | null = null;
  constructor(options: { reasoner?: RouteReasoner | null; benchmarkPlanner?: typeof planJungleRoute; now?: () => number } = {}) {
    this.reasoner = options.reasoner === undefined
      ? options.benchmarkPlanner ? null : configuredReasoner()
      : options.reasoner;
    this.benchmarkPlanner = options.benchmarkPlanner; // Explicit offline test injection, never a runtime fallback.
    this.now = options.now || Date.now;
  }
  enabled = false;
  tracker = new CampTracker();
  private player: PlayerSnapshot | null = null;
  private stats: SelfStats | null = null;
  private route: JungleState['route'] = null;
  private updatedAt: number | null = null;
  private lastPlanAt = -Infinity;
  private lastStatus = '刷野规划默认关闭。';
  private matchId: string | null = null;
  private treadsAttribute: 'unknown' | 'str' | 'agi' | 'int' = 'unknown';
  private contextId = randomUUID();
  private visionCandidate: { attribute: string; capturedAt: number } | null = null;
  private observation: NonNullable<JungleState['observation']> = { status: '未接收本地视觉校验；规划以 GSI 和手动确认属性为准。', capturedAt: null };
  reset(): void {
    this.cancelPending(); this.decision = null; this.recent = [];
    this.tracker = new CampTracker(); this.player = null; this.stats = null; this.route = null;
    this.updatedAt = null; this.lastPlanAt = -Infinity; this.matchId = null;
    this.treadsAttribute = 'unknown';
    this.contextId = randomUUID(); this.visionCandidate = null;
    this.observation = { status: '等待本地视觉校验。', capturedAt: null };
    this.lastStatus = this.enabled ? '等待本机玩家 GSI。' : '刷野规划默认关闭。';
  }
  setEnabled(enabled: boolean): JungleState {
    this.enabled = enabled; this.reset();
    return this.state();
  }
  setOptions(options: unknown, now: number): JungleState {
    const value = (options as { treadsAttribute?: unknown } | null)?.treadsAttribute;
    if (!['unknown', 'str', 'agi', 'int'].includes(value as string)) throw new Error('无效动力鞋属性');
    if (!this.enabled) throw new Error('请先开启刷野规划。');
    this.treadsAttribute = value as typeof this.treadsAttribute;
    this.cancelPending();
    if (this.player) {
      this.applyOptions(this.player); this.stats = calculateSelfStats(this.player);
      this.lastPlanAt = -Infinity;
    }
    return this.requestRoute(now);
  }
  private applyOptions(player: PlayerSnapshot): void {
    for (const item of player.items) if (item.name === 'item_power_treads') {
      if (this.treadsAttribute === 'unknown') delete item.attribute;
      else item.attribute = this.treadsAttribute;
    }
  }
  observe(value: any, now: number): JungleState {
    const p = this.player;
    if (!this.enabled || !p || this.updatedAt === null || now - this.updatedAt > RULES.staleAfterMs ||
        !value || value.contextId !== this.contextId || value.hero !== p.hero ||
        !Number.isFinite(value.capturedAt) || now - value.capturedAt > 6000 || value.capturedAt > now + 500 ||
        value.capturedAt <= (this.observation.capturedAt ?? -Infinity) ||
        !Number.isFinite(value.clock) || Math.abs(value.clock - p.clock) > 3 ||
        !Number.isFinite(value.confidence) || value.confidence < 0.95 || value.confidence > 1 ||
        !['str', 'agi', 'int', 'unknown'].includes(value.treadsAttribute)) {
      this.visionCandidate = null;
      throw new Error('视觉结果过期、不确定或与当前 GSI 不一致，未用于规划');
    }
    this.observation = { capturedAt: value.capturedAt, status: '本地视觉与 GSI 英雄/时钟相符；属性仅作待确认建议。' };
    // Two separate frames are evidence for a suggestion, not proof of correctness.
    // Never let model confidence silently overwrite a manually confirmed attribute.
    if (value.treadsAttribute !== 'unknown' && p.items.some(i => i.name === 'item_power_treads')) {
      if (this.visionCandidate && this.visionCandidate.attribute === value.treadsAttribute &&
          value.capturedAt - this.visionCandidate.capturedAt >= 1000 &&
          value.capturedAt - this.visionCandidate.capturedAt <= 8000) {
        this.observation.status = `连续两帧建议动力鞋 ${value.treadsAttribute}，请确认；尚未自动改变属性。`;
      }
      this.visionCandidate = { attribute: value.treadsAttribute, capturedAt: value.capturedAt };
    } else this.visionCandidate = null;
    return this.state();
  }
  ingest(payload: Record<string, any>, now: number, reset = false): boolean {
    const matchId = payload.map?.matchid ? String(payload.map.matchid) : null;
    const next = parsePlayerSnapshot(payload);
    if (reset || (matchId && matchId !== '0' && this.matchId && matchId !== this.matchId) ||
        (next && this.player && (next.clock < this.player.clock || next.hero !== this.player.hero || next.team !== this.player.team))) {
      this.reset();
    }
    if (matchId && matchId !== '0') this.matchId = matchId;
    if (!this.enabled) return false;
    if (!next) {
      this.cancelPending(); this.decision = null;
      this.tracker.invalidateVisit();
      this.player = null; this.stats = null; this.route = null; this.updatedAt = null;
      this.lastStatus = '需要本机玩家 GSI 的坐标、阵营、血蓝和等级；观战/不完整数据不用于规划。';
      return true;
    }
    this.applyOptions(next);
    const stats = calculateSelfStats(next);
    const previous = this.player;
    const previousStats = this.stats;
    this.player = next; this.stats = stats; this.updatedAt = now;
    this.recent.push({ clock: next.clock, position: { ...next.position }, health: next.health, mana: next.mana, lastHits: next.lastHits });
    if (this.recent.length > 12) this.recent.shift();
    if (!stats) { this.route = null; this.lastStatus = '当前英雄没有静态战斗数据。'; return true; }
    this.tracker.observeKills(previous, next);
    const changed = this.tracker.ingest(next, stats);
    if (changed || next.health < next.maxHealth * RULES.minHealthFraction || now - this.lastPlanAt >= RULES.replanMs || !previous || previous.alive !== next.alive ||
        previous.canAct !== next.canAct || previous.level !== next.level || next.health < previous.health * 0.85 ||
        distance(previous.position, next.position) > 1200 ||
        previousStats?.damage !== stats.damage || previousStats?.moveSpeed !== stats.moveSpeed ||
        (this.route && next.clock >= this.route.validUntil)) this.requestRoute(now);
    return true;
  }
  requestRoute(now: number): JungleState {
    if (!this.enabled) throw new Error('请先开启刷野规划。');
    if (!this.player || !this.stats || this.updatedAt === null || now - this.updatedAt > RULES.staleAfterMs) {
      this.cancelPending();
      this.route = null; this.lastStatus = '玩家数据缺失或已过期，请检查 GSI 并等待新的游戏数据。';
    } else if (!this.player.alive || !this.player.canAct || this.player.health < this.player.maxHealth * RULES.minHealthFraction) {
      this.cancelPending(); this.decision = null;
      this.route = null;
      this.lastStatus = this.player.alive && this.player.canAct
        ? '血量偏低，先补给或回到安全位置。' : '等待英雄存活且可行动后重新规划。';
    } else if (now - this.lastPlanAt >= 1000) {
      this.lastPlanAt = now;
      if (this.benchmarkPlanner) {
        this.route = this.benchmarkPlanner(this.player, this.stats, this.tracker);
        this.lastStatus = this.route.nextAction;
      } else if (!this.reasoner) {
        this.route = null;
        this.lastStatus = '大模型规划未配置：请设置独立规划模型和 API 访问；不会回退到规则选路。';
      } else {
        this.refreshModelDecision();
        if (!this.pending && now - this.lastModelRequestAt >= 30_000) this.startModelRequest(now);
        else if (!this.route) this.lastStatus = this.pending
          ? '大模型正在结合当前状态与录像案例推理；没有预设路线回退。'
          : '等待下一次大模型推理窗口（最多每 30 秒一次）；没有规则回退。';
      }
    }
    return this.state();
  }
  markCleared(id: string, now: number): JungleState {
    if (!this.enabled || !this.player || this.updatedAt === null || now - this.updatedAt > RULES.staleAfterMs) {
      throw new Error('需要已开启规划且有新鲜玩家数据。');
    }
    this.tracker.markCleared(id, this.player.clock); this.lastPlanAt = -Infinity;
    return this.requestRoute(now);
  }
  expire(now: number): boolean {
    if (!this.enabled || (!this.route && !this.pending) || this.updatedAt === null || now - this.updatedAt <= RULES.staleAfterMs) return false;
    this.cancelPending();
    this.route = null;
    this.tracker.invalidateVisit();
    this.lastStatus = 'GSI 已过期，路线已撤回；等待新的玩家数据。';
    return true;
  }
  state(): JungleState {
    return {
      planner: { kind: this.benchmarkPlanner ? 'numeric-benchmark' : 'llm', model: this.reasoner?.model || null,
        configured: !!this.reasoner || !!this.benchmarkPlanner, pending: !!this.pending },
      contextId: this.contextId, options: { treadsAttribute: this.treadsAttribute }, observation: this.observation,
      enabled: this.enabled, patch: MAP.patch, status: this.lastStatus,
      player: this.player, stats: this.stats, route: this.route,
      camps: MAP.camps.map(c => ({ ...c, clearedAt: this.tracker.cleared.get(c.id) ?? null })),
      calibration: this.tracker.calibration, updatedAt: this.updatedAt,
    };
  }
  private planningInput(): PlanningInput {
    return structuredClone({ player: this.player!, stats: this.stats!, cleared: this.tracker.cleared, recent: this.recent });
  }
  private refreshModelDecision(): void {
    if (!this.decision?.value.visits || !this.player || !this.stats) return;
    const decision = this.decision;
    // Consume only observed sequential clears, never elapsed predicted finish times.
    while (decision.cursor < decision.value.visits!.length) {
      const cleared = this.tracker.cleared.get(decision.value.visits![decision.cursor].campId);
      if (cleared === undefined || cleared <= decision.consumedThrough) break;
      decision.consumedThrough = cleared; decision.cursor++;
    }
    const remaining = decision.value.visits!.slice(decision.cursor);
    if (!remaining.length) { this.route = null; this.lastStatus = '上次模型路线已完成，等待下一次推理。'; return; }
    try {
      this.route = replayModelRoute(this.planningInput(), remaining);
      this.route.reasoning = { model: decision.value.model, decisionId: decision.value.id, decidedAt: decision.clock,
        summary: decision.value.summary, evidenceIds: decision.value.evidenceIds };
      this.lastStatus = this.route.nextAction;
    } catch {
      this.route = null; this.lastStatus = '上次大模型路线在最新状态下未通过复核，已撤回；等待重新推理。';
    }
  }
  private startModelRequest(now: number): void {
    const controller = new AbortController(), generation = this.generation, input = this.planningInput();
    this.pending = controller; this.lastModelRequestAt = now;
    this.lastStatus = this.route ? '正在重放上次模型路线，并请求新的模型推理。' : '大模型正在推理；未调用规则选路。';
    const timeout = setTimeout(() => controller.abort(new Error('大模型规划超过 25 秒，已取消；没有规则回退')), 25_000);
    timeout.unref();
    let onAbort: () => void;
    const aborted = new Promise<never>((_resolve, reject) => {
      onAbort = () => reject(controller.signal.reason || new Error('规划已取消'));
      controller.signal.addEventListener('abort', onAbort, { once: true });
    });
    void Promise.race([Promise.resolve().then(() => {
      controller.signal.throwIfAborted();
      return this.reasoner!.decide(input, controller.signal);
    }), aborted])
      .then(value => {
        if (generation !== this.generation || controller.signal.aborted || !this.enabled || !this.player ||
            this.updatedAt === null || this.now() - this.updatedAt > RULES.staleAfterMs) return;
        if (!this.player.alive || !this.player.canAct || this.player.health < this.player.maxHealth * RULES.minHealthFraction) return;
        const visits = value.visits === null ? null : parseModelVisits(value.visits);
        this.decision = { value: { ...value, visits }, clock: input.player.clock, cursor: 0, consumedThrough: input.player.clock };
        this.route = null;
        if (visits) this.refreshModelDecision(); // Replay from NOW, not the stale request snapshot.
        else this.lastStatus = value.summary;
        this.lastPlanAt = this.now();
      })
      .catch(() => {
        if (generation === this.generation && this.enabled) {
          this.lastStatus = controller.signal.aborted ? '大模型推理超时或已取消；没有规则回退。'
            : '大模型未能产生可复核路线，请检查独立模型部署/访问；没有规则回退。';
        }
      })
      .finally(() => {
        clearTimeout(timeout); controller.signal.removeEventListener('abort', onAbort!);
        if (this.pending === controller) {
          this.pending = null;
          try { this.onChange?.(); } catch { /* UI publication must not create an unhandled rejection. */ }
        }
      });
  }
  cancelPending(): void {
    this.generation++;
    const pending = this.pending; this.pending = null; pending?.abort();
  }
  dispose(): void { this.cancelPending(); this.onChange = undefined; }
}
