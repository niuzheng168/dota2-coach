import { createHash, timingSafeEqual } from 'node:crypto';
import { createServer } from 'node:http';
import type { Server } from 'node:http';

// This is an isolated evidence recorder, NOT a source for the live coach.
export const AUDIT_SCOPE = 'observer-audit-unverified-fog';
export const REQUESTED_COMPONENTS = [
  'provider', 'map', 'player', 'hero', 'abilities', 'items', 'buildings',
  'league', 'draft', 'wearables', 'events', 'minimap', 'roshan', 'couriers', 'neutralitems',
] as const;
type Obj = Record<string, unknown>;
export function object(value: unknown): Obj {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Obj : {};
}
// Do not match the substring "uri" in "couriers", "duration", etc.
const privateKey = /(?:auth|token|secret|password|steam_?id|account_?id|nickname|persona|email|address)|(?:^|_)(?:url|uri)(?:$|_)/i;
const machineString = /^(?:npc_dota_[a-z0-9_]+|dota_[a-z0-9_]+|item_[a-z0-9_]+|DOTA_[A-Z0-9_]+|radiant|dire|spectator|neutral|none|empty)$/;
// Documented enum names from the pinned antonpup/Dota2GSI reference. Unknown
// strings remain private; this is not a claim that the list is exhaustive.
const eventTypes = new Set(['courier_killed', 'roshan_killed', 'aegis_picked_up',
  'aegis_denied', 'tip', 'bounty_rune_pickup']);
const eventNumericKeys = new Set(['courier_team', 'killer_player_id', 'killed_by_team',
  'player_id', 'sender_player_id', 'receiver_player_id', 'tip_amount', 'bounty_value', 'team_gold']);
const OMIT = Symbol('omit');

/** Unknown strings keep their type/presence, not their potentially private contents.
 * Unknown identifiers used as object keys are hashed. Missing, null, zero and false
 * remain distinct. "previously" is retained but never treated as current state. */
export function sanitizePayload(input: unknown): Obj {
  let nodes = 0;
  function walk(value: unknown, key: string, path: string[], depth: number): unknown {
    if (++nodes > 60_000 || depth > 24) throw new Error('payload-complexity-limit');
    if (key === 'auth') return OMIT;
    if (privateKey.test(key) || key === 'matchid' || key === 'match_id') return '[redacted]';
    if (value === null || typeof value === 'boolean') return value;
    if (typeof value === 'number') return Number.isFinite(value) ? value : null;
    if (typeof value === 'string') {
      // Machine ability names are useful; do not admit player names matching this syntax.
      const section = path.find(p => REQUESTED_COMPONENTS.includes(p as typeof REQUESTED_COMPONENTS[number]));
      const entityName = key === 'name' && ['hero', 'abilities', 'items', 'buildings', 'couriers'].includes(section ?? '');
      // Only engine icon enum names, never file paths, URLs or player-supplied names.
      const minimapIcon = section === 'minimap' && key === 'image' && /^minimap_[a-z0-9_]+$/i.test(value);
      const playerActivity = section === 'player' && key === 'activity' && ['playing', 'menu'].includes(value);
      const eventType = section === 'events' && key === 'event_type' && eventTypes.has(value.toLowerCase());
      if (section === 'events' && key === 'data' && value.length <= 65_536) {
        // Some builds encode event data as a JSON string. Preserve only known
        // numeric/boolean game fields and label this as a recorder conversion.
        // Never decode arbitrary event text/chat into the log.
        try {
          const parsed = object(JSON.parse(value));
          const fields: Obj = Object.create(null);
          for (const [k, v] of Object.entries(parsed)) {
            if (eventNumericKeys.has(k) && typeof v === 'number' && Number.isFinite(v)) fields[k] = v;
            if (k === 'snatched' && typeof v === 'boolean') fields[k] = v;
          }
          if (Object.keys(fields).length) return { __auditSourceType: 'json-string', gameFields: fields };
        } catch { /* Retain redacted string, not an invented empty event. */ }
      }
      if (value.length <= 128 && ((machineString.test(value) && key !== 'name') ||
        (entityName && /^[a-z][a-z0-9_]{1,100}$/.test(value)) ||
        minimapIcon || playerActivity || eventType || value === '' ||
        (section === 'provider' && key === 'name' && value === 'Dota 2'))) return value;
      return '[redacted-string]';
    }
    if (Array.isArray(value)) return value.map(v => {
      const clean = walk(v, '', path, depth + 1);
      return clean === OMIT ? null : clean;
    });
    const out: Obj = Object.create(null);
    for (const [k, v] of Object.entries(object(value))) {
      if (k === '__proto__' || k === 'prototype' || k === 'constructor') continue;
      const safe = /^[A-Za-z_][A-Za-z0-9_]{0,95}$/.test(k) ? k :
        `key_${createHash('sha256').update(k).digest('hex').slice(0, 16)}`;
      const clean = walk(v, k, [...path, k], depth + 1);
      if (clean !== OMIT) out[safe] = clean;
    }
    return out;
  }
  return object(walk(input, '', [], 0));
}

