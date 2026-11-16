// Standalone local evidence collector. Deliberately imports neither server.ts,
// dotenv, Steam credentials, nor any model/planner code.
import fs from 'node:fs';
import path from 'node:path';
import { createHash, randomBytes } from 'node:crypto';
import { AuditCatalog, AUDIT_SCOPE, auditConfig, startAuditReceiver } from '../../src/observer-audit.ts';

const args = process.argv.slice(2);
function argument(name: string, fallback?: string) {
  const i = args.indexOf(name);
  if (i < 0) return fallback;
  if (!args[i + 1] || args[i + 1].startsWith('--')) throw new Error(`Missing ${name}`);
  return args[i + 1];
}
const cfgDirectory = path.resolve(argument('--cfg-dir', 'C:\\Steam\\steamapps\\common\\dota 2 beta\\game\\dota\\cfg\\gamestate_integration')!);
const out = path.resolve(argument('--out') ?? (() => { throw new Error('--out is required'); })());
const minutes = Number(argument('--minutes', '60'));
if (!Number.isInteger(minutes) || minutes < 1 || minutes > 120) throw new Error('Duration must be 1–120 minutes');
if (!fs.statSync(cfgDirectory).isDirectory()) throw new Error('Missing Dota GSI directory');
// A new folder and uniquely named config only: never overwrite user content.
fs.mkdirSync(out, { recursive: false });
let token = randomBytes(32).toString('hex');
let resumePort: number | undefined;
let continuedFrom: string | null = null;
const priorFile = argument('--continue-from');
if (priorFile) {
  // Keep the loaded game's endpoint/credential, so a recorder-only repair does
  // not require restarting or leaving the user's match. Never log the token.
  const priorPath = path.resolve(priorFile);
  const prior = JSON.parse(fs.readFileSync(priorPath, 'utf8'));
  const priorCfg = path.resolve(prior.cfgPath);
  if (prior.scope !== AUDIT_SCOPE || path.dirname(priorCfg) !== cfgDirectory ||
      !path.basename(priorCfg).startsWith('gamestate_integration_coach_observer_audit_') ||
      fs.lstatSync(priorCfg).isSymbolicLink() || hash(priorCfg) !== prior.cfgSha256)
    throw new Error('Unverified previous audit config');
  const content = fs.readFileSync(priorCfg, 'utf8');
  const priorToken = /"token" "([a-f0-9]{64})"/.exec(content)?.[1];
  if (!priorToken || content !== auditConfig(prior.port, priorToken)) throw new Error('Unexpected previous config');
  continuedFrom = path.dirname(priorPath);
  const previousStatus = JSON.parse(fs.readFileSync(path.join(continuedFrom, 'summary.json'), 'utf8'));
  if (previousStatus.status !== 'listening') throw new Error('Previous audit is not running');
  const stopFile = path.join(continuedFrom, 'STOP');
  fs.writeFileSync(stopFile, '', { flag: 'wx' });
  let closed = false;
  for (let attempt = 0; attempt < 50; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 200));
    const status = JSON.parse(fs.readFileSync(path.join(continuedFrom, 'summary.json'), 'utf8'));
    if (status.status === 'stopped' && status.cleanup === 'removed-owned-config') { closed = true; break; }
  }
  if (!closed) throw new Error('Previous audit did not close cleanly');
  token = priorToken;
  resumePort = prior.port;
}
const id = `${new Date().toISOString().replace(/[:.]/g, '-')}-${randomBytes(4).toString('hex')}`;
const cfgPath = path.join(cfgDirectory, `gamestate_integration_coach_observer_audit_${id}.cfg`);
const catalog = new AuditCatalog();
const startedAt = new Date().toISOString(), startedMs = performance.now();
const maxBytes = 512 * 1024 * 1024, maxPackets = 14_400;
let bytes = 0, stopped = false, cfgHash = '', cleanup = 'not-created';
let stopReason = '';
const packetFile = path.join(out, 'packets.sanitized.jsonl');
fs.writeFileSync(packetFile, '', { flag: 'wx' });
const receiver = await startAuditReceiver({
  token, port: resumePort,
  onPayload(payload, receivedAt, ingress) {
    if (stopped) throw new Error('stopped');
    const record = { sequence: catalog.packets + 1, receivedAt, scope: AUDIT_SCOPE, ingress, payload };
    const line = JSON.stringify(record) + '\n';
    if (bytes + Buffer.byteLength(line) > maxBytes || catalog.packets >= maxPackets) {
      queueMicrotask(() => stop('storage-limit')); throw new Error('storage-limit');
    }
    try {
      fs.appendFileSync(packetFile, line);
      bytes += Buffer.byteLength(line);
      catalog.ingest(payload, receivedAt);
    } catch {
      queueMicrotask(() => stop('storage-error')); throw new Error('storage-error');
    }
  },
});
function hash(file: string) { return createHash('sha256').update(fs.readFileSync(file)).digest('hex'); }
function writeSummary() {
  // Same-filesystem replacement prevents consumers reading a half-written JSON.
  const tmp = path.join(out, 'summary.pending.json');
  fs.writeFileSync(tmp, JSON.stringify({
    ...catalog.summary(), startedAt, updatedAt: new Date().toISOString(),
    durationSeconds: (performance.now() - startedMs) / 1000,
    status: stopped ? 'stopped' : 'listening', stopReason, bytes,
    limits: { minutes, maxBytes, maxPackets },
    transport: receiver.stats, cleanup,
  }, null, 2));
  fs.renameSync(tmp, path.join(out, 'summary.json'));
}
function removeOwnConfig() {
  if (!cfgHash || !fs.existsSync(cfgPath)) { cleanup = 'absent'; return; }
  // Resolve/validate directory and content; refuse symlink/changed file.
  if (path.dirname(path.resolve(cfgPath)) !== cfgDirectory || fs.lstatSync(cfgPath).isSymbolicLink() ||
      hash(cfgPath) !== cfgHash) { cleanup = 'preserved-changed-config'; return; }
  fs.unlinkSync(cfgPath); cleanup = 'removed-owned-config';
}
function stop(reason: string) {
  if (stopped) return;
  stopped = true; stopReason = reason;
  clearInterval(timer); clearTimeout(deadline);
  receiver.server.close(); receiver.server.closeAllConnections();
  try { removeOwnConfig(); } catch { cleanup = 'cleanup-failed'; }
  try { writeSummary(); } catch { process.exitCode = 1; }
}
const timer = setInterval(() => {
  try {
    if (fs.existsSync(path.join(out, 'STOP'))) stop('requested');
    else writeSummary();
  } catch { stop('summary-write-error'); }
}, 2000);
const deadline = setTimeout(() => stop('duration-limit'), minutes * 60_000);
process.on('SIGINT', () => stop('signal'));
process.on('SIGTERM', () => stop('signal'));
try {
  fs.writeFileSync(cfgPath, auditConfig(receiver.port, token), { flag: 'wx' });
  cfgHash = hash(cfgPath); cleanup = 'owned-config-active';
  fs.writeFileSync(path.join(out, 'session.json'), JSON.stringify({
    schema: 1, scope: AUDIT_SCOPE, startedAt, pid: process.pid, port: receiver.port,
    cfgPath, cfgSha256: cfgHash, fogFilteringVerified: false,
    sanitizerVersion: 3, ingressWitnessVersion: 1, continuedFrom,
    paidModelRequests: 0, forwardsToCoach: false,
    privacy: 'No auth, Steam IDs, nicknames, raw arbitrary string values. Local files only.',
    stop: 'Create an empty STOP file in this directory. Automatic bounded stop also removes the unchanged owned config.',
  }, null, 2), { flag: 'wx' });
  writeSummary();
  console.log(JSON.stringify({ ready: true, pid: process.pid, port: receiver.port, out, cfgPath, minutes }));
} catch (error) { stop('setup-failed'); throw error; }
