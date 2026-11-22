import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import type { ExpandedMatch, summarizeExpanded } from '../scripts/replays/pack-expanded-742.ts';
import type { Row742 } from '../scripts/replays/study-742.ts';

// Only the frozen small artifacts are needed. The roster oracle was calculated directly
// from the two raw catalog pairs; verified counts may grow if more evidence is recovered.
const file = 'data/farming-742-expanded.json', bytes = readFileSync(file);
const legacyBytes = readFileSync('data/farming-742-routes.json'), original = JSON.parse(legacyBytes.toString());
const study = JSON.parse(bytes.toString()) as { matches: ExpandedMatch[]; summary: ReturnType<typeof summarizeExpanded>;
  scope: string; map: typeof original.map; counts: Record<string,number>; provisional?: boolean };
const audit = JSON.parse(readFileSync('data/farming-742-expanded-audit.json','utf8')) as {
  counts: Record<string,number>; originalSha256: string; studySha256: string; progressSha256: string; eligibleSha256: string;
  rows: { row: Row742; legacy16: boolean; status: string; acquisition: { status: string; error?: string };
    evidence?: { [k: string]: string | number } }[];
};
const sha = (s: string | Buffer) => createHash('sha256').update(s).digest('hex');
const byId = new Map(study.matches.map(m => [m.id,m]));
const bounds = [300,600,900,1200], hex = /^[a-f0-9]{64}$/;
const med = (a: number[]) => {
  const s = a.toSorted((x,y) => x-y);
  return s.length ? (s[Math.floor((s.length-1)/2)] + s[Math.ceil((s.length-1)/2)]) / 2 : null;
};

test('101 frozen candidates, exact audit/compact partition, original 16 preserved once; no D2PT-only route', () => {
  assert.equal(study.provisional,undefined); assert.equal(study.scope,'omniscient-offline-only');
  assert.equal(audit.studySha256,sha(bytes)); assert.equal(audit.originalSha256,sha(legacyBytes));
  assert.equal(audit.originalSha256,'23d8b9c99095d4ff1d9da077dbb844a573720fd3c95f61e59c61b6f4d3ad7bc2');
  assert.match(audit.progressSha256,hex); assert.match(audit.eligibleSha256,hex);
  assert.deepEqual(study.map,original.map); assert.equal(audit.rows.length,101);
  const roster = audit.rows.map(({row:r}) => [r.match_id,r.hero_id,r.mmr,r.account_id,r.is_radiant,r.last_hits]).sort((a,b) => a[0]-b[0]);
  assert.equal(sha(JSON.stringify(roster)),'8e3c96b645ff0dc4c48b8abfdf64bcdd35e55e497075395df9bd73d6db2f6746');
  assert.equal(new Set(roster.map(r => r[0])).size,101);
  const c = study.counts; assert.deepEqual(c,audit.counts);
  assert.equal(c.pending,0); assert.equal(c.verified+c.acquisitionFailed+c.verificationRejected,101);
  assert.equal(c.original16,16); assert.equal(c.original16+c.newVerified,c.verified);
  assert.equal(c.verified,byId.size); assert.equal(c.verified,study.matches.length);
  assert.equal(c.short+c.full10to20,c.verified); assert.equal(c.full10to20,study.matches.filter(m => m.points.length === 4).length);
  for (const e of audit.rows) {
    const r = e.row, m = byId.get(r.match_id); assert.equal(!!m,e.status === 'verified');
    if (!m) {
      assert(!e.evidence && !e.legacy16);
      if (e.status === 'acquisition-failed') assert(e.acquisition.status === 'failed' && e.acquisition.error);
      else assert.equal(e.status,'rejected');
      continue;
    }
    assert(['parsed','reused'].includes(e.acquisition.status)); assert(e.evidence);
    for (const k of ['demSha256','archiveSha256','metadataSha256','factsSha256','acquisitionSha256']) assert.match(String(e.evidence[k]),hex);
    assert.equal(m.demSha256,e.evidence.demSha256); assert.equal(m.metadataSha256,e.evidence.metadataSha256);
    assert.deepEqual([m.hero,m.team,m.mmr,m.accountId,m.won,m.finalLH,m.start],
      [r.hero_id === 48 ? 'luna' : 'troll_warlord',r.is_radiant ? 'radiant' : 'dire',r.mmr,r.account_id,!!r.won,r.last_hits,r.activate_time]);
    assert.equal(m.endPoint.t,m.duration); assert.equal(m.endPoint.lh,m.finalLH);
    assert(m.startPoint.t >= 0 && m.startPoint.t < 1.2); assert(m.firstHeld && Array.isArray(m.trace) && Array.isArray(m.events));
    m.points.forEach((p,i) => assert(p.t >= bounds[i] && p.t < bounds[i]+1.2 && p.t <= m.endPoint.t));
    for (const [t] of m.events) assert(t >= 0 && t < 1200 && t <= m.duration);
    for (const [t] of m.trace) assert(t >= 300 && t < 1200 && t <= m.duration);
  }
  for (const old of original.matches) {
    assert(audit.rows.find(e => e.row.match_id === old.id)?.legacy16);
    const {startPoint,endPoint,...priorFields} = byId.get(old.id)!;
    assert.deepEqual(priorFields,old);
  }
});

