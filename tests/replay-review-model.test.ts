import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test, type TestContext } from 'node:test';
import { promisify } from 'node:util';
import { createReviewClient, historicalReviews, matchesReviewProfile, reviewDirectory, reviewProfile, ReviewTargetError } from '../scripts/replays/review-model.ts';

const profile = reviewProfile({}), key = 'synthetic-local-model-key';
const catalogRow = { id: 'gpt-6.2-sol', display_name: 'GPT-6.2 Sol (github-copilot)', supported_endpoints: ['/responses'],
  policy: { state: 'enabled' }, capabilities: { supports: { reasoning_effort: ['high', 'xhigh'] } } };
type Call = { url: string; method?: string; auth?: string; body: any };
async function gateway(t: TestContext, reply: (call: Call) => { json?: unknown; status?: number; location?: string }) {
  const calls: Call[] = [];
  const server = createServer(async (req, res) => {
    let text = ''; for await (const chunk of req) text += chunk;
    const call = { url: req.url!, method: req.method, auth: req.headers.authorization, body: text ? JSON.parse(text) : null };
    calls.push(call);
    const { json, status, location } = reply(call);
    res.writeHead(status ?? 200, { 'Content-Type': 'application/json', ...(location && { Location: location }) });
    res.end(JSON.stringify(json ?? {}));
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => new Promise<void>(ok => server.close(() => ok())));
  const addr = server.address(); assert.ok(addr && typeof addr !== 'string');
  return { calls, env: { CODEY_MODEL_API_KEY: key, DOTA_COACH_COPILOT_BASE_URL: `http://127.0.0.1:${addr.port}/v1` } };
}

test('replay defaults ignore live Azure settings; explicit provider/model/effort select a distinct cache', () => {
  const live = { JUNGLE_PLANNER_PROVIDER: 'azure', JUNGLE_PLANNER_MODEL: 'gpt-6-astra' };
  assert.deepEqual(reviewProfile(live), { provider: 'codey-copilot', model: 'gpt-6.2-sol', reasoningEffort: 'xhigh' });
  const azure = reviewProfile({ ...live, REPLAY_REVIEW_PROVIDER: 'azure' });
  assert.deepEqual(azure, { provider: 'azure', model: 'gpt-6-astra', reasoningEffort: 'medium' });
  assert.notEqual(reviewDirectory(azure), reviewDirectory(profile));
  assert.equal(matchesReviewProfile({ version: 1, model: 'gpt-6-astra' }, profile), false);
  assert.equal(matchesReviewProfile({ version: 2, ...profile, reasoningEffort: 'medium' }, profile), false);
  assert.throws(() => reviewProfile({ REPLAY_REVIEW_MODEL: '../other' }));
  assert.throws(() => reviewProfile({ REPLAY_REVIEW_PROVIDER: 'auto' }));
  assert.throws(() => reviewProfile({ REPLAY_REVIEW_REASONING_EFFORT: 'typo' }));
});

test('gateway credentials never fall back; only literal loopback /v1 is allowed', async () => {
  await assert.rejects(createReviewClient(profile, { AZURE_API_KEY: 'wrong', OPENAI_API_KEY: 'wrong', GITHUB_TOKEN: 'wrong' }), /CODEY_MODEL_API_KEY/);
  for (const url of ['https://example.org/v1', 'http://127.1:4141/v1', 'http://localhost/admin/../v1',
    'http://u@127.0.0.1/v1', 'http://127.0.0.1/v1?upstream=x']) {
    await assert.rejects(createReviewClient(profile, { CODEY_MODEL_API_KEY: key, DOTA_COACH_COPILOT_BASE_URL: url }), /loopback/);
  }
});

test('a missing effort, ambiguous/foreign model, wrong endpoint, policy, partial catalog or redirect prevents POST', async t => {
  let json: any, status = 200;
  const { env, calls } = await gateway(t, () => ({ json, status, location: status === 302 ? '/must-not-follow' : undefined }));
  const client = await createReviewClient(profile, env);
  for (const rows of [[], [catalogRow, catalogRow], [{ ...catalogRow, display_name: 'Sol (codex)' }],
    [{ ...catalogRow, supported_endpoints: ['ws:/responses', '/v1/responses'] }],
    [{ ...catalogRow, policy: { state: 'disabled' } }],
    [{ ...catalogRow, capabilities: { supports: { reasoning_effort: ['high'] } } }], [{ ...catalogRow, capabilities: {} }]]) {
    json = { data: rows };
    await assert.rejects(client.generate('JSON', 'fixture', AbortSignal.timeout(3000)), ReviewTargetError);
  }
  json = { data: [catalogRow], has_more: true };
  await assert.rejects(client.generate('JSON', 'fixture', AbortSignal.timeout(3000)), ReviewTargetError);
  status = 302;
  await assert.rejects(client.generate('JSON', 'fixture', AbortSignal.timeout(3000)), /HTTP 302/);
  assert.ok(calls.every(c => c.method === 'GET' && c.url === '/v1/models' && !c.body));
});

test('refuse a response from a different model or an explicitly downgraded effort', async t => {
  let response: any;
  const { env, calls } = await gateway(t, call => ({ json: call.method === 'GET' ? { data: [catalogRow] } : response }));
  const client = await createReviewClient(profile, env);
  for (response of [{ model: 'gpt-6-astra' }, {}, { model: profile.model, reasoning: { effort: 'high' } }]) {
    await assert.rejects(client.generate('JSON', 'fixture', AbortSignal.timeout(3000)), ReviewTargetError);
  }
  assert.equal(calls.filter(c => c.method === 'POST').length, 3);
});

test('study CLI: catalog-only check, real HTTP request shape, profile-scoped resume and untouched Astra archive', async t => {
  const parent = await fs.realpath(tmpdir()), cwd = await fs.mkdtemp(path.join(parent, 'dota-replay-review-'));
  t.after(async () => {
    const target = await fs.realpath(cwd);
    assert.equal(path.dirname(target), parent); assert.ok(path.basename(target).startsWith('dota-replay-review-'));
    await fs.rm(target, { recursive: true, force: true });
  });
  const write = (f: string, v: unknown) => fs.writeFile(path.resolve(cwd, f), JSON.stringify(v));
  const scripts = path.join(cwd, 'scripts/replays');
  await fs.mkdir(scripts, { recursive: true });
  // Isolated CLI and storage root: no workspace .env, real replay share, or real upstream.
  for (const file of ['review-hundred.ts', 'review-model.ts'])
    await fs.copyFile(new URL(`../scripts/replays/${file}`, import.meta.url), path.join(scripts, file));
  await fs.mkdir(path.join(cwd, 'src/replays'), { recursive: true });
  await fs.copyFile(new URL('../src/replays/storage.ts', import.meta.url), path.join(cwd, 'src/replays/storage.ts'));
  const work = path.join(cwd, 'external replay data'), dir = path.join(work, 'replays');
  await write('package.json', { type: 'module' });
  const id = 123, result = { matchId: id, summary: 'fixture', observations: Array.from({ length: 4 }, () => ({ evidenceIds: ['123:w0'] })),
    counterexamples: [], unknowns: [] };
  const { env, calls } = await gateway(t, call => ({ json: call.method === 'GET' ? { data: [catalogRow] } :
    { model: profile.model, reasoning: call.body.reasoning, status: 'completed', id: 'resp-fixture',
      output: [{ type: 'message', content: [{ type: 'output_text', text: JSON.stringify(result) }] }] } }));
  const cli = (flag: string, extra = {}) => promisify(execFile)(process.execPath, [path.join(scripts, 'review-hundred.ts'), flag],
    { cwd, windowsHide: true, timeout: 30000, env: { SystemRoot: process.env.SystemRoot, DOTA_COACH_REPLAY_WORK_ROOT: work, ...env, ...extra } });
  const checked = JSON.parse((await cli('--check-model')).stdout);
  assert.deepEqual(checked, { ...profile, catalog: 'confirmed', inferenceRequests: 0 });
  assert.deepEqual(calls.map(c => c.url), ['/v1/models']);
  await assert.rejects(fs.stat(path.join(cwd, '.tmp')), { code: 'ENOENT' });
  await assert.rejects(cli('--check-modle')); // A typo cannot start a paid study.
  await assert.rejects(fs.stat(work), { code: 'ENOENT' });
  const study = path.join(dir, 'study'), archive = historicalReviews(dir);
  await fs.mkdir(archive, { recursive: true }); await fs.mkdir(path.join(cwd, 'data'));
  const old = { version: 1, model: 'gpt-6-astra' };
  await write(`${archive}/${id}.json`, old);
  await write(`${dir}/hundred-progress.json`, { complete: true, completed: [{ matchId: id }] });
  await write(`${study}/${id}.evidence.json`, { matchId: id, worldSnapshots: [], windows: [{}] });
  await write('data/dota2-map-reference.json', { camps: [], towers: [] });
  await cli('--one'); await cli('--one');
  assert.deepEqual(calls.map(c => c.url), ['/v1/models', '/v1/models', '/v1/responses']);
  assert.ok(calls.every(c => c.auth === `Bearer ${key}`));
  const body = calls.at(-1)!.body;
  assert.equal(body.model, profile.model); assert.deepEqual(body.reasoning, { effort: 'xhigh' });
  assert.equal(body.store, false); assert.equal(body.text.format.type, 'json_object');
  assert.equal(body.input[0].content[0].type, 'input_text'); assert.match(body.input[0].content[0].text, /123:w0/);
  const read = async (folder: string) => JSON.parse(await fs.readFile(path.join(folder, `${id}.json`), 'utf8'));
  assert.ok(matchesReviewProfile(await read(reviewDirectory(profile,dir)), profile));
  await cli('--one', { REPLAY_REVIEW_REASONING_EFFORT: 'high' });
  assert.deepEqual(calls.filter(c => c.method === 'POST').map(c => c.body.reasoning.effort), ['xhigh', 'high']);
  const current = reviewDirectory(profile,dir);
  await write(`${current}/${id}.json`, { ...await read(current), provider: 'azure' });
  const before = calls.length;
  await assert.rejects(cli('--one'));
  assert.equal(calls.length, before, 'do not reuse or overwrite a mismatched profile');
  assert.deepEqual(await read(archive), old);
  await assert.rejects(fs.stat(path.join(cwd, '.tmp')), { code: 'ENOENT' });
});
