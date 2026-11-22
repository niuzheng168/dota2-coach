import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { bridge742Archive, BridgeFault, FarmShadowBridge, GSI_TTL_MS } from '../src/tactics/bridge-742.ts';
import { confirmation, playerPacket, startingMs, visible } from './farm-shadow-bridge-fixtures.ts';

const archive = bridge742Archive(JSON.parse(readFileSync(new URL('../data/farming-742-routes.json', import.meta.url), 'utf8')));
function trial(exercise: 'rehearsal' | 'custom-lobby' = 'rehearsal') {
  let now = startingMs;
  const b = new FarmShadowBridge(exercise, () => now, archive);
  b.ingest(playerPacket(now)); b.confirm(confirmation(b.state(), now)); b.observe(visible(b.state(), ++now));
  return { b, get now() { return now; }, tick(ms = 1) { return now += ms; } };
}
const fault = (fn: () => unknown, code: string) => assert.throws(fn, (e: unknown) => e instanceof BridgeFault && e.code === code);

test('only the visible intersection reaches the operator; inventory is positive main slots, not backpack/neutral or missing cooldowns', () => {
  const { b, now } = trial();
  const p = playerPacket(now);
  b.ingest({ ...p, minimap: { points: ['hidden-minimap'] }, previously: { hero: { xpos: 9999 }, items: { slot0: { name: 'private-old-item' } } },
    auth: { token: 'private-gsi-token' } });
  const s = b.state(), o = s.observation!;
  assert.equal(o.informationScope, 'player-visible'); assert.equal(o.hero, 'luna'); assert.equal(o.team, 'radiant');
  assert.equal(o.patch, '7.42'); assert.equal(o.capturedAtMs, p.provider.timestamp * 1000);
  assert.deepEqual(o.self.position, { x: 1100, y: -5400 });
  assert.deepEqual(o.self.itemsSeen, [{ itemId: 'power_treads', seenAtSec: 660, matchId: '742000001' }]);
  assert.deepEqual(s.own?.abilities, [{ name: 'luna_lucent_beam', level: 2, cooldown: 4 }]);
  assert.deepEqual(o.visibleTargets.map(t => [t.targetId, t.source, t.state]), [
    ['lane:bot', 'manual-confirmed', 'present'], ['good_1', 'manual-confirmed', 'empty'] ]);
  assert.equal(s.shadow?.status, 'observed'); assert.deepEqual(s.shadow.candidates.map(t => t.targetId), ['lane:bot']);
  assert.equal(s.shadow.evidence.scope, 'historical-only'); assert.equal(s.shadow.liveActivation, 'disabled');
  assert.equal(s.pathSafety, 'unknown'); assert.equal(s.action, null); assert.equal(s.gameControl, false);
  assert.doesNotMatch(JSON.stringify(s), /private-|hidden-minimap|worldSnapshots|item_bfury|9999/);
});

test('operator binding is required, practice mode cannot be claimed by GSI, patch/start are not inferred from timestamps', () => {
  let now = startingMs;
  const b = new FarmShadowBridge('custom-lobby', () => now, archive), p = playerPacket();
  b.ingest({ ...p, provider: { ...p.provider, timestamp: String(p.provider.timestamp), version: 6986 } });
  assert.equal(b.state().status.code, 'CONFIRM_CONTEXT');
  assert.equal(b.state().observation, null); fault(() => b.observe(visible(b.state(), now)), 'CONFIRM_FIRST');
  const c = () => confirmation(b.state(), ++now);
  fault(() => b.confirm({ ...c(), exercise: 'rehearsal' }), 'EXERCISE_ONLY');
  b.confirm({ ...c(), build: null }); b.observe(visible(b.state(), ++now));
  assert.equal(b.state().status.code, 'PATCH_UNKNOWN'); assert.equal(b.state().shadow, null);
  b.confirm({ ...c(), build: 6975 }); assert.equal(b.state().confirmation?.patch, null);
  b.confirm({ ...c(), matchStartUnix: null }); b.observe(visible(b.state(), ++now));
  assert.equal(b.state().status.code, 'START_UNKNOWN'); assert.equal(b.state().shadow, null);
  fault(() => b.confirm({ ...c(), matchStartUnix: Math.floor(now / 1000) }), 'MATCH_START');
  fault(() => b.confirm({ ...c(), matchStartUnix: archive.patchFrom - 1 }), 'MATCH_START');
  assert.equal(b.state().confirmation, null);
  fault(() => b.ingest({ ...p, map: { ...p.map, lobby_type: 7 } }), 'NOT_CUSTOM');
  assert.equal(b.state().own, null);
});

