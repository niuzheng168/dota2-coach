import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import { estimateCamp742, glaiveLinks742, neutral742, physicalMultiplier742 } from '../src/tactics/camp-742.ts';
import type { Attacker742, CampInput742 } from '../src/tactics/camp-742.ts';
import data from '../data/farming-742-camp-units.json' with { type: 'json' };
import catalog from '../data/farming-742-camp-catalog.json' with { type: 'json' };

const other: Attacker742 = { hero: 'generic', mode: 'melee', damage: [150, 150], attackSpeed: 100, bat: 1, weapon: 'none' };
const luna: Attacker742 = { hero: 'luna', damage: [200, 200], attackSpeed: 100, weapon: 'none', glaives: 1 };
const troll: Attacker742 = { hero: 'troll_warlord', mode: 'melee', damage: [150, 150], attackSpeed: 100, weapon: 'none', fervor: 1, rage: 1 };
const input = (attacker: Attacker742 = other, units = ['kobold'], params: Partial<CampInput742> = {}): CampInput742 =>
  ({ patch: '7.42', clock: 120, scope: 'listed-units', policy: 'uninterrupted-attacks', attacker, units, ...params });
const timing = (i: CampInput742) => estimateCamp742(i).timing!.firstHitToLastDeathSeconds;
const cents = ['centaur_khan', 'centaur_outrunner', 'centaur_outrunner'];

test('independent pins, raw min/max and the actual ordinary spawn catalog', () => {
  assert.equal(data.installedBuild, 6989);
  assert.deepEqual(data.builds.map(b => b.build), [6983, 6985, 6986, 6988, 6989]);
  assert.equal(Object.keys(data.commonFileSha256).length, 7);
  const u = neutral742('npc_dota_neutral_centaur_khan')!;
  assert.deepEqual([u.hp, u.armor, ...u.gold], [1100, 4, 49, 54]);
  assert.equal(neutral742('imaginary'), null); assert.equal(neutral742('__proto__'), null);
  assert.equal(catalog.samples.length, 16); assert.equal(catalog.batches, 2446);
  assert.equal(catalog.compositions.length, 31); assert.equal(catalog.compositions.reduce((s, c) => s + c.observations, 0), catalog.batches);
  for (const c of catalog.compositions) {
    assert(c.observations >= c.matchCount && c.matchCount > 0); assert.equal(c.units.length, new Set(c.witness.ids).size);
    assert(c.units.every(n => neutral742(n)?.abilities.includes('neutral_upgrade')));
  }
  assert(catalog.compositions.some(c => c.units.join(' ') === 'kobold kobold kobold_taskmaster kobold_tunneler'));
});

test('gross bounty: first upgrade widens only the high end, not +1 to both; returns no guessed camp payout', () => {
  for (const [t, expected] of [[420, [81, 90]], [610, [81, 93]], [930, [84, 93]]] as const) {
    // 49+16+16 = 81; 54+18+18 = 90.
    const r = estimateCamp742({ patch: '7.42', clock: t, scope: 'full-unstacked', units: cents });
    assert.equal(r.status, 'bounty-only'); assert.deepEqual(r.bounty?.wholeCamp, expected); assert.equal(r.timing, null);
  }
  assert.equal(estimateCamp742(input()).bounty?.wholeCamp, null);
  const golem = estimateCamp742(input(other, ['mud_golem', 'mud_golem'], { scope: 'full-unstacked' }));
  assert.deepEqual(golem.bounty?.listed, [38, 42]); assert.equal(golem.bounty?.wholeCamp, null); assert.equal(golem.timing, null);
  assert.equal(estimateCamp742(input(other, ['dark_troll_warlord', 'dark_troll', 'dark_troll'], { scope: 'full-unstacked' })).timing, null);
  const aura = estimateCamp742(input(other, ['granite_golem', 'rock_golem', 'rock_golem'], { scope: 'full-unstacked' }));
  assert(aura.bounty && !aura.timing);
});

test('landed-hit origin, overkill and continuous capped regen; contact time exists only when supplied', () => {
  assert.deepEqual(timing(input({ ...other, damage: [280, 280] })), [0, 0]);
  const once = estimateCamp742(input({ ...other, damage: [281, 281] }, ['kobold'], { engageToFirstHitSeconds: .35 }));
  assert.deepEqual(once.timing?.engageToLastDeathSeconds, [.35, .35]);
  assert.equal(estimateCamp742(input()).timing?.engageToLastDeathSeconds, null);
  assert.deepEqual(timing(input({ ...other, damage: [140, 140] })), [2, 2]); // 280 HP; regens 0.5 between swings
  assert.deepEqual(timing(input({ ...other, damage: [141, 141] })), [1, 1]);
  assert(Math.abs(physicalMultiplier742(5) - 1 / 1.3) < 1e-12);
  assert(physicalMultiplier742(-5) > 1); assert.equal(physicalMultiplier742(0), 1);
});

