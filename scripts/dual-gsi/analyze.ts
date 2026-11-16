import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import zlib from 'node:zlib';

// Offline comparison of gsi-probe.ps1 recordings from the playing machine (X) and the
// Dota Plus live spectator (Y). Observations only: nothing here feeds the live coach.
type Obj = Record<string, any>;
const RUNNING = new Set(['DOTA_GAMERULES_STATE_PRE_GAME', 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS']);
const TEAM_ID: Record<string, number> = { radiant: 2, dire: 3 };
const COMPONENTS = ['hero', 'items', 'abilities', 'minimap', 'buildings', 'events', 'roshan', 'couriers',
  'neutralitems', 'draft', 'wearables', 'league'] as const;

export type Mode = 'groups' | 'flat' | 'menu';
export type MinimapHero = { name: string; team: number | null; x: number; y: number; vision: number | null; image: string | null };
export type Packet = {
  seq: number; recvMs: number; clock: number | null; state: string | null; paused: boolean; matchId: string | null;
  mode: Mode; teamName: string | null;
  hero: { name: string; x: number | null; y: number | null } | null;
  items: number; abilities: number; groupedPlayers: number;
  minimapCount: number; minimapHeroes: MinimapHero[]; minimapKinds: Record<string, number>;
  buildings: string[]; nonEmpty: string[]; events: { time: number | null; type: string }[];
};
export type NtpSample = { server: string; localMs: number; offsetMs: number; rttMs: number; phase: string };
export type ProbeRecording = {
  dir: string; session: Obj | null; packets: Packet[]; ntp: NtpSample[]; badLines: number; truncated: boolean;
};
export type Stats = { n: number; median: number | null; p10: number | null; p90: number | null; min: number | null; max: number | null };

const obj = (v: unknown): Obj => v !== null && typeof v === 'object' && !Array.isArray(v) ? v as Obj : {};
const finite = (v: unknown): number | null => typeof v === 'number' && Number.isFinite(v) ? v : null;
const keys = (v: unknown) => Object.keys(obj(v));

export function quantile(sorted: number[], q: number): number | null {
  if (!sorted.length) return null;
  const at = (sorted.length - 1) * q, lo = Math.floor(at), hi = Math.ceil(at);
  return sorted[lo] + (sorted[hi] - sorted[lo]) * (at - lo);
}
export function stats(values: number[]): Stats {
  const s = values.filter(Number.isFinite).sort((a, b) => a - b);
  return { n: s.length, median: quantile(s, 0.5), p10: quantile(s, 0.1), p90: quantile(s, 0.9), min: s[0] ?? null, max: s.at(-1) ?? null };
}
const median = (values: number[]) => stats(values).median;
const pct = (part: number, whole: number) => whole ? Math.round(1000 * part / whole) / 10 : null;

/** Number of teamN.playerN entries, i.e. the full-spectator layout. */
function groupedCount(section: unknown): number {
  let n = 0;
  for (const team of keys(section).filter(k => /^team\d+$/.test(k))) n += keys(obj(section)[team]).filter(k => /^player\d+$/.test(k)).length;
  return n;
}

export function summarize(seq: number, recvMs: number, body: Obj): Packet {
  const map = obj(body.map), player = obj(body.player), hero = obj(body.hero), items = obj(body.items);
  const groupedPlayers = Math.max(groupedCount(body.player), groupedCount(body.hero), groupedCount(body.items), groupedCount(body.abilities));
  const minimap = Object.values(obj(body.minimap)).map(obj);
  const minimapKinds: Record<string, number> = {};
  const minimapHeroes: MinimapHero[] = [];
  for (const e of minimap) {
    const name = typeof e.unitname === 'string' ? e.unitname : '?';
    const kind = `${finite(e.team) ?? '?'}:${name}`;
    minimapKinds[kind] = (minimapKinds[kind] ?? 0) + 1;
    const x = finite(e.xpos), y = finite(e.ypos);
    if (name.startsWith('npc_dota_hero_') && x !== null && y !== null) {
      minimapHeroes.push({ name, team: finite(e.team), x, y, vision: finite(e.visionrange), image: typeof e.image === 'string' ? e.image : null });
    }
  }
  const matchId = typeof map.matchid === 'string' && /^[1-9]\d*$/.test(map.matchid) ? map.matchid : null;
  const nonEmpty = COMPONENTS.filter(c => Array.isArray(body[c]) ? body[c].length > 0
    : c === 'hero' ? typeof hero.name === 'string' && hero.name !== '' || groupedCount(hero) > 0 : keys(body[c]).length > 0);
  return {
    seq, recvMs, clock: finite(map.clock_time), state: typeof map.game_state === 'string' ? map.game_state : null,
    paused: map.paused === true, matchId,
    mode: groupedPlayers > 0 ? 'groups' : keys(map).length ? 'flat' : 'menu',
    teamName: typeof player.team_name === 'string' ? player.team_name : null,
    hero: typeof hero.name === 'string' && hero.name ? { name: hero.name, x: finite(hero.xpos), y: finite(hero.ypos) } : null,
    items: Object.entries(items).filter(([k, v]) => /^(slot|stash|teleport|neutral)\d+$/.test(k) && obj(v).name && obj(v).name !== 'empty').length,
    abilities: keys(body.abilities).filter(k => /^ability\d+$/.test(k)).length,
    groupedPlayers, minimapCount: minimap.length, minimapHeroes, minimapKinds,
    buildings: keys(body.buildings),
    nonEmpty,
    events: (Array.isArray(body.events) ? body.events : []).map(obj)
      .map(e => ({ time: finite(e.game_time), type: typeof e.event_type === 'string' ? e.event_type : '?' })),
  };
}

function readJsonFile(file: string): Obj | null {
  try { return JSON.parse(fs.readFileSync(file, 'utf8').trimStart()); } catch { return null; }
}

export async function readRecording(dir: string): Promise<ProbeRecording> {
  const plain = path.join(dir, 'gsi.jsonl'), gz = `${plain}.gz`;
  const file = fs.existsSync(plain) ? plain : fs.existsSync(gz) ? gz : null;
  if (!file) throw new Error(`No gsi.jsonl or gsi.jsonl.gz in ${dir}`);
  const packets: Packet[] = [];
  let badLines = 0, truncated = false;
  const raw = fs.createReadStream(file);
  // Z_SYNC_FLUSH lets a gzip cut off by a closed window still yield its complete lines.
  const input = file.endsWith('.gz') ? raw.pipe(zlib.createGunzip({ finishFlush: zlib.constants.Z_SYNC_FLUSH })) : raw;
  try {
    for await (const line of readline.createInterface({ input, crlfDelay: Infinity })) {
      if (!line.trim()) continue;
      try {
        const record = JSON.parse(line.trimStart());
        if (!Number.isFinite(record.recvMs) || typeof record.body !== 'object' || record.body === null) { badLines++; continue; }
        packets.push(summarize(record.seq, record.recvMs, record.body));
      } catch { badLines++; }
    }
  } catch { truncated = true; }
  const ntp: NtpSample[] = [];
  const ntpFile = path.join(dir, 'ntp.jsonl');
  if (fs.existsSync(ntpFile)) {
    for (const line of fs.readFileSync(ntpFile, 'utf8').split(/\r?\n/)) {
      try {
        const s = JSON.parse(line.trimStart());
        if (Number.isFinite(s.offsetMs) && Number.isFinite(s.rttMs)) ntp.push(s);
      } catch { /* blank or failed lookups */ }
    }
  }
  return { dir, session: readJsonFile(path.join(dir, 'session.json')), packets, ntp, badLines, truncated };
}

const STEAM_SHOT = /^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})_\d+\.(?:jpe?g|png)$/i;
/** Steam F12 names carry the local time to the second; other screenshots fall back to file mtime. */
export function readScreenshots(dir: string, fromMs: number, toMs: number) {
  return fs.readdirSync(dir).filter(f => /\.(?:jpe?g|png)$/i.test(f)).map(f => {
    const m = STEAM_SHOT.exec(f);
    return { file: path.join(dir, f), ms: m ? new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]).getTime() : fs.statSync(path.join(dir, f)).mtimeMs };
  }).filter(s => s.ms >= fromMs && s.ms <= toMs).sort((a, b) => a.ms - b.ms);
}

