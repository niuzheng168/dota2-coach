import type { Point } from '../tactics/types.ts';

const dist = (a: Point, b: Point) => Math.hypot(a.x - b.x, a.y - b.y);
export function validateCampGeometry(facts: any, reference: any) {
  if (facts.camps.length !== reference.camps.length || facts.spawners.length !== reference.camps.length)
    throw new Error('Missing/extra replay camp or spawner');
  // The parser's observed box index field is doubled in build 6975. Resolve a
  // whole-map bijection with named centers, never a per-unit nearest-camp guess.
  const modes = [1, 2].filter(divisor => facts.camps.every((c: any) => {
    const s = facts.spawners.find((s: any) => s.index === c.spawnerIndex / divisor);
    const r = reference.camps.find((r: any) => r.id === c.id);
    return s?.position && r && dist(s.position, r) < 1;
  }));
  if (modes.length !== 1) throw new Error('Unresolved/ambiguous spawner index encoding');
  const divisor = modes[0], indices = new Set();
  const rows = facts.camps.map((c: any) => {
    if (!Array.isArray(c.min) || !Array.isArray(c.max)) throw new Error('Missing recorded camp box');
    const s = facts.spawners.find((s: any) => s.index === c.spawnerIndex / divisor);
    const r = reference.camps.find((r: any) => r.id === c.id);
    if (indices.has(s.index)) throw new Error('Duplicate spawner mapping');
    indices.add(s.index);
    const boxError = Math.max(Math.abs(c.min[0] - r.bounds.min.x), Math.abs(c.min[1] - r.bounds.min.y),
      Math.abs(c.max[0] - r.bounds.max.x), Math.abs(c.max[1] - r.bounds.max.y));
    return { id: c.id, spawnerIndex: s.index, position: s.position, centerError: dist(s.position, r),
      boxError, initialType: s.types[0]?.type, expectedType: ['small', 'medium', 'large', 'ancient'].indexOf(r.kind),
      typeHistory: s.types };
  });
  return { status: rows.every((r: any) => r.centerError < 1 && r.boxError < 1 && r.initialType === r.expectedType) ? 'matched' : 'mismatch',
    indexDivisor: divisor, indexNote: 'Observed build-specific raw field conversion, validated as a 28-way bijection. Not assumed valid for other builds.',
    maxCenterError: Math.max(...rows.map((r: any) => r.centerError)),
    maxBoxError: Math.max(...rows.map((r: any) => r.boxError)), rows };
}
export function validateTowers(facts: any, reference: any) {
  const rows = facts.towers.map((t: any) => {
    const matches = reference.towers.filter((r: any) => dist(t.position, { x: r.position[0], y: r.position[1] }) < 1);
    if (matches.length !== 1) throw new Error('Tower position missing/ambiguous');
    const r = matches[0];
    return { name: t.name, referenceName: r.name, referenceId: r.id, team: t.team, expectedTeam: r.team === 'radiant' ? 2 : 3,
      error: dist(t.position, { x: r.position[0], y: r.position[1] }),
      attackRangeField: t.attackRangeField, maxHealth: t.maxHealth };
  });
  return { status: rows.length === reference.towers.length && new Set(rows.map((r: any) => r.referenceId)).size === rows.length &&
    rows.every((r: any) => r.team === r.expectedTeam) ? 'matched' : 'mismatch',
    count: rows.length, maxPositionError: Math.max(...rows.map((r: any) => r.error)), rows };
}
export function frameAfter(facts: any, seconds: number) {
  return facts.frames.find((f: any) => f.gameTime >= seconds && f.tick <= facts.postGameTick);
}
export function lunaWindow(facts: any, from: number, to: number) {
  const first = frameAfter(facts, from), last = frameAfter(facts, to);
  const hero = (f: any) => f?.heroes.find((h: any) => h.name === 'npc_dota_hero_luna');
  const a = hero(first), b = hero(last);
  if (!a?.economy || !b?.economy || to <= from) throw new Error('Missing Luna clock/economy evidence');
  const diff = (k: string) => {
    const x = a.economy[k], y = b.economy[k];
    if (!Number.isFinite(x) || !Number.isFinite(y) || y < x) throw new Error('Missing/nonmonotonic economy counter');
    return y - x;
  };
  return { start: first.gameTime, end: last.gameTime, elapsed: last.gameTime - first.gameTime,
    neutralGold: diff('m_iNeutralKillGold'), laneGold: diff('m_iCreepKillGold'),
    lastHits: diff('m_iLastHitCount'), heroKillGold: diff('m_iHeroKillGold'), buildingGold: diff('m_iBuildingGold'),
    passiveGold: diff('m_iIncomeGold'), totalEarnedGold: diff('m_iTotalEarnedGold'),
    initialHero: a, finalHero: b,
    note: 'Recorded team counters include owned units/illusions; not necessarily main-body last hits. Not an equipment-fixed experiment or a recommended route.' };
}
