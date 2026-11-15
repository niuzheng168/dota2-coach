export type Team = 'radiant' | 'dire';
export type CampKind = 'small' | 'medium' | 'large' | 'ancient';
export type DamageType = 'physical' | 'magical' | 'pure';
export interface Point { x: number; y: number }
export interface AbilityData {
  name: string;
  damageType: DamageType;
  cooldown: number[];
  mana: number[];
  castPoint: number[];
  damage: number[];
  values: Record<string, number[]>;
}
export interface HeroData {
  name: string;
  primary: 'str' | 'agi' | 'int' | 'all';
  attributes: number[];
  gains: number[];
  damage: number;
  armor: number;
  magicResistance: number;
  attackTime: number;
  attackSpeed: number;
  moveSpeed: number;
  healthRegen: number;
  manaRegen: number;
  melee: boolean;
  abilities: Record<string, AbilityData>;
}
export interface NeutralData {
  name: string;
  health: number;
  healthRegen: number;
  armor: number;
  magicResistance: number;
  damage: number;
  attackTime: number;
  attackSpeed: number;
  gold: number;
  xp: number;
  abilities: string[];
}
export interface CombatData {
  patch: string;
  generatedAt: string;
  sources: Record<string, string>;
  heroes: Record<string, HeroData>;
  items: Record<string, Record<string, number[]>>;
  neutrals: Record<string, NeutralData>;
  neutralUpgrade: Record<string, number>;
}
export interface Camp extends Point {
  id: string;
  label: string;
  team: Team;
  kind: CampKind;
  bounds: { min: Point; max: Point };
}
export interface MapData {
  patch: string;
  generatedAt: string;
  sources: Record<string, string>;
  camps: Camp[];
  spawn: { first: number; interval: number };
  grid: { width: number; height: number; cellSize: number; origin: Point; rows: string[] };
}
export interface Composition { id: string; kind: CampKind; units: Record<string, number> }
export interface AbilityState { name: string; level: number; cooldown: number }
export interface PlayerSnapshot {
  hero: string;
  team: Team;
  position: Point;
  clock: number;
  level: number;
  health: number;
  maxHealth: number;
  mana: number;
  maxMana: number;
  alive: boolean;
  canAct: boolean;
  abilities: AbilityState[];
  items: { name: string; passive: boolean }[];
  lastHits: number | null;
}
export interface SelfStats {
  damage: number;
  armor: number;
  magicResistance: number;
  attacksPerSecond: number;
  moveSpeed: number;
  healthRegen: number;
  manaRegen: number;
  cleave: number;
  creepDamage: number;
  warnings: string[];
}
export interface Resources {
  health: number;
  mana: number;
  // Absolute game clock when an ability can next be used.
  readyAt: Record<string, number>;
}
export interface ClearEstimate {
  seconds: number;
  healthAfter: number;
  manaAfter: number;
  damageTaken: number;
  gold: number;
  xp: number;
  readyAt: Record<string, number>;
  casts: Record<string, number>;
  feasible: boolean;
}
export interface RouteStep extends ClearEstimate {
  campId: string;
  label: string;
  kind: CampKind;
  position: Point;
  arrival: number;
  finish: number;
  travelSeconds: number;
  waitSeconds: number;
  path: Point[];
  availability: 'assumed' | 'respawn-assumed';
  clearRange: [number, number];
}
export interface JungleRoute {
  plannedAt: number;
  validUntil: number;
  steps: RouteStep[];
  gold: number;
  xp: number;
  seconds: number;
  score: number;
  nextAction: string;
  warnings: string[];
  status: 'ready' | 'recover' | 'unavailable';
}
export interface JungleState {
  enabled: boolean;
  patch: string;
  status: string;
  player: PlayerSnapshot | null;
  stats: SelfStats | null;
  route: JungleRoute | null;
  camps: (Camp & { clearedAt: number | null })[];
  calibration: number;
  updatedAt: number | null;
}
