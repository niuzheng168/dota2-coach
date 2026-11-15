// Real authenticated room + GSI + browser panel, no paid AI services or game client.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { AccessStore } from '../src/access-store.ts';
import { createCoachServer } from '../src/app-server.ts';
import { DEFAULT_ROOM_SETTINGS } from '../src/room-settings.ts';
import { MAP } from '../src/tactics/combat-data.ts';

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'jungle-browser-'));
const store = await AccessStore.open(dir);
await store.createUser('owner', 'browser-test-password', true);
const user = { username: 'owner', canCreateRooms: true };
const room = await store.createRoom(user, 'Jungle test', DEFAULT_ROOM_SETTINGS, []);
const { sessionToken } = await store.createSession(user.username);
let starts = 0;
const server = createCoachServer(store, (kind, options) => ({
  kind, role: options.role, async start() { starts++; }, async stop() {},
  appendAudio() {}, markSpeechEnd() {}, async updateMatchHeroes() {},
}));
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const base = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  browser = await chromium.launch({
    headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH || undefined,
    args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'],
  });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.context().addCookies([{ name: 'dota2_session', value: sessionToken, url: base }]);
  await page.route('**/*', route => {
    if (route.request().url().startsWith(base)) return route.continue();
    return route.abort();
  });
  await page.goto(`${base}/?room=${room.roomId}`);
  await page.waitForFunction(() => !document.getElementById('jungleEnabled').disabled);
  assert.equal(await page.locator('#jungleEnabled').isChecked(), false);
  await page.locator('#jungleEnabled').check();
  await page.locator('#jungleBody').waitFor({ state: 'visible' });
  const camp = MAP.camps.find(c => c.id === 'neutralcamp_good_5');
  const payload = {
    auth: { token: store.getRoom(room.roomId).gsiToken },
    map: { matchid: 'browser-test', clock_time: 600, game_state: 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS' },
    hero: { name: 'npc_dota_hero_sven', level: 15, xpos: camp.x, ypos: camp.y,
      health: 3000, max_health: 3000, mana: 800, max_mana: 800, alive: true },
    player: { team_name: 'radiant', last_hits: 80 },
    abilities: { ability0: { name: 'sven_storm_bolt', level: 4, cooldown: 0 },
      ability1: { name: 'sven_great_cleave', level: 4, cooldown: 0 } },
    items: { slot0: { name: 'item_power_treads', passive: false }, slot1: { name: 'item_broadsword' } },
  };
  const response = await fetch(`${base}/api/gsi?room=${room.roomId}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload),
  });
  assert.equal(response.status, 200);
  await page.locator('#jungleSteps li').first().waitFor({ state: 'visible' });
  const visitCount = await page.locator('#jungleSteps li').count();
  assert.ok(visitCount > 3, `expected a multi-minute farming route, got ${visitCount} visits`);
  assert.equal(await page.locator('#jungleMap circle').count(), 29);
  assert.equal(await page.locator('#jungleMap polyline').count(), visitCount);
  assert.match(await page.locator('#jungleSummary').textContent(), /^预计总金钱 \d+ 金 · 180 秒窗口内金钱优先/);
  assert.match(await page.locator('#jungleSummary').textContent(), /经验（仅展示）/);
  const expectedGold = await page.locator('#jungleSteps li').evaluateAll(nodes =>
    nodes.reduce((sum, node) => sum + Number(node.dataset.gold), 0));
  assert.match(await page.locator('#jungleSummary').textContent(), new RegExp(`^预计总金钱 ${Math.round(expectedGold)} 金`));
  assert.equal(await page.locator('.jungle-step-gold').count(), visitCount);
  assert.match(await page.locator('.jungle-step-gold').last().textContent(), new RegExp(`累计 ${Math.round(expectedGold)} 金$`));
  assert.match(await page.locator('#jungleSteps').textContent(), /回访/);
  assert.match(await page.locator('#jungleSteps').textContent(), /假定.*刷新/);
  const campIds = await page.locator('#jungleSteps li').evaluateAll(nodes => nodes.map(node => node.dataset.campId));
  assert.ok(new Set(campIds).size < campIds.length);
  assert.equal(await page.locator('#jungleMap text').count(), new Set(campIds).size,
    'repeat visits share a map label instead of overlapping labels');
  assert.equal(await page.locator('#jungleCamp option').count(), 14);
  assert.equal(await page.locator('#jungleVoice').isChecked(), false);
  assert.match(await page.locator('#jungleStats').textContent(), /ATK.*ARM/);
  assert.match(await page.locator('#jungleWarnings').textContent(), /未模拟野怪技能/);
  await page.locator('#jungleSteps').evaluate(element => { element.scrollTop = 120; });
  await new Promise(resolve => setTimeout(resolve, 550));
  payload.map.clock_time = 601;
  assert.equal((await fetch(`${base}/api/gsi?room=${room.roomId}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload),
  })).status, 200);
  await page.waitForFunction(() => document.getElementById('jungleStats').textContent.startsWith('10:01'));
  assert.ok(await page.locator('#jungleSteps').evaluate(element => element.scrollTop >= 119),
    'regular GSI updates must not reset the long route list scroll position');
  const initialStarts = starts;
  await new Promise(resolve => setTimeout(resolve, 1050));
  await page.locator('#jungleRequest').click();
  await page.locator('.jungle-panel').screenshot({ path: '.tmp/jungle-panel.png' });
  assert.equal(starts, initialStarts);
  await page.setViewportSize({ width: 390, height: 844 });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1),
    'enabled jungle panel must fit a mobile viewport');
  await new Promise(resolve => setTimeout(resolve, 1050));
  await page.locator('#jungleEnabled').uncheck();
  await page.locator('#jungleBody').waitFor({ state: 'hidden' });
  assert.equal(starts, initialStarts);
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  assert.deepEqual(errors, []);
  console.log('PASS jungle browser: opt-in, live GSI, time-horizon route/respawn revisits, map labels, warnings, no voice restart, disable, mobile width');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
  await fs.rm(dir, { recursive: true, force: true });
}
