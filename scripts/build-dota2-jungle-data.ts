import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { parseKeyValues, kvObject, numbers, numericValues } from './lib/valve-kv.ts';
import type { KV } from './lib/valve-kv.ts';
import type { AbilityData, CombatData, HeroData, MapData, NeutralData } from '../src/tactics/types.ts';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const pins = JSON.parse(await fs.readFile(path.join(root, 'data/dota2-jungle-sources.json'), 'utf8'));
const cacheArg = process.argv.find(arg => arg.startsWith('--cache='));
const cache = path.resolve(cacheArg?.slice(8) || path.join(root, '.tmp/jungle-data', pins.gameCommit));
const gameBase = `https://raw.githubusercontent.com/SteamDatabase/GameTracking-Dota2/${pins.gameCommit}/game/dota/pak01_dir/scripts/npc/`;
const mapBase = `https://raw.githubusercontent.com/Reino-Doce/dota-maps-data/${pins.mapCommit}/assets/data/${pins.mapPatch}/`;
const hashes: Record<string, string> = {};
const prior = await fs.readFile(path.join(root, 'data/dota2-jungle-data-hashes.json'), 'utf8').then(JSON.parse, () => null);
const expectedHashes = prior?.pins?.gameCommit === pins.gameCommit && prior?.pins?.mapCommit === pins.mapCommit
  ? prior.sha256 as Record<string, string> : {};
