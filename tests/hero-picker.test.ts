import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { AccessStore } from '../src/access-store.ts';
import { createCoachServer } from '../src/app-server.ts';
import { DOTA2_DICTIONARY } from '../src/dota2-glossary.ts';

test('every glossary hero has a valid attribute and a unique, checked-in Valve portrait', async () => {
  const portraits = new Set<string>();
  const sources = JSON.parse(await fs.readFile(new URL('../public/heroes/sources.json', import.meta.url), 'utf8'));
  const counts = [0, 0, 0, 0];
  for (const hero of DOTA2_DICTIONARY.heroes) {
    assert.ok(hero.primaryAttr !== undefined && counts[hero.primaryAttr] !== undefined, hero.en);
    counts[hero.primaryAttr!]++;
    assert.match(hero.portrait!, /^\/heroes\/[a-z0-9_]+\.(jpg|png)$/);
    assert.ok(!portraits.has(hero.portrait!), `Duplicate portrait: ${hero.en}`);
    portraits.add(hero.portrait!);
    const bytes = await fs.readFile(new URL(`../public${hero.portrait}`, import.meta.url));
    assert.ok(bytes.length > 1000);
    assert.equal(bytes.subarray(0, 2).toString('hex'), hero.portrait!.endsWith('.jpg') ? 'ffd8' : '8950');
    assert.match(sources.files[path.basename(hero.portrait!)], /^https:\/\/cdn\.cloudflare\.steamstatic\.com\/apps\/dota2\/images\//);
  }
  assert.ok(counts.every(count => count > 0));
  assert.equal(portraits.size, DOTA2_DICTIONARY.counts.heroes);
});

test('only allowlisted portrait assets and the picker module are served; image HEAD and MIME work', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'hero-assets-'));
  const server = createCoachServer(await AccessStore.open(directory));
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(async () => {
    await new Promise<void>(resolve => server.close(() => resolve()));
    await fs.rm(directory, { recursive: true, force: true });
  });
  const address = server.address();
  assert.ok(address && typeof address === 'object');
  const base = `http://127.0.0.1:${address.port}`;
  const jpg = DOTA2_DICTIONARY.heroes.find(hero => hero.portrait?.endsWith('.jpg'))!.portrait!;
  const png = '/heroes/attr_strength.png';
  for (const asset of [jpg, png]) {
    const response = await fetch(base + asset);
    assert.equal(response.status, 200);
    assert.equal(response.headers.get('content-type'), asset.endsWith('.jpg') ? 'image/jpeg' : 'image/png');
    assert.match(response.headers.get('cache-control')!, /public, max-age=/);
    assert.ok((await response.arrayBuffer()).byteLength > 1000);
    const head = await fetch(base + asset, { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal((await head.arrayBuffer()).byteLength, 0);
    assert.equal((await fetch(base + asset, { method: 'POST' })).status, 404);
  }
  assert.equal((await fetch(base + '/hero-picker.js')).status, 200);
  for (const denied of ['/heroes/sources.json', '/heroes/not_a_hero.jpg', '/heroes/%2e%2e%2f.env', '/.env']) {
    assert.equal((await fetch(base + denied)).status, 404);
  }
});
