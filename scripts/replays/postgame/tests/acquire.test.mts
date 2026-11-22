import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { acquire } from '../acquire.mts';

const id = 9097559249, url = `http://replay442.valve.net/570/${id}_600743976.dem.bz2`;
const complete = { match_id: id, duration: 1487, radiant_win: false, start_time: 1000, replay_url: url };
const original = globalThis.fetch;

test('completed metadata and strict public host/path BEFORE the archive GET', async () => {
  for (const override of [
    { match_id: 507624657 }, { duration: 0 }, { start_time: 99999999999 }, { radiant_win: null },
    { replay_url: null }, { replay_url: url + '?token=a' }, { replay_url: url + '#hash' },
    { replay_url: url.replace('442.valve.net', '442.valve.net.bad.test') },
    { replay_url: url.replace('replay442', 'user:pw@replay442') },
    { replay_url: url.replace('replay442.valve.net', '127.0.0.1') }, { replay_url: url.replace('570', '571') },
    { replay_url: url.replace('570', 'a/../570') }, { replay_url: url.replace('net/', 'net:80/') },
    { replay_url: url.replace(String(id), '123') }, { replay_url: 'file:///game.dem' },
  ]) {
    const requests: string[] = [];
    globalThis.fetch = async (input, init) => {
      requests.push(String(input));
      assert.equal(init?.redirect, 'error');
      assert.equal(init?.credentials, 'omit');
      return Response.json({ ...complete, ...override });
    };
    try {
      await assert.rejects(acquire(id, 'not-created'));
      assert.deepEqual(requests, [`https://api.opendota.com/api/matches/${id}`]);
    } finally { globalThis.fetch = original; }
  }
});

test('bounded error/missing/expired metadata, streaming and declared limits', async () => {
  for (const make of [
    () => new Response(null, { status: 404 }),
    () => new Response(null, { status: 429 }),
    () => new Response(null, { status: 302, headers: { Location: 'https://elsewhere.test' } }),
    () => new Response('not-json'),
    () => new Response('x', { headers: { 'Content-Length': String(9 * 1024 ** 2) } }),
    () => new Response(new Uint8Array(8 * 1024 ** 2 + 1)),
  ]) {
    let calls = 0;
    globalThis.fetch = async () => { calls++; return make(); };
    try { await assert.rejects(acquire(id, 'not-created')); assert.equal(calls, 1); }
    finally { globalThis.fetch = original; }
  }
});

test('reuse actual downloader; no follow, no retries, correct digest and compression magic', async () => {
  for (const mode of ['ok', 'expired', 'oversized', 'not-compressed']) {
    const dir = await mkdtemp(path.join(tmpdir(), 'postgame-acquire-'));
    const requests: string[] = [];
    globalThis.fetch = async (input, init) => {
      requests.push(String(input));
      assert.equal(init?.redirect, 'error');
      assert.ok(init?.signal);
      if (requests.length === 1) return Response.json(complete);
      if (mode === 'expired') return new Response(null, { status: 410 });
      if (mode === 'oversized') return new Response('x', { headers: { 'content-length': '536870913' } });
      return new Response(mode === 'ok' ? 'BZh9fixture' : 'html');
    };
    try {
      if (mode === 'ok') {
        const m = await acquire(id, dir);
        assert.equal(m.matchId, id);
        assert.equal(m.compression, 'bzip2');
        assert.equal(m.archiveSha256, createHash('sha256').update('BZh9fixture').digest('hex'));
        assert.equal(await readFile(path.join(dir, `${id}.archive`), 'utf8'), 'BZh9fixture');
      } else await assert.rejects(acquire(id, dir));
      assert.deepEqual(requests, [`https://api.opendota.com/api/matches/${id}`, url]);
    } finally { globalThis.fetch = original; await rm(dir, { recursive: true, force: true }); }
  }
});
