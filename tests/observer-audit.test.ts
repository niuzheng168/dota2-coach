import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { AuditCatalog, auditConfig, sanitizePayload, startAuditReceiver, ingressWitness } from '../src/observer-audit.ts';

const token = 'a'.repeat(64);
test('observer audit redacts credentials and identifiers at all depths', () => {
  const clean = sanitizePayload({
    auth: { token }, provider: { steamid: 'secret-id', name: 'Dota 2', version: 6975 },
    map: { matchid: 'sensitive-match', clock_time: 13, game_state: 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS' },
    player: { team2: { player0: { name: 'private-user', steamid: 'secret-id', net_worth: 1234 } } },
    hero: { team2: { player0: { name: 'npc_dota_hero_luna', xpos: 0, alive: false, ypos: null } } },
    abilities: { team2: { player0: { ability0: { name: 'luna_lucent_beam', level: 4 } } } },
    previously: { auth: { token }, player: { name: 'old-name' } },
    unknown: { token, '76561198012345678': { value: 'unknown personal value' } },
  });
  const json = JSON.stringify(clean);
  for (const privateValue of [token, 'secret-id', 'sensitive-match', 'private-user', 'old-name', '76561198012345678', 'unknown personal value'])
    assert.equal(json.includes(privateValue), false);
  assert.match(json, /luna_lucent_beam/);
  assert.match(json, /"xpos":0/);
  assert.match(json, /"alive":false/);
  assert.match(json, /"ypos":null/);
  assert.equal(Object.hasOwn(clean, 'auth'), false);
});

test('observer catalog distinguishes two teams, missing/null, and current vs previous', () => {
  const catalog = new AuditCatalog();
  catalog.ingest(sanitizePayload({
    hero: {
      team2: { player0: { name: 'npc_dota_hero_luna', xpos: 1, health: 600 } },
      team3: { player5: { name: 'npc_dota_hero_axe', xpos: 9, health: null } },
    },
    player: { team2: { player0: { gold: 10 } }, team3: { player5: { gold: 20 } } },
    previously: { hero: { team3: { player5: { xpos: 9999 } } } },
  }), '2026-11-16T00:00:00Z');
  assert.equal((catalog.latest.actors as unknown[]).length, 2);
  catalog.ingest(sanitizePayload({ hero: { team2: { player0: { name: 'npc_dota_hero_luna', xpos: 2 } } } }),
    '2026-11-16T00:00:01Z');
  const summary = catalog.summary();
  assert.equal(summary.fields['hero.team3.player5.xpos'].absentPackets, 1);
  assert.equal(summary.fields['hero.team3.player5.xpos'].max, 9);
  assert.equal(summary.fields['hero.team3.player5.health'].nullPackets, 1);
  assert.equal(summary.fields['hero.team2.player0.xpos'].valueChanges, 1);
  assert.equal(summary.fogFilteringVerified, false);
  assert.equal((summary.latest.actors as unknown[]).length, 1);
  assert.equal(Object.keys(summary.fields).some(p => p.startsWith('previously')), false);
});

test('flat selected player is recorded but never certified as own or observer', () => {
  const catalog = new AuditCatalog();
  catalog.ingest(sanitizePayload({ hero: { name: 'npc_dota_hero_luna', xpos: 0 }, player: { gold: 5 } }), 'now');
  assert.equal((catalog.latest.actors as unknown[]).length, 1);
  assert.equal(catalog.summary().spectatorModeVerified, false);
  assert.equal(catalog.summary().requestedButNotObserved.includes('map'), true);
});

test('audit HTTP receiver authenticates and bounds bodies; only sanitized payload reaches callback', async () => {
  const seen: unknown[] = [];
  const { server, port, stats } = await startAuditReceiver({
    token, maxBodyBytes: 2048, minIntervalMs: 0, onPayload: (p) => { seen.push(p); },
  });
  try {
    assert.equal((server.address() as { address: string }).address, '127.0.0.1');
    const post = (body: string, extra = {}) => fetch(`http://127.0.0.1:${port}/gsi`, {
      method: 'POST', headers: { 'content-type': 'application/json', ...extra }, body,
    });
    assert.equal((await post('{}')).status, 401);
    assert.equal((await post(JSON.stringify({ auth: { token } }), { Origin: 'https://example.com' })).status, 403);
    assert.equal((await post('{')).status, 400);
    assert.equal((await post('x'.repeat(2049))).status, 413);
    assert.equal((await post(JSON.stringify({ auth: { token }, map: { clock_time: 12 } }))).status, 200);
    assert.equal(seen.length, 1);
    assert.equal(JSON.stringify(seen).includes(token), false);
    assert.equal(stats.accepted, 1);
  } finally { server.closeAllConnections(); await new Promise<void>(resolve => server.close(() => resolve())); }
});

test('audit config contains unique loopback receiver and explicit component probes', () => {
  const config = auditConfig(43210, token);
  assert.match(config, /http:\/\/127.0.0.1:43210\/gsi/);
  assert.match(config, /"buildings" "1"/);
  assert.match(config, /"minimap" "1"/);
  assert.match(config, /"league" "1"/);
  assert.match(config, /"auth" "1"/);
  assert.throws(() => auditConfig(0, token));
  assert.throws(() => auditConfig(3219, 'bad-token'));
});

test('complete ten-player observer hero/items/abilities survive redaction without flattening', () => {
  const raw: Record<string, any> = { auth: { token }, hero: {}, items: {}, abilities: {}, player: {} };
  for (const team of [2, 3]) {
    for (const section of ['hero', 'items', 'abilities', 'player']) raw[section][`team${team}`] = {};
    for (let i = 0; i < 5; i++) {
      const player = `player${(team - 2) * 5 + i}`;
      raw.hero[`team${team}`][player] = { id: 48, name: 'npc_dota_hero_luna', health: 700 + i, alive: true, mana: 300, level: 10 };
      raw.items[`team${team}`][player] = { slot0: { name: 'item_power_treads', cooldown: 0 }, slot1: { name: 'item_mask_of_madness', cooldown: 9 } };
      raw.abilities[`team${team}`][player] = { ability0: { name: 'luna_lucent_beam', level: 4, cooldown: 3 } };
      raw.player[`team${team}`][player] = { name: 'private-player', steamid: 'private-id', gold: 1000 + i, team_name: team === 2 ? 'radiant' : 'dire' };
    }
  }
  const clean = sanitizePayload(raw);
  for (const section of ['hero', 'items', 'abilities'])
    assert.deepEqual(JSON.parse(JSON.stringify(clean[section])), raw[section]);
  const witness = ingressWitness(raw, clean);
  assert.equal(witness.critical.hero.unchanged, true);
  assert.equal(witness.critical.items.unchanged, true);
  assert.deepEqual(witness.originalShapes.hero.teamGroups, [{ team: 'team2', playerObjects: 5 }, { team: 'team3', playerObjects: 5 }]);
  assert.equal(JSON.stringify({ clean, witness }).includes('private-player'), false);
  assert.equal(JSON.stringify(witness).includes(token), false);
});

test('ingress witness distinguishes received empty objects, missing fields and recorder losses', () => {
  const raw = { hero: { id: 0 }, items: {}, player: { team_name: 'spectator', gold: 0 }, couriers: {} };
  const witness = ingressWitness(raw, sanitizePayload(raw));
  assert.equal(witness.originalKnownValues.heroId, 0);
  assert.equal(witness.originalKnownValues.heroHealth, undefined);
  assert.equal(witness.originalShapes.items.kind, 'object');
  assert.equal(witness.originalShapes.items.entries, 0);
  assert.equal(witness.originalShapes.abilities.kind, 'missing');
  assert.equal(witness.critical.hero.unchanged, true);
  const lost = ingressWitness({ hero: { id: 48, health: 700 }, items: { slot0: { name: 'item_blink' } } },
    { hero: { id: 0 }, items: {} });
  assert.equal(lost.critical.hero.unchanged, false);
  assert.equal(lost.critical.items.unchanged, false);
  assert.equal(lost.originalKnownValues.heroHealth, 700);
  const historicalBug = ingressWitness({ couriers: {} }, { couriers: '[redacted]' });
  assert.equal(historicalBug.originalShapes.couriers.kind, 'object');
  assert.equal(historicalBug.recordedShapes.couriers.kind, 'string');
});

test('known gameplay event types and JSON-string numeric data survive without nicknames', () => {
  const clean = sanitizePayload({ events: [
    { event_type: 'tip', game_time: 123, data: JSON.stringify({ sender_player_id: 0, receiver_player_id: 6,
      tip_amount: 50, name: 'private-name', steamid: 'private-id', token }) },
    { event_type: 'roshan_killed', killer_player_id: 2 },
    { event_type: 'unknown-private-event', data: 'unstructured private chat' },
  ] });
  const json = JSON.stringify(clean);
  assert.match(json, /"event_type":"tip"/);
  assert.match(json, /"tip_amount":50/);
  assert.match(json, /"__auditSourceType":"json-string"/);
  assert.match(json, /roshan_killed/);
  for (const value of ['private-name', 'private-id', token, 'unknown-private-event', 'unstructured private chat'])
    assert.equal(json.includes(value), false);
});

test('HTTP callback gets independent ingress witness, not a reconstruction from cleaned data', async () => {
  let observed: any;
  const { server, port } = await startAuditReceiver({ token, onPayload: (payload, _at, ingress) => {
    observed = { payload, ingress };
  } });
  try {
    const r = await fetch(`http://127.0.0.1:${port}/gsi`, { method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ auth: { token }, hero: { id: 48, health: 777, note: 'private text' }, items: {} }) });
    assert.equal(r.status, 200);
    assert.equal(observed.ingress.originalKnownValues.heroHealth, 777);
    assert.equal(observed.ingress.critical.hero.unchanged, false);
    assert.equal(observed.payload.hero.note, '[redacted-string]');
    assert.equal(JSON.stringify(observed).includes(token), false);
    assert.equal(JSON.stringify(observed).includes('private text'), false);
  } finally { server.closeAllConnections(); await new Promise<void>(resolve => server.close(() => resolve())); }
});

