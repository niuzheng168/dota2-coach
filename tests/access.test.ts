import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import type { TestContext } from 'node:test';
import WebSocket from 'ws';
import { AccessStore } from '../src/access-store.ts';
import { createCoachServer } from '../src/app-server.ts';
import { DEFAULT_ROOM_SETTINGS } from '../src/room-settings.ts';
import type { EngineCallbacks, EngineOptions } from '../src/engines/types.ts';
import { MAP } from '../src/tactics/combat-data.ts';

const password = 'only-a-test-password';
type Login = { cookie: string; csrfToken: string };

test('jungle controls are authorized, room-isolated, default off, and do not restart voice engines', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), guest = await f.login('coach1');
  const room = await f.create(owner), other = await f.create(owner);
  const student = f.socket(owner, `room=${room.roomId}&role=student`);
  const coach = f.socket(guest, `room=${room.roomId}&role=coach&invite=${room.inviteToken}`);
  const otherSocket = f.socket(owner, `room=${other.roomId}&role=student`);
  const events = [student, coach, otherSocket].map(recordMessages);
  await waitUntil(() => events.every(e => e.some(m => m.type === 'room.state')));
  assert.equal(events[0].find(m => m.type === 'room.state').tactics.enabled, false);
  coach.send(JSON.stringify({ type: 'room.set_tactics', enabled: true }));
  await waitUntil(() => events[1].some(m => m.type === 'room.error'));
  assert.match(events[1].find(m => m.type === 'room.error').message, /学员或房主/);
  const enginesBefore = f.started.length;
  student.send(JSON.stringify({ type: 'room.set_tactics', enabled: true }));
  await waitUntil(() => events[0].some(m => m.type === 'tactics.state' && m.state.enabled));
  const camp = MAP.camps.find(c => c.id === 'neutralcamp_good_5')!;
  const gsi = {
    auth: { token: f.store.getRoom(room.roomId)!.gsiToken },
    map: { matchid: 'tactics-integration', clock_time: 600, game_state: 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS' },
    hero: { name: 'npc_dota_hero_sven', xpos: camp.x, ypos: camp.y, health: 3000, max_health: 3000,
      mana: 600, max_mana: 600, level: 15, alive: true },
    player: { team_name: 'radiant', last_hits: 100 },
    abilities: { ability0: { name: 'sven_great_cleave', level: 4, cooldown: 0 },
      ability1: { name: 'sven_storm_bolt', level: 4, cooldown: 0 } },
    items: { slot0: { name: 'item_broadsword' } },
  };
  const result = await f.request(`/api/gsi?room=${room.roomId}`, 'POST', gsi);
  assert.equal(result.status, 200);
  await waitUntil(() => events[0].some(m => m.type === 'tactics.state' && m.state.route));
  assert.ok(events[1].some(m => m.type === 'tactics.state' && m.state.route), 'coach sees read-only route');
  assert.ok(!events[2].some(m => m.type === 'tactics.state'), 'other room gets no tactical state');
  assert.equal(f.started.length, enginesBefore);
  assert.equal(student.readyState, WebSocket.OPEN);
  const cfg = await (await f.request(`/api/gsi-config?room=${room.roomId}`, 'GET', undefined, owner)).text();
  assert.match(cfg, /"abilities"\s+"1"/);
  assert.match(cfg, /"items"\s+"1"/);
  assert.match(cfg, /"throttle"\s+"0.5"/);
  // Invalid/spectator payload never becomes a route, and still does not break existing GSI.
  await new Promise(resolve => setTimeout(resolve, 520));
  const spectator = await f.request(`/api/gsi?room=${room.roomId}`, 'POST', { ...gsi, hero: { team2: {} } });
  assert.equal(spectator.status, 200);
  await waitUntil(() => events[0].some(m => m.type === 'tactics.state' && /观战/.test(m.state.status)));
  assert.equal(events[0].findLast(m => m.type === 'tactics.state').state.route, null);
  const state = (await (await f.request(`/api/rooms/${room.roomId}`, 'GET', undefined, owner)).json()).room;
  assert.ok(!('tactics' in state.settings), 'runtime toggle is not durable room settings');
});

