import { createHash, randomUUID } from 'node:crypto';
import { buildShadow742Index, observeShadow742 } from './shadow-742.ts';
import type { Shadow742Index, Shadow742Observation, Shadow742Target } from './shadow-742.ts';
import type { PlayerSnapshot, Team } from './types.ts';

// Input boundary only. No TacticalEngine, old map/combat model, replay frames, network or game control.
export const GSI_TTL_MS = 5_000;
export const CLOCK_SKEW_MS = 500;
export const FACT_CLOCK_WINDOW = 3;
export const BRIDGE_SCHEMA = 'farm-shadow-bridge/1';
export type Exercise = 'rehearsal' | 'custom-lobby';
type Obj = Record<string, unknown>;
const obj = (v: unknown): v is Obj => v !== null && typeof v === 'object' && !Array.isArray(v);
const num = (v: unknown, lo: number, hi: number): v is number =>
  typeof v === 'number' && Number.isFinite(v) && v >= lo && v <= hi;
const integer = (v: unknown, lo: number, hi: number): v is number => num(v, lo, hi) && Number.isSafeInteger(v);
const time = (v: unknown, now: number): v is number =>
  integer(v, 0, Number.MAX_SAFE_INTEGER) && now - v < GSI_TTL_MS && v <= now + CLOCK_SKEW_MS;
const coordinate = (v: unknown): v is number => num(v, -12_000, 12_000);
const side = (s: unknown): Team | null =>
  s === 'radiant' || s === 'DOTA_TEAM_GOODGUYS' || s === 2 ? 'radiant' :
  s === 'dire' || s === 'DOTA_TEAM_BADGUYS' || s === 3 ? 'dire' : null;

export class BridgeFault extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code: string, message: string) {
    super(message);
    this.status = status; this.code = code;
  }
}
function reject(code: string, message: string, status = 422): never {
  throw new BridgeFault(status, code, message);
}
function schema(value: unknown, keys: string[]): asserts value is Obj {
  if (!obj(value) || Object.keys(value).length !== keys.length || !keys.every(k => Object.hasOwn(value, k)))
    reject('SCHEMA', 'JSON 字段缺失或含未允许的字段。', 400);
}

// Native GSI sends full current state plus optional change blocks. Never reconstruct a frame from deltas.
const coreSections = new Set(['provider', 'map', 'player', 'hero', 'abilities', 'items', 'previously', 'added', 'removed']);
const discardedSections = new Set(['auth', 'minimap', 'buildings', 'wearables', 'couriers', 'neutralitems', 'roshan', 'events', 'league']);
function playerShape(body: Obj) {
  const inspect: unknown[] = [];
  for (const [key, block] of Object.entries(body)) {
    if (coreSections.has(key) || key === 'draft') inspect.push(block);
    else if (!discardedSections.has(key)) reject('NOT_PLAYER_GSI', '只接收玩家 GSI；不接受观战、回放世界状态或自报权限。');
  }
  // Check even the delta envelopes for multiplexed player data; do not project anything from them.
  let nodes = 0;
  while (inspect.length) {
    const block = inspect.pop();
    if (!block || typeof block !== 'object') continue;
    for (const [key, v] of Object.entries(block)) {
      if (++nodes > 4_096) reject('GSI_COMPLEXITY', 'GSI 超过玩家包结构限制。');
      if (/^(?:team\d+|player\d+|allplayers|worldsnapshots|omniscient|spectator|coach)$/i.test(key))
        reject('NOT_PLAYER_GSI', '检测到观战或多玩家结构，已撤回输入。');
      if (/^is_?(?:spectat(?:or|ing)|observer|coach)$/i.test(key) && v !== false)
        reject('NOT_PLAYER_GSI', '输入标注为非本人视角，已撤回。');
      if (v !== null && typeof v === 'object') inspect.push(v);
    }
  }
}

