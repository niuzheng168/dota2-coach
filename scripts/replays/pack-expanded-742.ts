/** Offline packer. Acquisition owns eligible.json, progress.json and all replay evidence. */
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { replayArea } from '../../src/replays/storage.ts';
import { compact, endpoints, heroes, patchEvidence742, type Hero742, type Hit742, type Point742,
  type Row742, type Team742 } from './study-742.ts';

const legacy = replayArea('farm742'), more = replayArea('farm742-more'), cacheDir = `${more}/pack-cache`;
const originalFile = 'data/farming-742-routes.json', studyFile = 'data/farming-742-expanded.json';
const auditFile = 'data/farming-742-expanded-audit.json', docFile = 'docs/farming-742-expanded.md';
const sha = (bytes: Buffer | string) => createHash('sha256').update(bytes).digest('hex');
const json = (v: unknown, indent = 0) => JSON.stringify(v, null, indent) + '\n';
const read = (p: string) => JSON.parse(fs.readFileSync(p, 'utf8'));
const hash = (p: string) => sha(fs.readFileSync(p));
const errorText = (e: unknown) => e instanceof Error ? e.message : String(e);
const byText = (a: string, b: string) => a < b ? -1 : a > b ? 1 : 0;
const heroKeys = Object.values(heroes), sides = ['radiant', 'dire'] as const;
const highFloor = 7500;
type Cohort = { rows: Row742[]; sources: { heroId: 48 | 95; sha256: string; previousSha256: string;
  freshRows: number; previousRows: number; checkedAt: string; url: string; unionPatchRankedRows: number }[];
  minimumMatchMmr: number; selection: string; patch: string };
type Receipt = { status: string; error?: string | null; failureStage?: string; failures?: { stage: string; error: string; at: string }[] };
type Progress = { eligible: number; updatedAt: string; complete?: boolean; results: Record<string, Receipt> };
type Packed = ReturnType<typeof compact>;
export type ExpandedMatch = Packed['match'] & { startPoint: Point742; endPoint: Point742 };
type Landmarks = { camps: { id: string; x: number; y: number }[] };
type Evidence = { factsSha256: string; acquisitionSha256: string; archiveSha256: string; demSha256: string;
  metadataSha256: string; postGameTick: number; extractionScope: string };
type Entry = { row: Row742; legacy16: boolean; acquisition: Receipt | null; status: 'verified' | 'acquisition-failed' | 'rejected' | 'pending';
  evidence?: Evidence; reason?: string };

// Don't buffer a second copy of a whole replay just to hash it. Check actual bytes on cache hits too.
function fileDigest(p: string) {
  const fd = fs.openSync(p, 'r'), buf = Buffer.allocUnsafe(1024 * 1024), h = createHash('sha256');
  try {
    for (let n; (n = fs.readSync(fd, buf)) > 0;) h.update(buf.subarray(0, n));
    return h.digest('hex');
  } finally { fs.closeSync(fd); }
}
function save(target: string, text: string) {
  // Cache/preview on the replay share; only explicit publication writes to the checkout.
  // Stage beside the destination so replacement stays atomic across distinct work/publish volumes.
  const tmp = path.join(path.dirname(target), `pack-${randomUUID()}.next`);
  fs.writeFileSync(tmp, text, { flag: 'wx' });
  try { fs.renameSync(tmp, target); } finally { if (fs.existsSync(tmp)) fs.unlinkSync(tmp); }
}

// Reconcile the supplied eligibility ledger against the two frozen catalogs (not a fresh network query).
function checkCohort(c: Cohort) {
  assert.equal(c.patch, '7.42'); assert.equal(c.minimumMatchMmr, 7000);
  assert.deepEqual(c.sources.map(s => s.heroId).sort(), [48, 95]);
  const union = new Map<number, Row742>();
  for (const s of c.sources) {
    const pubs = new Map<number, Row742>();
    for (const [root, digest, count] of [[legacy, s.previousSha256, s.previousRows], [more, s.sha256, s.freshRows]] as const) {
      const p = `${root}/catalog-${s.heroId}.json`;
      assert.equal(hash(p), digest, `Catalog SHA: ${p}`);
      const feed = read(p); assert.equal(feed.length, count);
      for (const { data } of feed) {
        const r: Row742 = typeof data === 'string' ? JSON.parse(data) : data;
        if (r.hero_id === s.heroId && r.patch === c.patch && r.position === 'pos 1' && r.lobby_type === 7) pubs.set(r.match_id, r);
      }
    }
    assert.equal(pubs.size, s.unionPatchRankedRows);
    for (const r of pubs.values()) if (Number.isSafeInteger(r.account_id) && r.account_id > 0 && Number.isSafeInteger(r.mmr) && r.mmr >= c.minimumMatchMmr) {
      assert(!union.has(r.match_id), `Two study heroes in ${r.match_id}; need separate hero evidence`);
      union.set(r.match_id, r);
    }
  }
  const ids = c.rows.map(r => r.match_id);
  assert.equal(new Set(ids).size, ids.length, 'Duplicate eligible ID');
  assert.deepEqual([...ids].sort((a,b) => a-b), [...union.keys()].sort((a,b) => a-b), 'Eligible/catalog union differs');
  for (const r of c.rows) {
    for (const key of ['hero_id','account_id','name','mmr','is_radiant','won','last_hits','duration','activate_time','patch','position','lobby_type'] as const)
      assert.equal(r[key], union.get(r.match_id)![key], `${r.match_id} catalog ${key}`);
    assert(Number.isSafeInteger(r.match_id) && r.match_id > 0 && [0,1].includes(r.is_radiant) && [0,1].includes(r.won));
  }
}