test('Luna: explicit reach graph, repeat targets, limited hops, no unconditional splash on a lone target', () => {
  assert.deepEqual(glaiveLinks742([{ x: 0, y: 0 }, { x: 300, y: 400 }, { x: 1001, y: 0 }]), [[1], [0], []]);
  const ls = ['kobold', 'kobold'];
  assert.equal(estimateCamp742(input(luna, ls)).timing, null);
  assert.deepEqual(timing(input(luna, ls, { formation: { glaive: 'none' } })), [5.1, 5.1]);
  assert.deepEqual(timing(input(luna, ls, { formation: { glaive: 'all' } })), [3.4, 3.4]);
  assert.deepEqual(timing(input(luna)), [1.7, 1.7]);
  assert.equal(estimateCamp742(input(luna, ls, { formation: { glaive: [[0], [0]] } })).timing, null);
  assert.equal(estimateCamp742(input({ ...luna, weapon: 'quelling_blade' }, ls, { formation: { glaive: 'all' } })).timing, null);
  // Two high-damage primary kills are not instantaneous when the decisive projectile has a supplied hop delay.
  assert.deepEqual(timing(input({ ...luna, damage: [1000, 1000] }, ls, { formation: { glaive: 'all', hopSeconds: .2 } })), [.2, .2]);
});

test('Troll: stance BAT, Fervor builds on the same target and resets on switching, ranged Maim scenarios', () => {
  assert.deepEqual(timing(input(troll)), [1.217, 1.217]); // 1.4 * 100/115, not ranged BAT 1.7
  assert.deepEqual(timing(input(troll, ['kobold', 'kobold'])), [3.835, 3.835]); // second focus starts fresh
  const ranged = estimateCamp742(input({ ...troll, mode: 'ranged', fervor: 0, damage: [132, 132] }));
  assert.deepEqual(ranged.timing?.firstHitToLastDeathSeconds, [1.7, 3.4]);
  assert.deepEqual(new Set(ranged.timing?.scenarios.map(s => s.rangedMaim)), new Set(['none', 'every']));
});

test('ordinary Battle Fury grants melee/ranged Quell, but only melee cleaves (40% vs creeps, not 60%)', () => {
  const bf: Attacker742 = { ...other, weapon: 'bfury', damage: [86, 86] };
  assert.deepEqual(timing(input(bf)), [2, 2]); assert.deepEqual(timing(input({ ...bf, mode: 'ranged' })), [3, 3]);
  const us = ['satyr_trickster', 'tadpole'], big: Attacker742 = { ...bf, damage: [750, 750] };
  assert.equal(estimateCamp742(input(big, us)).timing, null);
  assert.deepEqual(timing(input(big, us, { formation: { cleave: 'all' } })), [1, 1]); // 60% would kill the secondary at t=0
  const huge: Attacker742 = { ...bf, damage: [1100, 1100] };
  assert.deepEqual(timing(input(huge, us, { formation: { cleave: 'all' } })), [0, 0]);
  assert.deepEqual(timing(input({ ...huge, mode: 'ranged' }, us, { formation: { cleave: 'all' } })), [1, 1]);
});

test('known Mask expiry is integrated mid-swing; static IAS and ordinary item damage are not added twice', () => {
  assert.deepEqual(timing(input({ ...other, bat: 2 })), [2, 2]);
  assert.deepEqual(timing(input({ ...other, bat: 2, madnessRemaining: .5 })), [1.5, 1.5]);
  assert.deepEqual(timing(input({ ...other, bat: 2, madnessRemaining: 6 })), [1, 1]);
  assert.equal(estimateCamp742(input({ ...other, madnessRemaining: 7 })).timing, null);
});

test('reject unknown context, nonstandard unit IDs, upgrade edges, unknown effects and unfinished trials', () => {
  for (const change of [{ patch: '7.41f' }, { scope: 'stacked' }, { clock: 450.4 }, { clock: 99999 },
    { units: [] }, { units: ['mud_golem_split'] }, { units: ['kobold_tunneler_gold'] },
    { scope: 'full-unstacked', units: ['kobold'] }]) {
    const r = estimateCamp742({ ...input(), ...change } as CampInput742);
    assert.equal(r.status, 'unsupported'); assert.equal(r.bounty, null); assert.equal(r.timing, null);
  }
  for (const a of [{ ...other, damage: [90, 80] }, { ...other, attackSpeed: NaN }, { ...other, item: 'mjollnir' },
    { ...luna, glaives: undefined }, { ...troll, mode: undefined }, { ...other, attackSpeed: 690, madnessRemaining: 1 }]) {
    const r = estimateCamp742(input(a as Attacker742)); assert.equal(r.status, 'bounty-only'); assert.equal(r.timing, null);
  }
  const crosses = estimateCamp742(input({ ...other, bat: 4 }, ['kobold'], { clock: 447 }));
  assert.equal(crosses.bounty, null); assert.equal(crosses.timing, null);
  assert(estimateCamp742(input({ ...other, damage: [.01, .01] })).reasons.includes('did-not-finish-within-120s-scenario-horizon'));
  const late = estimateCamp742(input(other, cents, { clock: 1210, scope: 'full-unstacked' }));
  assert.equal(late.status, 'bounty-only'); assert(late.bounty?.wholeCamp && !late.timing);
  assert.equal(estimateCamp742(input(other, cents, { clock: catalog.bountyTiers.at(-1)!.lastCensusClock + 1 })).bounty, null);
});