test('couriers is a component, not a URI; machine icons/classes remain distinct from player names', () => {
  const clean = sanitizePayload({
    couriers: { courier0: { health: 150, name: 'npc_dota_courier', accountid: 'private' } },
    minimap: { o0: { image: 'minimap_hero', unitname: 'dota_neutral_spawner', name: 'private-player' },
      o1: { image: 'https://private.example/image' } },
    player: { activity: 'playing', profile_uri: 'private', avatar_url: 'private' },
    uri: 'private', url: 'private',
  });
  assert.deepEqual(JSON.parse(JSON.stringify(clean.couriers)), {
    courier0: { health: 150, name: 'npc_dota_courier', accountid: '[redacted]' },
  });
  const json = JSON.stringify(clean);
  assert.match(json, /minimap_hero/);
  assert.match(json, /dota_neutral_spawner/);
  assert.match(json, /playing/);
  assert.equal(json.includes('private'), false);
});

test('audit CLI records only sanitized fixtures and removes only its unchanged config', async () => {
  const parent = fs.realpathSync(os.tmpdir());
  const root = fs.mkdtempSync(path.join(parent, 'dota-observer-audit-test-'));
  const cfgDir = path.join(root, 'cfg');
  fs.mkdirSync(cfgDir);
  fs.writeFileSync(path.join(cfgDir, 'existing.cfg'), 'user-owned');
  const script = fileURLToPath(new URL('../scripts/client/observer-audit.ts', import.meta.url));
  const delay = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
  async function run(name: string, modifyConfig: boolean) {
    const out = path.join(root, name);
    const child = spawn(process.execPath, [script, '--out', out, '--cfg-dir', cfgDir, '--minutes', '1'],
      { windowsHide: true, stdio: 'ignore' });
    let exited = false;
    const completion = new Promise<number | null>(resolve => child.on('exit', code => { exited = true; resolve(code); }));
    try {
      const ready = path.join(out, 'session.json');
      for (let i = 0; i < 100 && !fs.existsSync(ready) && !exited; i++) await delay(50);
      assert.equal(fs.existsSync(ready), true, 'collector ready');
      const session = JSON.parse(fs.readFileSync(ready, 'utf8'));
      const config = fs.readFileSync(session.cfgPath, 'utf8');
      const credential = /"token" "([a-f0-9]{64})"/.exec(config)![1];
      const result = await fetch(`http://127.0.0.1:${session.port}/gsi`, {
        method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ auth: { token: credential }, player: { name: 'private-fixture-name' }, map: { clock_time: 1 } }),
      });
      assert.equal(result.status, 200);
      if (modifyConfig) fs.appendFileSync(session.cfgPath, '\n// user change\n');
      fs.writeFileSync(path.join(out, 'STOP'), '');
      for (let i = 0; i < 100 && !exited; i++) await delay(50);
      assert.equal(exited, true);
      assert.equal(await completion, 0);
      assert.equal(fs.existsSync(session.cfgPath), modifyConfig);
      assert.equal(fs.readFileSync(path.join(cfgDir, 'existing.cfg'), 'utf8'), 'user-owned');
      const report = JSON.parse(fs.readFileSync(path.join(out, 'summary.json'), 'utf8'));
      assert.equal(report.packets, 1);
      assert.equal(report.status, 'stopped');
      assert.equal(report.cleanup, modifyConfig ? 'preserved-changed-config' : 'removed-owned-config');
      const evidence = fs.readFileSync(path.join(out, 'packets.sanitized.jsonl'), 'utf8');
      assert.equal(evidence.includes(credential), false);
      assert.equal(evidence.includes('private-fixture-name'), false);
    } finally { if (!exited) { child.kill(); await completion; } }
  }
  try {
    await run('normal', false);
    await run('modified', true);
  } finally {
    // Delete only the exact temporary directory created above; validate resolved target.
    const resolved = fs.realpathSync(root);
    assert.equal(path.dirname(resolved), parent);
    assert.ok(path.basename(resolved).startsWith('dota-observer-audit-test-'));
    fs.rmSync(resolved, { recursive: true });
  }
});