function sourceTime(provider: unknown, now: number): number {
  if (!obj(provider) || provider.appid !== 570 || provider.name !== 'Dota 2')
    reject('NOT_DOTA_GSI', '缺少 Dota 2 provider / appid 570。');
  const ts = typeof provider.timestamp === 'string' && /^\d{1,11}$/.test(provider.timestamp)
    ? Number(provider.timestamp) : provider.timestamp;
  if (!integer(ts, 0, 99_999_999_999) || !time(ts * 1000, now))
    reject('SOURCE_TIME', 'provider.timestamp 缺失、过期或超前；不会用收包时刻替代。');
  return ts * 1000;
}
function matchIdOf(raw: unknown): string | null {
  const id = integer(raw, 1, Number.MAX_SAFE_INTEGER) ? String(raw) : raw;
  return typeof id === 'string' && /^[1-9]\d{0,19}$/.test(id) ? id : null;
}
function entries(input: unknown, slots: RegExp): Obj[] {
  if (input === undefined) return [];
  if (!obj(input) || Object.keys(input).length > 64) reject('OWN_SCHEMA', '物品或技能块格式不符。');
  return Object.entries(input).filter(([k]) => slots.test(k)).map(([, v]) => {
    if (!obj(v)) reject('OWN_SCHEMA', '物品或技能栏不是对象。');
    return v;
  });
}
function ownPlayer(body: Obj): PlayerSnapshot {
  const { hero: h, player: p, map: m } = body;
  if (!obj(h) || !obj(p) || !obj(m)) reject('MISSING_SELF', '缺少当前玩家、地图或英雄。');
  if (m.game_state !== 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS' ||
      (p.activity !== undefined && p.activity !== 'playing'))
    reject('NOT_PLAYING', '不在进行中的玩家视角（菜单、选人、观战或终局），已撤回。');
  if (m.paused === true || m.is_paused === true) reject('PAUSED', '游戏已暂停，已撤回。');
  const controls = [h.stunned, h.hexed, h.disarmed, h.silenced];
  const flags = [m.paused, m.is_paused, ...controls, h.rooted, h.command_restricted, h.muted];
  if (flags.some(v => v !== undefined && typeof v !== 'boolean')) reject('OWN_SCHEMA', '暂停或英雄状态类型不符。');
  const team = side(p.team_name ?? p.team);
  if (!team || [p.team, h.team, h.team_name].some(v => v !== undefined && side(v) !== team))
    reject('TEAM_UNKNOWN', '没有一致的本人阵营；不能用观战阵营或营地名推断。');
  if (typeof h.name !== 'string' || !/^npc_dota_hero_[a-z0-9_]{1,64}$/.test(h.name) ||
      !coordinate(h.xpos) || !coordinate(h.ypos) || !integer(h.level, 1, 30) || typeof h.alive !== 'boolean' ||
      !num(h.max_health, 1, 100_000) || !num(h.health, 0, h.max_health) ||
      !num(h.max_mana, 0, 100_000) || !num(h.mana, 0, h.max_mana) || !num(m.clock_time, 0, 43_200))
    reject('MISSING_SELF', '需要本体英雄、坐标、等级、血蓝、存活及可信游戏时钟。');
  if (!h.alive || h.health === 0) reject('DEAD', '英雄已死亡，之前的输入已撤回。');
  if (flags.some(v => v === true)) reject('CANNOT_ACT', '本体当前不能正常行动，已撤回。');
  const items: PlayerSnapshot['items'] = [], abilities: PlayerSnapshot['abilities'] = [];
  for (const i of entries(body.items, /^slot[0-5]$/)) {
    if (i.name === 'empty') continue;
    if (typeof i.name !== 'string' || !/^item_[a-z0-9_]{1,76}$/.test(i.name) ||
        (i.cooldown !== undefined && !num(i.cooldown, 0, 10_000))) reject('OWN_SCHEMA', '本人装备名称或冷却无效。');
    // Same convention as PlayerSnapshot: these slots have active stats; it is NOT a cast-readiness flag.
    items.push({ name: i.name, passive: true, ...(typeof i.cooldown === 'number' ? { cooldown: i.cooldown } : {}) });
  }
  for (const a of entries(body.abilities, /^ability\d{1,2}$/)) {
    if (typeof a.name !== 'string' || !/^[a-z][a-z0-9_]{0,79}$/.test(a.name) || !integer(a.level, 0, 40))
      reject('OWN_SCHEMA', '本人技能名称或等级无效。');
    if (!a.level) continue;
    if (num(a.cooldown, 0, 10_000)) abilities.push({ name: a.name, level: a.level, cooldown: a.cooldown });
    else if (a.cooldown === undefined && a.passive === true) abilities.push({ name: a.name, level: a.level, cooldown: 0 });
    // Unknown active cooldown is not ready; don't put an invented zero in PlayerSnapshot.
  }
  return {
    hero: h.name, team, position: { x: h.xpos, y: h.ypos }, clock: m.clock_time, level: h.level,
    health: h.health, maxHealth: h.max_health, mana: h.mana, maxMana: h.max_mana, alive: true,
    canAct: (m.paused === false || m.is_paused === false) && controls.every(v => v === false),
    items, abilities, lastHits: integer(p.last_hits, 0, 100_000) ? p.last_hits : null,
  };
}