/** Relative clock correction: delay = (yLocal - xLocal) + correctionMs. */
export function ntpCorrection(x: NtpSample[], y: NtpSample[]) {
  const byServer = (samples: NtpSample[]) => {
    const out = new Map<string, number[]>();
    // Long round trips carry most of the asymmetry error.
    const rttCut = (median(samples.map(s => s.rttMs)) ?? 0) * 3 + 20;
    for (const s of samples) if (s.rttMs <= rttCut) out.set(s.server, [...(out.get(s.server) ?? []), s.offsetMs]);
    return out;
  };
  const xs = byServer(x), ys = byServer(y);
  const common = [...xs.keys()].filter(s => ys.has(s));
  const rtt = median([...x, ...y].map(s => s.rttMs));
  if (common.length) {
    const rel = common.map(s => median(ys.get(s)!)! - median(xs.get(s)!)!);
    return { correctionMs: median(rel)!, method: 'common-ntp-servers', servers: common, spreadMs: Math.max(...rel) - Math.min(...rel), rttMedianMs: rtt };
  }
  if (x.length && y.length) {
    return { correctionMs: median(y.map(s => s.offsetMs))! - median(x.map(s => s.offsetMs))!, method: 'different-ntp-servers', servers: [], spreadMs: null, rttMedianMs: rtt };
  }
  return { correctionMs: 0, method: 'uncorrected', servers: [], spreadMs: null, rttMedianMs: rtt };
}