test('matched rehearsal metadata needs the actual focal hero + side; it never imports that match’s trace or targets', () => {
  const { b, now } = trial();
  const p = playerPacket(now, '9098402606', 700, 'radiant', 'troll_warlord');
  b.ingest(p);
  const c = { ...confirmation(b.state(), now), build: null, matchStartUnix: null };
  b.confirm(c);
  assert.deepEqual(b.state().confirmation, { build: 6986, patch: '7.42',
    matchStartUnix: archive.index.games.find(g => g.id === p.map.matchid)!.start, metadataSource: 'matched-archive', checkedAt: now });
  assert.deepEqual(b.state().observation?.visibleTargets, []);
  assert.deepEqual(b.state().observation?.self.position, { x: p.hero.xpos, y: p.hero.ypos });
  fault(() => b.confirm({ ...c, capturedAt: now + 1, build: 6983 }), 'MATCH_CONFLICT');
  b.ingest({ ...p, player: { ...p.player, team_name: 'dire' } });
  fault(() => b.confirm({ ...confirmation(b.state(), now), build: null, matchStartUnix: null }), 'MATCH_CONFLICT');
});

test('only a flat active own POV; spectators, hidden envelopes, invalid clocks, incomplete/disabled/dead/paused/ended self revoke', () => {
  const p = playerPacket();
  const invalid: [unknown, string][] = [
    [{ ...p, hero: { ...p.hero, team2: {} } }, 'NOT_PLAYER_GSI'],
    [{ ...p, added: { items: { team3: { player7: {} } } } }, 'NOT_PLAYER_GSI'],
    [{ ...p, allplayers: {} }, 'NOT_PLAYER_GSI'], [{ ...p, worldSnapshots: [] }, 'NOT_PLAYER_GSI'],
    [{ ...p, player: { ...p.player, team_name: 'spectator', team: 2 } }, 'TEAM_UNKNOWN'],
    [{ ...p, map: { ...p.map, is_spectator: true } }, 'NOT_PLAYER_GSI'],
    [{ ...p, hero: { ...p.hero, team_name: 'dire' } }, 'TEAM_UNKNOWN'],
    [{ ...p, player: { ...p.player, team: 3 } }, 'TEAM_UNKNOWN'],
    [{ ...p, provider: { ...p.provider, appid: 730 } }, 'NOT_DOTA_GSI'],
    [{ ...p, provider: { version: 48, name: 'Dota 2', appid: 570 } }, 'SOURCE_TIME'],
    [{ ...p, provider: { ...p.provider, timestamp: p.provider.timestamp - 6 } }, 'SOURCE_TIME'],
    [{ ...p, provider: { ...p.provider, timestamp: p.provider.timestamp + 1 } }, 'SOURCE_TIME'],
    [{ ...p, map: { ...p.map, matchid: '0' } }, 'MATCH_UNKNOWN'],
    [{ ...p, map: { ...p.map, matchid: 2 ** 54 } }, 'MATCH_UNKNOWN'],
    [{ ...p, map: { ...p.map, clock_time: '660' } }, 'MISSING_SELF'],
    [{ ...p, hero: { ...p.hero, xpos: 20000 } }, 'MISSING_SELF'],
    [{ ...p, hero: { ...p.hero, alive: false } }, 'DEAD'],
    [{ ...p, hero: { ...p.hero, hexed: true } }, 'CANNOT_ACT'],
    [{ ...p, map: { ...p.map, paused: true } }, 'PAUSED'],
    [{ ...p, map: { ...p.map, game_state: 'DOTA_GAMERULES_STATE_POST_GAME' } }, 'NOT_PLAYING'],
    [{ ...p, player: { ...p.player, activity: 'menu' } }, 'NOT_PLAYING'],
  ];
  for (const [input, code] of invalid) {
    const { b } = trial(); fault(() => b.ingest(input), code);
    const s = b.state(); assert.equal(s.status.code, code);
    assert.deepEqual([s.own, s.binding, s.visible, s.confirmation, s.shadow, s.action], Array(6).fill(null));
  }
});

test('missing status/inventory is unknown, not actionable or remembered; explicit missing sections stay absent from the shadow input', () => {
  const t = trial(), p = playerPacket(t.now);
  const { stunned: _stun, ...unknownStun } = p.hero;
  const { items: _i, abilities: _a, ...noLoadout } = p;
  t.b.ingest({ ...noLoadout, hero: unknownStun }); t.b.confirm(confirmation(t.b.state(), t.tick()));
  t.b.observe(visible(t.b.state(), t.tick()));
  const s = t.b.state();
  assert.equal(s.status.code, 'SELF_STATUS_UNKNOWN'); assert.equal(s.shadow, null); assert.equal(s.own?.canAct, false);
  assert.equal(s.observation?.self.canAct, undefined); assert.equal(s.observation?.self.itemsSeen, undefined);
  assert.deepEqual(s.reported, { mainItems: false, abilities: false });
  t.b.ingest(p); assert.equal(t.b.state().visible, null); assert.equal(t.b.state().confirmation, null);
});

