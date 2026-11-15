import { MAP } from './combat-data.ts';
import { CampTracker, planJungleRoute } from './jungle.ts';
import { distance } from './map-data.ts';
import { calculateSelfStats, parsePlayerSnapshot } from './self-stats.ts';
import { RULES } from './rules.ts';
import type { JungleState, PlayerSnapshot, SelfStats } from './types.ts';

export class TacticalEngine {
  enabled = false;
  tracker = new CampTracker();
  private player: PlayerSnapshot | null = null;
  private stats: SelfStats | null = null;
  private route: JungleState['route'] = null;
  private updatedAt: number | null = null;
  private lastPlanAt = -Infinity;
  private lastStatus = '刷野规划默认关闭。';
  private matchId: string | null = null;
  reset(): void {
    this.tracker = new CampTracker(); this.player = null; this.stats = null; this.route = null;
    this.updatedAt = null; this.lastPlanAt = -Infinity; this.matchId = null;
    this.lastStatus = this.enabled ? '等待本机玩家 GSI。' : '刷野规划默认关闭。';
  }
  setEnabled(enabled: boolean): JungleState {
    this.enabled = enabled; this.reset();
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
      this.tracker.invalidateVisit();
      this.player = null; this.stats = null; this.route = null; this.updatedAt = null;
      this.lastStatus = '需要本机玩家 GSI 的坐标、阵营、血蓝和等级；观战/不完整数据不用于规划。';
      return true;
    }
    const stats = calculateSelfStats(next);
    const previous = this.player;
    const previousStats = this.stats;
    this.player = next; this.stats = stats; this.updatedAt = now;
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
      this.route = null; this.lastStatus = '玩家数据缺失或已过期，请检查 GSI 并等待新的游戏数据。';
    } else if (!this.player.alive || !this.player.canAct || this.player.health < this.player.maxHealth * RULES.minHealthFraction) {
      this.route = null;
      this.lastStatus = this.player.alive && this.player.canAct
        ? '血量偏低，先补给或回到安全位置。' : '等待英雄存活且可行动后重新规划。';
    } else if (now - this.lastPlanAt >= 1000) {
      this.lastPlanAt = now;
      this.route = planJungleRoute(this.player, this.stats, this.tracker);
      this.lastStatus = this.route.nextAction;
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
    if (!this.enabled || !this.route || this.updatedAt === null || now - this.updatedAt <= RULES.staleAfterMs) return false;
    this.route = null;
    this.tracker.invalidateVisit();
    this.lastStatus = 'GSI 已过期，路线已撤回；等待新的玩家数据。';
    return true;
  }
  state(): JungleState {
    return {
      enabled: this.enabled, patch: MAP.patch, status: this.lastStatus,
      player: this.player, stats: this.stats, route: this.route,
      camps: MAP.camps.map(c => ({ ...c, clearedAt: this.tracker.cleared.get(c.id) ?? null })),
      calibration: this.tracker.calibration, updatedAt: this.updatedAt,
    };
  }
}
