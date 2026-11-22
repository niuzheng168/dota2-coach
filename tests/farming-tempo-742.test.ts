import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
const load = (name: string) => JSON.parse(readFileSync(new URL('../data/' + name, import.meta.url), 'utf8'));
const file = load('farming-742-tempo.json'), rows: any[] = file.matches;

test('D2PT cadence covers 101 matches, not 101 spatial routes; four short games are not extrapolated', () => {
  assert.equal(file.patch, '7.42');
  assert.equal(new Set(rows.map(r => r.id)).size, 101);
  for (const r of rows) {
    assert(!('trace' in r || 'events' in r || 'laneGold' in r));
    assert(r.mmr >= 7000 && r.minuteLH.length === Math.floor(r.duration / 60) + 1);
    assert(r.minuteLH.every((n: number, i: number) => Number.isSafeInteger(n) && n >= 0 && n <= r.finalLH && (!i || n >= r.minuteLH[i - 1])));
  }
  const short = rows.filter(r => r.duration < 1200);
  assert.equal(short.length, 4);
  assert(short.every(r => r.hero === 'luna' && r.minuteLH[20] === undefined));
  const luna = file.summary.luna.all, troll = file.summary.troll_warlord.all;
  assert.deepEqual([luna.n, troll.n, luna.accounts, troll.accounts, luna.lh[20].n, troll.lh[20].n], [65, 36, 15, 13, 61, 36]);
});

test('directory clock alignment is independently checked against 64 Source 2 counter samples', () => {
  const reference = load('farming-742-routes.json');
  const errors = reference.matches.flatMap((m: any) => {
    const r = rows.find(r => r.id === m.id);
    assert.equal(r.accountId, m.accountId);
    return m.points.map((p: any) => Math.abs(p.lh - r.minuteLH[Math.floor(p.t / 60)]));
  });
  assert.deepEqual([errors.length, errors.filter((e: number) => e === 0).length, Math.max(...errors)], [64, 63, 1]);
  assert.deepEqual(file.replayCrossCheck, { matches: 16, boundaries: 64, exact: 63, maxLHError: 1 });
});

test('10–20 gains are paired within each match, with an account-weighted sensitivity estimate', () => {
  const med = (a: number[]) => {
    const s = a.sort((a, b) => a - b);
    return (s[Math.floor((s.length - 1) / 2)] + s[Math.floor(s.length / 2)]) / 2;
  };
  for (const [hero, n, gain, accountGain] of [['luna', 61, 155, 157], ['troll_warlord', 36, 131, 126]] as const) {
    const g = rows.filter(r => r.hero === hero && r.duration >= 1200), delta = (r: any) => r.minuteLH[20] - r.minuteLH[10];
    assert.equal(g.length, n);
    const accounts = [...new Set(g.map(r => r.accountId))];
    assert.equal(med(g.map(delta)), gain);
    assert.equal(med(accounts.map(id => med(g.filter(r => r.accountId === id).map(delta)))), accountGain);
    assert.equal(file.summary[hero].all.gain10to20.median, gain);
    assert.equal(file.summary[hero].all.gain10to20EachAccountOnce.median, accountGain);
  }
});
