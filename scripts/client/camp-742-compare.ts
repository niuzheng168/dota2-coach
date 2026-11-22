/** Independent outcome labels: join all-source neutral DAMAGE to entity transitions, not hero position. */
import assert from 'node:assert/strict';
import { gameTimeAt } from '../../src/replays/farming-routes.ts';
import { estimateCamp742, glaiveLinks742 } from '../../src/tactics/camp-742.ts';
import type { Attacker742, CampInput742, Range742 } from '../../src/tactics/camp-742.ts';
import { details, facts, group, packs, read, rounded, routes, sameBuild, save } from './camp-742-facts.ts';
import type { Creature, Pack } from './camp-742-facts.ts';

type Row = any[];
const source = read('data/farming-742-camp-units.json');
const before = (rows: Row[], tick: number) => rows.findLast(z => z[0] < tick);
const key = (tick: number, name: string) => `${tick}:${name}`;
const allowedPlain = new Set(('magic_wand magic_stick faerie_fire branches circlet boots tango famango great_famango ' +
  'ring_of_health pers broadsword boots_of_elves blade_of_alacrity diadem lifesteal quelling_blade bfury ' +
  'clarity wind_lace blink wraith_band power_treads yasha mask_of_madness gloves manta sange_and_yasha').split(' ').map(s => 'item_' + s));
const neutralItems = new Set(['item_possessed_mask', 'item_weighted_dice', 'item_stonefeather_satchel', 'item_metamorphic_mandible']);
const enhancement = new Set([null, 'item_enhancement_brawny', 'item_enhancement_tough', 'item_enhancement_quickened', 'item_enhancement_alert']);
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);
const physicalInflictors = new Set(['', 'luna_moon_glaive', 'item_bfury', 'troll_warlord_berserkers_rage']);

/** When BF hits several identically named creeps on its first tick, sometimes HP deltas independently
 * identify the primary. Require a one-to-one assignment of ALL hits, with the same unique primary
 * in EVERY possible assignment. Never assign it by proximity, roster order or death order. */
function openingPrimary(ls: Row[], tr: Row[]): string | null {
  if (ls.filter(l => !l[6]).length !== 1) return null;
  if (tr.length === 1) return tr[0][1];
  if (ls.length !== tr.length || tr.length > 6 || new Set(tr.map(t => t[1])).size !== tr.length) return null;
  const hits = [...ls].sort((a, b) => Number(Boolean(a[6])) - Number(Boolean(b[6]))), possible = new Set<string>();
  const assign = (i: number, rest: Row[], primary: string) => {
    if (i === hits.length) { possible.add(primary); return; }
    for (const h of rest) if (Math.abs(h[2] - h[3] - hits[i][7]) <= 1) {
      assign(i + 1, rest.filter(q => q !== h), i === 0 ? h[1] : primary);
    }
  };
  assign(0, tr, ''); return possible.size === 1 ? [...possible][0] : null;
}

