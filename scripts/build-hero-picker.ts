// Refresh checked-in Valve portraits and attributes without rebuilding the coaching glossary.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const dictionaryPath = path.join(root, 'data/dota2-dictionary.json');
const directory = path.join(root, 'public/heroes');
const cdn = 'https://cdn.cloudflare.steamstatic.com/apps/dota2/';
const dictionary = JSON.parse(await fs.readFile(dictionaryPath, 'utf8'));

async function download(url: string): Promise<Buffer | null> {
  for (let attempt = 0; ; attempt++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(30_000) });
      if (response.status === 404) return null;
      if (!response.ok) throw new Error(`HTTP ${response.status}: ${url}`);
      return Buffer.from(await response.arrayBuffer());
    } catch (error) {
      if (attempt >= 2) throw error;
      await new Promise(resolve => setTimeout(resolve, 500 * (attempt + 1)));
    }
  }
}

function verifyImage(bytes: Buffer, extension: string): void {
  const valid = extension === 'jpg'
    ? bytes[0] === 0xff && bytes[1] === 0xd8
    : bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
  if (!valid) throw new Error('Valve returned an invalid image');
}

const list = await download('https://www.dota2.com/datafeed/herolist?language=english');
if (!list) throw new Error('Valve hero list unavailable');
const heroes = new Map<number, { name: string; primary_attr: number }>(
  JSON.parse(list.toString()).result.data.heroes.map((hero: any) => [hero.id, hero]),
);
await fs.mkdir(directory, { recursive: true });
const sources: Record<string, string> = {};
let cursor = 0;
await Promise.all(Array.from({ length: 6 }, async () => {
  while (cursor < dictionary.heroes.length) {
    const hero = dictionary.heroes[cursor++];
    const official = heroes.get(hero.id);
    if (!official || official.name !== hero.key || ![0, 1, 2, 3].includes(official.primary_attr)) {
      throw new Error(`Missing/invalid Valve hero metadata: ${hero.key}`);
    }
    const name = hero.key.replace(/^npc_dota_hero_/, '');
    if (!/^[a-z0-9_]+$/.test(name)) throw new Error('Invalid hero asset name');
    let extension = 'jpg';
    let source = `${cdn}images/heroes/${name}_vert.jpg`;
    let bytes = await download(source);
    // A few newer heroes have no legacy vertical portrait.
    if (!bytes) {
      extension = 'png';
      source = `${cdn}images/dota_react/heroes/${name}.png`;
      bytes = await download(source);
    }
    if (!bytes) throw new Error(`Missing portrait: ${hero.key}`);
    // Some legacy "_vert.jpg" URLs actually contain PNG bytes.
    if (bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))) extension = 'png';
    verifyImage(bytes, extension);
    const filename = `${name}.${extension}`;
    await fs.writeFile(path.join(directory, filename), bytes);
    hero.primaryAttr = official.primary_attr;
    hero.portrait = `/heroes/${filename}`;
    sources[filename] = source;
  }
}));

for (const attribute of ['strength', 'agility', 'intelligence', 'universal']) {
  const filename = `attr_${attribute}.png`;
  const source = `${cdn}images/dota_react/icons/hero_${attribute}.png`;
  const bytes = await download(source);
  if (!bytes) throw new Error(`Missing attribute icon: ${attribute}`);
  verifyImage(bytes, 'png');
  await fs.writeFile(path.join(directory, filename), bytes);
  sources[filename] = source;
}

await fs.writeFile(dictionaryPath, JSON.stringify(dictionary, null, 2) + '\n');
await fs.writeFile(path.join(directory, 'sources.json'), JSON.stringify({
  updatedAt: new Date().toISOString(),
  metadata: 'https://www.dota2.com/datafeed/herolist?language=english',
  attribution: 'Dota 2 hero artwork and attribute icons belong to Valve. Original assets, unmodified.',
  files: Object.fromEntries(Object.entries(sources).sort(([a], [b]) => a.localeCompare(b))),
}, null, 2) + '\n');
console.log(`Updated ${dictionary.heroes.length} portraits and 4 attribute icons; coaching glossary unchanged.`);