type Config = ReturnType<typeof patchEvidence742>;
export function packOne(r: Row742, root: string, config: Config, codeKey: string, useCache: boolean, writeCache: boolean,
  map: Landmarks, original?: Packed['match'], cacheRoot = cacheDir) {
  const base = `${root}/${r.match_id}`, manifest = read(`${base}.acquisition.json`);
  const inputs = { factsSha256: fileDigest(`${base}.facts.json`), acquisitionSha256: hash(`${base}.acquisition.json`),
    archiveSha256: fileDigest(`${base}.archive`), demSha256: fileDigest(`${base}.dem`), metadataSha256: hash(`${base}.opendota.json`) };
  for (const key of ['archiveSha256','demSha256','metadataSha256'] as const)
    assert.equal(inputs[key], manifest[key], `${r.match_id}: ${key} vs acquisition`);
  const signature = sha(json([codeKey, config, r, inputs])), filename = path.join(cacheRoot, `${r.match_id}.json`);
  let result: Packed | undefined;
  if (useCache && fs.existsSync(filename)) {
    try {
      const hit = read(filename);
      if (hit.signature === signature && hit.checksum === sha(json([signature, hit.result]))) result = hit.result;
    } catch { /* Invalid/torn cache is not evidence: repeat verification. */ }
  }
  const hit = !!result;
  result ??= compact(r, config.patch, config.builds, { directory: root, allowShort: true });
  assert.deepEqual(result.landmarks, map, `Map differs in ${r.match_id}`);
  // JSON legitimately serializes a rounded -0 coordinate as 0.
  if (original) assert(json(result.match) === json(original), `Original compact differs in ${r.match_id}`);
  if (writeCache && !hit) save(filename, json({ signature, checksum: sha(json([signature, result])), result }));
  const { match, startPoint, endPoint, extractionScope, postGameTick } = result;
  return { match: { ...match, startPoint, endPoint }, evidence: { ...inputs, extractionScope, postGameTick } };
}

export function publishable(rows: Entry[], progress: Progress | null) {
  assert(progress && progress.complete !== false && progress.eligible === rows.length, 'Collection is not closed');
  assert.deepEqual(Object.keys(progress.results).sort(), rows.map(e => String(e.row.match_id)).sort(), 'Missing/unexpected collection IDs');
  for (const e of rows) {
    assert.equal(progress.results[e.row.match_id].status, e.acquisition?.status, `Stale receipt: ${e.row.match_id}`);
    assert(['parsed','reused','failed'].includes(e.acquisition?.status ?? '') && e.status !== 'pending', `Not terminal: ${e.row.match_id}`);
    assert((e.status === 'acquisition-failed') === (e.acquisition?.status === 'failed'), `Contradictory collection outcome: ${e.row.match_id}`);
    if (e.legacy16) assert.equal(e.status, 'verified', `Lost original match ${e.row.match_id}`);
  }
}

