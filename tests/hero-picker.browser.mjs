// Real UI + real room/GSI/vision routes, but temporary accounts, local artwork and fake models only.
import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { AccessStore } from '../src/access-store.ts';
import { createCoachServer } from '../src/app-server.ts';
import { DOTA2_DICTIONARY } from '../src/dota2-glossary.ts';
import { tokenManager } from '../src/azure-auth.ts';

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'hero-picker-browser-'));
const store = await AccessStore.open(directory);
await store.createUser('owner', 'browser-test-only', true);
await store.createUser('guest', 'browser-test-only');
const engines = [];
const server = createCoachServer(store, (kind, options, callbacks) => {
  const engine = { stopped: false, updates: [] };
  engines.push(engine);
  return {
    kind, role: options.role,
    async start() { callbacks.onStatus('test ready'); },
    async stop() { engine.stopped = true; },
    appendAudio() {}, markSpeechEnd() {},
    async updateMatchHeroes(heroes) { engine.updates.push([...heroes]); },
  };
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const base = `http://127.0.0.1:${server.address().port}`;
const originalFetch = globalThis.fetch;
const originalAuth = tokenManager.getAuthHeaders;
tokenManager.getAuthHeaders = async () => ({ 'api-key': 'test-only' });
globalThis.fetch = async (url, init) => {
  if (String(url).endsWith('/openai/v1/responses')) {
    return Response.json({ output_text: JSON.stringify({ phase: 'in_game', radiant: ['Lina'], dire: ['Lion'] }) });
  }
  assert.ok(String(url).startsWith(base), 'No external API calls allowed');
  return originalFetch(url, init);
};
let browser;
try {
  browser = await chromium.launch({
    headless: true,
    executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH || undefined,
    args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'],
  });
  const errors = [];
  async function pageFor(username, url) {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, permissions: ['microphone'] });
    await context.route('**/*', route => {
      if (!route.request().url().startsWith(base)) return route.abort();
      return route.continue();
    });
    await context.addInitScript(() => {
      window.testSockets = [];
      const Socket = window.WebSocket;
      window.WebSocket = class extends Socket {
        constructor(...args) { super(...args); window.testSockets.push(this); }
      };
    });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(url);
    await page.locator('#usernameInput').fill(username);
    await page.locator('#passwordInput').fill('browser-test-only');
    await page.locator('#loginSubmitBtn').click();
    await page.locator('#loginView').waitFor({ state: 'hidden' });
    return page;
  }
  const ready = page => page.waitForFunction(() => document.getElementById('connBadge').textContent === 'Connected' &&
    !document.getElementById('openMicBtn').disabled);
  const owner = await pageFor('owner', base);
  await owner.locator('#createRoomBtn').click();
  await owner.locator('#newRoomName').fill('英雄选择 · 界面验收');
  await owner.locator('#createAllowedUsers input[value="guest"]').check();
  await owner.locator('#createRoomSubmitBtn').click();
  await ready(owner);
  const room = store.listOwnedRooms('owner')[0];
  const guest = await pageFor('guest', `${base}/?room=${room.roomId}&role=coach#invite=${room.inviteToken}`);
  await ready(guest);
  assert.ok(await guest.locator('#openHeroPickerBtn').isHidden());
  await owner.locator('#openMicBtn').click();
  await owner.waitForFunction(() => document.getElementById('openMicBtn').textContent.includes('Stop Open Mic'));
  await owner.locator('#openHeroPickerBtn').click();
  const card = name => owner.locator(`.hero-card[data-hero="${name}"]`);
  const count = async number => {
    for (const page of [owner, guest]) {
      await page.waitForFunction(n => document.getElementById('matchHeroesCount').textContent === `${n} / 10`, number);
    }
  };
  assert.equal(await owner.locator('.hero-card').count(), DOTA2_DICTIONARY.heroes.length);
  for (const attribute of [0, 1, 2, 3]) {
    assert.equal(await owner.locator(`.hero-group[data-attribute="${attribute}"] .hero-card`).count(),
      DOTA2_DICTIONARY.heroes.filter(hero => hero.primaryAttr === attribute).length);
  }
  await owner.waitForFunction(() => [...document.querySelectorAll('.hero-card img')].every(img => img.complete && img.naturalWidth > 0));
  await card('Axe').focus();
  await owner.keyboard.press('Space');
  await count(1);
  assert.equal(await card('Axe').getAttribute('aria-pressed'), 'true');
  assert.equal(await guest.locator('#selectedHeroTags button').count(), 0);
  await card('Axe').click();
  await count(0);
  await owner.locator('#heroPickerSearch').fill('猴子');
  assert.equal(await owner.locator('.hero-card:visible').count(), 1);
  await owner.locator('#heroPickerSearch').press('Enter');
  await count(1);
  assert.equal(await card('Phantom Lancer').getAttribute('aria-pressed'), 'true');
  await owner.locator('#heroPickerSearch').fill('no-such-hero-123');
  assert.ok(await owner.locator('#heroPickerEmpty').isVisible());
  await owner.locator('#heroPickerSearch').press('Enter');
  await count(1);
  await owner.locator('#resetHeroFiltersBtn').click();
  await owner.locator('[data-attribute-filter="0"]').click();
  assert.equal(await owner.locator('.hero-group:visible').count(), 1);
  await owner.locator('[data-attribute-filter="all"]').click();
  await owner.locator('#clearPickerHeroesBtn').click();
  await count(0);
  const picks = ['Axe', 'Crystal Maiden', 'Lina', 'Pudge', 'Drow Ranger', 'Invoker', 'Earthshaker', 'Lion', 'Slark', 'Dazzle'];
  for (const name of picks) await card(name).click();
  await count(10);
  assert.equal(await card('Anti-Mage').getAttribute('aria-disabled'), 'true');
  await card('Anti-Mage').dispatchEvent('click');
  assert.match(await owner.locator('#heroPickerFeedback').textContent(), /已选满/);
  await count(10);
  await owner.locator('#heroOnlySelected').check();
  assert.equal(await owner.locator('.hero-card:visible').count(), 10);
  await owner.locator('#heroOnlySelected').uncheck();
  // Changes and keyboard selection must not reconnect or stop the open microphone.
  assert.equal(engines.length, 2);
  assert.ok(engines.every(engine => !engine.stopped && engine.updates.length > 0));
  assert.equal(await owner.evaluate(() => window.testSockets.length), 1);
  assert.match(await owner.locator('#openMicBtn').textContent(), /Stop Open Mic/);
  const screenshots = process.env.BROWSER_SCREENSHOT_DIR || '.tmp';
  await fs.mkdir(screenshots, { recursive: true });
  await owner.screenshot({ path: path.join(screenshots, 'hero-picker-desktop.png') });
  for (const width of [1024, 768, 390, 320]) {
    await owner.setViewportSize({ width, height: width < 600 ? 844 : 1000 });
    assert.ok(await owner.evaluate(() => {
      const dialog = document.getElementById('heroPickerDialog');
      const board = document.querySelector('.hero-picker-scroll');
      return dialog.scrollWidth <= dialog.clientWidth && board.scrollWidth <= board.clientWidth &&
        dialog.getBoundingClientRect().left >= 0 && dialog.getBoundingClientRect().right <= innerWidth;
    }), `picker overflows at ${width}px`);
    assert.ok(await owner.locator('.hero-picker-done').isVisible());
    if (width === 390) await owner.screenshot({ path: path.join(screenshots, 'hero-picker-mobile.png') });
  }
  await owner.setViewportSize({ width: 1440, height: 1000 });
  await owner.locator('#heroPickerLineup [data-slot="0"]').click();
  await count(9);
  await card('Anti-Mage').click();
  await count(10);
  await owner.locator('#clearPickerHeroesBtn').click();
  await count(0);
  // Actual GSI route updates an already-open panel, not just the collapsed sidebar.
  const gsi = await fetch(`${base}/api/gsi?room=${room.roomId}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ auth: { token: room.gsiToken }, hero: { name: 'npc_dota_hero_axe' } }),
  });
  assert.equal(gsi.status, 200);
  await count(1);
  assert.equal(await card('Axe').getAttribute('aria-pressed'), 'true');
  // Actual vision route uses a deterministic mocked model; no paid image requests.
  const vision = await owner.evaluate(async roomId => {
    const session = await (await fetch('/api/auth/session')).json();
    return (await fetch('/api/vision-draft', {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': session.csrfToken },
      body: JSON.stringify({ roomId, imageDataUrl: 'data:image/png;base64,dGVzdA==' }),
    })).json();
  }, room.roomId);
  assert.equal(vision.ok, true);
  await count(2);
  assert.equal(await card('Axe').getAttribute('aria-pressed'), 'false');
  assert.equal(await card('Lina').getAttribute('aria-pressed'), 'true');
  assert.equal(await card('Lion').getAttribute('aria-pressed'), 'true');
  // Broken artwork remains labeled and selectable.
  await card('Axe').locator('img').evaluate(img => img.dispatchEvent(new Event('error')));
  assert.match(await card('Axe').getAttribute('class'), /portrait-unavailable/);
  await card('Axe').click();
  await count(3);
  await owner.keyboard.press('Escape');
  assert.ok(await owner.locator('#heroPickerDialog').isHidden());
  assert.equal(await owner.evaluate(() => document.activeElement.id), 'openHeroPickerBtn');
  await owner.screenshot({ path: path.join(screenshots, 'hero-picker-room.png') });
  // A disconnected client cannot pretend it has changed the server's roster.
  await owner.evaluate(() => window.testSockets.at(-1).close());
  await owner.waitForFunction(() => document.getElementById('connBadge').textContent === 'Disconnected');
  await owner.locator('#openHeroPickerBtn').click();
  await card('Dazzle').dispatchEvent('click');
  assert.match(await owner.locator('#heroPickerFeedback').textContent(), /尚未连接/);
  assert.equal(await owner.locator('#matchHeroesCount').textContent(), '3 / 10');
  await owner.keyboard.press('Escape');
  await owner.locator('#leaveRoomBtn').click();
  assert.ok(await owner.locator('#heroPickerDialog').isHidden());
  assert.deepEqual(errors, []);
  console.log('PASS portraits, four attributes, click/keyboard toggle, search, 10-hero cap, guest sync/read-only, GSI/vision sync, uninterrupted mic, mobile, image fallback, offline guard and cleanup');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
  globalThis.fetch = originalFetch;
  tokenManager.getAuthHeaders = originalAuth;
  await fs.rm(directory, { recursive: true, force: true });
}