async function fixture(t: TestContext, factory?: Parameters<typeof createCoachServer>[1]) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'dota2-access-'));
  const store = await AccessStore.open(directory);
  await store.createUser('largeniu', password, true);
  await store.createUser('coach1', password);
  await store.createUser('outsider', password);
  const started: EngineOptions[] = [];
  const sockets: WebSocket[] = [];
  const server = createCoachServer(store, factory || ((kind, options, callbacks) => ({
    kind, role: options.role,
    async start() { started.push(options); callbacks.onStatus('test ready'); },
    async stop() {}, appendAudio() {}, markSpeechEnd() {},
    async updateMatchHeroes() {},
    async translateText(text) {
      callbacks.onTargetFinal('test-turn', `translated: ${text}`);
      callbacks.onTargetAudio('test-turn', Buffer.alloc(20));
    },
  })));
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  assert.ok(address && typeof address === 'object');
  const base = `http://127.0.0.1:${address.port}`;
  t.after(async () => {
    sockets.forEach(socket => socket.terminate());
    await new Promise<void>(resolve => server.close(() => resolve()));
    await fs.rm(directory, { recursive: true, force: true });
  });
  async function request(route: string, method = 'GET', body?: unknown, auth?: Login, extra: Record<string, string> = {}) {
    return fetch(base + route, {
      method,
      headers: {
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        ...(auth ? { Cookie: auth.cookie, 'X-CSRF-Token': auth.csrfToken } : {}),
        ...extra,
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  }
  async function login(username: string): Promise<Login> {
    const response = await request('/api/auth/login', 'POST', { username, password });
    assert.equal(response.status, 200);
    const cookieHeader = response.headers.get('set-cookie')!;
    assert.match(cookieHeader, /HttpOnly/);
    assert.match(cookieHeader, /SameSite=Lax/);
    const body = await response.json();
    assert.equal(body.user.username, username);
    return { cookie: cookieHeader.split(';')[0], csrfToken: body.csrfToken };
  }
  async function create(auth: Login, allowedUsernames = ['coach1']) {
    const response = await request('/api/rooms', 'POST', {
      name: 'Private coaching', allowedUsernames,
      settings: { ...DEFAULT_ROOM_SETTINGS, engineKind: 'gpt-realtime' },
    }, auth);
    assert.equal(response.status, 201);
    return (await response.json()).room;
  }
  function socket(auth?: Login, query = '', origin?: string) {
    const ws = new WebSocket(base.replace('http:', 'ws:') + '/ws?' + query, {
      headers: { ...(auth ? { Cookie: auth.cookie } : {}), ...(origin ? { Origin: origin } : {}) },
    });
    ws.on('error', () => {});
    sockets.push(ws);
    return ws;
  }
  async function deniedSocket(status: number, auth?: Login, query = '', origin?: string) {
    const ws = socket(auth, query, origin);
    const [, response] = await once(ws, 'unexpected-response');
    assert.equal(response.statusCode, status);
    response.resume();
    ws.terminate();
  }
  return { store, directory, request, login, create, socket, deniedSocket, started };
}

test('anonymous access cannot create, discover, join, or invoke paid APIs', async t => {
  const f = await fixture(t);
  for (const route of ['/api/rooms', '/api/config', '/api/users', '/api/dictionary', '/api/gsi-config?room=x']) {
    assert.equal((await f.request(route)).status, 401);
  }
  for (const route of ['/api/rooms', '/api/vision-draft', '/api/demo-speak']) {
    assert.equal((await f.request(route, 'POST', {})).status, 401);
  }
  assert.equal((await f.request('/api/gsi?room=guess', 'POST', {})).status, 403);
  await f.deniedSocket(401, undefined, 'room=guess&role=coach');
  for (const route of ['/.env', '/.local/access.json', '/src/server.ts']) {
    assert.equal((await f.request(route)).status, 404);
  }
  assert.equal(f.store.listOwnedRooms('largeniu').length, 0);
  assert.equal(f.started.length, 0);
});

test('password hashing, session cookies, failed login, and durable account storage', async t => {
  const f = await fixture(t);
  assert.equal((await f.request('/api/auth/login', 'POST', { username: 'largeniu', password: 'bad' })).status, 401);
  assert.equal((await f.request('/api/auth/login', 'POST', { username: 'unknown', password })).status, 401);
  const owner = await f.login('largeniu');
  const session = await (await f.request('/api/auth/session', 'GET', undefined, owner)).json();
  assert.deepEqual(session.user, { username: 'largeniu', canCreateRooms: true });
  const raw = await fs.readFile(path.join(f.directory, 'access.json'), 'utf8');
  assert.ok(!raw.includes(password));
  assert.ok(!raw.includes(owner.cookie.split('=')[1]));
  assert.equal((await fs.stat(path.join(f.directory, 'access.json'))).mode & 0o777, 0o600);
  const reopened = await AccessStore.open(f.directory);
  assert.equal((await reopened.verifyPassword('largeniu', password))?.canCreateRooms, true);
  assert.equal(reopened.getSession(owner.cookie.split('=')[1])?.user.username, 'largeniu');
});

test('only the authorized creator can create rooms; members need BOTH invitation and allowlist', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), guest = await f.login('coach1'), outsider = await f.login('outsider');
  assert.equal((await f.request('/api/rooms', 'POST', {
    name: 'spoof', ownerUsername: 'largeniu', canCreateRooms: true, settings: DEFAULT_ROOM_SETTINGS,
  }, guest)).status, 403);
  assert.equal((await f.request('/api/rooms', 'POST', {
    name: 'bad settings', settings: { ...DEFAULT_ROOM_SETTINGS, engineKind: 'invalid' },
  }, owner)).status, 400);
  assert.equal((await f.request('/api/rooms', 'POST', {
    name: 'bad access', settings: DEFAULT_ROOM_SETTINGS, allowedUsernames: ['missing-user'],
  }, owner)).status, 400);
  const room = await f.create(owner);
  const route = `/api/rooms/${room.roomId}`;
  assert.equal((await f.request(route, 'GET', undefined, guest)).status, 403);
  assert.equal((await f.request(route, 'GET', undefined, guest, { 'X-Room-Invite': 'wrong' })).status, 403);
  assert.equal((await f.request(route, 'GET', undefined, outsider, { 'X-Room-Invite': room.inviteToken })).status, 403);
  const response = await f.request(route, 'GET', undefined, guest, { 'X-Room-Invite': room.inviteToken });
  assert.equal(response.status, 200);
  const detail = (await response.json()).room;
  assert.equal(detail.isOwner, false);
  assert.ok(!('inviteToken' in detail) && !('gsiToken' in detail) && !('allowedUsernames' in detail));
  assert.deepEqual((await (await f.request('/api/rooms', 'GET', undefined, guest)).json()).rooms, []);
  await f.deniedSocket(403, guest, `room=${room.roomId}&role=coach`);
  await f.deniedSocket(403, outsider, `room=${room.roomId}&role=coach&invite=${room.inviteToken}`);
  const reopened = await AccessStore.open(f.directory);
  assert.equal(reopened.getRoom(room.roomId)?.settings.engineKind, 'gpt-realtime');
  assert.deepEqual(reopened.getRoom(room.roomId)?.allowedUsernames, ['coach1']);
});