export interface Bridge742Archive {
  name: 'fixed' | 'expanded'; index: Shadow742Index; builds: ReadonlyMap<string, number>; matchedBuilds: ReadonlySet<number>;
  patchFrom: number; patchUntil: number | null;
}
export function bridge742Archive(raw: unknown, name: Bridge742Archive['name'] = 'fixed'): Bridge742Archive {
  if (!obj(raw) || raw.scope !== 'omniscient-offline-only' || !obj(raw.patch) || raw.patch.version !== '7.42' ||
      !integer(raw.patch.from, 1, 99_999_999_999) ||
      (raw.patch.until !== null && !integer(raw.patch.until, raw.patch.from + 1, 99_999_999_999)) ||
      !Array.isArray(raw.matches) || !Array.isArray(raw.builds)) throw new Error('Expected the published offline 7.42 archive.');
  const index = buildShadow742Index(raw);
  // The checked-in manifest, NOT a current installation, provider.version, calendar guess or inclusive build range.
  const manifested = new Set<number>();
  for (const m of raw.builds) if (obj(m) && integer(m.build, 1, 10_000_000) && typeof m.text === 'string' &&
      createHash('sha256').update(m.text).digest('hex') === m.sha256 && /^appID=570$/m.test(m.text) &&
      Number(/^ClientVersion=(\d+)$/m.exec(m.text)?.[1]) === m.build &&
      Number(/^ServerVersion=(\d+)$/m.exec(m.text)?.[1]) === m.build) manifested.add(m.build);
  const builds = new Map<string, number>();
  for (const row of raw.matches) if (obj(row) && integer(row.build, 1, 10_000_000) && manifested.has(row.build) &&
      [row.demSha256, row.metadataSha256].every(v => typeof v === 'string' && /^[0-9a-f]{64}$/.test(v))) {
    if (builds.has(String(row.id)) && builds.get(String(row.id)) !== row.build) throw new Error('Conflicting archived match builds.');
    builds.set(String(row.id), row.build);
  }
  return { name, index, builds, matchedBuilds: new Set(builds.values()), patchFrom: raw.patch.from, patchUntil: raw.patch.until };
}
type Binding = { contextId: string; matchId: string; team: Team; hero: string };
type Cursor = Binding & { clock: number; sourceAt: number; advancedAt: number; openedAt: number };
type Frame = { player: PlayerSnapshot; sourceAt: number; receivedAt: number; expiresAt: number;
  itemsReported: boolean; abilitiesReported: boolean };
type Batch = { targets: Shadow742Target[]; danger: 'seen' | 'not-seen' | 'unknown'; capturedAt: number; expiresAt: number; clock: number };
type Confirmation = { build: number | null; patch: '7.42' | null; matchStartUnix: number | null;
  metadataSource: 'matched-archive' | 'operator-match-metadata'; checkedAt: number };
type Notice = { code: string; message: string };
const waiting: Notice = { code: 'WAITING_GSI', message: '等待独立端口上的本人玩家 GSI。' };
const stale: Notice = { code: 'STALE_GSI', message: '本人帧或游戏时钟已过期；旧输入和本局确认已撤回。' };
const stopped: Notice = { code: 'STOPPED', message: '本次桥已停止接收，重启进程才能恢复。' };

