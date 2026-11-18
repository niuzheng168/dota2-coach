import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { test } from 'node:test';
import { pathToFileURL } from 'node:url';
import { listScanSessions, readScanTimeline, validSession } from '../src/sandbox/hero-scan.ts';
import { createSandboxServer } from '../src/sandbox/server.ts';

function scratch() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hero-scan-'));
  return { dir, root: pathToFileURL(dir + path.sep) };
}
const line = (value: unknown) => JSON.stringify(value) + '\n';
const header = { type: 'header', started: '2026-11-18T12:00:00+0800', truth: true, every: 0.5, slots: [], names: { heroes: {}, items: {} } };
const snapshot = (t: number) => ({ type: 'snapshot', t, clock: 600 + t, mode: 'team-view', heroes: {} });

test('session names cannot leave the recordings folder', () => {
  for (const ok of ['20261118-120000', 'live-topgame_1']) assert.ok(validSession(ok));
  for (const bad of ['', '..', '../x', 'a/b', 'a\\b', 'x'.repeat(65), 'a b', '%2e%2e']) assert.equal(validSession(bad), false);
});

test('timeline reads complete lines only and continues from the returned offset', async () => {
  const { dir, root } = scratch();
  fs.mkdirSync(path.join(dir, 's1'));
  const file = path.join(dir, 's1', 'timeline.jsonl');
  fs.writeFileSync(file, line(header) + line(snapshot(0.5)) + '{"type":"snapshot","t":1');  // last line still being written
  const first = await readScanTimeline('s1', 0, root);
  assert.equal(first.header.every, 0.5);
  assert.deepEqual(first.snapshots.map(s => s.t), [0.5]);
  assert.equal(first.next, Buffer.byteLength(line(header) + line(snapshot(0.5))));
  fs.appendFileSync(file, ',"clock":601,"mode":"team-view","heroes":{}}\n' + line(snapshot(1.5)));
  const second = await readScanTimeline('s1', first.next, root);
  assert.equal(second.header, null);
  assert.deepEqual(second.snapshots.map(s => s.t), [1, 1.5]);
  assert.equal(second.next, second.size);
  // A recording replaced by a shorter one restarts the reader instead of reading mid-line.
  const replaced = await readScanTimeline('s1', second.size + 10, root);
  assert.equal(replaced.restarted, true);
  await assert.rejects(readScanTimeline('../s1', 0, root));
  await assert.rejects(readScanTimeline('s1', -1, root));
});

test('session list reads headers and summaries and skips folders that are not recordings', async () => {
  const { dir, root } = scratch();
  for (const name of ['done', 'running', 'not a session', 'empty']) fs.mkdirSync(path.join(dir, name));
  fs.writeFileSync(path.join(dir, 'done', 'timeline.jsonl'), line(header) + line(snapshot(0.5)));
  fs.writeFileSync(path.join(dir, 'done', 'summary.json'), JSON.stringify({ readings: 3 }));
  fs.writeFileSync(path.join(dir, 'running', 'timeline.jsonl'), line({ ...header, truth: false }));
  fs.writeFileSync(path.join(dir, 'not a session', 'timeline.jsonl'), line(header));
  const sessions = await listScanSessions(root);
  assert.deepEqual(sessions.map(s => s.id).sort(), ['done', 'running']);
  const done = sessions.find(s => s.id === 'done')!, running = sessions.find(s => s.id === 'running')!;
  assert.deepEqual([done.truth, done.finished, done.summary], [true, true, { readings: 3 }]);
  assert.deepEqual([running.truth, running.finished], [false, false]);
  assert.deepEqual(await listScanSessions(pathToFileURL(path.join(dir, 'missing') + path.sep)), []);
});

test('scan page and portraits are local static files; recordings need the session token', async () => {
  const { server, token } = createSandboxServer();
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const address = server.address();
  const origin = `http://127.0.0.1:${typeof address === 'object' && address ? address.port : 0}`;
  try {
    assert.match(await (await fetch(origin + '/hero-scan')).text(), /视觉扫描对局时间轴/);
    const portrait = await fetch(origin + '/heroes/luna.jpg');
    assert.equal(portrait.status, 200); assert.equal(portrait.headers.get('content-type'), 'image/jpeg');
    assert.equal((await fetch(origin + '/heroes/not_a_hero.jpg')).status, 404);
    assert.equal((await fetch(origin + '/heroes/luna.svg')).status, 401, 'only jpg/png portraits are static');
    assert.equal((await fetch(origin + '/api/hero-scan')).status, 401);
    const auth = { headers: { Authorization: `Bearer ${token}` } };
    const list = await fetch(origin + '/api/hero-scan', auth);
    assert.equal(list.status, 200); assert.ok(Array.isArray(((await list.json()) as any).sessions));
    assert.equal((await fetch(origin + '/api/hero-scan/bad%20name', auth)).status, 400);
    assert.equal((await fetch(origin + '/api/hero-scan/no-such-session-xyz', auth)).status, 404);
  } finally { server.close(); }
});
