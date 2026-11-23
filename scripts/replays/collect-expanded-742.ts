/** Explicit local acquisition only. No credentials, game launch, or cloud model. */
import fs from 'node:fs';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as wait } from 'node:timers/promises';
import { downloadReplay } from './download.ts';
import { replayArea, replayChildEnv } from '../../src/replays/storage.ts';

const run = promisify(execFile);
const root = replayArea('farm742-more'), oldRoot = replayArea('farm742');
const python = fs.existsSync('.tmp/replay-tools/Scripts/python.exe') ? '.tmp/replay-tools/Scripts/python.exe' : '.tmp/replay-tools/bin/python';
const read = (file: string) => JSON.parse(fs.readFileSync(file, 'utf8'));
const cohort = read(`${root}/eligible.json`);
const original = new Map<number, any>(read('data/farming-742-routes.json').matches.map((m: any) => [m.id, m]));
const progressFile = `${root}/progress.json`;
const history: Record<string, any> = fs.existsSync(progressFile) ? read(progressFile).results : {};
const exists = (dir: string, id: number, ext: string) => fs.existsSync(path.join(dir, `${id}.${ext}`));
const save = () => {
  fs.writeFileSync(progressFile + '.next', JSON.stringify({ version: 1, eligible: cohort.rows.length, updatedAt: new Date().toISOString(), results: history }, null, 2) + '\n');
  fs.renameSync(progressFile + '.next', progressFile);
};
function stage(id: number, status: string, extra: object = {}) {
  history[id] = { ...history[id], status, ...extra }; save();
  if (['reused', 'parsed', 'failed'].includes(status)) console.log(new Date().toISOString(), id, status, history[id].error ?? '');
}
function limiter(limit: number) {
  let n = 0; const queue: (() => void)[] = [];
  return async function <T>(job: () => Promise<T>): Promise<T> {
    if (n >= limit) await new Promise<void>(resolve => queue.push(resolve)); else n++;
    try { return await job(); } finally { if (queue.length) queue.shift()!(); else n--; }
  };
}
const network = limiter(4), cpu = limiter(5);
let nextMetadata = 0;
async function metadata(row: any) {
  const id = row.match_id, due = Math.max(nextMetadata, Date.now()); nextMetadata = due + 1250;
  await wait(Math.max(0, due - Date.now())); // stay below the public API's per-minute allowance
  const res = await fetch(`https://api.opendota.com/api/matches/${id}`, { redirect: 'error', signal: AbortSignal.timeout(42_000) });
  if (!res.ok) {
    if (res.status === 429) nextMetadata = Math.max(nextMetadata, Date.now() + Math.max(60, Number(res.headers.get('retry-after')) || 0) * 1000);
    throw new Error(`OpenDota HTTP ${res.status}`);
  }
  const bytes = Buffer.from(await res.arrayBuffer());
  if (bytes.length > 8 * 1024**2) throw new Error('Oversized metadata');
  const match = JSON.parse(bytes.toString()), player = match.players?.find((p: any) => p.hero_id === row.hero_id);
  if (match.match_id !== id || match.patch !== 61 || match.lobby_type !== 7 || !player ||
      (player.player_slot < 128) !== !!row.is_radiant || (player.account_id != null && player.account_id !== row.account_id))
    throw new Error('D2PT/OpenDota identity, patch, or ranked-lobby mismatch');
  return bytes;
}
async function acquire(row: any) {
  const id = row.match_id;
  if (original.get(id)?.hero === (row.hero_id === 48 ? 'luna' : 'troll_warlord') && exists(oldRoot, id, 'facts.json')) {
    stage(id, 'reused', { root: 'farm742', error: null }); return; // relative to the work root, not a machine path
  }
  if (exists(root, id, 'facts.json') && exists(root, id, 'acquisition.json')) {
    stage(id, 'parsed', { root: 'farm742-more', error: null }); return;
  }
  let currentStage = 'download';
  try {
    await network(async () => {
      if (!exists(root, id, 'acquisition.json')) {
        stage(id, 'download');
        // Keep interrupted files intact. Validate the retry in its own directory
        // before linking the completed acquisition back into the corpus.
        const dir = exists(root, id, 'archive.partial') || exists(root, id, 'archive') ?
          path.join(root, 'retries', String(id), String(Date.now())) : root;
        const bytes = await metadata(row);
        await downloadReplay(id, dir, bytes);
        if (dir !== root) {
          for (const ext of ['archive', 'opendota.json', 'acquisition.json']) {
            const name = `${id}.${ext}`;
            if (fs.existsSync(path.join(root, name))) throw new Error(`Existing unverified ${name}; no overwrite`);
            fs.linkSync(path.join(dir, name), path.join(root, name));
          }
        }
      }
      if (!exists(root, id, 'dem')) {
        currentStage = 'decompress';
        await run(python, ['scripts/replays/decompress.py', String(id), '--root', root], { windowsHide: true, env: replayChildEnv(root), timeout: 150_000 });
      }
      stage(id, 'ready');
    });
    await cpu(async () => {
      currentStage = 'parse'; stage(id, 'parsing');
      const hero = row.hero_id === 48 ? 'luna' : 'troll_warlord';
      const { stdout, stderr } = await run(python, ['scripts/replays/extract.py', String(id), '--root', root, '--hero', hero, '--route-core'],
        { windowsHide: true, env: replayChildEnv(root), timeout: 900_000, maxBuffer: 2 * 1024**2 });
      fs.writeFileSync(`${root}/${id}.parse.log`, stdout + stderr);
      stage(id, 'parsed', { root: 'farm742-more', error: null });
    });
  } catch (e: any) {
    const error = String(e.stderr || e.message || e).slice(-800);
    stage(id, 'failed', { failureStage: currentStage, error,
      failures: [...(history[id]?.failures ?? []), { stage: currentStage, error, at: new Date().toISOString() }] });
  }
}
if (cohort.rows.some((r: any) => ![48, 95].includes(r.hero_id) || !Number.isSafeInteger(r.match_id) || r.patch !== '7.42' ||
  r.position !== 'pos 1' || r.mmr < cohort.minimumMatchMmr || r.lobby_type !== 7) ||
  new Set(cohort.rows.map((r: any) => r.match_id)).size !== cohort.rows.length) throw new Error('Unexpected cohort IDs/filter');
const retrying = Object.values(history).some(r => r.status === 'failed');
const needsNetwork = cohort.rows.filter((r: any) => !original.has(r.match_id) && !exists(root, r.match_id, 'acquisition.json'));
if (retrying && needsNetwork.length) {
  // A service-wide outage is not a separate new acquisition attempt for every
  // match. Probe one candidate before a rerun; preserve the existing journal.
  try { await metadata(needsNetwork[0]); }
  catch (e) {
    console.error('One OpenDota probe failed; no batch retried, original diagnoses preserved:', (e as Error).message);
    process.exit(1);
  }
}
await Promise.all(cohort.rows.map(acquire));
const failures = Object.entries(history).filter(([, row]) => row.status === 'failed');
console.log(`Finished: ${cohort.rows.length} eligible, ${failures.length} acquisition failures`);
if (failures.length) process.exitCode = 1;