test('CLI accepts ordinary JSON over stdin; examples can be copied without installing Dota', () => {
  const run = (a: string[], stdin?: string) => JSON.parse(execFileSync(process.execPath, ['scripts/client/camp-742.ts', ...a], { input: stdin, encoding: 'utf8' }));
  const spec = run(['--input', 'troll-melee']);
  assert.deepEqual(run(['--stdin'], JSON.stringify(spec)), estimateCamp742(spec));
  assert.deepEqual(run(['--unit', 'tadpole']).baseDefinition.gold, [17, 19]);
  assert.deepEqual(run(['--unit', 'tadpole', '610']).tierQuote.bounty.listed, [17, 20]);
});

test('audited timings span first DAMAGE→final independently identified death; recompute every published scenario offline', () => {
  const report = JSON.parse(fs.readFileSync(new URL('../data/farming-742-camp-comparison.json', import.meta.url), 'utf8'));
  assert.equal(report.matches.length, 16); assert.equal(report.summary.strictEpisodes, report.episodes.length);
  const ms = new Set(report.matches.map((m: any) => m.matchId)); let compared = 0;
  for (const s of report.episodes) {
    assert(ms.has(s.matchId)); assert(s.firstHitTick <= s.firstKillTick && s.firstKillTick <= s.lastDeathTick);
    assert.equal(s.firstHitTick, Math.min(...s.members.map((u: any) => u.firstHitTick)));
    assert.equal(s.lastDeathTick, Math.max(...s.members.map((u: any) => u.deathTick)));
    assert.equal(s.members.length, new Set(s.members.map((u: any) => u.id)).size);
    assert(s.firstHitEvidence.every((x: any) => x.entityIds.every((id: string) => s.members.some((u: any) => u.id === id))));
    if (!s.model.predictions) continue;
    compared++; assert(s.firstHitTick > s.model.evidence.frameTick);
    for (const p of s.model.predictions) assert.deepEqual(estimateCamp742({ ...s.model.inputs, formation: p.formation }).timing?.firstHitToLastDeathSeconds, p.firstHitToLastDeath);
  }
  assert.equal(compared, report.summary.all.n); assert(compared > 0);
  assert(report.episodes.some((s: any) => s.firstKillToLastDeathOnly === 0 && s.observedFirstHitToLastDeath > 0));
});

test('published independent anchors: simultaneous deaths ≠ zero time, BF matching by HP, gross ≠ paid', () => {
  const { episodes, summary } = JSON.parse(fs.readFileSync(new URL('../data/farming-742-camp-comparison.json', import.meta.url), 'utf8'));
  const find = (id: number, tick: number) => episodes.find((e: any) => e.matchId === id && e.firstHitTick === tick);
  const luna = find(9100049738, 34656), troll = find(9099209663, 34236), stack = find(9098789898, 37348);
  assert.deepEqual([luna.firstKillTick, luna.lastDeathTick, luna.observedFirstHitToLastDeath, luna.observedSameTickReason14], [34736, 34736, 2.667, 104]);
  assert.equal(troll.openingPrimaryId, '641:436');
  assert.deepEqual(troll.firstHitEvidence[0].hpLoss, [64, 64, 159]);
  assert.deepEqual(troll.firstHitEvidence[0].damage, [['item_bfury', 64], ['item_bfury', 64], ['', 159]]);
  assert.deepEqual([stack.olderAtSpawn, stack.staticGrossInStartingTier, stack.observedSameTickReason14], [['2060:133'], [66, 74], 53]);
  const stable = episodes.filter((s: any) => s.allDeathsInQuotedTier);
  for (const s of stable) assert.deepEqual([0, 1].map(i => s.members.reduce((n: number, u: any) => n + u.lastGoldFields[i], 0)), s.staticGrossInStartingTier);
  assert.equal(stable.length, summary.observedGold.sameTierMatchesBothEntityLimits);
  assert.equal(episodes.length + Object.values<number>(summary.firstExclusion).reduce((a, b) => a + b), catalog.batches);
});