function collect(mode: 'partial' | 'publish' | 'check', noCache: boolean, finalProgress?: string) {
  const c: Cohort = read(`${more}/eligible.json`), eligibleSha256 = hash(`${more}/eligible.json`);
  checkCohort(c);
  const old = read(originalFile), originals = new Map<number, Packed['match']>(old.matches.map((m: Packed['match']) => [m.id, m]));
  assert.equal(originals.size, 16); assert.equal(old.matches.length, 16);
  for (const id of originals.keys()) assert(c.rows.some(r => r.match_id === id), `Original ${id} missing from union`);
  const patchRoot = fs.existsSync(`${more}/patch-list.json`) ? more : legacy, buildRoot = fs.existsSync(`${more}/builds.json`) ? more : legacy;
  const config = patchEvidence742(patchRoot, buildRoot), progressFile = `${more}/progress.json`;
  const progressBytes = fs.existsSync(progressFile) ? fs.readFileSync(progressFile) : null;
  const progress: Progress | null = progressBytes && JSON.parse(progressBytes.toString('utf8'));
  if (mode === 'publish' && (progress?.complete !== true || finalProgress !== undefined)) {
    assert(progressBytes && finalProgress === sha(progressBytes),
      'No complete:true seal. Use --partial, or freeze an explicitly accepted terminal attempt with --publish --final-progress=<progress.json SHA-256>');
  }
  // A report wording change does not invalidate compacts; a verifier/attribution change does.
  const codeKey = sha([packOne.toString(), hash('scripts/replays/study-742.ts'), hash('src/replays/farming-routes.ts')].join('|'));
  const matches: ExpandedMatch[] = [], rows: Entry[] = [];
  if (mode !== 'check' && !noCache) fs.mkdirSync(cacheDir, { recursive: true });
  for (const r of c.rows.toSorted((a,b) => a.hero_id-b.hero_id || b.mmr-a.mmr || b.match_id-a.match_id)) {
    const id = r.match_id, legacy16 = originals.has(id), acquisition = progress?.results[id] ?? null;
    const root = legacy16 ? legacy : more, e: Entry = { row: r, legacy16, acquisition, status: 'pending' };
    rows.push(e);
    if (!fs.existsSync(`${root}/${id}.facts.json`)) {
      if (acquisition?.status === 'failed') e.status = 'acquisition-failed';
      else if (['parsed','reused'].includes(acquisition?.status ?? '')) e.reason = 'Collector reported success but facts are missing';
      continue;
    }
    try {
      const result = packOne(r, root, config, codeKey, !noCache, mode !== 'check' && !noCache, old.map, originals.get(id));
      matches.push(result.match); e.status = 'verified'; e.evidence = result.evidence;
    } catch (error) {
      e.status = (error as NodeJS.ErrnoException).code === 'ENOENT' ? 'pending' : 'rejected';
      e.reason = errorText(error); console.error(`${e.status} ${id}: ${e.reason}`);
    }
  }
  if (mode !== 'partial') {
    publishable(rows, progress);
    assert.equal(hash(progressFile), sha(progressBytes!), 'Collection changed while packing');
    assert.equal(hash(`${more}/eligible.json`), eligibleSha256, 'Eligibility changed while packing');
  }
  // The stable labels are part of the published evidence; never publish the machine/share path.
  const label = (dir: string) => `.tmp/${path.basename(dir)}`;
  const provenance = { eligibleSha256, originalSha256: hash(originalFile), patchEvidenceRoot: label(patchRoot), buildsRoot: label(buildRoot) };
  const study = { version: 1, scope: old.scope, captureDateUTC: c.sources.map(s => new Date(s.checkedAt).toISOString().slice(0,10)).sort().at(-1),
    ...config, minimumMatchMmr: c.minimumMatchMmr, selection: c.selection, sources: c.sources, provenance, map: old.map,
    format: { ...old.format, ranking: undefined,
      points: 'First actual samples >= 5/10/15/20 min, reached boundaries only. t=game seconds, lane=m_iCreepKillGold, neutral=m_iNeutralKillGold, lh=m_iLastHitCount; includes owned units. No terminal substitution.',
      startPoint: 'First nonnegative sampled frame; same t/lane/neutral/lh keys as points.',
      endPoint: 'Actual terminal frame, even in games shorter than 20 minutes. Full/truncated window rates use measured elapsed time.' },
    summary: summarizeExpanded(matches, old.map), matches };
  const counts = { eligible: rows.length, verified: matches.length, original16: rows.filter(e => e.legacy16 && e.status === 'verified').length,
    newVerified: rows.filter(e => !e.legacy16 && e.status === 'verified').length, full10to20: matches.filter(m => m.points.length === 4).length,
    short: matches.filter(m => m.points.length < 4).length, acquisitionFailed: rows.filter(e => e.status === 'acquisition-failed').length,
    verificationRejected: rows.filter(e => e.status === 'rejected').length, pending: rows.filter(e => e.status === 'pending').length };
  const studyText = json({ ...(mode === 'partial' ? { provisional: true } : {}), ...study, counts });
  const audit = { version: 1, ...provenance, minimumMatchMmr: c.minimumMatchMmr, progressSha256: progressBytes && sha(progressBytes), updatedAt: progress?.updatedAt,
    studySha256: sha(studyText), counts, sources: c.sources, rows };
  if (mode === 'partial') {
    save(`${more}/pack-preview.json`, studyText);
    save(`${more}/pack-preview-audit.json`, json({ provisional: true, ...audit }, 2));
    save(`${more}/pack-preview.md`, '> **临时预览，不是最终发布；分母可能随采集重试而变。**\n\n' + renderReport(study, audit));
  } else {
    const outputs = [[studyFile, studyText], [auditFile, json(audit, 2)], [docFile, renderReport(study, audit)]] as const;
    for (const [name, text] of outputs) {
      if (mode === 'check') assert(fs.readFileSync(name, 'utf8') === text, `${name} differs`);
      else save(name, text);
    }
  }
  console.log(`${mode}: ${JSON.stringify(counts)}`);
}