test('HTTP and WebSocket validate origin and CSRF independently of UI restrictions', async t => {
  const f = await fixture(t);
  assert.equal((await f.request('/api/auth/login', 'POST', { username: 'largeniu', password }, undefined, { Origin: 'https://evil.example' })).status, 403);
  const owner = await f.login('largeniu');
  const invalidHeaders: Record<string, string>[] = [{ 'X-CSRF-Token': '' }, { Origin: 'https://evil.example' }];
  for (const extra of invalidHeaders) {
    assert.equal((await f.request('/api/rooms', 'POST', { name: 'blocked', settings: DEFAULT_ROOM_SETTINGS }, owner, extra)).status, 403);
  }
  const room = await f.create(owner);
  await f.deniedSocket(403, owner, `room=${room.roomId}&role=student`, 'https://evil.example');
  assert.equal(f.started.length, 0);
});

test('WebSocket derives identity from login and rejects legacy settings commands', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), guest = await f.login('coach1');
  const room = await f.create(owner);
  const ws = f.socket(guest, `room=${room.roomId}&role=coach&invite=${room.inviteToken}&name=largeniu`);
  const [initial] = await once(ws, 'message');
  const state = JSON.parse(initial.toString());
  assert.equal(state.participants[0].name, 'coach1');
  async function rejected(message: unknown) {
    const pending = new Promise<any>(resolve => {
      const onMessage = (data: WebSocket.RawData) => {
        const event = JSON.parse(data.toString());
        if (event.type === 'room.error') { ws.off('message', onMessage); resolve(event); }
      };
      ws.on('message', onMessage);
    });
    ws.send(JSON.stringify(message));
    return pending;
  }
  assert.match((await rejected({ type: 'room.set_engine', engineKind: 'gpt-live' })).message, /只有房主/);
  assert.match((await rejected({ type: 'room.set_tts_options', ttsZhVoice: 'evil' })).message, /只有房主/);
  assert.match((await rejected({ type: 'room.set_match_heroes', matchHeroes: ['Axe'] })).message, /房主/);
  assert.equal(f.store.getRoom(room.roomId)?.settings.engineKind, 'gpt-realtime');
  assert.equal(f.started[0].ttsZhVoice, DEFAULT_ROOM_SETTINGS.ttsZhVoice);
  assert.equal((await f.request(`/api/rooms/${room.roomId}`, 'PATCH', { settings: DEFAULT_ROOM_SETTINGS }, owner)).status, 405);
});

