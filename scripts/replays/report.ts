import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { MAP } from '../../src/tactics/combat-data.ts';
import { frameAfter, lunaWindow, validateCampGeometry, validateTowers } from '../../src/replays/validation.ts';

const reference = JSON.parse(fs.readFileSync('data/dota2-map-reference.json', 'utf8'));
const ids = process.argv.filter(a => /^\d{10}$/.test(a)).map(Number);
if (!ids.length) throw new Error('Supply explicit downloaded match IDs (no unbounded crawling)');
const matches: any[] = [], previews: any[] = [];
const round = (n: number) => Math.round(n * 1000) / 1000;
const sourcePins = JSON.parse(fs.readFileSync('data/dota2-replay-source-pins.json', 'utf8'));
const steamInf = sourcePins.steamInf.text;
const pins = JSON.parse(fs.readFileSync('data/dota2-jungle-sources.json', 'utf8'));
const build = Number(steamInf.match(/^ServerVersion=(\d+)/m)?.[1]);
if (!build || createHash('sha256').update(steamInf).digest('hex') !== sourcePins.steamInf.sha256)
  throw new Error('Missing/mismatched pinned steam.inf server version');
for (const id of ids) {
  const bytes = fs.readFileSync(`.tmp/replays/${id}.facts.json`);
  const facts = JSON.parse(bytes.toString()), api = JSON.parse(fs.readFileSync(`.tmp/replays/${id}.opendota.json`, 'utf8'));
  const acquisition = JSON.parse(fs.readFileSync(`.tmp/replays/${id}.acquisition.json`, 'utf8'));
  if (facts.schemaVersion !== 4 || !facts.complete || facts.matchId !== id || api.match_id !== id ||
      facts.demSha256 !== acquisition.demSha256 || facts.gameBuild !== build)
    throw new Error('Replay schema/completeness/hash/build mismatch');
  const windowPatch = sourcePins.patchWindow;
  if (api.start_time < windowPatch.fromTimestamp || (windowPatch.untilTimestamp !== null && api.start_time >= windowPatch.untilTimestamp))
    throw new Error('Match outside pinned patch date window');
  const camps = validateCampGeometry(facts, reference), towers = validateTowers(facts, reference);
  const campOf = (u: any) => camps.rows.find((c: any) => c.spawnerIndex === (u.spawnerHandle & 0x3fff))?.id || null;
  const window = lunaWindow(facts, 600, 780);
  const luna = api.players.find((p: any) => p.hero_id === 48);
  if (!luna) throw new Error('Requested match has no Luna');
  const finalHeroes = facts.frames.at(-1).heroes;
  const parity = api.players.map((p: any) => {
    const playerId = p.player_slot < 128 ? p.player_slot : p.player_slot - 128 + 5;
    const replay = finalHeroes.find((h: any) => h.playerIdField === playerId);
    return { playerId, apiLastHits: p.last_hits, replayLastHits: replay?.economy?.m_iLastHitCount ?? null,
      matches: replay?.economy?.m_iLastHitCount === p.last_hits };
  });
  const at10 = frameAfter(facts, 600);
  const ancientCounts = camps.rows.filter((c: any) => c.initialType === 3).map((c: any) => ({
    campId: c.id, units: at10.units.filter((u: any) => campOf(u) === c.id).map((u: any) => ({
      name: u.unitName, health: u.health, position: u.position,
    })),
  }));
  const lane = facts.creatures.filter((c: any) => c.class === 'CDOTA_BaseNPC_Creep_Lane' &&
    c.spawnedGameTime != null && c.spawnedGameTime >= -0.01 && c.spawnedGameTime < 180);
  const waveTicks = [...new Set<number>(lane.map((c: any) => c.spawnedTick))].sort((a, b) => a - b);
  const waves = waveTicks.map(tick => {
    const units = lane.filter((c: any) => c.spawnedTick === tick), composition: Record<string, number> = {};
    for (const u of units) composition[u.unitName ?? 'unknown'] = (composition[u.unitName ?? 'unknown'] || 0) + 1;
    return { tick, gameTime: units[0].spawnedGameTime, count: units.length, composition };
  });
  // Exact combat-log death tick+definition join; ambiguous camp attribution
  // stays null. This is NOT proximity-based inference or a strategy classifier.
  const deaths = facts.lunaCombatLog.filter((e: any) => e.log_type === 'DEATH' &&
    e.attacker_name === 'npc_dota_hero_luna' && e.target_name.startsWith('npc_dota_neutral_'));
  const neutralEvents = deaths.map((e: any) => {
    const matches = facts.creatures.filter((c: any) => c.diedTick === e.tick && c.unitName === e.target_name);
    const campIds = [...new Set(matches.map(campOf))].filter(Boolean);
    return { tick: e.tick, gameTime: e.game_time_s, target: e.target_name,
      campId: campIds.length === 1 && matches.every((c: any) => campOf(c)) ? campIds[0] : null,
      attribution: campIds.length === 1 && matches.every((c: any) => campOf(c)) ? 'exact-tick-name-single-camp' : 'unresolved',
      attackerIsIllusion: e.attacker_is_illusion };
  });
  const moving = facts.heroPath.filter((h: any) => h.gameTime >= 600 && h.gameTime <= 780 && h.lifeState === 0 && h.position);
  let blocked = 0;
  for (const h of moving) {
    const g = MAP.grid, x = Math.floor((h.position.x - g.origin.x) / g.cellSize), y = Math.floor((h.position.y - g.origin.y) / g.cellSize);
    if (g.rows[y]?.[x] !== '1') blocked++;
  }
  const speeds: number[] = [];
  for (let i = 1; i < moving.length; i++) {
    const a = moving[i - 1], b = moving[i], dt = b.gameTime - a.gameTime;
    if (a.id !== b.id || dt <= 0 || dt > 0.25) continue;
    speeds.push(Math.hypot(a.position.x - b.position.x, a.position.y - b.position.y) / dt);
  }
  speeds.sort((a, b) => a - b);
  const neutralBaseSpeeds = [...new Set(facts.creatures.filter((u: any) => u.class.endsWith('_Neutral'))
    .map((u: any) => u.baseMoveSpeedField))].sort((a: any, b: any) => a - b);
  matches.push({
    matchId: id, player: luna.name || null, apiRankTier: luna.rank_tier ?? null,
    accountId: luna.account_id, league: api.league, teams: [api.radiant_team?.name, api.dire_team?.name],
    startTime: api.start_time, startIso: new Date(api.start_time * 1000).toISOString(), duration: facts.durationSeconds,
    gameBuild: facts.gameBuild, complete: facts.complete, acquisition, parser: facts.parser,
    factsSha256: createHash('sha256').update(bytes).digest('hex'), frames: facts.frames.length, lunaSamples: facts.heroPath.length,
    campGeometry: camps, towerGeometry: towers, initialLaneWaves: waves, lastHitParity: parity,
    observedAncientsAt10Minutes: ancientCounts,
    window10To13: window, neutralDeathEvents: neutralEvents,
    movement: {
      baseMoveSpeedFields: [...new Set(moving.map((h: any) => h.baseMoveSpeedField))],
      neutralBaseSpeedFields: neutralBaseSpeeds, laneBaseSpeedFields: [...new Set(lane.map((u: any) => u.baseMoveSpeedField))],
      laneAttackRangeFields: [...new Set(lane.map((u: any) => u.attackRangeField))],
      sampledDisplacementSpeed: { p50: round(speeds[Math.floor(speeds.length * 0.5)]),
        p95: round(speeds[Math.floor(speeds.length * 0.95)]), max: round(speeds.at(-1)!) },
      heroSamplesOnOldBlockedGrid: blocked, heroSamples: moving.length,
      interpretation: 'Displacement speed is not effective movement stat (stops, impulses, teleports, network intervals). Old blocked cells may be cut trees, inflation or dynamics; do not erase obstacles to force agreement.',
    },
  });
  const sampledFrames = Array.from({ length: 37 }, (_, i) => frameAfter(facts, 600 + 5 * i));
  const previewFrames = sampledFrames.map(f => ({
    tick: f.tick, time: f.gameTime,
    units: f.units.filter((u: any) => u.position).map((u: any) => ({
      id: u.id, name: u.unitName || u.name, x: u.position.x, y: u.position.y, team: u.team,
      kind: u.class.endsWith('_Tower') ? 'tower' : u.class.endsWith('_Neutral') ? 'neutral' : 'lane',
      campId: campOf(u), hp: u.health,
    })),
    heroes: f.heroes.filter((h: any) => h.position).map((h: any) => ({
      name: h.name, x: h.position.x, y: h.position.y, team: h.team, hp: h.health, level: h.level,
    })),
    luna: f.heroes.find((h: any) => h.name === 'npc_dota_hero_luna'),
  }));
  previews.push({ id, label: `${luna.name || '未认证选手'} · ${api.radiant_team?.name} vs ${api.dire_team?.name}`,
    frames: previewFrames, path: moving.map((h: any) => [h.gameTime, h.position.x, h.position.y]),
    scoreWindow: { neutralGold: window.neutralGold, laneGold: window.laneGold, elapsed: window.elapsed } });
}
const report = {
  version: 'replay-map-validation-1', date: '2026-11-15', status: 'partial-not-certified',
  patchEvidence: sourcePins.patchWindow,
  buildEvidence: { gameCommit: pins.gameCommit, serverBuild: build,
    source: `https://raw.githubusercontent.com/SteamDatabase/GameTracking-Dota2/${pins.gameCommit}/game/dota/steam.inf`,
    sha256: createHash('sha256').update(steamInf).digest('hex'), text: steamInf,
    note: 'Both replays equal the pinned game-data server build. 7.41f label additionally checked against Valve patch date range, not upload titles.' },
  matches, remaining: ['Dynamic tree cuts/regrowth', 'River volumes/current speed and direction', 'Tower attack/aggro simulation',
    'Effective movement with all modifiers and turn rates', 'Dynamic hull collision and creep aggro',
    'Neutral composition probabilities and flooded evolutions', 'Live player-visible observations, not all-map replay knowledge'],
};
function save(file: string, content: unknown) {
  const text = JSON.stringify(content) + '\n';
  if (process.argv.includes('--check')) {
    if (fs.readFileSync(file, 'utf8') !== text) throw new Error(`Generated evidence differs: ${file}`);
  } else fs.writeFileSync(file, text);
}
save('data/replay-map-validation.json', report);
save('data/replay-map-samples.json', { version: 1, informationScope: 'omniscient-replay-audit-only', matches: previews });
console.log(JSON.stringify(matches.map(m => ({
  id: m.matchId, player: m.player, camps: m.campGeometry.status, maxCampError: m.campGeometry.maxCenterError,
  maxBoxError: m.campGeometry.maxBoxError, towers: m.towerGeometry.status, maxTowerError: m.towerGeometry.maxPositionError,
  lastHitParity: m.lastHitParity.every((p: any) => p.matches), ancientCounts: m.observedAncientsAt10Minutes.map((c: any) => [c.campId, c.units.length]),
  windowGold: { lane: m.window10To13.laneGold, neutral: m.window10To13.neutralGold },
  neutralAttribution: [m.neutralDeathEvents.filter((e: any) => e.campId).length, m.neutralDeathEvents.length],
  movement: m.movement,
})), null, 2));
