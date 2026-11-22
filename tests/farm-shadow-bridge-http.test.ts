import assert from 'node:assert/strict';
import http from 'node:http';
import { test } from 'node:test';
import { startFarmShadowBridge } from '../scripts/farm-shadow-bridge.ts';
import { confirmation, playerPacket, startingMs, visible } from './farm-shadow-bridge-fixtures.ts';
import type { State } from './farm-shadow-bridge-fixtures.ts';

test('real isolated HTTP: both roles, native body token, strict schemas, fresh read-back, silent expiry and latched termination', async t => {
  let now = startingMs;
  const s = await startFarmShadowBridge({ now: () => now, corpus: 'fixed' }); t.after(s.close);
  assert.match(s.origin, /^http:\/\/127\.0\.0\.1:\d+$/);
  assert.notEqual(s.operatorToken, s.gsiToken);
  const auth = { Authorization: `Bearer ${s.operatorToken}` };
  const post = (path: string, body: unknown, headers = auth as Record<string, string>) =>
    fetch(s.origin + path, { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body) });
  const get = async (): Promise<State> => (await fetch(s.origin + '/api/state', { headers: auth })).json();
  const p = playerPacket();
  for (const headers of [{}, { Authorization: `Bearer ${s.gsiToken}` }] as Record<string, string>[])
    assert.equal((await fetch(s.origin + '/api/state', { headers })).status, 401);
  for (const token of [undefined, '', s.operatorToken]) assert.equal((await post('/gsi', { ...p, auth: { token } })).status, 401);
  // Neither a public HTML shell nor the GSI producer can read operator data.
  const page = await fetch(s.origin + '/'); assert.equal(page.status, 200); assert.equal(page.headers.get('cache-control'), 'no-store');
  assert.match(page.headers.get('content-security-policy')!, /frame-ancestors 'none'/);
  const html = await page.text(); assert.ok(!html.includes(s.operatorToken) && !html.includes(s.gsiToken));
  assert.match(html, /没有桌面叠加/);
  const gsi = (packet: unknown) => post('/gsi', { ...(packet as object), auth: { token: s.gsiToken } }, {});
  assert.deepEqual(await (await gsi(p)).json(), { accepted: true });
  let state = await get(); const c = confirmation(state, ++now), v = visible(state, now + 1);
  assert.equal((await post('/api/confirm', { ...c, signIn: 'private' })).status, 400);
  assert.equal((await post('/api/confirm', c, { Authorization: `Bearer ${s.gsiToken}` })).status, 401);
  assert.equal((await post('/api/visible', v)).status, 409);
  assert.equal((await post('/api/confirm', c)).status, 200);
  now++; assert.equal((await post('/api/visible', v)).status, 200);
  state = await get();
  assert.equal(state.own?.health, 900); assert.equal(state.visible?.targets.length, 2); assert.equal(state.shadow?.status, 'observed');
  assert.doesNotMatch(JSON.stringify(state), /private-player/);
  // Rejected unauthenticated traffic cannot cancel someone else's valid input.
  assert.equal((await post('/gsi', { ...p, allplayers: {}, auth: { token: s.operatorToken } }, {})).status, 401);
  assert.equal((await get()).shadow?.status, 'observed');
  for (const [path, allowed] of [['/gsi', 'POST'], ['/api/state', 'GET'], ['/api/confirm', 'POST'], ['/api/visible', 'POST'], ['/api/stop', 'POST']]) {
    const r = await fetch(s.origin + path, { method: 'PUT', headers: auth });
    assert.equal(r.status, 405); assert.equal(r.headers.get('allow'), allowed);
  }
  assert.equal((await fetch(s.origin + '/api/state?token=untrusted')).status, 404);
  for (const path of ['/api/act', '/api/plan', '/api/load-example', '/data/farming-742-routes.json', '/.env', '/debug']) {
    assert.equal((await fetch(s.origin + path, { headers: auth })).status, 404);
  }
  now += 5000; state = await get(); assert.equal(state.status.code, 'STALE_GSI');
  assert.deepEqual([state.own, state.visible, state.shadow], [null, null, null]);
  assert.equal((await post('/api/stop', { stop: false })).status, 400);
  assert.equal((await post('/api/stop', { stop: true })).status, 200);
  assert.equal((await gsi(playerPacket(now, '742000002', 5))).status, 410);
  assert.equal((await get()).status.code, 'STOPPED');
});

test('loopback Host and browser origin, MIME, invalid JSON, declared and streamed body bounds, and occupied port isolation', async t => {
  const s = await startFarmShadowBridge(); t.after(s.close);
  const auth = { Authorization: `Bearer ${s.operatorToken}` };
  const port = Number(new URL(s.origin).port);
  await assert.rejects(startFarmShadowBridge({ port }), (e: NodeJS.ErrnoException) => e.code === 'EADDRINUSE');
  const disallowed: Record<string, string>[] = [{ Origin: 'https://other.invalid' }, { Origin: 'null' }, { 'Sec-Fetch-Site': 'cross-site' }];
  for (const headers of disallowed) {
    const r = await fetch(s.origin + '/api/state', { headers: { ...headers, ...auth } });
    assert.equal(r.status, 403); assert.equal(r.headers.get('access-control-allow-origin'), null);
  }
  const hostStatus = await new Promise<number | undefined>((resolve, reject) => {
    const req = http.get(s.origin + '/', { headers: { Host: 'other.invalid:' + port } }, res => { res.resume(); resolve(res.statusCode); });
    req.once('error', reject);
  });
  assert.equal(hostStatus, 403);
  const req = (payload: string, mime = 'application/json', path = '/api/visible') =>
    fetch(s.origin + path, { method: 'POST', headers: { ...auth, 'Content-Type': mime }, body: payload });
  assert.equal((await req('{}', 'text/plain')).status, 415);
  assert.equal((await req('not JSON')).status, 400);
  assert.equal((await req('{}', 'application/json; charset=UTF-8')).status, 400);
  assert.equal((await req('x'.repeat(8193))).status, 413);
  assert.equal((await req('x'.repeat(65537), 'application/json', '/gsi')).status, 413);
  const streamed = await new Promise<number | undefined>((resolve, reject) => {
    const r = http.request(s.origin + '/api/visible', { method: 'POST', headers: { ...auth, 'Content-Type': 'application/json' } },
      res => { res.resume(); resolve(res.statusCode); });
    r.once('error', reject); r.write('x'.repeat(5120)); r.end('y'.repeat(4096));
  });
  assert.equal(streamed, 413);
  assert.equal((await fetch(s.origin + '/api/state', { headers: auth })).status, 200);
});
