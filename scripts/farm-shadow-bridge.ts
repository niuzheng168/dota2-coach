import { randomBytes, timingSafeEqual } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { setTimeout as sleep } from 'node:timers/promises';
import { BridgeFault, bridge742Archive, FarmShadowBridge, GSI_TTL_MS } from '../src/tactics/bridge-742.ts';
import type { Exercise } from '../src/tactics/bridge-742.ts';

const HOST = '127.0.0.1';
const assets = new Map([
  ['/', ['farm-shadow-bridge.html', 'text/html; charset=utf-8']],
  ['/farm-shadow-bridge.js', ['farm-shadow-bridge.js', 'text/javascript; charset=utf-8']],
  ['/farm-shadow-bridge.css', ['farm-shadow-bridge.css', 'text/css; charset=utf-8']],
]);
const methods = new Map([['/gsi', 'POST'], ['/api/state', 'GET'], ['/api/confirm', 'POST'],
  ['/api/visible', 'POST'], ['/api/stop', 'POST'], ...[...assets.keys()].map(p => [p, 'GET'] as [string, string])]);
const token = () => randomBytes(32).toString('hex');
const equalToken = (raw: unknown, expected: string) => typeof raw === 'string' && /^[0-9a-f]{64}$/.test(raw) &&
  timingSafeEqual(Buffer.from(raw), Buffer.from(expected));
function json(res: ServerResponse, status: number, value: unknown) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(value));
}
async function readJson(req: IncomingMessage, limit: number): Promise<unknown> {
  if (!/^application\/json(?:\s*;\s*charset\s*=\s*(?:"utf-8"|utf-8))?$/i.test(req.headers['content-type'] ?? '') ||
      (req.headers['content-encoding'] && req.headers['content-encoding'] !== 'identity'))
    throw new BridgeFault(415, 'JSON_ONLY', '仅接收未压缩的 UTF-8 application/json。');
  const tooLarge = () => new BridgeFault(413, 'BODY_LIMIT', '请求超过本地接口大小限制。');
  if (Number(req.headers['content-length']) > limit) throw tooLarge();
  let n = 0;
  const chunks: Buffer[] = [];
  for await (const part of req.iterator({ destroyOnReturn: false })) {
    n += part.length;
    if (n > limit) throw tooLarge();
    chunks.push(part);
  }
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks))); }
  catch { throw new BridgeFault(400, 'BAD_JSON', '需要一个有效的 UTF-8 JSON 对象。'); }
}
const unauthorized = () => new BridgeFault(401, 'UNAUTHORIZED', '需要本次进程中此接口专用的 token。');

