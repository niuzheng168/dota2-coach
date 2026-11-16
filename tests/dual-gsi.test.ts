import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';
import { spawnSync } from 'node:child_process';
import {
  capabilities, clockTransitions, delayStats, heroVisibility, inferOwnTeam, minimapComparison, ntpCorrection,
  positionCheck, readRecording, readScreenshots, summarize, type Packet,
} from '../scripts/dual-gsi/analyze.ts';

// Synthetic match: game second T starts at server time START + T*1000. X's clock runs 250 ms
// behind the NTP servers, Y's 400 ms ahead, and Y sees the game 1500 ms late.
const START = 1_794_800_000_000, X_OFFSET = 250, Y_OFFSET = -400, LIVE_DELAY = 1500, TV_DELAY = 120_000;
const MATCH = '8123456789', BOM = String.fromCharCode(0xfeff);
const pos = (t: number) => ({ x: -6000 + t * 50, y: -5000 + t * 30 });
const map = (clock: number) => ({ matchid: MATCH, clock_time: clock, game_state: 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS', paused: false });

function xBody(gameMs: number) {
  const clock = Math.floor(gameMs / 1000), p = pos(gameMs / 1000);
  return { map: map(clock), player: { team_name: 'radiant', gold: 600 }, hero: { name: 'npc_dota_hero_luna', xpos: p.x, ypos: p.y },
    items: { slot0: { name: 'item_power_treads' }, slot1: { name: 'empty' } }, abilities: { ability0: { name: 'luna_lucent_beam' } },
    buildings: { radiant: { dota_goodguys_tower1_bot: { health: 1800 } } } };
}
function yTeamBody(gameMs: number) {
  const clock = Math.floor(gameMs / 1000), p = pos(gameMs / 1000);
  const minimap: Record<string, object> = {
    o0: { unitname: 'npc_dota_hero_luna', team: 2, xpos: p.x, ypos: p.y, visionrange: 1800, image: 'minimap_herocircle_self' },
    o1: { unitname: 'npc_dota_creep_goodguys_melee', team: 2, xpos: 0, ypos: 0, visionrange: 750 },
  };
  if (clock % 2 === 0) minimap.o2 = { unitname: 'npc_dota_hero_axe', team: 3, xpos: 900, ypos: 900, visionrange: 1800, image: 'minimap_enemyicon' };
  if (clock % 4 === 0) minimap.o3 = { unitname: 'npc_dota_hero_axe', team: 3, xpos: -3962, ypos: 461, visionrange: 0, image: 'minimap_enemyicon' };
  return { map: map(clock), player: { team_name: 'spectator', player_slot: 11, team_slot: -1, gold: 0 }, hero: { id: 0 }, items: {}, abilities: {},
    buildings: { radiant: { dota_goodguys_tower1_bot: { health: 1800 } } }, minimap,
    events: clock === 30 ? [{ game_time: 30, event_type: 'bounty_rune_pickup', bounty_value: 90 }] : [] };
}
function yGroupsBody(gameMs: number) {
  const clock = Math.floor(gameMs / 1000);
  const players = (base: number) => Object.fromEntries([0, 1, 2, 3, 4].map(i => [`player${base + i}`, { name: `npc_dota_hero_${base + i}` }]));
  return { map: map(clock), player: { team2: players(0), team3: players(5) }, hero: { team2: players(0), team3: players(5) },
    items: { team2: players(0), team3: players(5) }, abilities: {}, buildings: { radiant: {}, dire: {} } };
}

/** Packets every 200 ms from serverFrom to serverTo; body built from the game time the machine sees. */
function stream(serverFrom: number, serverTo: number, phase: number, delay: number, offset: number, build: (gameMs: number) => object) {
  const out: { seq: number; recvMs: number; body: object }[] = [];
  for (let s = serverFrom + phase; s < serverTo; s += 200) {
    const gameMs = s - delay - START;
    if (gameMs >= 0) out.push({ seq: out.length + 1, recvMs: s - offset, body: build(gameMs) });
  }
  return out;
}
const packets = (records: { seq: number; recvMs: number; body: object }[]): Packet[] => records.map(r => summarize(r.seq, r.recvMs, r.body));
const xRecords = stream(START, START + 200_000, 37, 0, X_OFFSET, xBody);
const yRecords = [
  ...stream(START, START + 100_000, 113, LIVE_DELAY, Y_OFFSET, yTeamBody),
  // The tester switches to ordinary delayed spectating near the end.
  ...stream(START + 160_000, START + 200_000, 113, TV_DELAY, Y_OFFSET, yGroupsBody),
].map((r, i) => ({ ...r, seq: i + 1 }));
const xNtp = [{ server: 'a', localMs: START, offsetMs: X_OFFSET, rttMs: 30, phase: 'start' }, { server: 'b', localMs: START, offsetMs: X_OFFSET + 8, rttMs: 40, phase: 'start' }];
const yNtp = [{ server: 'a', localMs: START, offsetMs: Y_OFFSET, rttMs: 35, phase: 'start' }, { server: 'b', localMs: START, offsetMs: Y_OFFSET + 8, rttMs: 45, phase: 'start' },
  { server: 'a', localMs: START, offsetMs: 5000, rttMs: 900, phase: 'periodic' }];

test('summarize separates player-mode, team-vision and full-spectator layouts', () => {
  const x = summarize(1, 0, xBody(30_500)), yTeam = summarize(1, 0, yTeamBody(30_500)), yFull = summarize(1, 0, yGroupsBody(30_500));
  assert.equal(x.mode, 'flat'); assert.equal(x.hero?.name, 'npc_dota_hero_luna'); assert.equal(x.items, 1); assert.equal(x.abilities, 1);
  assert.equal(yTeam.mode, 'flat'); assert.equal(yTeam.hero, null); assert.equal(yTeam.items, 0); assert.equal(yTeam.minimapHeroes.length, 2);
  assert.equal(yTeam.events[0].type, 'bounty_rune_pickup');
  assert.equal(yFull.mode, 'groups'); assert.equal(yFull.groupedPlayers, 10); assert.ok(yFull.nonEmpty.includes('hero'));
  assert.equal(x.matchId, MATCH);
  assert.equal(summarize(1, 0, { provider: { name: 'Dota 2' } }).mode, 'menu');
});

test('NTP correction uses common servers and ignores long round trips', () => {
  const c = ntpCorrection(xNtp, yNtp);
  assert.equal(c.method, 'common-ntp-servers');
  assert.equal(c.correctionMs, Y_OFFSET - X_OFFSET);
  assert.equal(ntpCorrection([], yNtp).method, 'uncorrected');
});

test('clock transitions recover the injected live delay per Y mode', () => {
  const x = packets(xRecords), y = packets(yRecords);
  const tx = clockTransitions(x), correction = ntpCorrection(xNtp, yNtp).correctionMs;
  const live = delayStats(tx, clockTransitions(y.filter(p => p.mode === 'flat')), correction);
  assert.ok(live.n > 80, `samples ${live.n}`);
  assert.ok(Math.abs(live.median! - LIVE_DELAY) <= 150, `live median ${live.median}`);
  const raw = delayStats(tx, clockTransitions(y.filter(p => p.mode === 'flat')), 0);
  assert.ok(Math.abs(raw.median! - (LIVE_DELAY + X_OFFSET - Y_OFFSET)) <= 150, 'uncorrected delay includes the clock skew');
  const tv = delayStats(tx, clockTransitions(y.filter(p => p.mode === 'groups')), correction);
  assert.ok(Math.abs(tv.median! - TV_DELAY) <= 150, `tv median ${tv.median}`);
});

test('transitions skip pauses and jumps instead of inventing samples', () => {
  const mk = (seq: number, recvMs: number, clock: number, paused = false) => summarize(seq, recvMs, { map: { ...map(clock), paused } });
  const t = clockTransitions([mk(1, 0, 10), mk(2, 1000, 11), mk(3, 2000, 12, true), mk(4, 3000, 13), mk(5, 4000, 15), mk(6, 5000, 16)]);
  assert.deepEqual([...t.keys()], [11, 16]);
});

test('position check is closest at zero lag and minimap stats reflect team vision', () => {
  const x = packets(xRecords), y = packets(yRecords.filter(r => (r.body as { player: { team_name?: string } }).player.team_name === 'spectator'));
  const rows = positionCheck(clockTransitions(x), clockTransitions(y));
  const best = rows.filter(r => r.n > 0).sort((a, b) => a.medianDistance! - b.medianDistance!)[0];
  assert.equal(best.lagSeconds, 0);
  assert.ok(best.medianDistance! < 20);
  assert.equal(minimapComparison(clockTransitions(x), clockTransitions(y)).compared, 0, 'X has no minimap here');
  assert.equal(inferOwnTeam(y), 2);
  const vis = heroVisibility(y, 2);
  assert.equal(vis.heroes.find(h => h.name === 'luna')?.presentPct, 100);
  assert.ok(Math.abs(vis.heroes.find(h => h.name === 'axe')!.presentPct! - 50) < 3);
  assert.ok(vis.duplicatePacketsPct! > 20 && vis.duplicatePacketsPct! < 30);
  const cx = capabilities(x), cy = capabilities(y);
  assert.equal(cx.heroPct, 100); assert.equal(cx.itemsPct, 100); assert.equal(cx.minimapMedian, 0);
  assert.equal(cy.heroPct, 0); assert.equal(cy.itemsPct, 0); assert.equal(cy.groupedPct, 0); assert.deepEqual(cy.buildings, ['radiant']);
  assert.equal(cy.eventTypes.bounty_rune_pickup, 1);
});

test('readRecording tolerates BOMs, broken lines and a cut-off gzip; CLI writes the report', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dual-gsi-'));
  const write = (dir: string, records: object[], ntp: object[], gzip: boolean) => {
    fs.mkdirSync(dir, { recursive: true });
    const text = records.map(r => JSON.stringify(r)).join('\n') + '\n{"seq":999,"recvMs":1,"body":{"map":';
    if (gzip) { const gz = zlib.gzipSync(text); fs.writeFileSync(path.join(dir, 'gsi.jsonl.gz'), gz.subarray(0, gz.length - 50)); }
    else fs.writeFileSync(path.join(dir, 'gsi.jsonl'), text);
    fs.writeFileSync(path.join(dir, 'ntp.jsonl'), BOM + ntp.map(s => JSON.stringify(s)).join('\n') + '\n{"server":"c","error":"timeout"}\n');
    fs.writeFileSync(path.join(dir, 'session.json'), BOM + '{"role":"probe"}');
  };
  const xDir = path.join(root, 'X'), yDir = path.join(root, 'Y'), out = path.join(root, 'report');
  write(xDir, xRecords, xNtp, true); write(yDir, yRecords, yNtp, false);
  // Steam F12 screenshots are named by local time; one is from an unrelated older session.
  const shotsDir = path.join(root, 'steam-screenshots'), at = new Date(yRecords[50].recvMs), p2 = (n: number) => String(n).padStart(2, '0');
  fs.mkdirSync(shotsDir);
  fs.writeFileSync(path.join(shotsDir, `${at.getFullYear()}${p2(at.getMonth() + 1)}${p2(at.getDate())}${p2(at.getHours())}${p2(at.getMinutes())}${p2(at.getSeconds())}_1.jpg`), '');
  fs.writeFileSync(path.join(shotsDir, '20200101120000_1.jpg'), '');
  return (async () => {
    const x = await readRecording(xDir), y = await readRecording(yDir);
    assert.ok(x.packets.length > xRecords.length - 20, `x packets ${x.packets.length}`);
    assert.equal(y.packets.length, yRecords.length); assert.equal(y.badLines, 1);
    assert.equal(y.ntp.length, 3); assert.equal(y.session?.role, 'probe');
    const shots = readScreenshots(shotsDir, y.packets[0].recvMs, y.packets.at(-1)!.recvMs);
    assert.equal(shots.length, 1); assert.ok(Math.abs(shots[0].ms - yRecords[50].recvMs) < 1000);
    const run = spawnSync(process.execPath, ['scripts/dual-gsi/compare.ts', '--x', xDir, '--y', yDir, '--y-shots', shotsDir, '--out', out], { encoding: 'utf8' });
    assert.equal(run.status, 0, run.stderr);
    const report = JSON.parse(fs.readFileSync(path.join(out, 'report.json'), 'utf8'));
    assert.equal(report.sameMatch, 'yes');
    assert.ok(Math.abs(report.delayMs.flat.median - LIVE_DELAY) <= 150);
    assert.ok(Math.abs(report.delayMs.groups.median - TV_DELAY) <= 150);
    assert.match(fs.readFileSync(path.join(out, 'report.md'), 'utf8'), /Y 相对 X 的延迟/);
    const csv = fs.readFileSync(path.join(out, 'y-screenshots.csv'), 'utf8').trim().split('\n');
    assert.equal(csv.length, 2); assert.match(csv[1], /\d{14}_1\.jpg,.*,flat,/);
    fs.rmSync(root, { recursive: true, force: true });
  })();
});
