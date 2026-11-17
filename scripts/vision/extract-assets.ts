// Read-only extraction of HUD recognition references from the installed client:
// item/ability icons, hero ability lists and Chinese display names.
// Valve artwork stays in the ignored .tmp folder; it is never committed.
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { parseKeyValues, kvObject, type KV } from '../lib/valve-kv.ts';

// --manifest-only rebuilds manifest.json from a previous extraction without touching the client.
const manifestOnly = process.argv.includes('--manifest-only');
const args = process.argv.slice(2).filter(a => a !== '--manifest-only');
const install = path.resolve(args[0] || 'C:/Steam/steamapps/common/dota 2 beta');
const out = path.resolve(args[1] || '.tmp/vision-assets');
const cli = path.resolve(args[2] || '.tmp/client-audit/vrf-21.1/Source2Viewer-CLI.exe');
const hash = (data: Buffer) => createHash('sha256').update(data).digest('hex');
const pin = JSON.parse(fs.readFileSync('data/dota2-client-tool-pin.json', 'utf8'));
if (hash(fs.readFileSync(cli)) !== pin.executableSha256) throw new Error('Source2Viewer executable pin mismatch');
if (fs.existsSync(out) !== manifestOnly) throw new Error(manifestOnly ? `No previous extraction in ${out}` : `Output exists; delete it to rebuild: ${out}`);
// A manifest rebuild describes the files already extracted, not whatever the client updated to since.
const serverBuild = manifestOnly ? JSON.parse(fs.readFileSync(path.join(out, 'manifest.json'), 'utf8')).serverBuild
  : Number(fs.readFileSync(path.join(install, 'game/dota/steam.inf'), 'utf8').match(/^ServerVersion=(\d+)/m)?.[1]);
if (!Number.isInteger(serverBuild)) throw new Error('Unreadable client build');

const raw = path.join(out, 'raw'), decoded = path.join(out, 'decoded');
const vpk = path.join(install, 'game/dota/pak01_dir.vpk');
const run = (a: string[]) => execFileSync(cli, a, { encoding: 'utf8', windowsHide: true, timeout: 900_000, maxBuffer: 64 * 1024 * 1024 });
const texts = ['scripts/npc/items.txt', 'scripts/npc/npc_heroes.txt', 'scripts/npc/heroes/',
  'resource/localization/dota_schinese.txt', 'resource/localization/abilities_schinese.txt'];
if (!manifestOnly) {
  run(['-i', vpk, '-f', texts.join(','), '-o', raw, '-q']);
  run(['-i', vpk, '-f', 'panorama/images/items/,panorama/images/spellicons/', '-e', 'vtex_c', '-o', raw, '-q']);
  run(['-i', raw, '--recursive', '-e', 'vtex_c', '-d', '-o', decoded, '-q', '--threads', '8']);
}

const read = (file: string) => parseKeyValues(fs.readFileSync(path.join(raw, file), 'utf8'));
const tokens = (file: string) => kvObject(kvObject(read(file).lang).Tokens);
// Token keys differ in case between files (DOTA_Tooltip_Ability_item_x); hero names use "<hero>:n".
const zh = new Map(Object.entries({ ...tokens('resource/localization/dota_schinese.txt'),
  ...tokens('resource/localization/abilities_schinese.txt') }).map(([k, v]) => [k.toLowerCase(), v]));
const text = (key: string) => { const v = zh.get(key.toLowerCase()); return typeof v === 'string' ? v : null; };
const icon = (folder: string, texture: string) => {
  const file = path.join(decoded, 'panorama/images', folder, `${texture}_png.png`);
  return fs.existsSync(file) ? path.relative(out, file).replaceAll('\\', '/') : null;
};

const items: Record<string, { icon: string, altIcons: string[] } & Record<string, unknown>> = {}, missing: string[] = [];
const byTexture = new Map<string, string[]>();
for (const [name, value] of Object.entries(kvObject(read('scripts/npc/items.txt').DOTAAbilities))) {
  const def = kvObject(value);
  if (!name.startsWith('item_') || typeof value !== 'object') continue;
  // Every recipe shows the same scroll in the HUD, so recipes share one reference.
  const texture = name.startsWith('item_recipe_') ? 'recipe' : String(def.AbilityTextureName || name).replace(/^item_/, '');
  const file = icon('items', texture);
  if (!file) { missing.push(name); continue; }
  byTexture.set(texture, [...byTexture.get(texture) ?? [], name]);
  items[name] = {
    icon: file, altIcons: [], nameZh: text(`DOTA_Tooltip_ability_${name}`), cost: Number(def.ItemCost) || 0,
    recipe: def.ItemRecipe === '1', purchasable: def.ItemPurchasable !== '0',
    neutral: def.ItemIsNeutralActiveDrop === '1' || def.ItemIsNeutralPassiveDrop === '1',
    // Flag present but "0" (grisgris, eldwurms_edda): seen both in the neutral slot and the inventory.
    formerNeutral: (def.ItemIsNeutralActiveDrop ?? def.ItemIsNeutralPassiveDrop) === '0',
    enhancement: name.startsWith('item_enhancement_'),
  };
}
// The HUD swaps some icons by state (power_treads_agi, bottle_empty, tranquil_boots_active,
// arcana variants). Unreferenced textures go to the item whose texture is their longest prefix.
const itemDir = path.join(decoded, 'panorama/images/items');
for (const file of fs.readdirSync(itemDir).filter(f => f.endsWith('_png.png'))) {
  const base = file.slice(0, -'_png.png'.length);
  if (byTexture.has(base) || base.includes('opacity_mask')) continue;
  const owner = [...byTexture.keys()].filter(t => base.length > t.length && base.startsWith(t) && /^[_.\d]/.test(base[t.length]))
    .sort((a, b) => b.length - a.length)[0];
  for (const name of owner ? byTexture.get(owner)! : []) items[name].altIcons.push(icon('items', base)!);
}

const heroes: Record<string, unknown> = {};
const heroDir = path.join(raw, 'scripts/npc/heroes');
for (const file of fs.readdirSync(heroDir).filter(f => /^npc_dota_hero_\w+\.txt$/.test(f))) {
  for (const [name, value] of Object.entries(kvObject(read(`scripts/npc/heroes/${file}`).DOTAHeroes))) {
    if (!name.startsWith('npc_dota_hero_') || name === 'npc_dota_hero_base' || typeof value !== 'object') continue;
    const def = value as KV;
    const abilities = Object.entries(def).filter(([k]) => /^Ability\d+$/.test(k)).map(([, v]) => String(v))
      .filter(a => a && !a.startsWith('special_bonus') && a !== 'generic_hidden')
      .map(a => ({ name: a, icon: icon('spellicons', a) })).filter(a => a.icon);
    heroes[name] = { nameZh: text(`${name}:n`), abilities };
  }
}

const manifest = {
  version: 1, serverBuild,
  attribution: 'Dota 2 artwork and text belong to Valve; extracted locally from the installed client, unmodified.',
  tool: { name: pin.name, version: pin.version, executableSha256: pin.executableSha256 },
  items, heroes, missingItemIcons: missing,
};
fs.writeFileSync(path.join(out, 'manifest.json'), JSON.stringify(manifest, null, 1) + '\n');
console.log(`Client build ${serverBuild}: ${Object.keys(items).length} item icons (${missing.length} without icon), ` +
  `${Object.keys(heroes).length} heroes.`);