type Corpus = 'auto' | 'fixed' | 'expanded';
const files = { fixed: 'farming-742-routes.json', expanded: 'farming-742-expanded.json' };
async function loadArchive(choice: Corpus) {
  const load = async (name: keyof typeof files) => bridge742Archive(
    JSON.parse(await readFile(new URL('../data/' + files[name], import.meta.url), 'utf8')), name);
  if (choice !== 'auto') return load(choice);
  try { return await load('expanded'); }
  catch (e) { if ((e as NodeJS.ErrnoException).code !== 'ENOENT') throw e; return load('fixed'); }
}
/** Starts only a new listener. No connection to a running sandbox, game, installation, or credential store. */
export async function startFarmShadowBridge(options: { port?: number; exercise?: Exercise; corpus?: Corpus; now?: () => number } = {}) {
  const { port = 0, exercise = 'rehearsal', corpus = 'auto', now = Date.now } = options;
  if (!Number.isInteger(port) || (port !== 0 && (port < 1024 || port > 65535))) throw new Error('端口须为 0 或 1024–65535。');
  if (exercise !== 'custom-lobby' && exercise !== 'rehearsal') throw new Error('只允许 rehearsal 或 custom-lobby。');
  if (!['auto', 'fixed', 'expanded'].includes(corpus)) throw new Error('只允许内置 7.42 归档。');
  const archive = await loadArchive(corpus);
  const bridge = new FarmShadowBridge(exercise, now, archive);
  const operatorToken = token(), gsiToken = token();
  let authority = '';
  const server = createServer({ requestTimeout: 6_000, headersTimeout: 5_000, maxHeaderSize: 8_192 }, async (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Content-Security-Policy', "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'");
    try {
      if (req.socket.remoteAddress !== HOST || req.headers.host !== authority ||
          (req.headers.origin !== undefined && req.headers.origin !== `http://${authority}`) ||
          req.headers['sec-fetch-site'] === 'cross-site')
        throw new BridgeFault(403, 'LOOPBACK_ONLY', '仅接受本机指定 Host 和同源访问。');
      const path = req.url ?? '';
      const method = methods.get(path); // Exact paths; never accept a query token, arbitrary filenames or proxy URLs.
      if (!method) throw new BridgeFault(404, 'NOT_FOUND', '接口不存在。');
      if (req.method !== method) {
        res.setHeader('Allow', method);
        throw new BridgeFault(405, 'METHOD', `此接口仅支持 ${method}。`);
      }
      const asset = assets.get(path);
      if (asset) {
        const bytes = await readFile(new URL('../public/' + asset[0], import.meta.url));
        res.writeHead(200, { 'Content-Type': asset[1] });
        // The checked-in public HTML is inert if another static server happens to serve it.
        res.end(path === '/' ? bytes.toString('utf8').replace('__BRIDGE_LOOPBACK_ORIGIN__', `http://${authority}`) : bytes);
        return;
      }
      if (path === '/gsi') {
        const raw = await readJson(req, 65_536);
        if (!raw || typeof raw !== 'object' || !('auth' in raw) || !raw.auth || typeof raw.auth !== 'object' ||
            !('token' in raw.auth) || !equalToken(raw.auth.token, gsiToken)) throw unauthorized();
        bridge.ingest(raw);
      } else {
        const auth = req.headers.authorization;
        if (!auth?.startsWith('Bearer ') || !equalToken(auth.slice(7), operatorToken)) throw unauthorized();
        if (path === '/api/state') { json(res, 200, bridge.state()); return; }
        const value = await readJson(req, 8_192);
        if (path === '/api/confirm') bridge.confirm(value);
        else if (path === '/api/visible') bridge.observe(value);
        else {
          if (!value || typeof value !== 'object' || Object.keys(value).length !== 1 || !('stop' in value) || value.stop !== true)
            throw new BridgeFault(400, 'STOP_SCHEMA', '停止需提交 {"stop":true}。');
          bridge.stop();
        }
      }
      json(res, 200, { accepted: true });
    } catch (error) {
      // No raw payloads, identities, token values, exception stacks or local paths in diagnostics.
      req.resume();
      const e = error instanceof BridgeFault ? error : new BridgeFault(500, 'BRIDGE_ERROR', '本地桥处理失败。');
      if (e.status === 413) res.shouldKeepAlive = false;
      if (!res.destroyed && !res.headersSent) json(res, e.status, { code: e.code, message: e.message });
    }
  });
  server.maxHeadersCount = 32;
  server.setTimeout(8_000, socket => socket.destroy());
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, HOST, () => { server.off('error', reject); resolve(); });
  });
  const addr = server.address();
  if (!addr || typeof addr === 'string') throw new Error('未建立本机监听。');
  authority = `${HOST}:${addr.port}`;
  const timer = setInterval(() => bridge.expire(), 200).unref();
  const close = async () => {
    bridge.stop(); clearInterval(timer);
    await new Promise<void>(resolve => { server.close(() => resolve()); server.closeAllConnections(); });
  };
  return { origin: `http://${authority}`, gsiToken, operatorToken, close };
}