async function read(name: string, url: string): Promise<Buffer> {
  const dest = path.join(cache, name);
  let data: Buffer;
  try { data = await fs.readFile(dest); } catch {
    let downloaded: Buffer | undefined;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const response = await fetch(url, { signal: AbortSignal.timeout(25_000) });
        if (!response.ok) throw new Error(`HTTP ${response.status}: ${url}`);
        downloaded = Buffer.from(await response.arrayBuffer());
        break;
      } catch (error) { if (attempt === 2) throw error; }
    }
    data = downloaded!;
    await fs.mkdir(path.dirname(dest), { recursive: true });
    await fs.writeFile(dest, data);
  }
  hashes[name] = crypto.createHash('sha256').update(data).digest('hex');
  if (expectedHashes[name] && hashes[name] !== expectedHashes[name]) {
    throw new Error(`Source hash mismatch for pinned input ${name}; remove stale cache, do not mix patches`);
  }
  return data;
}
async function kv(name: string): Promise<KV> {
  return parseKeyValues((await read(name, gameBase + name)).toString('utf8'));
}
const n = (value: string | KV | undefined, fallback = 0) => numbers(value)[0] ?? fallback;
const dictionary = JSON.parse(await fs.readFile(path.join(root, 'data/dota2-dictionary.json'), 'utf8'));
const generatedAt = new Date().toISOString();
const sources = { game: gameBase, map: mapBase, patchList: 'https://www.dota2.com/datafeed/patchnoteslist?language=english' };
const heroBase = kvObject(kvObject((await kv('heroes/npc_dota_hero_base.txt')).DOTAHeroes).npc_dota_hero_base);
const units = kvObject((await kv('npc_units.txt')).DOTAUnits);
const unitBase = kvObject(units.npc_dota_units_base);
const abilityBase = kvObject((await kv('npc_abilities.txt')).DOTAAbilities);
const itemSource = kvObject((await kv('items.txt')).DOTAAbilities);
const combat: CombatData = {
  patch: pins.patch, generatedAt, sources, heroes: {}, items: {}, neutrals: {},
  neutralUpgrade: Object.fromEntries(Object.entries(numericValues(kvObject(abilityBase.neutral_upgrade).AbilityValues))
    .map(([key, value]) => [key, value[0]])),
};
let cursor = 0;
await Promise.all(Array.from({ length: 6 }, async () => {
  while (cursor < dictionary.heroes.length) {
    const hero = dictionary.heroes[cursor++];
    const raw = kvObject(kvObject((await kv(`heroes/${hero.key}.txt`)).DOTAHeroes)[hero.key]);
    if (n(raw.HeroID) !== hero.id) throw new Error(`Missing/mismatched hero: ${hero.key}`);
    const h = { ...heroBase, ...raw };
    const abilities: Record<string, AbilityData> = {};
    for (const [name, value] of Object.entries(kvObject(h.AbilityDefinitions))) {
      const a = kvObject(value);
      if (name.startsWith('special_bonus')) continue;
      abilities[name] = {
        name, damageType: a.AbilityUnitDamageType === 'DAMAGE_TYPE_PURE' ? 'pure'
          : a.AbilityUnitDamageType === 'DAMAGE_TYPE_PHYSICAL' ? 'physical' : 'magical',
        damage: numbers(a.AbilityDamage), cooldown: numbers(a.AbilityCooldown),
        mana: numbers(a.AbilityManaCost), castPoint: numbers(a.AbilityCastPoint),
        values: numericValues(a.AbilityValues),
      };
    }
    const primary: HeroData['primary'] = h.AttributePrimary === 'DOTA_ATTRIBUTE_STRENGTH' ? 'str'
      : h.AttributePrimary === 'DOTA_ATTRIBUTE_AGILITY' ? 'agi'
        : h.AttributePrimary === 'DOTA_ATTRIBUTE_INTELLECT' ? 'int' : 'all';
    combat.heroes[hero.key] = {
      name: hero.zh, primary,
      attributes: ['Strength', 'Agility', 'Intelligence'].map(attr => n(h[`AttributeBase${attr}`])),
      gains: ['Strength', 'Agility', 'Intelligence'].map(attr => n(h[`Attribute${attr}Gain`])),
      damage: (n(h.AttackDamageMin) + n(h.AttackDamageMax)) / 2,
      armor: n(h.ArmorPhysical), magicResistance: n(h.MagicalResistance, 25) / 100,
      attackTime: n(h.AttackRate, 1.7), attackSpeed: n(h.BaseAttackSpeed, 100),
      moveSpeed: n(h.MovementSpeed, 300), healthRegen: n(h.StatusHealthRegen),
      manaRegen: n(h.StatusManaRegen), melee: h.AttackCapabilities === 'DOTA_UNIT_CAP_MELEE_ATTACK',
      abilities,
    };
  }
}));
for (const [key, raw] of Object.entries(units)) {
  if (!key.startsWith('npc_dota_neutral_') || key.endsWith('_default')) continue;
  const u = { ...unitBase, ...kvObject(raw) };
  const neutral: NeutralData = {
    name: key, health: n(u.StatusHealth), healthRegen: n(u.StatusHealthRegen),
    armor: n(u.ArmorPhysical), magicResistance: n(u.MagicalResistance) / 100,
    damage: (n(u.AttackDamageMin) + n(u.AttackDamageMax)) / 2,
    attackTime: n(u.AttackRate, 1.7), attackSpeed: n(u.BaseAttackSpeed, 100),
    gold: (n(u.BountyGoldMin) + n(u.BountyGoldMax)) / 2, xp: n(u.BountyXP),
    abilities: Object.entries(u).filter(([key, val]) => /^Ability\d+$/.test(key) && typeof val === 'string' && val)
      .map(([, val]) => val as string),
  };
  if (neutral.health > 0) combat.neutrals[key] = neutral;
}
for (const [key, raw] of Object.entries(itemSource)) {
  if (key.startsWith('item_') && !key.startsWith('item_recipe_')) {
    combat.items[key] = numericValues(kvObject(raw).AbilityValues);
  }
}
combat.heroes = Object.fromEntries(Object.entries(combat.heroes).sort(([a], [b]) => a.localeCompare(b)));