test('match, hero, team, rewind and forward seek each rotate context; old manual writes and retired matches cannot revive old inputs', () => {
  for (const p of [playerPacket(startingMs, '742000002'), playerPacket(startingMs, '742000001', 660, 'dire'),
    playerPacket(startingMs, '742000001', 660, 'radiant', 'troll_warlord'),
    playerPacket(startingMs, '742000001', 640), playerPacket(startingMs, '742000001', 950)]) {
    const t = trial(), old = t.b.state();
    t.b.ingest(p);
    assert.notEqual(t.b.state().binding?.contextId, old.binding?.contextId); assert.equal(t.b.state().visible, null);
    assert.equal(t.b.state().shadow, null); assert.equal(t.b.state().confirmation, null);
    fault(() => t.b.confirm(confirmation(old, t.tick())), 'CONTEXT_MISMATCH');
    fault(() => t.b.observe(visible(old, t.tick())), 'CONTEXT_MISMATCH');
    if (p.map.matchid === '742000002') {
      fault(() => t.b.ingest(playerPacket(t.now)), 'RETIRED_MATCH'); assert.equal(t.b.state().binding?.matchId, '742000002');
    }
  }
});

test('no late GSI refresh, no wall-time lease extension by a duplicate, no eternal heartbeats on a frozen game clock', () => {
  const t = trial(), t0 = t.b.state().timing!;
  t.tick(1000); t.b.ingest(playerPacket(t.now, '742000001', 661));
  const newer = t.b.state();
  fault(() => t.b.ingest(playerPacket(startingMs)), 'OUT_OF_ORDER');
  assert.deepEqual(t.b.state().timing, newer.timing);
  assert.notEqual(t0.sourceAt, newer.timing?.sourceAt);
  t.tick(1500); t.b.ingest(playerPacket(startingMs + 1000, '742000001', 661));
  assert.equal(t.b.state().timing?.expiresAt, newer.timing?.expiresAt);
  t.tick(2000); t.b.ingest(playerPacket(t.now, '742000001', 661));
  t.tick(1501); fault(() => t.b.ingest(playerPacket(t.now, '742000001', 661)), 'FROZEN_CLOCK');
  assert.equal(t.b.state().shadow, null);
  t.b.ingest(playerPacket(t.now, '742000001', 662));
  assert.notEqual(t.b.state().binding?.contextId, newer.binding?.contextId);
  assert.equal(t.b.state().confirmation, null);
});

test('wall silence expires even with no further writes; STOP latches, including for subsequently valid input', () => {
  const t = trial(), previous = t.b.state();
  t.tick(GSI_TTL_MS); const s = t.b.state();
  assert.equal(s.status.code, 'STALE_GSI'); assert.equal(s.own, null); assert.equal(s.observation, null); assert.equal(s.shadow, null);
  fault(() => t.b.observe(visible(previous, t.now)), 'CONTEXT_MISMATCH');
  t.b.stop(); t.b.stop(); fault(() => t.b.ingest(playerPacket(t.now)), 'STOPPED');
  fault(() => t.b.confirm(confirmation(previous, t.now)), 'STOPPED');
  assert.equal(t.b.state().status.code, 'STOPPED');
});

test('a terminal match stays retired, an inactive second cannot be replayed as active, a capturedAt predating context cannot be retagged', () => {
  const t = trial(), p = playerPacket(t.tick(2000));
  fault(() => t.b.ingest({ ...p, map: { ...p.map, paused: true } }), 'PAUSED');
  fault(() => t.b.ingest(playerPacket(startingMs + 1000)), 'OUT_OF_ORDER');
  fault(() => t.b.ingest(p), 'OUT_OF_ORDER'); assert.equal(t.b.state().own, null);
  t.b.ingest(playerPacket(t.tick(1000), '742000001', 661));
  fault(() => t.b.confirm(confirmation(t.b.state(), t.now - 1)), 'OBSERVATION_TIME');
  const ended = playerPacket(t.tick(1000));
  fault(() => t.b.ingest({ ...ended, map: { ...ended.map, game_state: 'DOTA_GAMERULES_STATE_POST_GAME' } }), 'NOT_PLAYING');
  fault(() => t.b.ingest(playerPacket(t.tick(1000))), 'RETIRED_MATCH'); assert.equal(t.b.state().observation, null);
});

