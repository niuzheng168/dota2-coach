// Optional real-browser regression: supply an installed Playwright module/browser via env.
// Uses only temporary accounts and a fake upstream, never production rooms or paid APIs.
import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { AccessStore } from '../src/access-store.ts';
import { createCoachServer } from '../src/app-server.ts';

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'coach-settings-browser-'));
const store = await AccessStore.open(directory);
await store.createUser('owner', 'browser-test-only', true);
await store.createUser('guest', 'browser-test-only');
const engines = [];
const server = createCoachServer(store, (kind, options, callbacks) => {
  const engine = { kind, options, stopped: false, frames: 0 };
  engines.push(engine);
  return {
    kind, role: options.role,
    async start() { callbacks.onStatus('test ready'); },
    async stop() { engine.stopped = true; },
    appendAudio() { engine.frames++; },
    markSpeechEnd() {},
    async translateText(text) {
      callbacks.onTargetFinal('browser-turn', `translated: ${text}`);
      callbacks.onTargetAudio('browser-turn', Buffer.alloc(2400));
    },
  };
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const base = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  browser = await chromium.launch({
    headless: true,
    executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH || undefined,
    args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'],
  });
  const errors = [];
  async function pageFor(username, url) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, permissions: ['microphone'] });
    await context.addInitScript(() => {
      window.testSockets = [];
      window.testStreams = [];
      const Socket = window.WebSocket;
      window.WebSocket = class extends Socket {
        constructor(...args) {
          super(...args);
          window.testSockets.push(this);
        }
      };
      const getUserMedia = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
      navigator.mediaDevices.getUserMedia = async options => {
        const stream = await getUserMedia(options);
        window.testStreams.push(stream);
        return stream;
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
  async function ready(page) {
    await page.waitForFunction(() => !document.getElementById('roomView').hidden &&
      document.getElementById('connBadge').textContent === 'Connected' &&
      !document.getElementById('openMicBtn').disabled);
  }
  const owner = await pageFor('owner', base);
  await owner.locator('#createRoomBtn').click();
  await owner.locator('#newRoomName').fill('Settings browser regression');
  await owner.locator('#createAllowedUsers input[value="guest"]').check();
  await owner.locator('#createRoomSubmitBtn').click();
  await ready(owner);
  const room = store.listOwnedRooms('owner')[0];
  const guest = await pageFor('guest', `${base}/?room=${room.roomId}&role=coach#invite=${room.inviteToken}`);
  await ready(guest);
  assert.ok(await guest.locator('#editRoomSettingsBtn').isHidden());
  await guest.locator('#selfMonitorToggle').check();
  for (const page of [owner, guest]) {
    await page.locator('#openMicBtn').click();
    await page.waitForFunction(() => document.getElementById('openMicBtn').textContent.includes('Stop Open Mic'));
  }
  await owner.locator('#editRoomSettingsBtn').click();
  await owner.locator('#roomSettingsDialog').waitFor({ state: 'visible' });
  assert.equal(await owner.locator('#editEngineSelect').inputValue(), 'cascade');
  await owner.locator('#editEngineSelect').selectOption('gpt-live');
  assert.ok(await owner.locator('#editVoiceOptions').isHidden());
  assert.ok(await owner.locator('#editLiveEngineNote').isVisible());
  await owner.locator('#editEngineSelect').selectOption('gpt-realtime');
  await owner.locator('#editTtsZhVoiceSelect').selectOption('zh-CN-YunxiNeural');
  await owner.locator('#editTtsEnVoiceSelect').selectOption('en-US-EmmaMultilingualNeural');
  await owner.locator('#editVoiceLiveAsrSelect').selectOption('mai-transcribe-2-streaming');
  for (const width of [1280, 390]) {
    await owner.setViewportSize({ width, height: 900 });
    assert.ok(await owner.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `overflow at ${width}`);
  }
  await owner.locator('#saveRoomSettingsBtn').click();
  await owner.locator('#roomSettingsDialog').waitFor({ state: 'hidden' });
  for (const page of [owner, guest]) {
    await page.waitForFunction(() => window.testSockets.length === 2);
    await ready(page);
    assert.ok(await page.evaluate(() => window.testSockets[0].readyState === WebSocket.CLOSED));
    assert.ok(await page.evaluate(() => window.testStreams.every(stream => stream.getTracks().every(track => track.readyState === 'ended'))));
    assert.match(await page.locator('#openMicBtn').innerText(), /Start Open Mic/);
    assert.match(await page.locator('#roomSettingsSummary').innerText(), /Realtime 2.1/);
    assert.match(await page.locator('#roomSettingsSummary').innerText(), /云希/);
    assert.match(await page.locator('#roomSettingsSummary').innerText(), /mai-transcribe-2-streaming/);
    assert.match(await page.locator('#participantsList').innerText(), /owner/);
    assert.match(await page.locator('#participantsList').innerText(), /guest/);
  }
  assert.ok(engines.slice(0, 2).every(engine => engine.stopped));
  assert.ok(engines.slice(2).every(engine => engine.kind === 'gpt-realtime'));
  assert.equal(await guest.locator('#roleSelect').inputValue(), 'coach');
  assert.ok(await guest.locator('#selfMonitorToggle').isChecked());
  assert.ok(new URL(guest.url()).hash.includes(room.inviteToken));
  await guest.locator('#textSendInput').fill('after reset');
  await guest.locator('#textSendForm button').click();
  await owner.waitForFunction(() => document.getElementById('transcriptFeed').textContent.includes('translated: after reset'));
  await guest.locator('#openMicBtn').click();
  await guest.waitForFunction(() => document.getElementById('openMicBtn').textContent.includes('Stop Open Mic'));
  // Saving the same settings must not reset either connection or an active microphone.
  await owner.locator('#editRoomSettingsBtn').click();
  await owner.locator('#saveRoomSettingsBtn').click();
  await owner.locator('#roomSettingsDialog').waitFor({ state: 'hidden' });
  for (const page of [owner, guest]) assert.equal(await page.evaluate(() => window.testSockets.length), 2);
  assert.match(await guest.locator('#openMicBtn').innerText(), /Stop Open Mic/);
  await owner.reload();
  await ready(owner);
  assert.match(await owner.locator('#roomSettingsSummary').innerText(), /云希/);
  assert.equal((await AccessStore.open(directory)).getRoom(room.roomId).settings.engineKind, 'gpt-realtime');
  assert.deepEqual(errors, []);
  console.log('PASS owner editing, guest restrictions, engine/voice/ASR, mobile layout, full reconnect, media cleanup, preserved identity, text/audio, no-op save and persistence');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
  await fs.rm(directory, { recursive: true, force: true });
}