const windows = [[0,300], [300,600], [600,900], [900,1200], [600,1200]] as const;
type Coverage = 'full' | 'truncated' | 'notReached';
export function windowPoints(m: ExpandedMatch, from: number, to: number) {
  const at = (s: number) => s === 0 ? m.startPoint : m.points[endpoints.findIndex(t => t === s)];
  const a = at(from), boundary = at(to), b = boundary ?? m.endPoint;
  const coverage: Coverage = !a || b.t <= a.t ? 'notReached' : boundary ? 'full' : 'truncated';
  return { a, b, coverage };
}
function median(values: number[]) {
  const s = values.toSorted((a,b) => a-b);
  return s.length ? (s[(s.length-1)>>1] + s[s.length>>1]) / 2 : null;
}
type Camp = Landmarks['camps'][number];
type Observation = { t: number; tick: number; site: string | null; sample: number };
type Example = { id: number; times: number[]; ticks: number[] };
function firstIndex(length: number, accept: (i: number) => boolean) {
  let lo = 0, hi = length;
  while (lo < hi) { const mid = (lo + hi) >>> 1; if (accept(mid)) hi = mid; else lo = mid+1; }
  return lo;
}
// Consume already attributed events. Trace proximity can *veto* an origin as walking
// evidence but can never supply a missing camp/lane, order same-tick hits, or move a dot.
function observations(m: ExpandedMatch, camps: Map<string, Camp>) {
  const trace = m.trace, breaks = trace.filter(p => !p[3] || p[4]).map(p => p[0]);
  const stream: Observation[] = [];
  for (let i = 0; i < m.events.length;) {
    const [t,,,,,tick] = m.events[i], batch: Hit742[] = [];
    do { const e = m.events[i++]; if (e[2] !== 'illusion') batch.push(e); } while (m.events[i]?.[5] === tick);
    if (!batch.length || t < 300) continue;
    const j = firstIndex(trace.length, k => trace[k][0] >= t);
    const p = j && (!trace[j] || t-trace[j-1][0] <= trace[j][0]-t) ? trace[j-1] : trace[j];
    const site = batch[0][1], camp = camps.get(site);
    const ok = p && p[3] && Math.abs(p[0]-t) <= 1.05 && (camp || /^lane:(top|mid|bot)$/.test(site)) &&
      (!camp || Math.hypot(p[1]-camp.x, p[2]-camp.y) <= 1000) &&
      batch.every(([, loc, actor, x, y]) => loc === site && actor === 'body' &&
        x != null && y != null && Math.hypot(x-p[1], y-p[2]) <= 1000);
    stream.push({ t, tick, site: ok ? site : null, sample: p?.[0] ?? t });
  }
  return { stream, breaks };
}
function routePaths(id: number, obs: ReturnType<typeof observations>, from: number, to: number) {
  const paths = new Map<string, Example>();
  let chain: { first: Observation; last: Observation }[] = [];
  const add = (nodes: Observation[]) => {
    const sites = nodes.map(p => p.site!);
    if (sites.every(s => s.startsWith('lane:')) || nodes.at(-1)!.t - nodes[0].t > 90) return;
    const key = sites.join('>');
    if (!paths.has(key)) paths.set(key, { id, times: nodes.map(p => p.t), ticks: nodes.map(p => p.tick) });
  };
  for (const p of obs.stream) {
    if (p.t < from) continue; if (p.t >= to) break;
    if (!p.site) { chain = []; continue; }
    const last = chain.at(-1)?.last;
    if (last) {
      const start = Math.min(last.t, last.sample), end = Math.max(p.t, p.sample);
      const br = obs.breaks[firstIndex(obs.breaks.length, i => obs.breaks[i] > start)];
      if (p.t-last.t > 30 || br <= end) chain = [];
    }
    const prev = chain.at(-1);
    if (prev?.last.site === p.site) { prev.last = p; continue; }
    if (prev) add([prev.last, p]);
    if (chain.length >= 2) add([chain.at(-2)!.last, prev!.first, p]);
    chain = [...chain.slice(-1), { first: p, last: p }];
  }
  return paths;
}
function measure(m: ExpandedMatch, camps: Map<string, Camp>) {
  const obs = observations(m, camps);
  return { m, windows: windows.map(([from,to]) => {
    const { a, b, coverage } = windowPoints(m, from, to);
    if (coverage === 'notReached') return null;
    const stop = coverage === 'full' ? to : b.t, seconds = b.t-a.t;
    const lane = b.lane-a.lane, neutral = b.neutral-a.neutral, gold = lane+neutral;
    const hits = m.events.filter(e => from <= e[0] && e[0] < stop);
    return { coverage, seconds, lane, neutral, lh: b.lh-a.lh, rate: 60*gold/seconds, share: gold ? lane/gold : null,
      camps: new Set(hits.filter(e => e[2] === 'body' && camps.has(e[1])).map(e => e[1])).size,
      paths: from >= 300 ? routePaths(m.id, obs, from, stop) : null };
  }) };
}
type Measured = ReturnType<typeof measure>;
type Window = NonNullable<Measured['windows'][number]>;
function bucket(ws: Window[], traceWindow: boolean) {
  const support = new Map<string, { sites: string[]; matches: number; example: Example }>();
  for (const w of ws) for (const [key, example] of w.paths ?? []) {
    const prev = support.get(key);
    if (prev) prev.matches++; else support.set(key, { sites: key.split('>'), matches: 1, example });
  }
  const paths = [...support.values()].sort((a,b) => b.matches-a.matches || byText(a.sites.join('>'), b.sites.join('>')));
  const shares = ws.flatMap(w => w.share === null ? [] : [w.share]);
  return { n: ws.length, medianSeconds: median(ws.map(w => w.seconds)), medianLHDelta: median(ws.map(w => w.lh)),
    medianLaneGold: median(ws.map(w => w.lane)), medianNeutralGold: median(ws.map(w => w.neutral)),
    medianFarmPerMinute: median(ws.map(w => w.rate)), medianLaneShare: median(shares), laneShareN: shares.length,
    bothGoldMatches: ws.filter(w => w.lane > 0 && w.neutral > 0).length, medianDistinctBodyCamps: median(ws.map(w => w.camps)),
    routes: traceWindow ? { matchesWithLink: ws.filter(w => w.paths!.size).length, topLinks: paths.filter(r => r.sites.length === 2).slice(0,3),
      topTriples: paths.filter(r => r.sites.length === 3).slice(0,3) } : null };
}
function group(rows: Measured[]) {
  return { n: rows.length, accounts: new Set(rows.map(({m}) => m.accountId)).size, wins: rows.filter(({m}) => m.won).length,
    cumulativeLH: endpoints.map((t,i) => {
      const values = rows.flatMap(({m}) => m.points[i] ? [m.points[i].lh] : []);
      return { seconds: t, n: values.length, median: median(values) };
    }), windows: windows.map(([from,to],i) => {
      const samples = rows.map(r => r.windows[i]);
      return { from, to, notReached: samples.filter(w => !w).length,
        full: bucket(samples.filter((w): w is Window => w?.coverage === 'full'), from >= 300),
        truncated: bucket(samples.filter((w): w is Window => w?.coverage === 'truncated'), from >= 300) };
    }) };
}
export function summarizeExpanded(matches: ExpandedMatch[], map: Landmarks) {
  const camps = new Map(map.camps.map(c => [c.id,c])), measured = matches.map(m => measure(m, camps));
  const highBands = [...new Set(matches.filter(m => m.mmr >= highFloor).map(m => Math.floor(m.mmr/500)*500))].sort((a,b) => a-b);
  const groups = heroKeys.flatMap(hero => (['both', ...sides] as const).flatMap(team => {
    const base = measured.filter(({m}) => m.hero === hero && (team === 'both' || m.team === team));
    return [['all',7000,null], ['high',highFloor,null], ...highBands.map(n => [`band-${n}`,n,n+500] as const)].map(t => {
      const [tier, minMmr, maxExclusive] = t as readonly [string, number, number | null];
      return { hero, team, tier, minMmr, maxExclusive,
        ...group(base.filter(({m}) => m.mmr >= minMmr && (maxExclusive === null || m.mmr < maxExclusive))) };
    });
  }));
  return { highFloor, groups };
}