async function waitUntil(check: () => boolean) {
  for (let i = 0; i < 200; i++) {
    if (check()) return;
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.ok(check(), 'timed out waiting for expected state');
}

function recordMessages(ws: WebSocket) {
  const events: any[] = [];
  ws.on('message', (raw, binary) => events.push(binary ? { type: 'audio' } : JSON.parse(raw.toString())));
  return events;
}

test('only the owner can save validated settings with a same-origin CSRF-protected request', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), guest = await f.login('coach1');
  const room = await f.create(owner);
  const route = `/api/rooms/${room.roomId}/settings`;
  const body = { settings: DEFAULT_ROOM_SETTINGS };
  assert.equal((await f.request(route, 'PATCH', body)).status, 401);
  assert.equal((await f.request(route, 'PATCH', body, guest, { 'X-Room-Invite': room.inviteToken })).status, 403);
  assert.equal((await f.request(route, 'PATCH', body, owner, { 'X-CSRF-Token': '' })).status, 403);
  assert.equal((await f.request(route, 'PATCH', body, owner, { Origin: 'https://evil.example' })).status, 403);
  for (const settings of [
    null, [], {}, { ...DEFAULT_ROOM_SETTINGS, engineKind: 'unknown' },
    { ...DEFAULT_ROOM_SETTINGS, ttsZhVoice: 'unknown' },
    { ...DEFAULT_ROOM_SETTINGS, ttsEnVoice: 'unknown' },
    { ...DEFAULT_ROOM_SETTINGS, voiceLiveAsrModel: 'unknown' },
    { ...DEFAULT_ROOM_SETTINGS, ownerUsername: 'coach1' },
  ]) assert.equal((await f.request(route, 'PATCH', { settings }, owner)).status, 400);
  await assert.rejects(f.store.updateSettings({ username: 'coach1', canCreateRooms: false }, room.roomId, DEFAULT_ROOM_SETTINGS), /只有房主/);
  assert.deepEqual(f.store.getRoom(room.roomId)?.settings, room.settings);
  const response = await f.request(route, 'PATCH', body, owner);
  assert.equal(response.status, 200);
  const reopened = await AccessStore.open(f.directory);
  const stored = reopened.getRoom(room.roomId)!;
  assert.deepEqual(stored.settings, DEFAULT_ROOM_SETTINGS);
  assert.equal(stored.inviteToken, room.inviteToken);
  assert.equal(stored.ownerUsername, 'largeniu');
  assert.deepEqual(stored.allowedUsernames, room.allowedUsernames);
});

test('saving settings resets all room connections, preserves other rooms and rejoins with new options', async t => {
  const engines: { kind: string; options: EngineOptions; stopped: boolean }[] = [];
  const f = await fixture(t, (kind, options) => {
    const item = { kind, options, stopped: false };
    engines.push(item);
    return {
      kind, role: options.role, async start() {}, async stop() { item.stopped = true; },
      appendAudio() {}, markSpeechEnd() {}, async updateMatchHeroes() {},
    };
  });
  const owner = await f.login('largeniu'), guest = await f.login('coach1');
  const room = await f.create(owner), other = await f.create(owner);
  const ownerQuery = `room=${room.roomId}&role=student&selfMonitor=1`;
  const guestQuery = `room=${room.roomId}&role=coach&invite=${room.inviteToken}`;
  const ownerWs = f.socket(owner, ownerQuery), guestWs = f.socket(guest, guestQuery);
  const ownerTabWs = f.socket(owner, ownerQuery);
  const otherWs = f.socket(owner, `room=${other.roomId}&role=student`);
  const all = [ownerWs, guestWs, ownerTabWs, otherWs];
  const messages = all.map(recordMessages);
  await waitUntil(() => messages.every(events => events.some(event => event.state === 'ready')));
  ownerWs.send(JSON.stringify({ type: 'room.set_match_heroes', matchHeroes: ['Axe'] }));
  await waitUntil(() => messages[0].some(event => event.matchHeroes?.includes('Axe')));
  const oldEngines = engines.slice();
  const closed = [ownerWs, guestWs, ownerTabWs].map(ws => once(ws, 'close'));
  const settings = { ...DEFAULT_ROOM_SETTINGS, engineKind: 'gpt-live', ttsZhVoice: 'zh-CN-YunxiNeural', voiceLiveAsrModel: 'mai-transcribe-2-streaming' };
  const response = await f.request(`/api/rooms/${room.roomId}/settings`, 'PATCH', { settings }, owner);
  assert.equal(response.status, 200);
  assert.equal((await response.json()).reconnected, true);
  for (const result of await Promise.all(closed)) assert.equal(result[0], 4000);
  assert.equal(oldEngines.filter(engine => engine.stopped).length, 3);
  assert.equal(otherWs.readyState, WebSocket.OPEN);
  for (const events of messages.slice(0, 3)) {
    assert.deepEqual(events.find(event => event.type === 'room.settings.changed')?.settings, settings);
  }
  assert.ok(!messages[3].some(event => event.type === 'room.settings.changed'));
  const nextOwner = f.socket(owner, ownerQuery), nextGuest = f.socket(guest, guestQuery);
  const nextMessages = [nextOwner, nextGuest].map(recordMessages);
  await waitUntil(() => nextMessages.every(events => events.some(event => event.state === 'ready')));
  for (const engine of engines.slice(4)) {
    assert.equal(engine.kind, 'gpt-live');
    assert.equal(engine.options.ttsZhVoice, settings.ttsZhVoice);
    assert.equal(engine.options.voiceLiveAsrModel, settings.voiceLiveAsrModel);
    assert.deepEqual(engine.options.matchHeroes, ['Axe']);
  }
  assert.deepEqual(engines.slice(4).map(engine => engine.options.role).sort(), ['coach', 'student']);
  const state = nextMessages[0].findLast(event => event.type === 'room.state');
  assert.equal(state.engineKind, 'gpt-live');
  assert.equal(state.participants.find((p: any) => p.name === 'largeniu').selfMonitor, true);
  assert.deepEqual((await AccessStore.open(f.directory)).getRoom(room.roomId)?.settings, settings);
});

