import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { AccessStore } from '../src/access-store.ts';

test('production startup honors loopback binding, external data and secure cookies', { timeout: 15_000 }, async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'dota2-startup-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const store = await AccessStore.open(directory);
  await store.createUser('deploy-test', 'startup-test-password', true);

  const child = spawn(process.execPath, ['src/server.ts'], {
    cwd: fileURLToPath(new URL('../', import.meta.url)),
    env: {
      ...process.env,
      HOST: '127.0.0.1',
      PORT: '0',
      APP_DATA_DIR: directory,
      COOKIE_SECURE: '1',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) {
      const exited = once(child, 'exit');
      child.kill('SIGTERM');
      await exited;
    }
  });

  const port = await new Promise<number>((resolve, reject) => {
    let output = '';
    const timer = setTimeout(() => reject(new Error('Server did not start')), 10_000);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', () => { clearTimeout(timer); reject(new Error('Server exited before startup')); });
    child.stdout.on('data', chunk => {
      output += chunk.toString();
      const match = /listening on http:\/\/127\.0\.0\.1:(\d+)/.exec(output);
      if (match) { clearTimeout(timer); resolve(Number(match[1])); }
    });
  });
  assert.ok(port > 0);

  const response = await fetch(`http://127.0.0.1:${port}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'deploy-test', password: 'startup-test-password' }),
  });
  assert.equal(response.status, 200);
  assert.equal((await response.json()).user.username, 'deploy-test');
  for (const flag of ['Secure', 'HttpOnly', 'SameSite=Lax']) {
    assert.ok(response.headers.get('set-cookie')?.includes(flag));
  }
});