/** First packet showing clock T right after T-1: the moment each machine learnt that second ticked. */
export function clockTransitions(packets: Packet[]): Map<number, Packet> {
  const out = new Map<number, Packet>();
  let prev: Packet | null = null;
  for (const p of packets) {
    if (p.clock === null || p.paused || !RUNNING.has(p.state ?? '')) { prev = null; continue; }
    if (prev && p.clock === prev.clock! + 1 && !out.has(p.clock)) out.set(p.clock, p);
    if (!prev || p.clock !== prev.clock) prev = p;
  }
  return out;
}

export function delayStats(x: Map<number, Packet>, y: Map<number, Packet>, correctionMs: number) {
  const samples = [...y].filter(([t]) => x.has(t)).sort((a, b) => a[0] - b[0])
    .map(([t, p]) => ({ clock: t, ms: p.recvMs - x.get(t)!.recvMs + correctionMs }));
  const third = Math.floor(samples.length / 3);
  return {
    ...stats(samples.map(s => s.ms)),
    clockFrom: samples[0]?.clock ?? null, clockTo: samples.at(-1)?.clock ?? null,
    firstThirdMedian: third ? median(samples.slice(0, third).map(s => s.ms)) : null,
    lastThirdMedian: third ? median(samples.slice(-third).map(s => s.ms)) : null,
  };
}

const teamOf = (name: string | null) => name ? TEAM_ID[name.toLowerCase()] ?? null : null;

/** Where Y's minimap puts A's hero vs X's own hero coordinates, at Y clock = X clock + lag. */
export function positionCheck(x: Map<number, Packet>, y: Map<number, Packet>) {
  const out: { lagSeconds: number; n: number; medianDistance: number | null }[] = [];
  for (let lag = -3; lag <= 3; lag++) {
    const d: number[] = [];
    for (const [t, px] of x) {
      const py = y.get(t + lag), team = teamOf(px.teamName);
      if (!py || !px.hero || px.hero.x === null || px.hero.y === null) continue;
      const mine = py.minimapHeroes.filter(h => h.name === px.hero!.name && (team === null || h.team === team));
      if (mine.length) d.push(Math.min(...mine.map(h => Math.hypot(h.x - px.hero!.x!, h.y - px.hero!.y!))));
    }
    out.push({ lagSeconds: lag, n: d.length, medianDistance: median(d) });
  }
  return out;
}

export function minimapComparison(x: Map<number, Packet>, y: Map<number, Packet>) {
  const jaccard: number[] = [];
  let heroSetsEqual = 0, compared = 0;
  for (const [t, px] of x) {
    const py = y.get(t);
    if (!py || !px.minimapCount || !py.minimapCount) continue;
    compared++;
    let both = 0, either = 0;
    for (const k of new Set([...Object.keys(px.minimapKinds), ...Object.keys(py.minimapKinds)])) {
      both += Math.min(px.minimapKinds[k] ?? 0, py.minimapKinds[k] ?? 0);
      either += Math.max(px.minimapKinds[k] ?? 0, py.minimapKinds[k] ?? 0);
    }
    jaccard.push(either ? both / either : 1);
    const heroes = (p: Packet) => [...new Set(p.minimapHeroes.map(h => `${h.team}:${h.name}`))].sort().join(',');
    if (heroes(px) === heroes(py)) heroSetsEqual++;
  }
  return { compared, kindJaccardMedian: median(jaccard), heroSetsEqualPct: pct(heroSetsEqual, compared) };
}