test('unchanged or invalid settings do not interrupt active engines', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), room = await f.create(owner);
  const ws = f.socket(owner, `room=${room.roomId}&role=student`);
  const events = recordMessages(ws);
  await waitUntil(() => events.some(event => event.state === 'ready'));
  const route = `/api/rooms/${room.roomId}/settings`;
  const response = await f.request(route, 'PATCH', { settings: room.settings }, owner);
  assert.equal(response.status, 200);
  assert.equal((await response.json()).reconnected, false);
  assert.equal((await f.request(route, 'PATCH', { settings: { ...room.settings, ttsEnVoice: 'bad' } }, owner)).status, 400);
  assert.equal(ws.readyState, WebSocket.OPEN);
  assert.equal(f.started.length, 1);
  assert.ok(!events.some(event => event.type === 'room.settings.changed'));
  ws.send(JSON.stringify({ type: 'text.send', text: 'still connected' }));
  await waitUntil(() => events.some(event => event.text === 'translated: still connected'));
});

test('a failed settings write leaves the runtime and persisted configuration untouched', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), room = await f.create(owner);
  const ws = f.socket(owner, `room=${room.roomId}&role=student`);
  const events = recordMessages(ws);
  await waitUntil(() => events.some(event => event.state === 'ready'));
  const write = t.mock.method(fs, 'writeFile', async () => { throw new Error('test disk failure'); });
  let response: Response;
  try {
    response = await f.request(`/api/rooms/${room.roomId}/settings`, 'PATCH', { settings: DEFAULT_ROOM_SETTINGS }, owner);
  } finally { write.mock.restore(); }
  assert.equal(response.status, 500);
  assert.deepEqual(f.store.getRoom(room.roomId)?.settings, room.settings);
  assert.deepEqual((await AccessStore.open(f.directory)).getRoom(room.roomId)?.settings, room.settings);
  assert.equal(ws.readyState, WebSocket.OPEN);
  assert.ok(!events.some(event => event.type === 'room.settings.changed'));
});

test('settings reset cancels startup and discards every stale engine callback after rejoin', async t => {
  let release = () => {};
  let stops = 0;
  const callbacks: Parameters<NonNullable<Parameters<typeof createCoachServer>[1]>>[2][] = [];
  const f = await fixture(t, (kind, options, cb) => {
    callbacks.push(cb);
    const first = callbacks.length === 1;
    return {
      kind, role: options.role,
      start: () => first ? new Promise<void>(resolve => { release = resolve; }) : Promise.resolve(),
      async stop() { if (first) stops++; },
      appendAudio() {}, markSpeechEnd() {},
    };
  });
  const owner = await f.login('largeniu'), room = await f.create(owner);
  const query = `room=${room.roomId}&role=student&selfMonitor=1`;
  const ws = f.socket(owner, query);
  await once(ws, 'open');
  const closed = once(ws, 'close');
  assert.equal((await f.request(`/api/rooms/${room.roomId}/settings`, 'PATCH', { settings: DEFAULT_ROOM_SETTINGS }, owner)).status, 200);
  assert.equal((await closed)[0], 4000);
  assert.ok(stops > 0, 'pending engine must be stopped before startup resolves');
  const next = f.socket(owner, query);
  const events = recordMessages(next);
  await waitUntil(() => events.some(event => event.state === 'ready'));
  const old = callbacks[0];
  const count = events.length;
  old.onConnectionState?.('failed');
  old.onSourcePartial('STALE');
  old.onSourceFinal('old', 'STALE');
  old.onTargetPartial('old', 'STALE');
  old.onTargetFinal('old', 'STALE');
  old.onTargetAudio('old', Buffer.alloc(20));
  old.onStatus('STALE');
  old.onError('STALE');
  old.onTurnMetrics({
    turnId: 'old', engine: 'gpt-realtime', role: 'student', sourceText: 'STALE', targetText: 'STALE',
    passThrough: 'none', speechEndToFirstAudioMs: 1, totalTurnMs: 1, outputAudioDurationMs: 1, timestamp: Date.now(),
  });
  release();
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(events.length, count);
  next.send(JSON.stringify({ type: 'client.set_self_monitor', selfMonitor: true }));
  await waitUntil(() => events.length > count);
  assert.deepEqual(events.findLast(event => event.type === 'room.state').recentMetrics, []);
});

