import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';
import { DEFAULT_FOUNDRY_CONFIG } from '../src/azure-auth.ts';
import type { FoundryConfig } from '../src/azure-auth.ts';
import { recognizeDraftFromTopBarImage } from '../src/auto-draft.ts';

const image = 'data:image/png;base64,dGVzdA==';
const config: FoundryConfig = {
  ...DEFAULT_FOUNDRY_CONFIG,
  openAiHost: 'primary-resource.openai.azure.com',
  apiKey: 'test-primary-key',
  deployments: { ...DEFAULT_FOUNDRY_CONFIG.deployments, visionLlm: 'custom-vision-deployment' },
};
const draft = {
  phase: 'in_game',
  radiant: ['斧王', 'Axe', 'not a hero', 'Crystal Maiden', 'Lina', 'Pudge', 'Drow Ranger', 'Tiny'],
  dire: ['Axe', 'Invoker', 'Earthshaker', 'Lion', 'Slark', 'Dazzle', 'Tiny'],
};

test('vision defaults to the actual deployment name and honors an explicit override', () => {
  for (const [override, expected] of [['', 'gpt-6.1-terra'], ['my-vision-deployment', 'my-vision-deployment']]) {
    const result = spawnSync(process.execPath, ['--input-type=module', '-e', `
      import { DEFAULT_FOUNDRY_CONFIG } from './src/azure-auth.ts';
      process.stdout.write(DEFAULT_FOUNDRY_CONFIG.deployments.visionLlm);
    `], {
      cwd: new URL('..', import.meta.url),
      env: { ...process.env, DEPLOYMENT_GPT_VISION: override },
      encoding: 'utf8',
    });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout, expected);
  }
});

test('vision uses the selected resource credentials, deployment, image and structured output', async t => {
  const requests = t.mock.method(globalThis, 'fetch', async (url: unknown, init?: RequestInit) => {
    assert.equal(url, 'https://primary-resource.openai.azure.com/openai/v1/responses');
    const headers = new Headers(init?.headers);
    assert.equal(headers.get('api-key'), 'test-primary-key');
    assert.equal(headers.get('authorization'), null);
    assert.equal(init?.method, 'POST');
    assert.ok(init?.signal);
    const body = JSON.parse(String(init?.body));
    assert.equal(body.model, 'custom-vision-deployment');
    assert.equal(body.input[1].content[1].image_url, image);
    assert.equal(body.text.format.type, 'json_schema');
    assert.equal(body.text.format.strict, true);
    return Response.json({ output: [{ type: 'message', content: [{ type: 'output_text', text: JSON.stringify(draft) }] }] });
  });
  const result = await recognizeDraftFromTopBarImage(image, config);
  assert.equal(result.phase, 'in_game');
  assert.deepEqual(result.radiant, ['Axe', 'Crystal Maiden', 'Lina', 'Pudge', 'Drow Ranger']);
  assert.deepEqual(result.dire, ['Invoker', 'Earthshaker', 'Lion', 'Slark', 'Dazzle']);
  assert.equal(new Set(result.heroes).size, 10);
  assert.equal(result.model, 'custom-vision-deployment');
  assert.ok(result.latencyMs >= 1);
  assert.equal(requests.mock.callCount(), 1);
});

test('GPT-6.1 vision uses supported reasoning efforts without changing deployment routing', async t => {
  const bodies: Record<string, any>[] = [];
  t.mock.method(globalThis, 'fetch', async (_url: unknown, init?: RequestInit) => {
    bodies.push(JSON.parse(String(init?.body)));
    return Response.json({ output_text: JSON.stringify({ phase: 'menu_or_other', radiant: [], dire: [] }) });
  });
  const cases: [string, string | undefined][] = [
    ['gpt-6.1-terra', 'none'],
    ['gpt-6.1-sol', 'low'],
    ['gpt-6.1-sol-custom-deployment', 'low'],
    ['gpt-6-luna', 'none'],
    ['gpt-5', 'minimal'],
    ['custom-vision-deployment', undefined],
  ];
  for (const [deployment, effort] of cases) {
    const result = await recognizeDraftFromTopBarImage(image, {
      ...config, deployments: { ...config.deployments, visionLlm: deployment },
    });
    assert.equal(bodies.at(-1)?.model, deployment);
    assert.equal(bodies.at(-1)?.reasoning?.effort, effort);
    assert.equal(result.model, deployment);
  }
});

test('DeploymentNotFound identifies the resource and deployment setting without exposing credentials', async t => {
  t.mock.method(globalThis, 'fetch', async () => Response.json({
    error: { code: 'DeploymentNotFound', message: 'The API deployment for this resource does not exist.' },
  }, { status: 404 }));
  await assert.rejects(recognizeDraftFromTopBarImage(image, config), (error: Error) => {
    assert.match(error.message, /DeploymentNotFound.*404/);
    assert.match(error.message, /custom-vision-deployment/);
    assert.match(error.message, /primary-resource\.openai\.azure\.com/);
    assert.match(error.message, /DEPLOYMENT_GPT_VISION/);
    assert.doesNotMatch(error.message, /test-primary-key/);
    return true;
  });
});