test('all hero/side/tier medians and denominators are per-match, with real endpoint times and evidenced route exemplars', () => {
  const get = (m: ExpandedMatch, s: number) => s === 0 ? m.startPoint : m.points[bounds.indexOf(s)];
  for (const g of study.summary.groups) {
    const ms = study.matches.filter(m => m.hero === g.hero && (g.team === 'both' || m.team === g.team) &&
      m.mmr >= g.minMmr && (g.maxExclusive === null || m.mmr < g.maxExclusive));
    assert.equal(g.n,ms.length); assert.equal(g.accounts,new Set(ms.map(m => m.accountId)).size);
    for (const p of g.cumulativeLH) {
      const v = ms.map(m => get(m,p.seconds)).filter(Boolean).map(x => x.lh);
      assert.deepEqual([p.n,p.median],[v.length,med(v)]);
    }
    for (const w of g.windows) {
      assert.equal(w.full.n+w.truncated.n+w.notReached,ms.length);
      for (const k of ['full','truncated'] as const) {
        const rates: number[] = [], shares: number[] = [], ids = new Set<number>();
        for (const m of ms) {
          const a = get(m,w.from), reached = get(m,w.to), b = reached || m.endPoint;
          if (!a || b.t<=a.t || !!reached !== (k === 'full')) continue;
          const lane = b.lane-a.lane, both = lane+b.neutral-a.neutral;
          rates.push(60*both/(b.t-a.t)); if (both) shares.push(lane/both); ids.add(m.id);
        }
        const b = w[k]; assert.equal(b.n,ids.size); assert.equal(b.laneShareN,shares.length);
        assert.equal(b.medianFarmPerMinute,med(rates)); assert.equal(b.medianLaneShare,med(shares));
        if (!b.routes) { assert.equal(w.from,0); continue; }
        for (const r of [...b.routes.topLinks,...b.routes.topTriples]) {
          const {id,times,ticks} = r.example, m = byId.get(id)!;
          assert(ids.has(id) && r.matches >= 1 && r.matches <= b.routes.matchesWithLink && b.routes.matchesWithLink <= b.n);
          assert.equal(r.sites.length,times.length); assert.equal(r.sites.length,ticks.length);
          r.sites.forEach((site,i) => {
            assert(times[i] >= w.from && times[i] < Math.min(w.to,m.duration)); if (i) assert(ticks[i]>ticks[i-1]);
            assert(m.events.some(e => e[5] === ticks[i] && e[0] === times[i] && e[1] === site && e[2] === 'body' && e[3] != null));
          });
        }
      }
    }
  }
});

test('public summary is independently reproducible with no raw cache or write access; document distinguishes the two evidence layers', () => {
  const out = execFileSync(process.execPath,['--permission',`--allow-fs-read=${file}`,'--allow-fs-read=package.json',
    '--allow-fs-read=scripts/replays/pack-expanded-742.ts','--allow-fs-read=scripts/replays/study-742.ts','--allow-fs-read=src',
    'scripts/replays/pack-expanded-742.ts','--summary'],{encoding:'utf8',maxBuffer:8*1024**2});
  assert.deepEqual(JSON.parse(out),study.summary);
  const doc = readFileSync('docs/farming-742-expanded.md','utf8');
  for (const part of ['../data/farming-742-tempo.json','farming-742-tempo.md','../data/farming-742-expanded-audit.json',
    `${study.counts.verified} 场逐局核验通过`,`新增 ${study.counts.newVerified} 场`,'短局','≥7500','两层证据']) assert(doc.includes(part));
  assert.equal((doc.match(/^- \*\*(?:天辉|夜魇)高分对秒\*\*：/gm) ?? []).length,4);
});