// Synthetic producer for --demo only. It cannot be pointed at any other process or port.
async function demo() {
  const s = await startFarmShadowBridge({ corpus: 'fixed' }); // pinned demonstration, not a comparison of archive performance
  try {
    const request = async (path: string, body?: unknown) => {
      const r = await fetch(s.origin + path, { method: body === undefined ? 'GET' : 'POST',
        headers: { ...(path !== '/gsi' ? { Authorization: `Bearer ${s.operatorToken}` } : {}),
          ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) }, body: JSON.stringify(body) });
      const result = await r.json();
      if (!r.ok) throw new Error(`${r.status}: ${result.code}`);
      return result;
    };
    const began = Date.now(), gameClock = () => 660 + Math.floor((Date.now() - began) / 1_000);
    const sendSelf = (i: number) => request('/gsi', { auth: { token: s.gsiToken },
      provider: { name: 'Dota 2', appid: 570, version: 48, timestamp: Math.floor(Date.now() / 1000) },
      map: { matchid: '742000001', game_state: 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS', clock_time: gameClock(), paused: false },
      player: { team_name: 'radiant', activity: 'playing', last_hits: 80 }, hero: { name: 'npc_dota_hero_luna',
        alive: true, stunned: false, hexed: false, disarmed: false, silenced: false,
        xpos: 1100, ypos: -5400, level: 9, health: 900 - i, max_health: 1100, mana: 400, max_mana: 520 },
      items: { slot0: { name: 'item_power_treads', cooldown: 0 } }, abilities: { ability0: { name: 'luna_lucent_beam', level: 2, cooldown: 4 } } });
    await sendSelf(0);
    const initial = await request('/api/state');
    const anchor = () => ({ ...initial.binding, capturedAt: Date.now(), clock: initial.own.clock });
    await request('/api/confirm', { ...anchor(), exercise: 'rehearsal', ownPlayerView: true, build: 6986,
      matchStartUnix: Math.floor(began / 1000) - 3600 }); // Declared start of this invented exercise, not a live inference.
    await request('/api/visible', { ...anchor(), visibleNow: true, danger: 'not-seen', targets: [
      { targetId: 'lane:bot', state: 'present', area: 'own' }, { targetId: 'good_1', state: 'empty', area: 'own' }] });
    const seen = await request('/api/state');
    if (seen.visible?.targets.length !== 2 || seen.observation?.self.itemsSeen[0].itemId !== 'power_treads' || seen.action !== null)
      throw new Error('合成输入未端到端到达。');
    const samples = [];
    for (let i = 1; i <= 40; i++) {
      const started = performance.now(); await sendSelf(i);
      if ((await request('/api/state')).own?.health !== 900 - i) throw new Error('读到的不是刚发送的本体帧。');
      samples.push(performance.now() - started);
    }
    samples.sort((a, b) => a - b);
    await sleep(GSI_TTL_MS + 100);
    const expired = await request('/api/state');
    if (expired.own !== null || expired.shadow !== null || expired.status.code !== 'STALE_GSI') throw new Error('未撤回过期输入。');
    console.log(JSON.stringify({ mode: 'synthetic-loopback-rehearsal-only', visible: seen.visible.targets,
      shadow: seen.shadow?.status, reason: seen.shadow?.reason, action: seen.action, afterSilence: expired.status.code,
      timing: { boundary: 'GSI POST start -> authenticated GET with matching own HP, same host, warm, ms',
        n: samples.length, p50: +samples[19].toFixed(2), p95: +samples[37].toFixed(2) } }, null, 2));
  } finally { await s.close(); }
}
async function main(args: string[]) {
  if (args.length === 1 && args[0] === '--demo') { await demo(); return; }
  const usage = 'node scripts/farm-shadow-bridge.ts [--mode rehearsal|custom-lobby] [--port 0|1024..65535] [--corpus auto|fixed|expanded]\nnode scripts/farm-shadow-bridge.ts --demo';
  if (args.length === 1 && args[0] === '--help') { console.log(usage); return; }
  let port = 0, exercise: Exercise = 'rehearsal', corpus: Corpus = 'auto';
  for (let i = 0; i < args.length; i += 2) {
    const k = args[i], v = args[i + 1];
    if (k === '--port' && /^\d{1,5}$/.test(v ?? '')) port = Number(v);
    else if (k === '--mode' && (v === 'rehearsal' || v === 'custom-lobby')) exercise = v;
    else if (k === '--corpus' && (v === 'auto' || v === 'fixed' || v === 'expanded')) corpus = v;
    else throw new Error(usage);
  }
  const s = await startFarmShadowBridge({ port, exercise, corpus });
  console.log(`Farm shadow bridge · ${exercise}\n浏览器：${s.origin}/\n本人 GSI POST：${s.origin}/gsi\n` +
    `GSI token（仅发送端）：${s.gsiToken}\nOperator token（网页 / 本机 API）：${s.operatorToken}\n` +
    '未接入游戏。仅在本机终端查看 token；不放 URL、不保存。Ctrl+C 停止。');
  process.once('SIGINT', () => void s.close());
  process.once('SIGTERM', () => void s.close());
}
if (import.meta.main) main(process.argv.slice(2)).catch((e: unknown) => {
  console.error(e instanceof Error ? e.message : '桥启动失败。'); process.exitCode = 1;
});