test('voice-only and ASR-only changes reset connections even when the engine kind stays the same', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), room = await f.create(owner);
  let settings = room.settings;
  for (const change of [{ ttsEnVoice: 'en-US-EmmaMultilingualNeural' }, { voiceLiveAsrModel: 'mai-transcribe-2' }]) {
    const ws = f.socket(owner, `room=${room.roomId}&role=student`);
    const events = recordMessages(ws);
    await waitUntil(() => events.some(event => event.state === 'ready'));
    const closed = once(ws, 'close');
    settings = { ...settings, ...change };
    const response = await f.request(`/api/rooms/${room.roomId}/settings`, 'PATCH', { settings }, owner);
    assert.equal(response.status, 200);
    assert.equal((await closed)[0], 4000);
    assert.deepEqual(f.store.getRoom(room.roomId)?.settings, settings);
  }
});

test('concurrent owner saves leave reconnecting clients on the final durable configuration', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), room = await f.create(owner);
  const ws = f.socket(owner, `room=${room.roomId}&role=student`);
  const events = recordMessages(ws);
  await waitUntil(() => events.some(event => event.state === 'ready'));
  const closed = once(ws, 'close');
  const responses = await Promise.all(['zh-CN-YunxiNeural', 'zh-CN-XiaoxiaoNeural'].map(ttsZhVoice =>
    f.request(`/api/rooms/${room.roomId}/settings`, 'PATCH', { settings: { ...room.settings, ttsZhVoice } }, owner)));
  assert.ok(responses.every(response => response.status === 200));
  assert.equal((await closed)[0], 4000);
  const stored = (await AccessStore.open(f.directory)).getRoom(room.roomId)!;
  const next = f.socket(owner, `room=${room.roomId}&role=student`);
  const nextEvents = recordMessages(next);
  await waitUntil(() => nextEvents.some(event => event.state === 'ready'));
  assert.equal(nextEvents.find(event => event.type === 'room.state').ttsZhVoice, stored.settings.ttsZhVoice);
  assert.equal(f.started.at(-1)?.ttsZhVoice, stored.settings.ttsZhVoice);
});

test('revoking access and logging out close active sockets and block reconnection', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), guest = await f.login('coach1');
  const room = await f.create(owner);
  const query = `room=${room.roomId}&role=coach&invite=${room.inviteToken}`;
  const ws = f.socket(guest, query);
  await once(ws, 'open');
  assert.equal((await f.request(`/api/rooms/${room.roomId}/access`, 'PATCH', { allowedUsernames: [] }, guest)).status, 403);
  const closed = once(ws, 'close');
  assert.equal((await f.request(`/api/rooms/${room.roomId}/access`, 'PATCH', { allowedUsernames: [] }, owner)).status, 200);
  assert.equal((await closed)[0], 4003);
  await f.deniedSocket(403, guest, query);
  await f.request(`/api/rooms/${room.roomId}/access`, 'PATCH', { allowedUsernames: ['coach1'] }, owner);
  const ws2 = f.socket(guest, query);
  await once(ws2, 'open');
  const loggedOut = once(ws2, 'close');
  assert.equal((await f.request('/api/auth/logout', 'POST', undefined, guest)).status, 200);
  assert.equal((await loggedOut)[0], 4001);
  await f.deniedSocket(401, guest, query);
  assert.equal((await f.request('/api/rooms', 'GET', undefined, guest)).status, 401);
});

test('GSI uses a separate owner-only secret; invitations cannot mutate roster through GSI', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu'), guest = await f.login('coach1');
  const room = await f.create(owner);
  const record = f.store.getRoom(room.roomId)!;
  assert.equal((await f.request(`/api/gsi-config?room=${room.roomId}`, 'GET', undefined, guest)).status, 403);
  const cfg = await (await f.request(`/api/gsi-config?room=${room.roomId}`, 'GET', undefined, owner)).text();
  assert.ok(cfg.includes(record.gsiToken));
  assert.ok(!cfg.includes(record.inviteToken));
  assert.equal((await f.request(`/api/gsi?room=${room.roomId}`, 'POST', { auth: { token: room.inviteToken } })).status, 403);
  assert.equal((await f.request(`/api/gsi?room=${room.roomId}`, 'POST', { auth: { token: record.gsiToken } })).status, 200);
  assert.equal((await f.request('/api/vision-draft', 'POST', {
    roomId: room.roomId, imageDataUrl: 'data:image/png;base64,invalid',
  }, guest)).status, 403);
});

test('repeated bad login attempts are rate limited', async t => {
  const f = await fixture(t);
  for (let i = 0; i < 20; i++) {
    assert.equal((await f.request('/api/auth/login', 'POST', { username: 'largeniu', password: 'wrong' })).status, 401);
  }
  const response = await f.request('/api/auth/login', 'POST', { username: 'largeniu', password });
  assert.equal(response.status, 429);
  assert.equal(response.headers.get('retry-after'), '900');
});

