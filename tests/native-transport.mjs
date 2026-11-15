// Optional real .NET transport integration on Linux: temporary server, fake translation engines, no cloud costs.
import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { WebSocketServer } from 'ws';
import { AccessStore } from '../src/access-store.ts';
import { createCoachServer } from '../src/app-server.ts';

const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'native-transport-'));
const store = await AccessStore.open(directory);
await store.createUser('native-owner', 'native-test-only', true);
await store.createUser('native-guest', 'native-test-only');
const server = createCoachServer(store, (kind, options, callbacks) => {
  let sent = false;
  return { kind, role: options.role, async start() {}, async stop() {}, markSpeechEnd() {},
    appendAudio() {
      if (sent) return;
      sent = true;
      callbacks.onSourceFinal('native-' + options.inputKind, 'Go Roshan');
      callbacks.onTargetFinal('native-' + options.inputKind, '打肉山');
      callbacks.onTargetAudio('native', Buffer.alloc(480));
    },
  };
});
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const origin = `http://127.0.0.1:${server.address().port}`;
// Proxy ordinary HTTP/WS to the actual app; one path simulates a stale server ignoring the listener handshake.
const old = new WebSocketServer({ noServer: true });
const gateway = http.createServer((req, res) => {
  const headers = { ...req.headers, host: new URL(origin).host };
  if (req.headers.origin === `http://${req.headers.host}`) headers.origin = origin;
  const upstream = http.request(origin + req.url, { method: req.method, headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
  upstream.on('error', () => { res.writeHead(502); res.end(); }); req.pipe(upstream);
});
gateway.on('upgrade', (req, socket, head) => {
  if (new URL(req.url, origin).searchParams.get('room') === 'outdated') {
    old.handleUpgrade(req, socket, head, ws => ws.send(JSON.stringify({ type: 'engine.state', state: 'ready' })));
    return;
  }
  const originalHost = req.headers.host;
  req.headers.host = new URL(origin).host;
  if (req.headers.origin === `http://${originalHost}`) req.headers.origin = origin;
  server.emit('upgrade', req, socket, head);
});
gateway.listen(0, '127.0.0.1'); await once(gateway, 'listening');
try {
  const command = process.env.DOTNET || 'dotnet';
  const child = spawn(command, ['run', '--project', 'desktop/DotaCoach.CoreTests', '-c', 'Release', '--', '--transport', `http://127.0.0.1:${gateway.address().port}`], { stdio: 'inherit' });
  const timeout = setTimeout(() => child.kill(), 90000);
  const [code] = await once(child, 'exit'); clearTimeout(timeout);
  assert.equal(code, 0);
} finally {
  for (const ws of old.clients) ws.terminate();
  old.close(); await new Promise(resolve => gateway.close(resolve)); await new Promise(resolve => server.close(resolve));
  await fs.rm(directory, { recursive: true, force: true });
}
