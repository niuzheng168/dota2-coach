// Real GSI/server/browser + fake Responses transport. This is NOT a paid-model performance evaluation.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { AccessStore } from '../src/access-store.ts';
import { createCoachServer } from '../src/app-server.ts';
import { DEFAULT_ROOM_SETTINGS } from '../src/room-settings.ts';
import { TacticalEngine } from '../src/tactics/index.ts';
import { createResponsesReasoner } from '../src/tactics/reasoning-planner.ts';
import { MAP } from '../src/tactics/combat-data.ts';

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'llm-jungle-'));
const store = await AccessStore.open(directory);
await store.createUser('owner', 'test-password-only', true);
const room = await store.createRoom({ username: 'owner', canCreateRooms: true }, 'LLM test', DEFAULT_ROOM_SETTINGS, []);
const { sessionToken } = await store.createSession('owner');
let modelRequests = 0, voiceStarts = 0;
const reasoner = createResponsesReasoner('mock-responses-model-not-paid', async body => {
  modelRequests++;
  const output = body.input.findLast(m => m.type === 'function_call_output');
  const feedback = output && JSON.parse(output.output);
  const name = feedback ? 'finish_plan' : 'simulate_route';
  const args = feedback ? { candidateId: feedback.candidateId, summary: '模型选择先去中野，再去大野；这是合成接口测试，不是实战收益。', evidenceIds: [] }
    : { visits: ['neutralcamp_good_9', 'neutralcamp_good_5'].map(campId => ({ campId, casts: [], targetPriority: [] })) };
  return { status: 'completed', output: [{ type: 'function_call', call_id: `c${modelRequests}`, name, arguments: JSON.stringify(args) }] };
});
const server = createCoachServer(store, (kind, options) => ({
  kind, role: options.role, async start() { voiceStarts++; }, async stop() {}, appendAudio() {}, markSpeechEnd() {},
  async updateMatchHeroes() {},
}), () => new TacticalEngine({ reasoner }));
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const base = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.context().addCookies([{ name: 'dota2_session', value: sessionToken, url: base }]);
  await page.route('**/*', route => route.request().url().startsWith(base) ? route.continue() : route.abort());
  await page.goto(`${base}/?room=${room.roomId}`);
  await page.waitForFunction(() => !document.getElementById('jungleEnabled').disabled);
  await page.locator('#jungleEnabled').check();
  const camp = MAP.camps.find(c => c.id === 'neutralcamp_good_5');
  const gsi = {
    auth: { token: store.getRoom(room.roomId).gsiToken },
    map: { clock_time: 600, matchid: 'llm-browser-test', game_state: 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS' },
    player: { team_name: 'radiant', last_hits: 100 },
    hero: { name: 'npc_dota_hero_luna', xpos: camp.x, ypos: camp.y, level: 10, health: 4000, max_health: 4000,
      mana: 556, max_mana: 556, alive: true },
    abilities: { ability0: { name: 'luna_moon_glaive', level: 4, cooldown: 0 } },
    items: { slot0: { name: 'item_power_treads', passive: true }, slot1: { name: 'item_mask_of_madness', cooldown: 0, passive: true } },
  };
  assert.equal((await fetch(`${base}/api/gsi?room=${room.roomId}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(gsi),
  })).status, 200);
  await page.locator('#jungleSteps li').first().waitFor({ state: 'visible' });
  assert.equal(await page.locator('#jungleSteps li').first().getAttribute('data-camp-id'), 'neutralcamp_good_9');
  assert.match(await page.locator('#junglePlanner').textContent(), /mock-responses-model-not-paid/);
  assert.match(await page.locator('#jungleReasoning').textContent(), /模型选择先去中野/);
  assert.equal(modelRequests, 2);
  assert.equal(voiceStarts, 1);
  await page.locator('.jungle-panel').screenshot({ path: '.tmp/llm-jungle-panel.png' });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  await new Promise(r => setTimeout(r, 1100));
  await page.locator('#jungleEnabled').uncheck();
  await page.locator('#jungleBody').waitFor({ state: 'hidden' });
  assert.equal(voiceStarts, 1);
  assert.deepEqual(errors, []);
  console.log('PASS mocked LLM: GSI -> Responses tools -> model choice -> latest-state verifier -> UI/map, no heuristic substitution, no voice restart.');
} finally {
  await browser?.close(); await new Promise(resolve => server.close(resolve));
  await fs.rm(directory, { recursive: true, force: true });
}