export function capabilities(packets: Packet[]) {
  const game = packets.filter(p => p.clock !== null && RUNNING.has(p.state ?? ''));
  const intervals = game.slice(1).map((p, i) => p.recvMs - game[i].recvMs).filter(ms => ms >= 0 && ms < 10_000);
  const count = (f: (p: Packet) => boolean) => pct(game.filter(f).length, game.length);
  const teamNames: Record<string, number> = {};
  for (const p of game) teamNames[p.teamName ?? '-'] = (teamNames[p.teamName ?? '-'] ?? 0) + 1;
  const nonEmptyPct: Record<string, number | null> = {};
  for (const c of COMPONENTS) nonEmptyPct[c] = count(p => p.nonEmpty.includes(c));
  const eventTypes: Record<string, number> = {};
  const seen = new Set<string>();
  for (const e of game.flatMap(p => p.events)) {
    const id = `${e.time}|${e.type}`;
    if (!seen.has(id)) { seen.add(id); eventTypes[e.type] = (eventTypes[e.type] ?? 0) + 1; }
  }
  return {
    packets: packets.length, inGamePackets: game.length,
    clockFrom: game[0]?.clock ?? null, clockTo: game.at(-1)?.clock ?? null,
    intervalMs: stats(intervals), teamNames,
    heroPct: count(p => p.hero !== null), heroSample: game.find(p => p.hero)?.hero?.name ?? null,
    heroPositionPct: count(p => p.hero?.x != null), itemsPct: count(p => p.items > 0), abilitiesPct: count(p => p.abilities > 0),
    groupedPct: count(p => p.groupedPlayers > 0), groupedPlayersMedian: median(game.map(p => p.groupedPlayers)),
    minimapMedian: median(game.map(p => p.minimapCount)),
    buildings: [...new Set(game.flatMap(p => p.buildings))].sort(), nonEmptyPct, eventTypes,
  };
}

export function heroVisibility(packets: Packet[], ownTeam: number | null) {
  const game = packets.filter(p => p.clock !== null && RUNNING.has(p.state ?? '') && p.minimapCount > 0);
  const seen: Record<string, number> = {};
  let duplicates = 0;
  for (const p of game) {
    const names = p.minimapHeroes.map(h => `${h.team}:${h.name}`);
    if (new Set(names).size < names.length) duplicates++;
    for (const n of new Set(names)) seen[n] = (seen[n] ?? 0) + 1;
  }
  const rows = Object.entries(seen).map(([k, n]) => {
    const [team, name] = [Number(k.split(':')[0]), k.slice(k.indexOf(':') + 1)];
    return { name: name.replace('npc_dota_hero_', ''), team, side: ownTeam === null ? '?' : team === ownTeam ? '己方' : '敌方', presentPct: pct(n, game.length) };
  }).sort((a, b) => a.side.localeCompare(b.side) || (b.presentPct ?? 0) - (a.presentPct ?? 0));
  return { packets: game.length, duplicatePacketsPct: pct(duplicates, game.length), heroes: rows };
}

/** The friend team in team-vision spectating: own buildings or the "self" circle. */
export function inferOwnTeam(packets: Packet[]): number | null {
  for (const p of packets) {
    const self = p.minimapHeroes.find(h => h.image === 'minimap_herocircle_self');
    if (self?.team) return self.team;
    if (p.buildings.length === 1) return teamOf(p.buildings[0]);
  }
  return null;
}

export function screenshotIndex(shots: { file: string; ms: number }[], packets: Packet[]) {
  return shots.map(s => {
    let lo = 0, hi = packets.length - 1;
    while (lo < hi) { const mid = (lo + hi) >> 1; if (packets[mid].recvMs < s.ms) lo = mid + 1; else hi = mid; }
    const near = [packets[lo - 1], packets[lo]].filter(Boolean).sort((a, b) => Math.abs(a.recvMs - s.ms) - Math.abs(b.recvMs - s.ms))[0];
    return { file: s.file, ms: s.ms, clock: near?.clock ?? null, mode: near?.mode ?? null, packetGapMs: near ? near.recvMs - s.ms : null };
  });
}