function compare(f: any, d: any, p: Pack, start: number, end: number, firstPrimary: Creature | undefined, spell: boolean) {
  const skip = (why: string) => ({ why, predictions: null });
  if (spell || !firstPrimary) return skip('non-auto-damage-or-start-was-only-splash');
  const st = before(d.bodyStats, start);
  if (!st || !st.slice(1).every(Number.isFinite) ||
      d.bodyStats.some((r: Row) => r[0] >= start && r[0] <= end && !same(st.slice(1), r.slice(1)))) return skip('attack-panel-changed');
  const enh = before(d.enhancements, start);
  if (!enh || !enhancement.has(enh[1]) || d.enhancements.some((r: Row) => r[0] >= start && r[0] <= end && !same(enh.slice(1), r.slice(1)))) {
    return skip('unresolved-or-changing-neutral-enhancement');
  }
  const frame = f.frames.findLast((v: any) => v.tick < start), h = frame?.heroes.find((h: any) => h.id === d.bodyId);
  if (!h || start - frame.tick > 36) return skip('no-fresh-pre-hit-body-snapshot');
  const loadout = (h: any) => h.items.filter((i: any) => i.slot < 6 || i.slot === 16).map((i: any) => `${i.slot}:${i.name}`);
  const skills = (h: any) => h.abilities.filter((a: any) => a.level && a.slot < 20).map((a: any) => [a.name, a.level, a.toggleState]);
  for (const v of f.frames) if (v.tick >= start && v.tick <= end) {
    const h2 = v.heroes.find((x: any) => x.id === d.bodyId);
    if (!h2 || !same(loadout(h), loadout(h2)) || !same(skills(h), skills(h2))) return skip('inventory-skill-or-toggle-changed');
  }
  const main: string[] = h.items.filter((i: any) => i.slot < 6).map((i: any) => i.name);
  const neutral = h.items.find((i: any) => i.slot === 16)?.name ?? null;
  if (!main.every(i => allowedPlain.has(i)) || (neutral && !neutralItems.has(neutral))) return skip('unmodeled-equipped-item');
  // A cast log and 6s duration are not proof a dispellable buff persisted. Exclude instead of assuming it.
  const lua = d.hero.endsWith('_luna'), log = f[lua ? 'lunaCombatLog' : 'trollCombatLog'];
  const clock = (t: number) => gameTimeAt(f.clock, t);
  if (log.some((e: any) => (e.inflictor_name === 'item_mask_of_madness' || e.inflictor_name === 'troll_warlord_battle_trance') &&
      ['ITEM', 'ABILITY'].includes(e.log_type) && e.attacker_name === d.hero && e.tick <= end && clock(start) - clock(e.tick) <=
      (e.inflictor_name === 'item_mask_of_madness' ? 6 : 15))) return skip('active-mask-or-trance-window');
  const lvl = (n: string) => h.abilities.find((a: any) => a.name === n)?.level ?? 0;
  const stats = (k: string) => Number(source.mechanics[k]?.bonus_attack_speed ?? 0);
  const enchantIas = enh[1] === 'item_enhancement_alert' ?
    Number(source.mechanics.item_enhancement_alert.bonus_attack_speed.split(' ')[enh[2] - 1]) : 0;
  if (!Number.isFinite(enchantIas)) return skip('unknown-enhancement-level');
  const base = { damage: [st[1] + st[3], st[2] + st[3]] as Range742,
    attackSpeed: source.heroes[lua ? 'luna' : 'troll_warlord'].baseAttackSpeed + st[4] + main.reduce((n, k) => n + stats(k), 0) + enchantIas,
    weapon: (main.includes('item_bfury') ? 'bfury' : main.includes('item_quelling_blade') ? 'quelling_blade' : 'none') as Attacker742['weapon'] };
  if (main.includes('item_bfury') && main.includes('item_quelling_blade')) return skip('overlapping-quell-sources');
  const mode = st[6] === 1 ? 'melee' : st[6] === 2 ? 'ranged' : null;
  if (!mode || (lua ? mode !== 'ranged' : (mode === 'melee') !== h.abilities.find((a: any) => a.name === 'troll_warlord_switch_stance')?.toggleState)) {
    return skip('ambiguous-stance');
  }
  const attacker: Attacker742 = lua ? { ...base, hero: 'luna', glaives: lvl('luna_moon_glaive'), glaiveTalent: !!lvl('special_bonus_unique_luna_7') } :
    { ...base, hero: 'troll_warlord', mode, fervor: lvl('troll_warlord_fervor'), rage: lvl('troll_warlord_berserkers_rage'),
      fervorTalent: !!lvl('special_bonus_unique_troll_warlord_5') };
  // Use only information available at first impact for the policy: first primary, then lower pre-fight MAX HP.
  // Do not tune order, IAS or a time multiplier to the future death sequence.
  const members = p.units.map(u => ({ u, snap: frame.units.find((m: any) => m.id === u.id) }));
  if (members.some(m => !m.snap || !m.snap.position)) return skip('no-pre-hit-member-geometry');
  const upgrade = source.mechanics.neutral_upgrade, tier = Math.floor(clock(start) / Number(upgrade.increase_time));
  if (members.some(m => m.snap.maxHealth !== source.units[m.u.unitName].hp + tier * Number(upgrade.increase_health))) {
    return skip('pre-hit-maximum-hp-differs-from-model');
  }
  members.sort((a, b) => Number(b.u.id === firstPrimary.id) - Number(a.u.id === firstPrimary.id) ||
    a.snap.maxHealth - b.snap.maxHealth || a.u.id.localeCompare(b.u.id, 'en'));
  const link = lua ? glaiveLinks742(members.map(m => m.snap.position)) : undefined;
  const input: CampInput742 = { patch: '7.42', scope: 'full-unstacked', clock: clock(start),
    units: members.map(m => m.u.unitName.slice(17)), policy: 'uninterrupted-attacks', attacker };
  const formations: CampInput742['formation'][] = lua ? [{ glaive: link, hopSeconds: 0 }] :
    base.weapon === 'bfury' && mode === 'melee' ? [{ cleave: 'all' }, { cleave: 'none' }] : [{}];
  const predictions = [];
  for (const formation of formations) {
    const spec = { ...input, formation }, result = estimateCamp742(spec);
    if (!result.timing || !result.bounty?.wholeCamp) return skip(result.reasons.join(';'));
    predictions.push({ formation, firstHitToLastDeath: result.timing.firstHitToLastDeathSeconds });
  }
  return { why: null, inputs: input, predictions, evidence: { frameTick: frame.tick, panelTick: st[0], damageMinMaxBonus: st.slice(1, 4),
    agilityTotal: st[4], capabilities: st[6], main, neutral, enhancement: enh.slice(1), level: h.level, orderedIds: members.map(m => m.u.id) } };
}