const rawMap = JSON.parse((await read('mapdata.json', mapBase + 'mapdata.json')).toString()).data;
const nav = await read('gridnav.gnv', mapBase + 'gridnav.gnv');
const navHash = crypto.createHash('sha256').update(nav).digest('hex');
if (rawMap.trigger_multiple.some((box: any) =>
  box.source?.patch !== pins.mapPatch || box.source?.gridnavSha256 !== navHash)) {
  throw new Error('Camp geometry provenance does not match the pinned patch/navigation grid');
}
if (nav.readUInt32LE(0) !== 0xfadebead) throw new Error('Invalid GNV magic');
const cellSize = nav.readFloatLE(4), width = nav.readInt32LE(16), height = nav.readInt32LE(20);
if (cellSize !== 64 || width <= 0 || height <= 0 || nav.length !== 32 + width * height) throw new Error('Invalid GNV grid');
const origin = { x: nav.readInt32LE(24) * cellSize, y: nav.readInt32LE(28) * cellSize };
// GNV flags: Traversable=1, Blocked=2, HeroBlocking=4. Never infer walkability from image colors.
const walk = [...nav.subarray(32)].map(flags => Boolean(flags & 1) && !(flags & 6));
// Static trees, plus a small hero hull. Cut/regrown trees cannot be observed through player GSI.
for (const tree of rawMap.ent_dota_tree) {
  const cx = Math.floor((tree.x - origin.x) / cellSize), cy = Math.floor((tree.y - origin.y) / cellSize);
  for (let y = cy - 2; y <= cy + 2; y++) for (let x = cx - 2; x <= cx + 2; x++) {
    if (x >= 0 && y >= 0 && x < width && y < height &&
        Math.hypot(origin.x + (x + 0.5) * cellSize - tree.x, origin.y + (y + 0.5) * cellSize - tree.y) < 80) {
      walk[y * width + x] = false;
    }
  }
}
const kinds = ['small', 'medium', 'large', 'ancient'] as const;
const kindNames = ['小野', '中野', '大野', '远古'];
const map: MapData = {
  patch: pins.patch, generatedAt, sources,
  spawn: { first: 60, interval: 60 },
  camps: rawMap.npc_dota_neutral_spawner.map((c: any) => {
    const box = rawMap.trigger_multiple.find((b: any) => b.campId === c.triggerName);
    if (!box?.worldBounds || !kinds[Number(c.neutralType)]) throw new Error(`Missing camp bounds/type: ${c.triggerName}`);
    const team = c.triggerName.includes('_good_') ? 'radiant' : 'dire';
    const region = Math.abs(c.y) > 6500 ? (c.y > 0 ? '上边缘' : '下边缘')
      : Math.abs(c.x) > 6500 ? (c.x > 0 ? '右边缘' : '左边缘')
        : Math.abs(c.x) > Math.abs(c.y) ? (c.x > 0 ? '右侧' : '左侧') : (c.y > 0 ? '上侧' : '下侧');
    return {
      id: c.triggerName, label: `${team === 'radiant' ? '天辉' : '夜魇'} ${region}${kindNames[Number(c.neutralType)]} ${c.triggerName.split('_').at(-1)}`,
      team, kind: kinds[Number(c.neutralType)], x: c.x, y: c.y,
      bounds: { min: { x: box.worldBounds.min[0], y: box.worldBounds.min[1] },
        max: { x: box.worldBounds.max[0], y: box.worldBounds.max[1] } },
    };
  }).sort((a: any, b: any) => a.id.localeCompare(b.id)),
  grid: { width, height, cellSize, origin, rows: Array.from({ length: height }, (_, y) =>
    walk.slice(y * width, (y + 1) * width).map(v => v ? '1' : '0').join('')) },
};
const compositions = JSON.parse(await fs.readFile(path.join(root, 'data/dota2-camp-compositions.json'), 'utf8'));
for (const c of compositions.camps) for (const key of Object.keys(c.units)) {
  if (!combat.neutrals[`npc_dota_neutral_${key}`]) throw new Error(`Unknown neutral in composition: ${key}`);
}
// Write only after every source and composition passed validation. No runtime network dependency.
for (const [name, data] of Object.entries({ 'dota2-combat.json': combat, 'dota2-map.json': map,
  'dota2-jungle-data-hashes.json': { generatedAt, pins, sha256: Object.fromEntries(Object.entries(hashes).sort()) } })) {
  await fs.writeFile(path.join(root, 'data', name), JSON.stringify(data, null, 2) + '\n');
}
console.log(`Built ${Object.keys(combat.heroes).length} heroes, ${Object.keys(combat.items).length} items, ` +
  `${Object.keys(combat.neutrals).length} neutral units, ${map.camps.length} camps (${pins.patch}).`);
