import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { downloadReplay } from '../scripts/replays/download.ts';
import { replayArea, replayChildEnv } from '../src/replays/storage.ts';

test('download, interrupted archive and child scratch use the replay workspace; explicit directory wins', async t => {
  const parent = await fs.realpath(tmpdir()), work = await fs.mkdtemp(path.join(parent, 'dota-replay-storage-'));
  const previous = process.env.DOTA_COACH_REPLAY_WORK_ROOT;
  const set = (value: string | undefined) => {
    if (value === undefined) delete process.env.DOTA_COACH_REPLAY_WORK_ROOT;
    else process.env.DOTA_COACH_REPLAY_WORK_ROOT = value;
  };
  t.after(async () => {
    set(previous);
    const target = await fs.realpath(work);
    assert.equal(path.dirname(target), parent); assert.ok(path.basename(target).startsWith('dota-replay-storage-'));
    await fs.rm(target, { recursive: true, force: true });
  });
  set(undefined);
  if (process.platform === 'win32') assert.equal(replayArea(), 'Z:\\dota2coach\\data\\replays');
  set(work);
  const dir = path.join(work, 'replays'), other = path.join(work, 'explicit dir');
  const id = 123, meta = Buffer.from(JSON.stringify({ match_id: id, replay_url: `https://replay1.valve.net/570/${id}_2.dem.bz2` }));
  let good = true, requests = 0;
  t.mock.method(globalThis, 'fetch', async () => { requests++; return new Response(good ? 'BZhfixture' : 'invalid'); });
  await downloadReplay(id, undefined, meta);
  assert.equal(await fs.readFile(path.join(dir, `${id}.archive`), 'utf8'), 'BZhfixture');
  assert.deepEqual(await fs.readFile(path.join(dir, `${id}.opendota.json`)), meta);
  const oldTemp = process.env.TEMP, child = replayChildEnv(dir);
  for (const key of ['TMP', 'TEMP', 'TMPDIR']) assert.equal(child[key], path.join(dir, 'tmp'));
  assert.ok((await fs.stat(child.TEMP!)).isDirectory()); assert.equal(process.env.TEMP, oldTemp);
  good = false; set(path.join(work, 'broken'));
  await fs.writeFile(process.env.DOTA_COACH_REPLAY_WORK_ROOT!, 'not a directory');
  await assert.rejects(downloadReplay(id, undefined, meta)); // cannot fall back to the C: drive
  assert.equal(requests, 1);
  await assert.rejects(downloadReplay(id, other, meta), /neither bzip2 nor Zstandard/);
  assert.deepEqual(await fs.readdir(other), [`${id}.archive.partial`]);
  assert.equal(await fs.readFile(path.join(dir, `${id}.archive`), 'utf8'), 'BZhfixture');
});