/** Independent, pre-redaction structural evidence. Never persists raw HTTP,
 * auth, account/nickname values, or arbitrary unknown property names.
 * The critical gameplay-node digests allow testing whether redaction changed
 * their content. This cannot retroactively certify older recordings. */
const witnessSections = ['player', 'hero', 'items', 'abilities', 'minimap', 'couriers', 'events', 'league'] as const;
function sectionShape(value: unknown) {
  const obj = object(value);
  return {
    kind: value === undefined ? 'missing' : value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value,
    entries: Array.isArray(value) ? value.length : Object.keys(obj).length,
    teamGroups: Object.entries(obj).filter(([k]) => /^team\d+$/.test(k))
      .map(([team, players]) => ({ team, playerObjects: Object.entries(object(players))
        .filter(([k, v]) => /^player\d+$/.test(k) && v !== null && typeof v === 'object').length })),
  };
}
function criticalDigests(payload: Obj) {
  return Object.fromEntries(['hero', 'items', 'abilities'].map(k => [k, payload[k] === undefined ? null :
    createHash('sha256').update(JSON.stringify(payload[k])).digest('hex')]));
}
function captureIngress(raw: Obj) {
  const rawHero = object(raw.hero), rawPlayer = object(raw.player);
  const scalar = (o: Obj, k: string) => typeof o[k] === 'number' || typeof o[k] === 'boolean' || o[k] === null ? o[k] : undefined;
  return {
    originalShapes: Object.fromEntries(witnessSections.map(k => [k, sectionShape(raw[k])])),
    digests: criticalDigests(raw),
    originalKnownValues: {
      heroId: scalar(rawHero, 'id'), heroHealth: scalar(rawHero, 'health'), heroMana: scalar(rawHero, 'mana'),
      playerTeam: typeof rawPlayer.team_name === 'string' &&
        ['radiant', 'dire', 'spectator'].includes(rawPlayer.team_name) ? rawPlayer.team_name : undefined,
      playerGold: scalar(rawPlayer, 'gold'), playerGpm: scalar(rawPlayer, 'gpm'),
    },
  };
}
function completeIngress(original: ReturnType<typeof captureIngress>, recorded: Obj) {
  const after = criticalDigests(recorded);
  return {
    schema: 1, originalShapes: original.originalShapes,
    recordedShapes: Object.fromEntries(witnessSections.map(k => [k, sectionShape(recorded[k])])),
    critical: Object.fromEntries(Object.entries(original.digests).map(([k, originalSha256]) => [k,
      { originalSha256, recordedSha256: after[k], unchanged: originalSha256 === after[k] }])),
    originalKnownValues: original.originalKnownValues,
    note: 'Computed before redaction; hashes are not raw payload storage. Missing fields are not zero.',
  };
}
export function ingressWitness(raw: Obj, recorded: Obj) {
  return completeIngress(captureIngress(raw), recorded);
}
export type IngressWitness = ReturnType<typeof ingressWitness>;

type Field = {
  types: string[]; presentPackets: number; nullPackets: number; valueChanges: number;
  firstSequence: number; lastSequence: number; min?: number; max?: number;
};
export function leaves(input: unknown, base = '', output = new Map<string, unknown>()) {
  const entries = Object.entries(object(input));
  if (Array.isArray(input)) input.forEach((value, i) => leaves(value, `${base}[${i}]`, output));
  else if (entries.length) for (const [key, value] of entries) leaves(value, base ? `${base}.${key}` : key, output);
  else output.set(base, input);
  return output;
}
export function describeActors(payload: Obj) {
  const actors: Obj[] = [];
  function visit(value: unknown, path: string[]) {
    const obj = object(value);
    if (typeof obj.name === 'string' && obj.name.startsWith('npc_dota_hero_')) {
      let player: unknown = payload.player;
      for (const part of path.slice(1)) player = object(player)[part];
      actors.push({ path: path.join('.'), hero: obj, player: object(player) });
      return;
    }
    for (const [key, child] of Object.entries(obj)) if (typeof child === 'object') visit(child, [...path, key]);
  }
  visit(payload.hero, ['hero']);
  return actors;
}