const cnHero = { luna: '露娜', troll_warlord: '巨魔战将' }, cnSide = { radiant: '天辉', dire: '夜魇' };
const num = (n: number | null, places = 1) => n === null ? '—' : String(Number(n.toFixed(places)));
const pct = (n: number | null) => n === null ? '—' : `${(100*n).toFixed(1)}%`;
const clock = (t: number) => `${Math.floor(t/60)}:${String(Math.floor(t%60)).padStart(2,'0')}`;
const siteName = (s: string) => ({ 'lane:bot':'下路兵', 'lane:mid':'中路兵', 'lane:top':'上路兵' })[s] ?? `\`${s}\``;
const table = (heading: string[], rows: (string | number)[][]) =>
  [heading, heading.map(() => '---'), ...rows].map(cells => `| ${cells.join(' | ')} |`).join('\n');
const studyLink = '[逐场机器数据](../data/farming-742-expanded.json)', auditLink = '[逐候选审计](../data/farming-742-expanded-audit.json)';
type ReportStudy = { summary: ReturnType<typeof summarizeExpanded>; captureDateUTC?: string; matches: ExpandedMatch[] };
type ReportAudit = { rows: Entry[]; counts: { eligible: number; verified: number; original16: number; newVerified: number;
  full10to20: number; short: number; acquisitionFailed: number; verificationRejected: number } };
