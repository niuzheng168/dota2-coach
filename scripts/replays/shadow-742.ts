/** Local files/stdin -> JSON. No game observation, network, credentials, or server startup. */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildShadow742Index, observeShadow742 } from '../../src/tactics/shadow-742.ts';
import { evaluateShadow742 } from './shadow-742-eval.ts';

const root = fileURLToPath(new URL('../../', import.meta.url));
const files = { fixed: 'data/farming-742-routes.json', expanded: 'data/farming-742-expanded.json' };
const reports = { fixed: 'data/farming-742-shadow-fixed16.json', expanded: 'data/farming-742-shadow-expanded.json' };
const usage = `Local 7.42 read-only shadow (no game/cloud access)
  node scripts/replays/shadow-742.ts observe --input observation.json [--corpus auto|fixed|expanded|FILE]
  node scripts/replays/shadow-742.ts observe --input -                 # JSON on stdin
  node scripts/replays/shadow-742.ts evaluate [--corpus auto|fixed|expanded|FILE] [--summary] [--write|--check]
Default: use the expanded file if published, otherwise the fixed 16. --write writes only the designated shadow report.
The observe command returns historical candidates, never a route or actionable advice.`;
const json = (bytes: Buffer) => JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
function main(args: string[]) {
  const [command, ...rest] = args;
  if (command === '--help' || command === '-h' || !command) { console.log(usage); return; }
  if (command !== 'observe' && command !== 'evaluate') throw new Error(usage);
  const opts = new Map<string, string>();
  for (let i = 0; i < rest.length; i++) {
    const key = rest[i];
    if (opts.has(key)) throw new Error(`Duplicate ${key}`);
    if (key === '--corpus' || (command === 'observe' && key === '--input')) {
      const value = rest[++i]; if (!value || value.startsWith('--')) throw new Error(`Missing ${key} value`);
      opts.set(key, value);
    } else if (command === 'evaluate' && ['--summary', '--write', '--check'].includes(key)) opts.set(key, '1');
    else throw new Error(`Unknown option ${key}\n${usage}`);
  }
  let source = opts.get('--corpus') ?? 'auto';
  if (source === 'auto') source = fs.existsSync(path.join(root, files.expanded)) ? 'expanded' : 'fixed';
  const corpusPath = source === 'fixed' || source === 'expanded' ? path.join(root, files[source]) : path.resolve(source);
  const bytes = fs.readFileSync(corpusPath), corpus = json(bytes), index = buildShadow742Index(corpus);
  if (command === 'observe') {
    const input = opts.get('--input'); if (!input) throw new Error('Supply --input FILE or --input -');
    console.log(JSON.stringify(observeShadow742(index, json(fs.readFileSync(input === '-' ? 0 : input))), null, 2));
    return;
  }
  if (opts.has('--write') && opts.has('--check')) throw new Error('Choose --write or --check, not both');
  const canonical = (Object.keys(files) as (keyof typeof files)[]).find(k => path.resolve(root, files[k]) === corpusPath);
  const c = canonical === 'expanded' ? corpus.counts : null;
  const upstreamSpatialAudit = c && [c.eligible, c.verified, c.original16, c.newVerified].every(Number.isSafeInteger) ?
    { eligible: c.eligible, verified: c.verified, original16: c.original16, newVerified: c.newVerified } : null;
  const report = { source: { file: path.relative(root, corpusPath).replaceAll('\\', '/'), tier: 'located-replays-only', upstreamSpatialAudit,
    sha256: createHash('sha256').update(bytes).digest('hex') }, ...evaluateShadow742(index) };
  const text = JSON.stringify(report, null, 2) + '\n';
  if (opts.has('--write') || opts.has('--check')) {
    if (!canonical) throw new Error('Custom corpus: can score, but cannot replace a canonical report');
    const file = path.join(root, reports[canonical]);
    if (opts.has('--check')) assert.equal(fs.readFileSync(file, 'utf8') === text, true, `Stale report ${reports[canonical]}`);
    else fs.writeFileSync(file, text);
    console.error(`${opts.has('--check') ? 'Verified' : 'Wrote'} ${reports[canonical]}`);
  }
  if (opts.has('--summary')) {
    const brief = (s: typeof report.frozenChronological.overall) => ({
      matches: s.matches, accounts: s.accounts, fullWindows: s.fullWindows, rightCensored: s.rightCensored,
      nextFamily: { retrievalCoverage: s.nextFamily.retrieval.coverage, retrievalTop1: s.nextFamily.retrieval.top1,
        clockBaselineCoverage: s.nextFamily.baseline.coverage, clockBaselineTop1: s.nextFamily.baseline.top1,
        pairedTop1: s.nextFamily.pairedTop1 },
      retrievalCoverage: s.retrieval.coverage, retrievalTop1: s.retrieval.top1,
      clockBaselineCoverage: s.baseline.coverage, clockBaselineTop1: s.baseline.top1, pairedTop1: s.pairedTop1,
      visibleDisplay: s.visibleDisplay,
    });
    console.log(JSON.stringify({ source: report.source, corpus: report.corpus,
      frozenChronological: brief(report.frozenChronological.overall), rollingOrigin: brief(report.rollingOrigin.overall) }, null, 2));
  } else if (!opts.has('--write') && !opts.has('--check')) process.stdout.write(text);
}
if (process.argv[1] && path.resolve(process.argv[1]) === import.meta.filename) {
  try { main(process.argv.slice(2)); } catch (e) { console.error(e instanceof Error ? e.message : e); process.exitCode = 1; }
}