export class AuditCatalog {
  packets = 0;
  fields: Record<string, Field> = Object.create(null);
  previous = new Map<string, unknown>();
  sections: Record<string, number> = Object.create(null);
  latest: Obj = {};
  ingest(payload: Obj, receivedAt: string) {
    this.packets++;
    // Change-history fragments are not observations of present state.
    const current = Object.fromEntries(Object.entries(payload).filter(([k]) => !['previously', 'added'].includes(k)));
    const values = leaves(current);
    for (const key of Object.keys(current)) this.sections[key] = (this.sections[key] ?? 0) + 1;
    for (const [path, value] of values) {
      const type = value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
      const f = this.fields[path] ??= {
        types: [], presentPackets: 0, nullPackets: 0, valueChanges: 0,
        firstSequence: this.packets, lastSequence: this.packets,
      };
      if (!f.types.includes(type)) f.types.push(type);
      f.presentPackets++; f.lastSequence = this.packets;
      if (value === null) f.nullPackets++;
      if (this.previous.has(path) && JSON.stringify(this.previous.get(path)) !== JSON.stringify(value)) f.valueChanges++;
      if (typeof value === 'number') { f.min = Math.min(f.min ?? value, value); f.max = Math.max(f.max ?? value, value); }
    }
    this.previous = values;
    this.latest = {
      receivedAt, map: current.map ?? null, provider: current.provider ?? null,
      sectionKeys: Object.fromEntries(Object.entries(current).map(([k, v]) => [k, Object.keys(object(v))])),
      actors: describeActors(current),
    };
  }
  summary() {
    return {
      scope: AUDIT_SCOPE, packets: this.packets, requestedComponents: REQUESTED_COMPONENTS,
      observedSections: this.sections,
      requestedButNotObserved: REQUESTED_COMPONENTS.filter(k => !this.sections[k]),
      visibilityRelatedPaths: Object.keys(this.fields).filter(p => /visib|fog|reveal|observ|spectat/i.test(p)),
      fields: Object.fromEntries(Object.entries(this.fields).map(([k, v]) => [k, { ...v, absentPackets: this.packets - v.presentPackets }])),
      latest: this.latest, fogFilteringVerified: false, spectatorModeVerified: false,
      warnings: [
        'Requests are probes, not proof the installed Dota build supports each component.',
        'Field presence/absence is not visibility. Never merge previously/added into current state.',
        'No payload enters the live coach; no API model is called.',
        'Game timestamps may be delayed. Receive time does not prove server freshness.',
      ],
    };
  }
}

export function auditConfig(port: number, token: string) {
  if (!Number.isInteger(port) || port < 1 || port > 65535 || !/^[a-f0-9]{64}$/.test(token)) throw new Error('invalid-config');
  return `"Dota Coach Observer Audit"\n{\n  "uri" "http://127.0.0.1:${port}/gsi"\n  "timeout" "2.0"\n  "buffer" "0.1"\n  "throttle" "0.5"\n  "heartbeat" "1.0"\n  "auth" { "token" "${token}" }\n  "data"\n  {\n    "auth" "1"\n${REQUESTED_COMPONENTS.map(k => `    "${k}" "1"`).join('\n')}\n  }\n}\n`;
}

export async function startAuditReceiver(options: {
  token: string; port?: number; onPayload: (payload: Obj, receivedAt: string, witness: IngressWitness) => void;
  minIntervalMs?: number; maxBodyBytes?: number;
}): Promise<{ server: Server; port: number; stats: Record<string, number> }> {
  if (!/^[a-f0-9]{64}$/.test(options.token)) throw new Error('invalid-token');
  const expected = Buffer.from(options.token);
  const stats = { accepted: 0, unauthorized: 0, malformed: 0, oversized: 0, throttled: 0, storageErrors: 0 };
  let lastAccepted = -Infinity;
  const server = createServer({ maxHeaderSize: 8192, requestTimeout: 5000 }, (req, res) => {
    const reject = (status: number) => { res.writeHead(status, { Connection: 'close' }); res.end(); req.resume(); };
    if (req.headers.origin || req.method !== 'POST' || req.url !== '/gsi') return reject(403);
    if (req.headers['content-encoding'] || !req.headers['content-type']?.startsWith('application/json')) return reject(415);
    const limit = options.maxBodyBytes ?? 1024 * 1024;
    if (Number(req.headers['content-length']) > limit) { stats.oversized++; return reject(413); }
    let bytes = 0, rejected = false;
    const chunks: Buffer[] = [];
    req.on('error', () => {});
    req.on('data', (chunk: Buffer) => {
      if (rejected) return;
      bytes += chunk.length;
      if (bytes > limit) { rejected = true; stats.oversized++; reject(413); }
      else chunks.push(chunk);
    });
    req.on('end', () => {
      if (rejected) return;
      let raw: Obj;
      try {
        const parsed: unknown = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('shape');
        raw = object(parsed);
      } catch { stats.malformed++; return reject(400); }
      const value = object(raw.auth).token;
      const actual = typeof value === 'string' ? Buffer.from(value) : Buffer.alloc(0);
      if (expected.length !== actual.length || !timingSafeEqual(expected, actual)) {
        stats.unauthorized++; return reject(401);
      }
      const now = performance.now();
      if (now - lastAccepted < (options.minIntervalMs ?? 200)) { stats.throttled++; return reject(429); }
      let clean: Obj, witness: IngressWitness;
      try {
        const original = captureIngress(raw);
        clean = sanitizePayload(raw);
        witness = completeIngress(original, clean);
      }
      catch { stats.malformed++; return reject(400); }
      try { options.onPayload(clean, new Date().toISOString(), witness); }
      catch { stats.storageErrors++; return reject(503); }
      lastAccepted = now; stats.accepted++;
      res.writeHead(200); res.end();
    });
  });
  server.maxConnections = 16;
  server.headersTimeout = 5000; server.keepAliveTimeout = 2000;
  server.on('clientError', (_error, socket) => socket.destroy());
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(options.port ?? 0, '127.0.0.1', resolve);
  });
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('listen-failed');
  return { server, port: address.port, stats };
}