test('a build named in a packet, or in an archive row without its checked build witness, is not a known game patch', () => {
  const raw = JSON.parse(readFileSync(new URL('../data/farming-742-routes.json', import.meta.url), 'utf8'));
  raw.builds.find((m: { build: number }) => m.build === 6986).text += 'changed witness';
  const bad = bridge742Archive(raw);
  assert.equal(bad.matchedBuilds.has(6986), false);
  const b = new FarmShadowBridge('rehearsal', () => startingMs, bad);
  const p = playerPacket();
  b.ingest({ ...p, provider: { ...p.provider, version: 6986 } });
  b.confirm(confirmation(b.state(), startingMs));
  assert.equal(b.state().status.code, 'PATCH_UNKNOWN'); assert.equal(b.state().shadow, null);
});

test('manual writes are strictly scoped and ordered: no client-supplied provenance, area certificates, future game clock or stale wall capture', () => {
  const t = trial(), s = t.b.state(), v = visible(s, t.tick());
  for (const change of [{ matchId: '0' }, { team: 'dire' }, { hero: 'npc_dota_hero_axe' }, { contextId: 'old' }])
    fault(() => t.b.observe({ ...v, ...change }), 'CONTEXT_MISMATCH');
  for (const change of [{ clock: 661 }, { clock: 656 }, { capturedAt: t.now + 501 }, { capturedAt: t.now - GSI_TTL_MS }])
    fault(() => t.b.observe({ ...v, ...change }), 'OBSERVATION_TIME');
  for (const change of [{ informationScope: 'player-visible' }, { pathSafe: true }, { worldSnapshots: [] }, { source: 'minimap' }])
    fault(() => t.b.observe({ ...v, ...change }), 'SCHEMA');
  t.b.observe(v); fault(() => t.b.observe(v), 'OBSERVATION_ORDER');
  const bad = { ...v, capturedAt: t.tick() };
  fault(() => t.b.observe({ ...bad, targets: [{ ...v.targets[0], source: 'screen' }] }), 'SCHEMA');
  assert.equal(t.b.state().shadow, null);
  fault(() => t.b.observe(v), 'OBSERVATION_ORDER'); // delayed valid input cannot resurrect a withdrawn prior sighting
  fault(() => t.b.observe({ ...bad, capturedAt: t.tick(), targets: [v.targets[0], v.targets[0]] }), 'VISIBLE_SCHEMA');
  fault(() => t.b.observe({ ...bad, capturedAt: t.tick(), visibleNow: false }), 'VISIBLE_SCHEMA');
  fault(() => t.b.observe({ ...bad, capturedAt: t.tick(), targets: Array(33).fill(v.targets[0]) }), 'VISIBLE_SCHEMA');
});

test('unknown area, enemy camp, unknown/empty state, danger and explicit withdrawal never synthesize actions or safe control', () => {
  const t = trial();
  for (const [targetId, state, area] of [['lane:bot', 'present', 'unknown'], ['good_1', 'present', 'enemy'],
    ['evil_1', 'present', 'own'], ['lane:bot', 'empty', 'own'], ['good_1', 'unknown', 'own']]) {
    t.b.observe({ ...visible(t.b.state(), t.tick()), targets: [{ targetId, state, area }] });
    const s = t.b.state();
    assert.equal(s.visible?.targets[0].state, state); assert.equal(s.visible?.targets[0].area, area);
    assert.equal(s.shadow?.status, 'abstain'); assert.deepEqual(s.shadow.candidates, []); assert.equal(s.action, null);
  }
  const v = visible(t.b.state(), t.tick()); t.b.observe(v);
  t.b.observe({ ...v, danger: 'seen', targets: [] }); // even a same-millisecond danger can revoke, never promote
  assert.equal(t.b.state().shadow?.status, 'cancelled'); assert.deepEqual(t.b.state().shadow?.candidates, []);
  t.b.observe({ ...v, capturedAt: t.tick(), danger: 'unknown' }); assert.equal(t.b.state().shadow, null);
  assert.equal(t.b.state().observation?.cancelled, true, 'standalone shadow consumer also sees the hold');
  t.b.observe({ ...v, capturedAt: t.tick(), targets: [] }); assert.equal(t.b.state().shadow?.status, 'abstain');
  assert.equal(t.b.state().pathSafety, 'unknown');
});

test('sightings expire independently by game seconds or wall milliseconds while own GSI continues', () => {
  const t = trial();
  t.tick(2000); t.b.ingest(playerPacket(t.now, '742000001', 663)); assert.ok(t.b.state().visible);
  t.tick(700); t.b.ingest(playerPacket(t.now, '742000001', 664)); assert.equal(t.b.state().visible, null);
  assert.equal(t.b.state().own?.clock, 664); assert.equal(t.b.state().shadow, null);
  const t2 = trial();
  t2.tick(3000); t2.b.ingest(playerPacket(t2.now, '742000001', 661));
  t2.tick(2000); t2.b.ingest(playerPacket(t2.now, '742000001', 662));
  assert.ok(t2.b.state().own); assert.ok(t2.b.state().confirmation); assert.equal(t2.b.state().visible, null);
});