export class FarmShadowBridge {
  readonly exercise: Exercise;
  private readonly now: () => number;
  private readonly archive?: Bridge742Archive;
  private cursor: Cursor | null = null;
  private frame: Frame | null = null;
  private confirmation: Confirmation | null = null;
  private batch: Batch | null = null;
  private lastConfirmationAt = -Infinity;
  private lastManualAt = -Infinity;
  private notice = waiting;
  private sealed = false;
  private retired = new Set<string>();
  private newestSource = -Infinity;
  private inactiveThrough = -Infinity;
  constructor(exercise: Exercise = 'rehearsal', now: () => number = Date.now, archive?: Bridge742Archive) {
    this.exercise = exercise; this.now = now; this.archive = archive;
  }
  private clear(reason: Notice) {
    this.frame = null; this.confirmation = null; this.batch = null;
    this.lastConfirmationAt = -Infinity; this.lastManualAt = -Infinity;
    this.notice = reason;
  }
  stop() { this.sealed = true; this.clear(stopped); this.cursor = null; this.retired.clear(); }
  private open() { if (this.sealed) reject(stopped.code, stopped.message, 410); }
  private retire(matchId: string) {
    this.retired.add(matchId);
    if (this.retired.size > 32) this.retired.delete(this.retired.values().next().value!);
  }
  expire(now = this.now()) {
    if (this.frame && (now >= this.frame.expiresAt || now + CLOCK_SKEW_MS < this.frame.receivedAt)) this.clear(stale);
    if (this.batch && (!this.frame || now >= this.batch.expiresAt || !time(this.batch.capturedAt, now) ||
        this.frame.player.clock - this.batch.clock > FACT_CLOCK_WINDOW)) this.batch = null;
  }
  // Called only after HTTP transport and the GSI-specific token have been checked.
  ingest(body: unknown) {
    this.open();
    const now = this.now(); this.expire(now);
    let examinedAt: number | null = null;
    try {
      if (!obj(body)) reject('SCHEMA', '需要 GSI JSON 对象。', 400);
      const sourceAt = sourceTime(body.provider, now);
      const matchId = obj(body.map) ? matchIdOf(body.map.matchid) : null;
      const old = this.cursor;
      if (matchId && this.retired.has(matchId)) reject('RETIRED_MATCH', '已结束的对局，丢弃迟到包。', 409);
      if (sourceAt < this.newestSource || sourceAt <= this.inactiveThrough) reject('OUT_OF_ORDER', '来源时戳落后或不能区分于撤回帧，未覆盖。', 409);
      this.newestSource = examinedAt = sourceAt;
      playerShape(body);
      if (!matchId) reject('MATCH_UNKNOWN', '本局需要非零 matchid，不给无编号试玩或菜单分配假局号。');
      if (obj(body.map) && (body.map.game_state === 'DOTA_GAMERULES_STATE_POST_GAME' ||
          body.map.game_state === 'DOTA_GAMERULES_STATE_DISCONNECT')) {
        this.retire(matchId); reject('NOT_PLAYING', '本局已结束或断开；不能用迟到包复活。');
      }
      if (this.exercise === 'custom-lobby' && obj(body.map) && body.map.lobby_type !== undefined && body.map.lobby_type !== 1)
        reject('NOT_CUSTOM', '明确不是练习房类型；不接入普通匹配或排位。');
      const player = ownPlayer(body);
      const changed = !old || old.matchId !== matchId || old.team !== player.team || old.hero !== player.hero;
      const seek = old && !changed && (player.clock < old.clock ||
        player.clock - old.clock > Math.max(0, (sourceAt - old.sourceAt) / 1000) + FACT_CLOCK_WINDOW);
      if (old && old.matchId !== matchId) this.retire(old.matchId);
      const advancedAt = !old || changed || seek || player.clock > old.clock ? now : old.advancedAt;
      if (now - advancedAt >= GSI_TTL_MS) reject('FROZEN_CLOCK', '没有新的游戏时钟；重复心跳不能复活输入。', 409);
      if (changed || seek || !this.frame || this.frame.player.canAct !== player.canAct) {
        this.clear(waiting);
        this.cursor = { contextId: randomUUID(), matchId, team: player.team, hero: player.hero,
          clock: player.clock, sourceAt, advancedAt, openedAt: now };
      } else {
        this.cursor = { ...old!, clock: player.clock, sourceAt, advancedAt };
      }
      this.frame = { player, sourceAt, receivedAt: now, expiresAt: Math.min(now, sourceAt, advancedAt) + GSI_TTL_MS,
        itemsReported: obj(body.items), abilitiesReported: obj(body.abilities) };
      this.expire(now);
    } catch (err) {
      // A demonstrably superseded frame cannot overwrite a newer one or prolong its TTL.
      if (err instanceof BridgeFault && !['OUT_OF_ORDER', 'RETIRED_MATCH'].includes(err.code)) {
        if (examinedAt !== null && err.code !== 'FROZEN_CLOCK') this.inactiveThrough = examinedAt;
        this.clear(err);
      }
      throw err;
    }
  }
  private current(value: Obj, now: number) {
    this.open(); this.expire(now);
    const c = this.cursor, f = this.frame;
    if (!c || !f || value.contextId !== c.contextId || value.matchId !== c.matchId ||
        value.team !== c.team || value.hero !== c.hero)
      reject('CONTEXT_MISMATCH', '当前局、阵营、英雄或 contextId 已不同；请重新核对。', 409);
    if (!time(value.capturedAt, now) || value.capturedAt < c.openedAt ||
        !num(value.clock, Math.max(0, f.player.clock - FACT_CLOCK_WINDOW), f.player.clock))
      reject('OBSERVATION_TIME', '观测不是此刻的墙钟和游戏时钟，未采纳。', 409);
    return { c, f, capturedAt: value.capturedAt, clock: value.clock };
  }
  confirm(value: unknown) {
    schema(value, ['contextId', 'matchId', 'team', 'hero', 'capturedAt', 'clock', 'exercise', 'ownPlayerView', 'build', 'matchStartUnix']);
    const { c, f, capturedAt } = this.current(value, this.now());
    if (capturedAt <= this.lastConfirmationAt || capturedAt < this.lastManualAt) reject('CONFIRM_ORDER', '重复或倒序的本局确认。', 409);
    this.lastConfirmationAt = capturedAt; this.confirmation = null; this.batch = null;
    if (value.exercise !== this.exercise || value.ownPlayerView !== true)
      reject('EXERCISE_ONLY', '仅可人工确认本局为自定义练习或本人视角复盘演练。');
    if (value.build !== null && !integer(value.build, 1, 10_000_000)) reject('BUILD_SCHEMA', 'build 须为所见的整数，或 null（未知）。');
    let build = value.build;
    // Only an upper bound, NEVER estimate match start as receipt time minus the game clock (pauses, seeks).
    const latestStart = Math.floor(Math.min(f.receivedAt, f.sourceAt) / 1000 - f.player.clock) + 2;
    if (value.matchStartUnix !== null && !integer(value.matchStartUnix, 1, latestStart))
      reject('MATCH_START', '开局 Unix 秒无效或与当前游戏时钟矛盾，不使用本机当前日期猜开局。');
    let start = value.matchStartUnix;
    const known = this.archive?.index.games.find(g => g.id === c.matchId);
    if (known) {
      const kb = this.archive!.builds.get(c.matchId);
      if (this.exercise !== 'rehearsal' || !known.tracks.some(t => 'npc_dota_hero_' + t.hero === c.hero && t.team === c.team) ||
          (start !== null && start !== known.start) || (build !== null && kb !== build))
        reject('MATCH_CONFLICT', '与归档的本局身份、用途、build 或开局时间不符。', 409);
      build = kb ?? null; start = known.start;
    }
    const patch = build !== null && this.archive?.matchedBuilds.has(build) ? '7.42' : null;
    if (start !== null && (start > latestStart || (patch && this.archive && (start < this.archive.patchFrom ||
        (this.archive.patchUntil !== null && start >= this.archive.patchUntil)))))
      reject('MATCH_START', '声明的开局时间不在对应版本的已知区间内。');
    this.confirmation = { build, patch, matchStartUnix: start,
      metadataSource: known ? 'matched-archive' : 'operator-match-metadata', checkedAt: capturedAt };
  }
  observe(value: unknown) {
    schema(value, ['contextId', 'matchId', 'team', 'hero', 'capturedAt', 'clock', 'visibleNow', 'danger', 'targets']);
    const now = this.now(), { c, capturedAt, clock } = this.current(value, now);
    if (!this.confirmation) reject('CONFIRM_FIRST', '先确认本局、演练用途及本人视角。', 409);
    if (capturedAt < this.confirmation.checkedAt || capturedAt < this.lastManualAt ||
        (capturedAt === this.lastManualAt && value.danger !== 'seen'))
      reject('OBSERVATION_ORDER', '重复、倒序或早于本局确认的人工观测。', 409);
    this.batch = null; this.lastManualAt = capturedAt; // Even a malformed fresh replacement supersedes earlier positives.
    if (value.visibleNow !== true || !Array.isArray(value.targets) || value.targets.length > 32 ||
        (value.danger !== 'seen' && value.danger !== 'not-seen' && value.danger !== 'unknown'))
      reject('VISIBLE_SCHEMA', '须确认本人视角与危险读数，最多提交 32 个可见目标。');
    const ids = new Set<string>();
    const targets = value.targets.map((v): Shadow742Target => {
      schema(v, ['targetId', 'state', 'area']);
      if (typeof v.targetId !== 'string' || !/^(?:lane:(?:top|mid|bot)|(?:good|evil)_[1-9]\d?)$/.test(v.targetId) ||
          ids.has(v.targetId) || (v.state !== 'present' && v.state !== 'empty' && v.state !== 'unknown') ||
          (v.area !== 'own' && v.area !== 'enemy' && v.area !== 'unknown'))
        reject('VISIBLE_SCHEMA', '目标编号、重复目标、所见状态或局部区域无效。');
      ids.add(v.targetId);
      return { targetId: v.targetId, matchId: c.matchId, seenAtSec: clock, source: 'manual-confirmed', state: v.state, area: v.area };
    });
    // Atomic replacement, not a cache of last-known enemies/camp spawns.
    this.batch = { targets, capturedAt, clock, danger: value.danger, expiresAt: Math.min(now, capturedAt) + GSI_TTL_MS };
  }
  state() {
    const now = this.now(); this.expire(now);
    const f = this.frame, c = this.cursor, conf = this.confirmation;
    const batch = this.batch;
    const hero = f?.player.hero.slice('npc_dota_hero_'.length);
    const observation: Shadow742Observation | null = f && c && conf && (hero === 'luna' || hero === 'troll_warlord') ? {
      informationScope: 'player-visible', patch: conf.patch ?? 'unknown', contextId: c.contextId, matchId: c.matchId,
      ...(conf.matchStartUnix !== null ? { matchStartUnix: conf.matchStartUnix } : {}),
      capturedAtMs: Math.min(f.receivedAt, f.sourceAt, batch?.capturedAt ?? Infinity), hero, team: c.team, timeSec: f.player.clock,
      self: { observedAtSec: f.player.clock, alive: f.player.alive, ...(f.player.canAct ? { canAct: true } : {}), position: f.player.position,
        ...(f.itemsReported ? { itemsSeen: f.player.items.map(i => ({ itemId: i.name.slice(5), seenAtSec: f.player.clock, matchId: c.matchId })) } : {}) },
      visibleTargets: batch?.targets ?? [], ...(batch?.danger === 'seen' ? { dangerVisible: true } :
        !batch || batch.danger === 'unknown' ? { cancelled: true } : {}),
    } : null;
    // No ranks or replay world are exposed. The independent strategy module alone computes this read-only intersection.
    const shadow = this.archive && observation && f?.player.canAct && conf?.patch && conf.matchStartUnix !== null && batch && batch.danger !== 'unknown'
      ? observeShadow742(this.archive.index, observation, { nowMs: now }) : null;
    const status: Notice = !f ? this.notice : !conf ?
      { code: 'CONFIRM_CONTEXT', message: '已收到当前本人帧；请核对本局、用途、视角与 build。' } :
      !conf.patch ? { code: 'PATCH_UNKNOWN', message: '本局 build 未与 7.42 样本对应，只显示当前观测。' } :
      conf.matchStartUnix === null ? { code: 'START_UNKNOWN', message: '缺本局可信开局时间；不能确定哪些样本是历史，只显示输入。' } :
      !observation ? { code: 'UNSUPPORTED_HERO', message: '已显示本人状态，此英雄不在当前影子研究范围内。' } :
      !f.player.canAct ? { code: 'SELF_STATUS_UNKNOWN', message: '本体暂停或受控字段未完整上报，仅显示状态。' } :
      !batch || batch.danger === 'unknown' ? { code: 'NEED_SIGHTING', message: '请核对当前本人视野、危险和局部区域；看不到不是安全。' } :
      shadow?.status === 'observed' ? { code: 'SHADOW_OVERLAP', message: '当前亲见目标与历史样本重合；不是走法，路径安全未知。' } :
      shadow?.status === 'cancelled' ? { code: 'CANCELLED', message: '已撤回影子候选，不行动。' } :
      { code: 'ABSTAIN', message: '区域、目标或历史依据不足；不行动。' };
    return structuredClone({
      schema: BRIDGE_SCHEMA, exercise: this.exercise, now, status: { code: status.code, message: status.message },
      archive: this.archive ? { name: this.archive.name, matches: this.archive.index.games.length } : null,
      binding: f && c ? { contextId: c.contextId, matchId: c.matchId, team: c.team, hero: c.hero } : null,
      own: f?.player ?? null, reported: f ? { mainItems: f.itemsReported, abilities: f.abilitiesReported } : null, confirmation: conf,
      timing: f ? { receivedAt: f.receivedAt, sourceAt: f.sourceAt, expiresAt: f.expiresAt,
        factsExpireAt: this.batch ? Math.min(f.expiresAt, this.batch.expiresAt) : null } : null,
      visible: batch ? { capturedAt: batch.capturedAt, clock: batch.clock, danger: batch.danger, targets: batch.targets } : null,
      observation, shadow, pathSafety: 'unknown' as const, action: null, gameControl: false as const,
    });
  }
}
