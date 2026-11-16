import assert from 'node:assert/strict';
import fs from 'node:fs';
import { test } from 'node:test';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { replayUrl } from '../scripts/replays/download.ts';
import { validateCampGeometry, validateTowers, lunaWindow } from '../src/replays/validation.ts';
import { createSandboxServer } from '../src/sandbox/server.ts';

const read = (f: string) => JSON.parse(fs.readFileSync(new URL(`../data/${f}`, import.meta.url), 'utf8'));
const reference = read('dota2-map-reference.json');
const report = read('replay-map-validation.json');
const samples = read('replay-map-samples.json');

test('replay download rejects guessed/foreign origins, wrong match IDs, credentials and query strings', () => {
  const valid = 'http://replay273.valve.net/570/9090576268_1829811815.dem.bz2';
  assert.equal(replayUrl(valid, 9090576268).href, valid);
  for (const bad of [undefined, 'https://attacker.example/a', valid + '?x=1',
    valid.replace('valve.net', 'valve.net.attacker.example'), valid.replace('273.valve.net', '273.valve.net:8080'),
    valid.replace('http://', 'http://secret@'), valid.replace('9090576268_', '9090576269_')])
    assert.throws(() => replayUrl(bad, 9090576268));
});

test('reference contains distinct real lane directions, towers and static trees, not fake mirrored lanes', () => {
  assert.equal(reference.camps.length, 28);
  assert.equal(reference.camps.filter((c: any) => c.kind === 'ancient' && c.team === 'dire').length, 1);
  assert.equal(reference.towers.length, 22); assert.equal(reference.trees.length, 2475);
  assert.equal(reference.lanePaths.length, 6);
  const paths = reference.lanePaths.filter((l: any) => l.lane === 'bot');
  assert.notDeepEqual(paths[0].points, [...paths[1].points].reverse());
  assert.equal(reference.overview.coordinateSpan, 18944, 'Valve overview scale is 1024-based, not texture-width based');
  assert.equal(reference.sources.files.filter((f: any) => !f.upstreamManifestMatches).length, 3);
  const build = read('dota2-replay-source-pins.json');
  assert.equal(createHash('sha256').update(build.steamInf.text).digest('hex'), build.steamInf.sha256);
  assert.equal(build.steamInf.serverBuild, 6975);
});

test('all original replay camp centers/boxes, tower positions and build cross-checks are preserved', () => {
  assert.equal(report.status, 'partial-not-certified');
  assert.equal(report.matches.length, 3);
  for (const m of report.matches) {
    assert.equal(m.complete, true); assert.equal(m.gameBuild, report.buildEvidence.serverBuild);
    assert.equal(m.campGeometry.status, 'matched'); assert.equal(m.campGeometry.rows.length, 28);
    assert.ok(m.campGeometry.maxCenterError < 0.02); assert.ok(m.campGeometry.maxBoxError < 0.001);
    assert.equal(m.towerGeometry.status, 'matched'); assert.equal(m.towerGeometry.count, 22);
    assert.ok(m.towerGeometry.maxPositionError < 0.02);
    assert.equal(m.lastHitParity.every((p: any) => p.matches), true, 'terminal entity snapshot must match API counters');
    assert.ok(m.neutralDeathEvents.some((e: any) => e.attribution === 'unresolved'), 'ambiguous kills must not be invented');
    assert.ok(m.movement.heroSamplesOnOldBlockedGrid > 0, 'do not hide static/dynamic path disagreement');
  }
});

test('camp validation rejects a wrong axis, duplicate spawner and missing bounds, rather than repairing observations', () => {
  const c = reference.camps[0];
  const base = { camps: [{ id: c.id, spawnerIndex: 20, min: [c.bounds.min.x, c.bounds.min.y], max: [c.bounds.max.x, c.bounds.max.y] }],
    spawners: [{ index: 10, position: { x: c.x, y: c.y }, types: [{ type: ['small', 'medium', 'large', 'ancient'].indexOf(c.kind) }] }] };
  const ref = { camps: [c] };
  assert.equal(validateCampGeometry(base, ref).indexDivisor, 2);
  const bad = structuredClone(base); bad.spawners[0].position.y *= -1;
  assert.throws(() => validateCampGeometry(bad, ref));
  const missing = structuredClone(base) as any; missing.camps[0].min = null;
  assert.throws(() => validateCampGeometry(missing, ref));
  assert.throws(() => validateCampGeometry({ camps: [...base.camps, ...base.camps], spawners: [...base.spawners, ...base.spawners] }, { camps: [c, c] }));
});

test('two tier-4 towers can share a unit name; identity is the unique source entity ID', () => {
  const towers = reference.towers.filter((t: any) => t.name === 'npc_dota_badguys_tower4');
  assert.equal(towers.length, 2);
  const facts = { towers: towers.map((t: any) => ({ name: 'dota_badguys_tower4', position: { x: t.position[0], y: t.position[1] }, team: 3 })) };
  assert.equal(validateTowers(facts, { towers }).status, 'matched');
});

test('replay previews preserve observations, exact clocks and per-source counters without simulating new creeps', () => {
  assert.equal(samples.informationScope, 'omniscient-replay-audit-only');
  assert.equal(samples.matches.length, report.matches.length);
  for (const m of samples.matches) {
    assert.equal(m.frames.length, 37);
    assert.ok(m.frames.every((f: any) => f.luna.name === 'npc_dota_hero_luna'));
    assert.ok(m.frames.every((f: any) => new Set(f.units.map((u: any) => u.id)).size === f.units.length));
    const facts = { frames: m.frames.map((f: any) => ({ gameTime: f.time, tick: f.tick, heroes: [f.luna] })), postGameTick: Infinity };
    const w = lunaWindow(facts, m.frames[0].time, m.frames.at(-1).time);
    assert.equal(w.neutralGold, m.scoreWindow.neutralGold); assert.equal(w.laneGold, m.scoreWindow.laneGold);
    assert.ok(w.initialHero.items.some((i: any) => i.name?.startsWith('item_')));
  }
});

test('map audit endpoints require a local session, disclose only recorded previews, and keep advice blocked', async t => {
  const { server, token } = createSandboxServer();
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => new Promise<void>(resolve => server.close(() => resolve())));
  const address = server.address(); assert.ok(address && typeof address === 'object');
  const origin = `http://127.0.0.1:${address.port}`;
  assert.equal((await fetch(origin + '/api/map-audit')).status, 401);
  assert.equal((await fetch(origin + '/api/map-overview')).status, 401);
  const r = await fetch(origin + '/api/map-audit', { headers: { Authorization: `Bearer ${token}` } });
  assert.equal(r.status, 200);
  const result = await r.json() as any;
  assert.equal(result.samples.matches.length, 3);
  const state = await (await fetch(origin + '/api/state', { headers: { Authorization: `Bearer ${token}` } })).json() as any;
  assert.equal(state.plan, null); assert.equal(state.recommendationScope.status, 'blocked');
  const page = await (await fetch(origin + '/map-audit')).text();
  assert.match(page, /先核对地图/); assert.doesNotMatch(page, /<button id="farm"/);
});