test('other upstream failures remain errors and invalid images never call the upstream', async t => {
  const requests = t.mock.method(globalThis, 'fetch', async () =>
    Response.json({ error: { code: 'TooManyRequests' } }, { status: 429 }));
  await assert.rejects(recognizeDraftFromTopBarImage('https://example.test/image.png', config), /data:image/);
  assert.equal(requests.mock.callCount(), 0);
  await assert.rejects(recognizeDraftFromTopBarImage(image, config), /Vision API HTTP 429.*TooManyRequests/);
});

test('empty or malformed output is not cached as a successful menu detection', async t => {
  let response: unknown = { output: [] };
  t.mock.method(globalThis, 'fetch', async () => Response.json(response));
  await assert.rejects(recognizeDraftFromTopBarImage(image, config), /未返回识别结果/);
  for (const value of [null, {}, { phase: 'in_game', radiant: 'Axe', dire: [] }]) {
    response = { output_text: JSON.stringify(value) };
    await assert.rejects(recognizeDraftFromTopBarImage(image, config), /阵容格式无效/);
  }
  response = { output_text: JSON.stringify({ phase: 'menu_or_other', radiant: [], dire: [] }) };
  assert.deepEqual((await recognizeDraftFromTopBarImage(image, config)).heroes, []);
});

// Exercise the actual watcher/crop functions with deterministic pixels and mocked network;
// no real screen capture, browser dependency, credentials or paid requests are needed.
const appSource = fs.readFileSync(new URL('../public/app.js', import.meta.url), 'utf8');
const watcherSource = appSource.slice(
  appSource.indexOf('let visionStream = null;'),
  appSource.indexOf('async function startVisionWatch()'),
);
function watcher(fetchImpl: typeof fetch) {
  const pixels = new Uint8ClampedArray(64 * 12 * 4).fill(100);
  const ctx = { drawImage() {}, getImageData: () => ({ data: pixels }) };
  const context = vm.createContext({
    document: { createElement: () => ({ getContext: () => ctx, toDataURL: () => image }) },
    activeRoom: { roomId: 'room-a', isOwner: true },
    matchHeroes: [], csrfToken: 'test-csrf',
    visionStatusLine: { textContent: '' }, toggleVisionWatchBtn: null,
    fetch: fetchImpl, AbortSignal, clearInterval,
  });
  vm.runInContext(watcherSource + '\nvisionVideo = { videoWidth: 1920, videoHeight: 1080 };', context);
  return {
    tick: (force = false) => vm.runInContext(`runVisionTick(${force})`, context) as Promise<void>,
    read: (script: string) => vm.runInContext(script, context),
    context,
  };
}

test('a failed static frame is retried, while a successfully recognized static frame is skipped', async () => {
  let calls = 0;
  const w = watcher(async () => {
    calls++;
    return calls === 1
      ? Response.json({ ok: false, error: 'DeploymentNotFound' }, { status: 500 })
      : Response.json({ ok: true });
  });
  await w.tick();
  assert.match(w.context.visionStatusLine.textContent, /DeploymentNotFound/);
  assert.equal(w.read('lastTopBarThumb'), null);
  await w.tick();
  assert.equal(calls, 2);
  assert.ok(w.read('lastTopBarThumb'));
  await w.tick();
  assert.equal(calls, 2);
});

test('a failed forced scan invalidates an old successful frame even with ten locked heroes', async () => {
  let calls = 0;
  const w = watcher(async () => {
    calls++;
    if (calls === 2) throw new Error('network unavailable');
    return Response.json({ ok: true });
  });
  await w.tick();
  w.context.matchHeroes = new Array(10).fill('hero');
  await w.tick(true);
  assert.match(w.context.visionStatusLine.textContent, /network unavailable/);
  assert.equal(w.read('lastTopBarThumb'), null);
  assert.equal(w.read('visionBusy'), false);
  await w.tick();
  assert.equal(calls, 3);
});

test('late responses do not update a stopped watcher or a different room', async () => {
  for (const change of ['stopVisionWatch()', 'activeRoom = { roomId: "room-b", isOwner: true }']) {
    let resolve!: (response: Response) => void;
    const w = watcher(() => new Promise<Response>(r => { resolve = r; }));
    const pending = w.tick();
    w.read(change);
    resolve(Response.json({ ok: true }));
    await pending;
    assert.equal(w.read('lastTopBarThumb'), null);
    assert.equal(w.read('visionBusy'), false);
  }
});