test('expired sessions cannot access HTTP or open a room socket', async t => {
  const f = await fixture(t);
  const owner = await f.login('largeniu');
  const room = await f.create(owner);
  const expiresAt = f.store.getSession(owner.cookie.split('=')[1])!.session.expiresAt;
  const clock = t.mock.method(Date, 'now', () => expiresAt + 1);
  try {
    assert.equal((await f.request('/api/rooms', 'GET', undefined, owner)).status, 401);
    await f.deniedSocket(401, owner, `room=${room.roomId}&role=student`);
  } finally { clock.mock.restore(); }
});

test('disconnecting during engine startup stops the late-started engine', async t => {
  let completeStartup = () => {};
  let stopped = false;
  const f = await fixture(t, (kind, options) => ({
    kind, role: options.role,
    start: () => new Promise(resolve => { completeStartup = resolve; }),
    async stop() { stopped = true; },
    appendAudio() {}, markSpeechEnd() {},
  }));
  const owner = await f.login('largeniu');
  const room = await f.create(owner);
  const ws = f.socket(owner, `room=${room.roomId}&role=student`);
  await once(ws, 'open');
  const closed = once(ws, 'close');
  ws.close();
  await closed;
  completeStartup();
  for (let i = 0; i < 50 && !stopped; i++) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(stopped);
});

test('early microphone frames get an explicit not-ready receipt; ready frames reach the engine', async t => {
  let release = () => {};
  const frames: Buffer[] = [];
  const f = await fixture(t, (kind, options) => ({
    kind, role: options.role,
    start: () => new Promise(resolve => { release = resolve; }),
    async stop() {}, markSpeechEnd() {},
    appendAudio(frame) { frames.push(frame); },
  }));
  const owner = await f.login('largeniu'), room = await f.create(owner);
  const ws = f.socket(owner, `room=${room.roomId}&role=student`);
  const events: any[] = [];
  ws.on('message', raw => events.push(JSON.parse(raw.toString())));
  await once(ws, 'open');
  ws.send(Buffer.alloc(1920));
  for (let i = 0; i < 50 && !events.some(event => event.type === 'audio.input.status'); i++) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  const receipt = events.find(event => event.type === 'audio.input.status');
  assert.equal(receipt.engineReady, false);
  assert.equal(receipt.receivedFrames, 1);
  assert.equal(receipt.forwardedFrames, 0);
  assert.equal(frames.length, 0);
  assert.ok(!events.some(event => event.type === 'engine.state' && event.state === 'ready'));
  release();
  for (let i = 0; i < 50 && !events.some(event => event.type === 'engine.state' && event.state === 'ready'); i++) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.ok(events.some(event => event.type === 'engine.state' && event.state === 'ready'));
  ws.send(Buffer.alloc(1920));
  for (let i = 0; i < 50 && !frames.length; i++) await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(frames.length, 1);
});

test('a transient initial startup failure recovers without requiring a typed message', async t => {
  let attempts = 0;
  const f = await fixture(t, (kind, options) => ({
    kind, role: options.role,
    async start() { if (++attempts === 1) throw new Error('Temporary startup failure'); },
    async stop() {}, appendAudio() {}, markSpeechEnd() {},
  }));
  const owner = await f.login('largeniu'), room = await f.create(owner);
  const ws = f.socket(owner, `room=${room.roomId}&role=student`);
  const states: string[] = [];
  ws.on('message', raw => {
    const event = JSON.parse(raw.toString());
    if (event.type === 'engine.state') states.push(event.state);
  });
  for (let i = 0; i < 250 && !states.includes('ready'); i++) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(states.includes('failed'));
  assert.ok(states.includes('reconnecting'));
  assert.ok(states.includes('ready'));
  assert.equal(attempts, 2);
});