const exclusions: Record<string, number> = {}, selected: any[] = [], matches = [];
for (const matchId of (routes.matches as any[]).map(m => m.id as number).sort()) {
  const f = facts(routes.matches.find((m: any) => m.id === matchId)), d = details(f);
  const ps = packs(f), all = new Map<string, Creature>(f.creatures.map((u: Creature) => [u.id, u]));
  const drops = d.transitions as Row[], transitions = group(drops, r => key(r[0], all.get(r[1])!.unitName));
  const damage = group((d.combat as Row[]).filter(r => r[1] === 'DAMAGE' && r[7] > 0), r => key(r[0], r[2]));
  const deaths = group((d.combat as Row[]).filter(r => r[1] === 'DEATH'), r => key(r[0], r[2]));
  const deathTick = group((d.combat as Row[]).filter(r => r[1] === 'DEATH'), r => r[0]);
  const byId = group(drops, r => r[1]), payments = group(d.gold as Row[], r => r[0]);
  const body = (l: Row) => l[3] === d.hero && l[4] === d.team && l[5] === false && l[9] === 4;
  const clock = (t: number) => gameTimeAt(f.clock, t);
  function audit(p: Pack): string | null {
    if (!p.units.every(u => u.diedTick != null && u.diedGameTime < 1200)) return 'not-all-members-dead-before-20';
    const end = Math.max(...p.units.map(u => u.diedTick!)), ids = new Set(p.units.map(u => u.id));
    if (p.units.some(u => /_(mud_golem|dark_troll_warlord)$/.test(u.unitName))) return 'conditional-split-or-summon';
    const changes = p.units.flatMap(u => (byId.get(u.id) ?? []).filter(r => r[0] >= p.spawnTick && r[0] <= u.diedTick!)).sort((a, b) => a[0] - b[0]);
    if (!changes.length) return 'no-independent-health-history';
    const start = changes[0][0];
    if (ps.some(other => other !== p && other.handle === p.handle && other.spawnTick <= end &&
        other.units.some(u => u.diedTick == null || u.diedTick >= start))) return 'overlapping-live-spawn-batches';
    const labels = new Map<string, Row[]>(), firsts = new Map<string, Row>(), membersAt = (k: string) => transitions.get(k) ?? [];
    for (const r of changes) {
      const k = key(r[0], all.get(r[1])!.unitName);
      if (!firsts.has(r[1])) {
        if (r[2] !== r[4] || r[2] <= 0) return 'member-not-full-before-first-damage';
        firsts.set(r[1], r);
      }
      if (labels.has(k)) continue;
      const ls = damage.get(k) ?? [], tr = membersAt(k);
      if (!ls.length || !ls.every(body)) return 'missing-damage-or-other-actor';
      if (tr.some(x => !ids.has(x[1]))) return 'same-tick-other-entity-outside-roster';
      const deltaHp = tr.reduce((sum, x) => sum + x[2] - x[3], 0), deal = ls.reduce((sum, l) => sum + l[7], 0);
      if (new Set(tr.map(x => x[1])).size > ls.length || Math.abs(deltaHp - deal) > tr.length + ls.length) return 'hp-log-mismatch';
      labels.set(k, ls);
    }
    if (firsts.size !== ids.size) return 'missing-member-hit';
    const deathTicks = group(p.units, u => u.diedTick!); let paid: number | null = 0;
    for (const [tick, us] of deathTicks) {
      for (const [name, members] of group(us, u => u.unitName)) {
        const k = key(tick, name), ds = deaths.get(k) ?? [], dead = membersAt(k).filter(x => x[5] > 0);
        if (ds.length !== members.length || !ds.every(body) || dead.some(x => !ids.has(x[1])) ||
            !members.every(u => dead.some(x => x[1] === u.id))) return 'missing-or-ambiguous-death';
      }
      const gs = payments.get(tick) ?? [];
      if ((deathTick.get(tick)?.length ?? 0) !== us.length || gs.length !== us.length) paid = null;
      else if (paid !== null) paid += gs.reduce((n, g) => n + g[1], 0);
    }
    const logs = [...labels.values()].flat(), firstKeys = [...labels].filter(([, ls]) => ls[0][0] === start);
    const ticks = [...new Set(logs.map(l => Number(l[0])))].sort((a, b) => a - b);
    const opening = firstKeys.filter(([, ls]) => ls.some(l => !l[6]));
    const primaryId = opening.length === 1 ? openingPrimary(opening[0][1], membersAt(opening[0][0])) : null;
    const primary = primaryId ? all.get(primaryId) : undefined;
    const olderAtSpawn = ps.filter(other => other !== p && other.handle === p.handle && other.spawnTick < p.spawnTick)
      .flatMap(other => other.units.filter(u => u.diedTick == null || u.diedTick >= p.spawnTick).map(u => u.id));
    const model = olderAtSpawn.length ? { why: 'prior-wave-still-alive-at-spawn', predictions: null } :
      compare(f, d, p, start, end, primary, logs.some(l => !physicalInflictors.has(l[6]) || l[8] !== 'physical'));
    const names = p.units.map(u => u.unitName.slice(17)), quote = estimateCamp742({ patch: '7.42', clock: clock(start), scope: 'full-unstacked', units: names });
    const endQuote = estimateCamp742({ patch: '7.42', clock: clock(end), scope: 'full-unstacked', units: names });
    selected.push({ matchId, hero: d.hero.slice(14), camp: p.camp, spawnerHandle: p.handle, spawnTick: p.spawnTick, olderAtSpawn,
      firstHitTick: start, firstKillTick: Math.min(...deathTicks.keys()), lastDeathTick: end, firstHitClock: rounded(clock(start)),
      observedFirstHitToLastDeath: rounded(clock(end) - clock(start)), firstKillToLastDeathOnly: rounded(clock(end) - clock(Math.min(...deathTicks.keys()))),
      longestGapBetweenBodyDamageSeconds: rounded(Math.max(0, ...ticks.slice(1).map((t, i) => clock(t) - clock(ticks[i])))),
      members: p.units.map(u => ({ id: u.id, name: u.unitName.slice(17), firstHitTick: firsts.get(u.id)![0], deathTick: u.diedTick,
        preHitHp: firsts.get(u.id)![2], lastGoldFields: membersAt(key(u.diedTick!, u.unitName)).find(z => z[1] === u.id && z[5] > 0)!.slice(6, 8) })),
      firstHitEvidence: firstKeys.map(([k, ls]) => ({ entityIds: membersAt(k).map(x => x[1]), hpLoss: membersAt(k).map(x => x[2] - x[3]),
        damage: ls.map(l => [l[6], l[7]]) })), openingPrimaryId: primaryId,
      bodyDamageEntries: logs.length, inflictors: [...new Set(logs.map(l => l[6]))].sort(),
      observedSameTickReason14: paid, staticGrossInStartingTier: quote.bounty?.wholeCamp ?? null,
      allDeathsInQuotedTier: Boolean(quote.bounty?.wholeCamp && endQuote.bounty?.wholeCamp && quote.tier === endQuote.tier), model });
    return null;
  }
  for (const p of ps) {
    const why = audit(p);
    if (why) exclusions[why] = (exclusions[why] ?? 0) + 1;
  }
  matches.push({ matchId, demSha256: f.demSha256, gameBuild: f.gameBuild, hero: d.hero, bodyId: d.bodyId,
    spawnsExamined: ps.length, completeToTick: f.postGameTick, damageThroughTick: d.lastTick });
}
const errors: Record<string, number> = {};
const compared = selected.filter(s => s.model.predictions !== null);
for (const s of selected) if (s.model.why) errors[s.model.why] = (errors[s.model.why] ?? 0) + 1;
function metrics(rows: typeof compared) {
  const trials = rows.map(s => {
    const p = s.model.predictions.flatMap((x: any) => x.firstHitToLastDeath as number[]), lo = Math.min(...p), hi = Math.max(...p);
    return { actual: s.observedFirstHitToLastDeath, lo, hi };
  });
  const med = (a: number[]) => { const b = [...a].sort((x, y) => x - y); return b.length ? rounded((b[(b.length - 1) >> 1] + b[b.length >> 1]) / 2) : null; };
  return { n: trials.length, insideScenarioSpan: trials.filter(x => x.lo <= x.actual && x.actual <= x.hi).length,
    medianSignedMidpointError: med(trials.map(x => (x.lo + x.hi) / 2 - x.actual)),
    medianAbsoluteMidpointError: med(trials.map(x => Math.abs((x.lo + x.hi) / 2 - x.actual))) };
}
const stable = selected.filter(s => s.allDeathsInQuotedTier), credited = stable.filter(s => s.observedSameTickReason14 != null);
const inGross = (s: any) => s.staticGrossInStartingTier[0] <= s.observedSameTickReason14 && s.observedSameTickReason14 <= s.staticGrossInStartingTier[1];
const noPrior = credited.filter(s => s.olderAtSpawn.length === 0);
const summary = { strictEpisodes: selected.length, comparisonByHero: { luna: metrics(compared.filter(s => s.hero === 'luna')),
  troll_warlord: metrics(compared.filter(s => s.hero === 'troll_warlord')) }, installedBuildOnly: metrics(compared.filter(s => sameBuild.includes(s.matchId))),
  all: metrics(compared), observedGold: { sameTierEpisodes: stable.length,
    sameTierMatchesBothEntityLimits: stable.filter(s => [0, 1].every(i => s.members.reduce((n: number, u: any) => n + u.lastGoldFields[i], 0) === s.staticGrossInStartingTier[i])).length,
    unambiguousPaymentBatches: selected.filter(s => s.observedSameTickReason14 != null).length, unambiguousSameTierPayments: credited.length,
    insideStartingTierGross: credited.filter(inGross).length, withoutOlderMembersAtSpawn: noPrior.length,
    insideGrossWithoutOlderAtSpawn: noPrior.filter(inGross).length },
  firstExclusion: exclusions, noComparison: errors };
save('comparison', { patch: '7.42', selection: 'All sixteen original DEMs; enumerate every natural batch spawned at 1..20 min. No outcome fitting.',
  endpoint: 'First recorded body DAMAGE cross-checked with spawned-entity HP transition → last independently identified member DEATH. NOT approach/aggro/order time.',
  matches, summary, episodes: selected });
console.log(JSON.stringify(summary, null, 2));
