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
import type { EngineOptions } from '../src/engines/types.ts';

const password = 'only-a-test-password';
type Login = { cookie: string; csrfToken: string };

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

test('WebSocket derives identity from login and rejects all post-creation settings changes', async t => {
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
  assert.match((await rejected({ type: 'room.set_engine', engineKind: 'gpt-live' })).message, /不可修改/);
  assert.match((await rejected({ type: 'room.set_tts_options', ttsZhVoice: 'evil' })).message, /不可修改/);
  assert.match((await rejected({ type: 'room.set_match_heroes', matchHeroes: ['Axe'] })).message, /房主/);
  assert.equal(f.store.getRoom(room.roomId)?.settings.engineKind, 'gpt-realtime');
  assert.equal(f.started[0].ttsZhVoice, DEFAULT_ROOM_SETTINGS.ttsZhVoice);
  assert.equal((await f.request(`/api/rooms/${room.roomId}`, 'PATCH', { settings: DEFAULT_ROOM_SETTINGS }, owner)).status, 405);
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