test('audit continuation preserves the loaded GSI endpoint without retaining private credentials in metadata', async () => {
  const parent = fs.realpathSync(os.tmpdir());
  const root = fs.mkdtempSync(path.join(parent, 'dota-observer-audit-resume-test-'));
  const cfgDir = path.join(root, 'cfg');
  fs.mkdirSync(cfgDir);
  const script = fileURLToPath(new URL('../scripts/client/observer-audit.ts', import.meta.url));
  const children: ReturnType<typeof spawn>[] = [];
  const delay = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
  async function waitFile(file: string) {
    for (let i = 0; i < 200 && !fs.existsSync(file); i++) await delay(50);
    assert.ok(fs.existsSync(file));
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  }
  function launch(out: string, extra: string[] = []) {
    const child = spawn(process.execPath, [script, '--out', out, '--cfg-dir', cfgDir, '--minutes', '1', ...extra],
      { windowsHide: true, stdio: 'ignore' });
    children.push(child); return child;
  }
  try {
    const a = path.join(root, 'a'), b = path.join(root, 'b');
    const first = launch(a);
    const sa = await waitFile(path.join(a, 'session.json'));
    const credential = /"token" "([a-f0-9]{64})"/.exec(fs.readFileSync(sa.cfgPath, 'utf8'))![1];
    launch(b, ['--continue-from', path.join(a, 'session.json')]);
    const sb = await waitFile(path.join(b, 'session.json'));
    assert.equal(first.exitCode, 0);
    assert.equal(sb.port, sa.port);
    assert.equal(sb.continuedFrom, a);
    assert.equal(fs.existsSync(sa.cfgPath), false);
    assert.equal(JSON.stringify(sb).includes(credential), false);
    const result = await fetch(`http://127.0.0.1:${sa.port}/gsi`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ auth: { token: credential }, couriers: { c0: { health: 100 } } }),
    });
    assert.equal(result.status, 200);
    fs.writeFileSync(path.join(b, 'STOP'), '');
    for (let i = 0; i < 100 && children[1].exitCode === null; i++) await delay(50);
    assert.equal(children[1].exitCode, 0);
    const summary = JSON.parse(fs.readFileSync(path.join(b, 'summary.json'), 'utf8'));
    assert.equal(summary.packets, 1);
    assert.equal(summary.cleanup, 'removed-owned-config');
    assert.equal(summary.fields['couriers.c0.health'].min, 100);
  } finally {
    for (const child of children) if (child.exitCode === null) {
      child.kill(); await new Promise(resolve => child.once('exit', resolve));
    }
    const resolved = fs.realpathSync(root);
    assert.equal(path.dirname(resolved), parent);
    assert.ok(path.basename(resolved).startsWith('dota-observer-audit-resume-test-'));
    fs.rmSync(resolved, { recursive: true });
  }
});
