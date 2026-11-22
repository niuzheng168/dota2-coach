// Opt-in local integration test: no npm install, running Codey, live credentials or inference.
import assert from 'node:assert/strict';
import { fork, spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import { homedir, tmpdir } from 'node:os';
import { basename, delimiter, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { coachUA, labels, models, startMock } from '../tests/codey/copilot-mock.mjs';

const { values } = parseArgs({ options: {
  gateway: { type: 'string' }, 'routes-only': { type: 'boolean' }, evidence: { type: 'string' },
} });
const gateway = realpathSync(values.gateway ?? join(homedir(),
  '.local/share/codey-machine-windows/releases/package-0.3.43-b06e2556da50d9ba/app/gateway'));
const tests = fileURLToPath(new URL('../tests/codey/', import.meta.url));
const workerFile = join(tests, 'gateway.mjs'), imageFile = join(tests, 'gray.jpg');
const pixels = readFileSync(imageFile), jpeg = `data:image/jpeg;base64,${pixels.toString('base64')}`;
const frontKey = `smoke-front-${randomUUID()}`, upstreamKey = `smoke-up-${randomUUID()}`, alternateKey = `smoke-alt-${randomUUID()}`;
const tmp = realpathSync(tmpdir()), home = mkdtempSync(join(tmp, 'dota-codey-smoke-'));
const keyFile = join(home, 'client.env'), audit = join(home, 'front.ndjson');
const dotnetRoot = join(process.env.ProgramFiles || 'C:\\Program Files', 'dotnet');
const dotnet = join(dotnetRoot, 'dotnet.exe');
// Start with an allowlist, not a copy of process.env minus a handful of known secrets.
const env = {
  SystemRoot: process.env.SystemRoot || 'C:\\Windows', ProgramFiles: dirname(dotnetRoot),
  PATH: [dirname(process.execPath), dotnetRoot, join(process.env.SystemRoot || 'C:\\Windows', 'System32')].join(delimiter),
  HOME: home, USERPROFILE: home, TEMP: home, TMP: home, APPDATA: join(home, 'roaming'), LOCALAPPDATA: join(home, 'local'),
  COPILOT_API_HOME: home, CODEY_SETTINGS_FILE: join(home, 'settings.json'), CODEX_HOME: join(home, 'codex'),
  COPILOT_API_SQLITE_DB_PATH: ':memory:',
  DOTNET_CLI_HOME: home, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false',
  DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: 'false', DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: 'true', MSBuildEnableWorkloadResolver: 'false',
  NUGET_PACKAGES: join(home, 'packages'), NO_COLOR: '1',
  DOTA_COACH_FOUNDRY_ENV_FILE: join(home, 'no-foundry-config'),
};
const artifacts = join(home, 'build'), nuget = join(home, 'NuGet.Config');
const post = (id, kind, schema) => `POST /responses ${id} ${kind} ${schema}`;
const pair = id => [post(id, 'text', 'minute_farm_choice'), post(id, 'image', 'visible_farming_advice')];
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const evidence = { startedUtc: new Date().toISOString(), node: process.version, codey: '0.3.43',
  upstream: 'loopback HTTP mock', listeners: [], csharp: {} };
const regressions = [];
async function checkRegression(test) {
  try { await test(); } catch (e) { regressions.push(e.message); }
}
function runDotnet(args, label = 'build') {
  const child = spawn(dotnet, args, { cwd: home, env, windowsHide: true, stdio: 'inherit', timeout: 90000 });
  return once(child, 'exit').then(([code, signal]) => {
    if (code !== 0) throw new Error(`${label} exited ${code ?? signal}`);
  });
}
async function client(base, scenario, expectedPosts = []) {
  const mark = mock.calls.length, offset = front().length;
  const result = evidence.csharp[scenario] = { passed: false };
  try {
    await runDotnet([join(artifacts, 'bin/CodeySmoke/release/CodeySmoke.dll'), scenario, base, keyFile, imageFile], `CloudCoach/${scenario}`);
    const trace = front().slice(offset);
    assert.ok(trace.some(r => r.method === 'GET') && trace.every(r => r.status === 200 && goodWire(r)));
    assert.equal(trace.filter(r => r.method === 'POST').length, expectedPosts.length);
    assert.deepEqual(mock.calls.slice(mark).filter(line => line.startsWith('POST ')), expectedPosts);
    result.passed = true;
  } finally {
    result.front = front().slice(offset).map(r => `${r.method} ${r.path} ${r.status}`);
    result.upstream = mock.calls.slice(mark);
  }
}
function request(base, path, body, auth = true) {
  const method = body === undefined ? 'GET' : 'POST';
  return new Promise((ok, fail) => {
    const req = http.request(`${base}/${path}`, { method, agent: false, signal: AbortSignal.timeout(8000), headers: {
      'user-agent': coachUA, ...(auth ? { authorization: `Bearer ${frontKey}` } : {}), 'content-type': 'application/json',
    } }, async (res) => {
      try {
        let data = ''; for await (const part of res) data += part;
        ok({ status: res.statusCode, json: JSON.parse(data) });
      } catch (e) { fail(e); }
    });
    req.on('error', fail);
    req.end(body === undefined ? undefined : JSON.stringify(body));
  });
}
function payload(model, image = false) {
  const content = [{ type: 'input_text', text: 'synthetic route probe' }];
  if (image) content.push({ type: 'input_image', detail: 'high', image_url: jpeg });
  return { model, input: [{ role: 'user', content }], store: false, stream: false, service_tier: 'auto' };
}
const front = () => readFileSync(audit, 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
const goodWire = r => r.ua === coachUA && !r.otherKey && !r.blocked && r.bearer === (r.status !== 401);

async function withGateway(mock, exercise) {
  const app = dirname(gateway);
  const child = fork(workerFile, [gateway, mock.origin], { cwd: home, windowsHide: true,
    env: { ...env, SMOKE_UPSTREAM_KEY: upstreamKey, SMOKE_FRONT_KEY: frontKey },
    execArgv: ['--permission', ...[workerFile, home, gateway, join(app, 'package.json'), join(app, 'node_modules')]
      .map(path => `--allow-fs-read=${path}`), `--allow-fs-write=${home}`] });
  const exit = once(child, 'exit');
  const timeout = AbortSignal.timeout(15000);
  try {
    const ready = await Promise.race([
      once(child, 'message', { signal: timeout }).then(([r]) => r),
      exit.then(([code]) => { throw new Error(`Isolated gateway exited before ready (${code})`); }),
    ]);
    assert.match(ready.base, /^http:\/\/127\.0\.0\.1:[1-9]\d*\/v1$/);
    Object.assign(evidence, { bundle: ready.server, sha256: ready.sha256, mapSha256: ready.mapSha256 });
    if (!evidence.listeners.length) console.log(`Codey 0.3.43 ${ready.server} SHA256=${ready.sha256} map=${ready.mapSha256}`);
    evidence.listeners.push({ gateway: ready.base, upstream: mock.origin });
    console.log(`  ${ready.base} -> HTTP MOCK ${mock.origin}`);
    await exercise(ready.base);
  } finally {
    if (child.connected) child.disconnect();
    const kill = setTimeout(() => child.kill(), 3000);
    try { assert.equal((await exit)[0], 0, 'Gateway worker failed'); } finally { clearTimeout(kill); }
  }
}

let mock;
try {
  for (const dir of [env.APPDATA, env.LOCALAPPDATA]) mkdirSync(dir);
  writeFileSync(env.CODEY_SETTINGS_FILE, '{}', { mode: 0o600 });
  writeFileSync(join(home, 'config.json'), JSON.stringify({ auth: { apiKeys: [frontKey] }, providers: {},
    useResponsesApiWebSocket: false }), { mode: 0o600 });
  writeFileSync(keyFile, `CODEY_MODEL_API_KEY=${frontKey}\n`, { mode: 0o600 });
  writeFileSync(audit, '');
  if (!values['routes-only']) {
    writeFileSync(nuget, '<configuration><packageSources><clear/></packageSources></configuration>');
    await runDotnet(['build', join(tests, 'CodeySmoke.csproj'), '-c', 'Release', '--artifacts-path', artifacts,
      '--disable-build-servers', `-p:RestoreConfigFile=${nuget}`, '-p:NuGetAudit=false', '-p:UseSharedCompilation=false']);
    evidence.clientAssemblySha256 = sha256(readFileSync(join(artifacts, 'bin/CodeySmoke/release/DotaCoach.TwoPc.Core.dll')));
  }
  mock = await startMock(upstreamKey, alternateKey, jpeg);
  await withGateway(mock, async base => {
    const unauth = await request(base, 'models', undefined, false);
    assert.deepEqual([unauth.status, unauth.json.error.type], [401, 'authentication_error']);
    const catalog = await request(base, 'models');
    assert.equal(catalog.status, 200);
    assert.deepEqual([catalog.json.object, catalog.json.has_more], ['list', false]);
    evidence.catalog = catalog.json.data.map(r => [r.id, r.display_name, r.supported_endpoints, r.capabilities.supports.vision]);
    assert.deepEqual(evidence.catalog, models.map((id, i) => [id, `${labels[i]} (github-copilot)`, ['/responses'], true]));
    const unknown = await request(base, 'responses', payload('unlisted-fixture'));
    assert.deepEqual([unknown.status, unknown.json.error.type], [400, 'invalid_request_error']);
    assert.deepEqual(mock.calls, ['GET /models'], 'Listing/unauthorized/unsupported requests must not send inference');
    for (const [i, id] of models.entries()) {
      const kind = i ? 'image' : 'text';
      const { status, json } = await request(base, 'responses', payload(id, !!i));
      assert.deepEqual([status, json.model, json.id, json.status], [200, id, `resp-smoke-${kind}`, 'completed']);
      assert.equal(mock.calls.at(-1), post(id, kind, 'probe'));
    }
    console.log('PASS actual routes: 401 without bearer; normalized catalog; 400 unlisted; native text + byte-identical image');
    if (!values['routes-only']) {
      await client(base, 'happy', models.flatMap(pair));
      mock.fixture.wrongEcho = true;
      await client(base, 'wrong-echo', pair(models[0]));
      mock.fixture.wrongEcho = false;
    }
    // Codey itself can publish another provider under the SAME bare ID. Change only this
    // temp process's settings and use the same mock socket, with a different fixture key.
    writeFileSync(env.CODEY_SETTINGS_FILE, JSON.stringify({ additionalCopilotProxies: [
      { id: 'smoke', endpoint: mock.origin + '/alternate', key: alternateKey, models: [models[0]] },
    ] }));
    const foreign = await request(base, 'models');
    assert.equal(foreign.status, 200);
    evidence.alternateAstra = foreign.json.data.filter(r => r.id === models[0]).map(r =>
      [r.display_name, r.supported_endpoints, r.capabilities.supports.vision]);
    assert.deepEqual(evidence.alternateAstra, [[`${labels[0]} (codey-extra-smoke)`, ['/responses'], true]]);
    assert.equal(mock.calls.at(-1), 'GET /alternate/v1/models');
    if (!values['routes-only']) await client(base, 'foreign-provider');
    writeFileSync(env.CODEY_SETTINGS_FILE, '{}');
    console.log('PASS actual catalog: same bare Astra ID labeled (codey-extra-smoke), not (github-copilot)');
  });
  // Fresh real process and cache: metadata "/v1/responses" is NOT native HTTP "/responses"
  // in this build. A client must not offer that model; a text-only Sol must not upload images.
  mock.fixture.limited = true;
  await withGateway(mock, async base => {
    const mark = mock.calls.length;
    const { json, status } = await request(base, 'models');
    assert.equal(status, 200);
    evidence.limitedCatalog = json.data.map(r => [r.id, r.supported_endpoints, r.capabilities.supports.vision]);
    assert.deepEqual(evidence.limitedCatalog, [[models[0], ['/v1/responses'], true], [models[1], ['/responses'], false]]);
    const denied = await request(base, 'responses', payload(models[0]));
    assert.deepEqual([denied.status, denied.json.error.type], [400, 'invalid_request_error']);
    assert.match(denied.json.error.message, /does not support the responses endpoint/);
    console.log('PASS actual route: model advertising only "/v1/responses" returned 400 before upstream');
    const textOnly = values['routes-only'] ? [] : [post(models[1], 'text', 'minute_farm_choice')];
    if (!values['routes-only']) {
      await client(base, 'no-vision', textOnly);
      await checkRegression(() => client(base, 'non-native-endpoint'));
    }
    assert.deepEqual(mock.calls.slice(mark), textOnly, 'Only the text-only model answering text may reach the mock');
  });
  // Use fresh Codey caches and the same JPEG. The direct probe observes what the route
  // actually did; separately assert what the production C# client chose to send (or not).
  mock.fixture.limited = false;
  const estimate = Math.floor(jpeg.length * 3 / 4);
  evidence.fixtureJpeg = { rawBytes: pixels.length, dataUrlChars: jpeg.length, gatewayEstimateBytes: estimate, sha256: sha256(pixels) };
  evidence.imageBounds = [];
  for (const [scenario, cap, intact] of [
    ['image-size-edge', pixels.length, false], ['image-cap-minus-one', estimate - 1, false], ['image-cap-exact', estimate, true],
  ]) {
    mock.fixture.imageLimit = cap;
    await withGateway(mock, async base => {
      const { json, status } = await request(base, 'models');
      assert.equal(status, 200);
      assert.equal(json.data[0].capabilities.limits.vision.max_prompt_image_size, cap);
      const mark = mock.calls.length;
      assert.equal((await request(base, 'responses', payload(models[0], true))).status, 200);
      assert.deepEqual(mock.calls.slice(mark), [post(models[0], 'image', 'probe') + (intact ? '' : ' REDACTED_PNG')]);
      const observedAtMock = intact ? 'original JPEG, detail=high' : '96x32 PNG, detail=low';
      evidence.imageBounds.push({ cap, observedAtMock });
      console.log(`PASS actual route boundary: cap=${cap}, estimate=${estimate}: ${observedAtMock}`);
      if (!values['routes-only']) await checkRegression(() => client(base, scenario,
        intact ? [post(models[0], 'image', 'visible_farming_advice')] : []));
    });
  }
  assert.ok(front().length && front().every(goodWire));
  assert.deepEqual(mock.errors, []);
  if (regressions.length) throw new Error(regressions.join('; '));
  evidence.passed = true;
} catch (error) {
  evidence.passed = false;
  evidence.failure = error.message;
  console.error(`FAIL: ${error.message}`);
  process.exitCode = 1;
} finally {
  if (mock) {
    await mock.close();
    const seen = front();
    evidence.userAgents = [...new Set(seen.map(r => r.ua))];
    evidence.frontAuthAndEgressChecksPassed = seen.length > 0 && seen.every(goodWire);
    evidence.front = Object.fromEntries([...new Set(seen.map(r => `${r.method} ${r.path} ${r.status}`))]
      .map(row => [row, seen.filter(r => `${r.method} ${r.path} ${r.status}` === row).length]));
    evidence.mockCalls = mock.calls;
    console.log(`Front wire: ${JSON.stringify(evidence.front)}; UA/auth/egress checks=${evidence.frontAuthAndEgressChecksPassed}`);
    console.log('HTTP MOCK wire:\n  ' + mock.calls.join('\n  '));
    if (mock.errors.length) console.error('HTTP mock assertion:', mock.errors[0].message);
  }
  // Delete only the newly created child of the resolved OS temp directory, never an argument.
  const target = realpathSync(home);
  assert.ok(dirname(target) === tmp && basename(target).startsWith('dota-codey-smoke-'));
  rmSync(target, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  if (values.evidence) writeFileSync(resolve(values.evidence), JSON.stringify(evidence, null, 2) + '\n', { flag: 'wx' });
}
