// Child of scripts/test-codey-gateway.mjs. Never import Codey in the caller's environment.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { appendFileSync, readFileSync } from 'node:fs';
import { createRequire, registerHooks } from 'node:module';
import { Socket } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const [gateway, upstream] = process.argv.slice(2);
const home = process.cwd();
assert.ok(process.send && process.permission && process.env.COPILOT_API_HOME === home);
assert.ok(!process.permission.has('child') && !process.permission.has('fs.write', gateway));
assert.match(upstream, /^http:\/\/127\.0\.0\.1:[1-9]\d*$/);

// No URL rewriting and no mocked fetch results. Allow only the fixture's exact HTTP resources
// (the alternate catalog is read-only). Also fence TCP connects from package-imported clients.
const nativeFetch = globalThis.fetch;
const connect = Socket.prototype.connect;
let blocked = 0;
function deny() { blocked++; throw new Error('Codey smoke: blocked a non-fixture destination'); }
Socket.prototype.connect = function (...args) {
  const opts = Array.isArray(args[0]) ? args[0][0] : args[0];
  if (opts?.host !== '127.0.0.1' || String(opts.port) !== new URL(upstream).port || opts.path) deny();
  return Reflect.apply(connect, this, args);
};
globalThis.fetch = (input, init) => {
  const url = input instanceof Request ? input.url : String(input);
  const method = init?.method ?? (input instanceof Request ? input.method : 'GET');
  if (!(method === 'GET' && [upstream + '/models', upstream + '/alternate/v1/models'].includes(url) ||
        method === 'POST' && url === `${upstream}/responses`)) deny();
  return nativeFetch(input, { ...init, redirect: 'error' });
};

const app = dirname(gateway);
const pkg = JSON.parse(readFileSync(join(app, 'package.json')));
assert.deepEqual([pkg.name, pkg.version], ['codey', '0.3.43']);
const server = 'server-BotZPvTv.js', runtime = 'copilot-runtime-CcBmm7yD.js', config = 'config-store-BIm8NFbE.js';
const hashes = {
  [server]: 'be7dcb0d52d6571317dd27c08282141152742022dee5c224550f412e56904f2b',
  [`${server}.map`]: 'f1d176b487945336860e8d1fb519f229ca2389cebb7812e2228855a21cca4f3f',
  [runtime]: '618aead30c32fb2af63f64d373b9fc7e19d41a1514fdfbd811195df1fdc2105b',
  [config]: '99de02a4ee63efb6d4e24d1134c4e74570b3445c5ca095179db438b3d4e452de',
};
for (const [file, hash] of Object.entries(hashes))
  assert.equal(createHash('sha256').update(readFileSync(join(gateway, file))).digest('hex'), hash, `Unexpected build: ${file}`);
const map = JSON.parse(readFileSync(join(gateway, `${server}.map`)));
const src = (file) => map.sourcesContent[map.sources.indexOf(`../src/${file}`)];
assert.ok(src('server.ts').includes('server.route("/v1/models", modelRoutes)'));
assert.ok(src('server.ts').includes('server.route("/v1/responses", responsesRoutes)'));
assert.ok(src('services/copilot/create-responses.ts').includes('`${copilotBaseUrl(state)}/responses`'));
const chunk = (file) => pathToFileURL(join(gateway, file)).href;
const { N: state, O: paths } = await import(chunk(config));
assert.equal(resolve(paths.APP_DIR), home);
assert.ok(!state.githubToken && !state.copilotToken);
Object.assign(state, { copilotApiUrl: upstream, copilotToken: process.env.SMOKE_UPSTREAM_KEY, vsCodeVersion: 'smoke' });

// The public setupCopilotRuntime also initializes identity/OAuth: do NOT call it. Expose
// just the existing cache initializer in memory; its default getModels, filter and cache
// are the shipped implementations. Neither the route bundle nor the installation is edited.
const hook = registerHooks({ load(url, ctx, next) {
  const loaded = next(url, ctx);
  return url === chunk(runtime)
    ? { ...loaded, source: `${Buffer.from(loaded.source)}\nexport { cacheModels as primeSmokeCatalog };` }
    : loaded;
} });
const { primeSmokeCatalog, n: stopRefresh } = await import(chunk(runtime));
hook.deregister();
try { await primeSmokeCatalog(); } finally { stopRefresh(); }

const { createServer } = await import(chunk(server));
const router = createServer({ networkExposed: false }); // Uses the real auth middleware + temp config.json.
const { serve } = await import(pathToFileURL(createRequire(chunk(server)).resolve('srvx')).href);
const listener = serve({
  hostname: '127.0.0.1', port: 0, manual: true, silent: true, gracefulShutdown: false,
  async fetch(req) {
    const path = new URL(req.url).pathname;
    if (!(req.method === 'GET' && path === '/v1/models' || req.method === 'POST' && path === '/v1/responses'))
      return new Response(null, { status: 404 }); // Do not expose the other admin/provider/CLI routes.
    const res = await router.fetch(req);
    appendFileSync(join(home, 'front.ndjson'), JSON.stringify({
      method: req.method, path, status: res.status, ua: req.headers.get('user-agent'),
      bearer: req.headers.get('authorization') === `Bearer ${process.env.SMOKE_FRONT_KEY}`,
      otherKey: req.headers.has('api-key') || req.headers.has('x-api-key'), blocked,
    }) + '\n');
    return res;
  },
});
await listener.serve();
process.on('disconnect', async () => { await listener.close(true); if (blocked) process.exitCode = 1; });
process.send({ base: new URL('v1', listener.url).href, server, sha256: hashes[server], mapSha256: hashes[`${server}.map`] });