function renderReport(study: ReportStudy, audit: ReportAudit) {
  const { counts: c, rows } = audit, { matches, summary } = study;
  const g = (hero: Hero742, team: Team742 | 'both' = 'both', tier = 'all') =>
    summary.groups.find(r => r.hero === hero && r.team === team && r.tier === tier)!;
  const interval = (from: number, to: number) => `${from/60}–${to/60}`;
  const sourceCounts = heroKeys.map(h => {
    const candidates = rows.filter(e => heroes[e.row.hero_id] === h), a = g(h), b = g(h,'both','high');
    return [cnHero[h], `${a.n}/${candidates.length}`, a.accounts, `${g(h,'radiant').n} / ${g(h,'dire').n}`,
      a.windows.at(-1)!.full.n, a.n-a.cumulativeLH[3].n, `${b.n}/${candidates.filter(e => e.row.mmr >= highFloor).length}`,
      b.windows.at(-1)!.full.n];
  });
  const short = matches.filter(m => m.points.length < 4);
  const link = (m: ExpandedMatch) => `[${m.id}](${m.url})`;
  const economy = heroKeys.flatMap(hero => g(hero).windows.map((w,i) => {
    const h = g(hero,'both','high').windows[i].full;
    return [cnHero[hero], interval(w.from,w.to), `${w.full.n} / ${w.truncated.n} / ${w.notReached}`,
      pct(w.full.medianLaneShare), num(w.full.medianFarmPerMinute), num(w.full.medianDistinctBodyCamps),
      `${h.n} / ${pct(h.medianLaneShare)} / ${num(h.medianFarmPerMinute)} / ${num(h.medianDistinctBodyCamps)}`];
  }));
  const trunc = heroKeys.flatMap(hero => g(hero).windows.flatMap((w,i) => !w.truncated.n ? [] :
    [[cnHero[hero], interval(w.from,w.to), w.truncated.n, num(w.truncated.medianSeconds),
      pct(w.truncated.medianLaneShare), num(w.truncated.medianFarmPerMinute), num(w.truncated.medianDistinctBodyCamps),
      g(hero,'both','high').windows[i].truncated.n]]));
  type Bucket = ReturnType<typeof bucket>;
  const best = (b: Bucket) => b.routes?.topTriples[0] ?? b.routes?.topLinks[0];
  const routeCell = (b: Bucket) => {
    const r = best(b);
    return r ? `${r.sites.map(siteName).join(' → ')}（${r.matches}/${b.n}）` : b.n ? `无可连证据（0/${b.n}）` : '—（无完整窗）';
  };
  const sideTables = heroKeys.flatMap(hero => [
    `### ${cnHero[hero]}`,
    table(['阵营','分钟','全体：最常见三站（支持局/完整窗局）','≥7500：同口径路线'],
      sides.flatMap(side => g(hero,side).windows.slice(1,4).map((w,i) => [cnSide[side], interval(w.from,w.to),
        routeCell(w.full), routeCell(g(hero,side,'high').windows[i+1].full)]))),
    sides.flatMap(side => {
      const b = g(hero,side,'high').windows.at(-1)!.full, r = best(b);
      if (!r) return [];
      const m = matches.find(m => m.id === r.example.id)!;
      return [`- **${cnSide[side]}高分对秒**：${link(m)}（对局均分 ${m.mmr}，${m.won ? '胜' : '负'}）。` +
        r.sites.map((s,i) => `${clock(r.example.times[i])} ${siteName(s)}`).join(' → ') +
        `。完整 10–20 分钟同组 ${r.matches}/${b.n} 局出现过这段顺序；不是说这些局都照此走完十分钟。`];
    }).join('\n'),
  ]);
  const bandRows = summary.groups.filter(r => r.team !== 'both' && r.tier.startsWith('band-')).map(r => {
    const b = r.windows.at(-1)!.full;
    return [cnHero[r.hero], cnSide[r.team as Team742], `${r.minMmr}–${r.maxExclusive!-1}`, `${r.n} / ${r.accounts}`,
      b.n, pct(b.medianLaneShare), routeCell(b)];
  });
  const lh = heroKeys.map(hero => [cnHero[hero], ...g(hero).cumulativeLH.map(p => `${num(p.median)}（${p.n}）`)]);
  const catalogShort = rows.filter(e => e.row.duration < 1200);
  const failures = rows.filter(e => e.status === 'acquisition-failed'), err = (e: Entry) => e.acquisition?.error ?? '';
  const http520 = failures.filter(e => /OpenDota HTTP 520/i.test(err(e))).length;
  const timeout = failures.filter(e => /timeout|timed out/i.test(err(e)) && !/OpenDota HTTP 520/i.test(err(e))).length;
  const failureNote = failures.length ? `本轮最终采集失败按对局计：OpenDota HTTP 520 ${http520} 场，超时 ${timeout} 场，其他原因 ${failures.length-http520-timeout} 场。` : '';
  const examples = heroKeys.map(hero => {
    const a = g(hero).windows.at(-1)!.full, b = g(hero,'both','high').windows.at(-1)!.full;
    return `- **${cnHero[hero]}**：完整 10–20 分钟，${a.bothGoldMatches}/${a.n} 局两类金币均有增加；线占比中位数 ${pct(a.medianLaneShare)}，` +
      `在相同的 ≥7500 门槛下为 ${pct(b.medianLaneShare)}（${b.n} 局）。`;
  }).join('\n');
  return [
    `# 7.42 一号位打钱路线：${c.newVerified ? '扩展样本' : '扩展采集审计（未新增已验回放）'}`,
    `${studyLink} · ${auditLink} · [原 16 场研究（独立保留）](farming-742.md)`,
    `**${c.eligible} 个候选，${c.verified} 场逐局核验通过：原 ${c.original16} 场只计一次，新增 ${c.newVerified} 场。** ` +
      `${c.full10to20} 场覆盖完整 10–20 分钟，${c.short} 场比赛在 20 分钟前已正常结束。` +
      `未纳入：采集失败 ${c.acquisitionFailed}，证据核验未通过 ${c.verificationRejected}。失败不是零补刀，也不进入下表分母。` +
      (c.newVerified ? '' : '**下列路线仍然只来自原 16，不能据此声称扩展复现，或检验较低 MMR 档。**'),
    '## 两层证据：节奏不是路线',
    table(['层级','露娜 / 巨魔','能回答什么'], [
      ['D2PT 分钟层 · [节奏研究](farming-742-tempo.md) / [分钟数据](../data/farming-742-tempo.json)',
        heroKeys.map(h => rows.filter(e => heroes[e.row.hero_id] === h).length).join(' / '),
        '对局目录及分钟累计补刀的节奏。各分钟分母看节奏研究；没有英雄坐标、出生营、击杀归属或主栏首见。'],
      [`Valve 回放空间层 · ${studyLink}`, heroKeys.map(h => g(h).n).join(' / '),
        '只对通过回放核验的局描述兵线来源、野点、本体轨迹和实测端点。是上一行的子集，不是另外一批可相加的对局。'],
    ]),
    failureNote + '没有可核回放的 ID 只留在候选审计；不由 D2PT 补刀差反推走位、野点或幻象。后续成功重试可以更新本快照，旧 16 发布包不变。',
    examples, '## 空间层样本与分母',
    `目录冻结日 **${study.captureDateUTC} UTC**。合并两次 D2PT 快照，逐行限定 7.42、高分排位（lobby 7）、一号位、` +
      '有效账号、对局平均 MMR ≥7000。以 match ID 去重，不按时长、胜负、出装或每账号限额挑局。两英雄没有同场重合。' +
      '≥7500 是与旧研究相同的**分数门槛**，不是再次每边只取四个账号；分数不是选手个人 MMR。',
    table(['英雄','已验/候选','不同账号','天辉 / 夜魇','完整 10–20','短于 20','≥7500 已验/候选','≥7500 完整 10–20'], sourceCounts),
    '## 补刀、收益和野点：先按局计算，再取中位数',
    table(['英雄','5 分补刀（n）','10 分（n）','15 分（n）','20 分（n）'], lh),
    '“线占比”是玩家小兵类金币增量 ÷（小兵类＋野怪类金币增量），含部分召唤物；合计金/分用两次取样的实际秒差，不是总 GPM。' +
      '两类金币都为零时不算占比（JSON 的 `laneShareN`），其他指标仍计该局。野点数只数有本体击杀、能确认出生器的不同营地，不表示清营或英雄一定站在营地里。',
    table(['英雄','分钟','完整 / 截断 / 未开始','完整：线占比','合计金/分','野点数','≥7500 完整：n / 线占比 / 金/分 / 野点数'], economy),
    '### 短局：截断不是回放损坏',
    `目录时长不足 20 分钟的候选 ${catalogShort.length} 场，${catalogShort.filter(e => e.status === 'verified').length} 场通过核验；分组以回放时钟为准。` +
      '完整解析到比赛结束才算核验成功。短局已走完的早窗照常计入完整组；跨过终局的窗只算到终局帧，' +
      '未到起点（或没有正时间差）则无观察。下表只列有实际截断观察的窗；10–20 与 15–20 可以包含同一局，不能相加。',
    trunc.length ? table(['英雄','目标窗（分）','截断 n','实际秒数中位','线占比','合计金/分','野点数','其中 ≥7500 n'], trunc) : '本次没有通过核验且窗口被终局截断的记录。',
    ...short.map(m => `- ${link(m)}，${cnHero[m.hero]}、${cnSide[m.team]}、MMR ${m.mmr}：终局 ${clock(m.endPoint.t)}（${m.endPoint.t} 秒，${m.endPoint.lh} 补）；` +
      `已到达整五分钟端点：${endpoints.slice(0,m.points.length).map(t => t/60).join('、') || '无'}。`),
    '## 分阵营看本体怎样接线、接野',
    '箭头只连有轨迹佐证的连续本体击杀，兵按**出生路**、野按**出生营**。' +
      '表中优先列同一窗支持局数最多的连续三站；没有三站才列两站。同一局重复走十遍仍只算一局，分母包括没走成任何可确认路线的完整窗；并列按地点 ID 排序。' +
      '例如天辉下路邻近普通营是 `good_1`（小）、`good_2`（大）；夜魇上路为 `evil_2`（小）、`evil_1`（大）。[地图地标](assets/farming-742-landmarks.svg)。',
    ...sideTables,
    '### 进一步固定 MMR 分档：完整 10–20 分钟', table(['英雄','阵营','对局均分','已验局 / 账号','完整 n','线占比','本档路线（支持局/完整 n）'], bandRows),
    '分档内若只有一两局，只能当作找回放的线索。常见路径不是最优走法；没有记录到连续击杀也不等于没有经过。' +
      '目录曝光、回放可获取性、重复账号、对线形势和早早结束比赛都会影响比较，不能据此推断职业总体、个人胜率或出装因果。',
    '## 证据与复算',
    '- 版本与身份：官方补丁起止、OpenDota 补丁 61/开局/阵营/英雄/胜负/时长、可见账号、逐版 `steam.inf`，以及十人终局正补。每个成功 ID 都重新核对压缩回放、DEM 和元数据 SHA-256；与 facts 的 DEM 哈希相符并不认证 HTTP 传输。',
    '- 原 16 用全量 facts；新 `route-core` 普通秒帧只需目标英雄，终局仍核十人，均验证相同 28 个野点、22 座塔。' +
      '共用 `study-742.ts` 的同 tick/单位名归属，不猜最近野点，不合并同 tick 的多个死者。`events` 保留本体、幻象、未知及未定位事件；`firstHeld` 是全局主栏首见，不是购买时间。',
    '- `points` 只含真正到达的 5/10/15/20 分钟首帧（延迟 <1.2 秒）。`startPoint` 是首个非负游戏时刻；`endPoint` 是终局，不冒充整点。经济含受控单位；事件窗左闭右开，研究最多到 20 分钟。',
    '- 路线：5–20 分钟本体 `trace` 约每 2 游戏秒采样；0–5 只有事件和经济，不画连线。' +
      '要求存活样本距事件 ≤1.05 秒、距死点 ≤1000 地图单位，野怪还须距出生点 ≤1000。只考虑本体；幻象忽略，身份/出身/死点不明或同 tick 有多种地点则断链。' +
      '相邻击杀间隔 ≤30 秒，不跨轨迹中断、死亡或时间窗；合并同地连续击杀，三站跨度 ≤90 秒。读的是击杀顺序，不是两站之间走过的每一步。',
    `- ${auditLink}包含所有候选的目录行、采集状态与错误、核验结论和证据散列。所有地图与轨迹仅供离线回放，不是实战可见信息。`,
    '```powershell\n# 只读发布包内统计；无需网络或 .tmp\nnode scripts/replays/pack-expanded-742.ts --summary\n' +
      '# 采集中：只更新工作根目录下的 farm742-more/pack-cache 与 pack-preview*\nnode scripts/replays/pack-expanded-742.ts --partial\n' +
      '# 采集负责人确认冻结本轮、所有 ID 均为终态后才发布；后续重试另封快照（complete:true 时可省 --final-progress）\n' +
      '$work = node --input-type=module -e "import {replayArea} from \'./src/replays/storage.ts\'; console.log(replayArea(\'farm742-more\'))"\n' +
      '$seal = (Get-FileHash (Join-Path $work progress.json) -Algorithm SHA256).Hash.ToLowerInvariant()\n' +
      'node scripts/replays/pack-expanded-742.ts --publish "--final-progress=$seal"\n# 从原始输入冷复核三份发布物\n' +
      'node scripts/replays/pack-expanded-742.ts --check --no-cache\n# 原始 16 不受影响\nnode scripts/replays/study-742.ts --check\n```',
  ].join('\n\n') + '\n';
}

if (process.argv[1] && path.resolve(process.argv[1]) === import.meta.filename) {
  const args = process.argv.slice(2), modes = args.filter(a => ['--partial','--publish','--check','--summary'].includes(a));
  const seal = args.filter(a => /^--final-progress=[0-9a-f]{64}$/i.test(a));
  assert(modes.length <= 1 && seal.length <= 1 && (!seal.length || modes[0] === '--publish') &&
    args.every(a => modes.includes(a) || a === '--no-cache' || seal.includes(a)),
    'Usage: pack-expanded-742.ts [--partial (default) | --publish [--final-progress=<sha256>] | --check | --summary] [--no-cache]');
  if (modes[0] === '--summary') {
    const study = read(studyFile), summary = summarizeExpanded(study.matches, study.map);
    assert(json(summary) === json(study.summary), 'Published aggregates differ from match evidence');
    console.log(json(summary, 2).trimEnd());
  }
  else collect(modes[0] === '--check' ? 'check' : modes[0] === '--publish' ? 'publish' : 'partial',
    args.includes('--no-cache'), seal[0]?.split('=')[1].toLowerCase());
}