test('external listening is authenticated, private, text-only and independent from room microphone audio', async t => {
  const engines: { options: EngineOptions; callbacks: EngineCallbacks; stopped: boolean; frames: number }[] = [];
  const f = await fixture(t, (kind, options, callbacks) => {
    const state = { options, callbacks, stopped: false, frames: 0 };
    engines.push(state);
    return {
      kind, role: options.role,
      async start() {}, async stop() { state.stopped = true; },
      appendAudio() { state.frames++; }, markSpeechEnd() {},
    };
  });
  const owner = await f.login('largeniu'), guest = await f.login('coach1'), outsider = await f.login('outsider');
  const room = await f.create(owner);
  const query = `room=${room.roomId}&role=coach&inputKind=process&sourceLabel=dota2.exe`;
  await f.deniedSocket(401, undefined, query);
  await f.deniedSocket(403, outsider, `${query}&invite=${room.inviteToken}`);
  await f.deniedSocket(403, owner, query, 'https://evil.test');
  await f.deniedSocket(400, owner, query.replace('role=coach', 'role=student'));
  await f.deniedSocket(400, owner, query.replace('dota2.exe', '%00'));
  await f.deniedSocket(400, owner, query.replace('inputKind=process', 'inputKind=system'));
  const student = f.socket(owner, `room=${room.roomId}&role=student`);
  const coach = f.socket(guest, `room=${room.roomId}&role=coach&invite=${room.inviteToken}`);
  const listener = f.socket(owner, query);
  const collected = new Map<WebSocket, any[]>();
  for (const socket of [student, coach, listener]) {
    const events: any[] = [];
    collected.set(socket, events);
    socket.on('message', (raw, binary) => events.push(binary ? { type: 'binary' } : JSON.parse(raw.toString())));
  }
  for (let i = 0; i < 100 && ![...collected.values()].every(events =>
    events.some(event => event.type === 'engine.state' && event.state === 'ready')); i++) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal(engines.length, 3);
  await f.deniedSocket(409, owner, query);
  const processEngine = engines.find(engine => engine.options.inputKind === 'process')!;
  const coachEngine = engines.find(engine => engine.options.inputKind === 'microphone' && engine.options.role === 'coach')!;
  processEngine.callbacks.onSourceFinal('private-turn', 'Go Roshan');
  processEngine.callbacks.onTargetFinal('private-turn', '打肉山');
  processEngine.callbacks.onStatus('listener-only-status');
  processEngine.callbacks.onTargetAudio('private-turn', Buffer.alloc(240));
  listener.send(Buffer.alloc(2880));
  listener.send(JSON.stringify({ type: 'text.send', text: 'not allowed' }));
  await new Promise(resolve => setTimeout(resolve, 40));
  const privateEvents = collected.get(listener)!;
  assert.ok(privateEvents.some(event => event.type === 'listener.config' && event.protocolVersion === 1 &&
    event.privacy === 'connection' && event.output === 'text' && event.sampleRate === 24000 && event.channels === 1));
  assert.ok(privateEvents.findIndex(event => event.type === 'listener.config') <
    privateEvents.findIndex(event => event.type === 'engine.state' && event.state === 'ready'));
  assert.equal(privateEvents.filter(event => event.turnId === 'private-turn').length, 2);
  assert.ok(privateEvents.some(event => event.private === true && event.sourceKind === 'process' && event.speaker === 'dota2.exe'));
  assert.ok(privateEvents.some(event => event.type === 'room.error' && /只接受音频/.test(event.message)));
  for (const socket of [student, coach]) {
    assert.ok(!collected.get(socket)!.some(event => event.turnId === 'private-turn' || event.type === 'binary' || event.status === 'listener-only-status'));
  }
  assert.equal(processEngine.frames, 1);
  const roster = collected.get(student)!.filter(event => event.type === 'room.state').at(-1);
  assert.equal(roster.participants.length, 2);
  assert.ok(!roster.participants.some((person: any) => person.name === 'dota2.exe'));
  coachEngine.callbacks.onTargetAudio('normal-turn', Buffer.alloc(240));
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.ok(collected.get(student)!.some(event => event.type === 'binary'));
  assert.ok(!privateEvents.some(event => event.type === 'binary'));
  const closed = once(listener, 'close');
  listener.close();
  await closed;
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(processEngine.stopped, true);
  assert.equal(coachEngine.stopped, false);
  assert.equal(student.readyState, WebSocket.OPEN);
});

test('revoking a listener or changing room settings stops capture engines and rejects stale callbacks', async t => {
  const engines: { callbacks: EngineCallbacks; stopped: boolean }[] = [];
  const f = await fixture(t, (kind, options, callbacks) => {
    const engine = { callbacks, stopped: false };
    engines.push(engine);
    return { kind, role: options.role, async start() {}, async stop() { engine.stopped = true; },
      appendAudio() {}, markSpeechEnd() {} };
  });
  const owner = await f.login('largeniu'), guest = await f.login('coach1');
  const room = await f.create(owner);
  const query = `room=${room.roomId}&role=coach&inputKind=process&sourceLabel=YY.exe`;
  const listener = f.socket(guest, `${query}&invite=${room.inviteToken}`);
  await once(listener, 'open');
  const closed = once(listener, 'close');
  const revoke = await f.request(`/api/rooms/${room.roomId}/access`, 'PATCH', { allowedUsernames: [] }, owner);
  assert.equal(revoke.status, 200);
  assert.equal((await closed)[0], 4003);
  assert.equal(engines[0]!.stopped, true);
  engines[0]!.callbacks.onSourceFinal('late', 'must not leak');
  const next = f.socket(owner, query);
  await once(next, 'open');
  const reset = once(next, 'close');
  const response = await f.request(`/api/rooms/${room.roomId}/settings`, 'PATCH', {
    settings: { ...room.settings, engineKind: 'cascade' },
  }, owner);
  assert.equal(response.status, 200);
  assert.equal((await reset)[0], 4000);
  assert.equal(engines.at(-1)!.stopped, true);
});
