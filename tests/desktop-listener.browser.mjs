// Real browser + mocked native bridge/upstream. No production accounts, audio, or model calls.
import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { AccessStore } from '../src/access-store.ts';
import { createCoachServer } from '../src/app-server.ts';
import { DEFAULT_ROOM_SETTINGS } from '../src/room-settings.ts';

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'coach-desktop-browser-'));
const store = await AccessStore.open(directory);
await store.createUser('owner', 'browser-test-only', true);
const engines = [];
const server = createCoachServer(store, (kind, options, callbacks) => {
  const engine = { options, callbacks, frames: 0, stopped: false };
  engines.push(engine);
  return {
    kind, role: options.role,
    async start() {}, async stop() { engine.stopped = true; }, markSpeechEnd() {},
    appendAudio() {
      engine.frames++;
      if (options.inputKind === 'process' && engine.frames === 1) {
        callbacks.onSourceFinal(`external-${engines.length}`, 'We should smoke and take Roshan.');
        callbacks.onTargetFinal(`external-${engines.length}`, '我们开雾去打肉山。');
        callbacks.onTargetAudio('forbidden-audio', Buffer.alloc(480)); // Defense in depth: server must discard it.
      }
    },
  };
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const base = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  browser = await chromium.launch({
    headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH,
    args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'],
  });
  const context = await browser.newContext({ viewport: { width: 1400, height: 1080 }, permissions: ['microphone'] });
  await context.addInitScript(() => {
    window.nativeMessages = [];
    window.testStreams = [];
    let handler;
    let timer;
    window.deliverNative = message => handler?.({ data: message });
    const bridge = {
      addEventListener(_type, callback) { handler = callback; },
      postMessage(message) {
        window.nativeMessages.push(message);
        if (message.type === 'desktop.hello') queueMicrotask(() =>
          window.deliverNative({ type: 'desktop.ready', version: 1, overlayPositioning: 'available-space-v1',
            monitors: ['1 · Primary', '2 · Secondary'] }));
        if (message.type === 'process.list') queueMicrotask(() => window.deliverNative({
          type: 'process.list', processes: [
            { pid: 123, name: 'dota2.exe', startedAt: '100' },
            { pid: 456, name: 'YY.exe', startedAt: '200' },
          ],
        }));
        if (message.type === 'capture.start') {
          queueMicrotask(() => window.deliverNative({ type: 'capture.started', captureId: message.captureId }));
          timer = setInterval(() => window.deliverNative({
            type: 'capture.audio', captureId: message.captureId, audio: btoa('\0'.repeat(2880)),
          }), 60);
        }
        if (message.type === 'capture.stop') clearInterval(timer);
      },
    };
    window.chrome = window.chrome || {};
    window.chrome.webview = bridge;
    const getUserMedia = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
    navigator.mediaDevices.getUserMedia = async options => {
      const stream = await getUserMedia(options);
      window.testStreams.push(stream);
      return stream;
    };
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(base);
  await page.locator('#usernameInput').fill('owner');
  await page.locator('#passwordInput').fill('browser-test-only');
  await page.locator('#loginSubmitBtn').click();
  await page.locator('#createRoomBtn').waitFor();
  const roomResponse = await page.evaluate(async settings => {
    const session = await (await fetch('/api/auth/session')).json();
    return (await (await fetch('/api/rooms', {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': session.csrfToken },
      body: JSON.stringify({ name: 'Desktop listener test', allowedUsernames: [], settings }),
    })).json()).room;
  }, DEFAULT_ROOM_SETTINGS);
  await page.goto(`${base}/?room=${roomResponse.roomId}&role=student`);
  await page.waitForFunction(() => !document.getElementById('openMicBtn').disabled);
  await page.locator('.desktop-listener summary').click();
  await page.locator('#desktopControls').waitFor();
  assert.equal(await page.locator('#processSelect').inputValue(), '123');
  await page.locator('#listenProcessBtn').click();
  assert.match(await page.locator('#processListenStatus').textContent(), /请先确认/);
  assert.equal(engines.filter(e => e.options.inputKind === 'process').length, 0);
  await page.locator('#openMicBtn').click();
  await page.waitForFunction(() => window.testStreams.some(stream => stream.getTracks()[0].readyState === 'live'));
  await page.locator('#processConsent').check();
  await page.locator('#listenProcessBtn').click();
  await page.waitForFunction(() => document.getElementById('transcriptFeed').textContent.includes('我们开雾去打肉山。'));
  assert.equal(await page.locator('.turn-card').count(), 1);
  assert.match(await page.locator('.turn-card').textContent(), /外部监听（仅自己可见）/);
  assert.equal(await page.locator('#processSelect').isDisabled(), true);
  await page.waitForFunction(() => window.nativeMessages.some(message =>
    message.type === 'overlay.caption' && message.source === 'We should smoke and take Roshan.' && message.target === '我们开雾去打肉山。'));
  const microphoneEngine = engines.find(e => e.options.inputKind === 'microphone' && !e.stopped);
  microphoneEngine.callbacks.onError('Simulated independent microphone warning');
  microphoneEngine.callbacks.onSourceFinal('app-pair', '我们撤退');
  microphoneEngine.callbacks.onTargetFinal('app-pair', 'Fall back');
  await page.waitForFunction(() => window.nativeMessages.some(message =>
    message.type === 'overlay.caption' && message.source === '我们撤退' && message.target === 'Fall back'));
  assert.equal(await page.locator('#listenProcessBtn').textContent(), '停止监听');
  assert.equal(await page.locator('.turn-card').count(), 2);
  await page.locator('#overlayTargetColor').fill('#40ff80');
  await page.locator('#overlayOpacity').evaluate(el => { el.value = '0.5'; el.dispatchEvent(new Event('input')); });
  await page.locator('#overlayMonitor').selectOption('1');
  assert.equal(await page.locator('#overlayTop').getAttribute('max'), '100');
  await page.locator('#overlayTop').focus();
  await page.keyboard.press('End');
  assert.equal(await page.locator('#overlayTop').inputValue(), '100');
  assert.equal(await page.locator('#overlayTopValue').textContent(), '100%');
  assert.match(await page.locator('#overlayTop').getAttribute('aria-valuetext'), /底部/);
  const settings = await page.evaluate(() => window.nativeMessages.filter(m => m.type === 'overlay.settings').at(-1));
  assert.equal(settings.targetColor, '#40ff80');
  assert.equal(settings.opacity, 0.5);
  assert.equal(settings.monitor, 1);
  assert.equal(settings.top, 100);
  await page.locator('#overlayFontSize').evaluate(el => { el.value = '48'; el.dispatchEvent(new Event('input')); });
  const resized = await page.evaluate(() => window.nativeMessages.filter(m => m.type === 'overlay.settings').at(-1));
  assert.equal(resized.fontSize, 48);
  assert.equal(resized.top, 100);
  assert.equal(await page.evaluate(() => JSON.parse(localStorage.getItem('desktop.overlay')).top), 100);
  assert.equal(await page.locator('#listenProcessBtn').textContent(), '停止监听');
  const firstCapture = await page.evaluate(() => window.nativeMessages.find(m => m.type === 'capture.start').captureId);
  await page.locator('#listenProcessBtn').click();
  assert.equal(await page.locator('#processSelect').isDisabled(), false);
  assert.equal(await page.evaluate(() => window.testStreams[0].getTracks()[0].readyState), 'live');
  const firstEngine = engines.find(e => e.options.inputKind === 'process');
  await page.waitForTimeout(60);
  assert.equal(firstEngine.stopped, true);
  const frames = firstEngine.frames;
  await page.evaluate(captureId => window.deliverNative({
    type: 'capture.audio', captureId, audio: btoa('\0'.repeat(2880)),
  }), firstCapture);
  await page.waitForTimeout(80);
  assert.equal(firstEngine.frames, frames);
  await page.locator('#processSelect').selectOption('456');
  await page.locator('#listenProcessBtn').click();
  await page.waitForFunction(() => document.getElementById('transcriptFeed').textContent.includes('YY.exe'));
  assert.equal(await page.locator('.turn-card').count(), 3);
  await page.locator('#previewOverlayBtn').click();
  await fs.mkdir('.tmp/listener-screenshots', { recursive: true });
  await page.screenshot({ path: '.tmp/listener-screenshots/controls.png', fullPage: true });
  await page.locator('#leaveRoomBtn').click();
  await page.waitForFunction(() => document.getElementById('roomView').hidden);
  assert.equal(await page.evaluate(() => window.testStreams[0].getTracks()[0].readyState), 'ended');
  assert.equal(await page.locator('#processConsent').isChecked(), false);
  assert.equal(await page.locator('.turn-card').count(), 0);
  await page.waitForTimeout(80);
  assert.ok(engines.every(engine => engine.stopped));
  assert.ok(await page.evaluate(() => window.nativeMessages.some(m => m.type === 'overlay.clear')));
  // Reload restores the bottom preference, then an old binary gets an honest 0–60% fallback.
  await page.goto(`${base}/?room=${roomResponse.roomId}&role=student`);
  await page.waitForFunction(() => !document.getElementById('openMicBtn').disabled);
  await page.locator('.desktop-listener summary').click();
  assert.equal(await page.locator('#overlayTop').inputValue(), '100');
  await page.evaluate(() => window.deliverNative({ type: 'desktop.ready', version: 1, monitors: ['1 · Primary'] }));
  assert.equal(await page.locator('#overlayTop').getAttribute('max'), '60');
  assert.equal(await page.locator('#overlayTop').inputValue(), '60');
  assert.match(await page.locator('#overlayPositionHint').textContent(), /升级到 v0\.1\.3/);
  assert.equal(await page.evaluate(() => JSON.parse(localStorage.getItem('desktop.overlay')).top), 100);
  await page.evaluate(() => window.deliverNative({ type: 'desktop.ready', version: 1,
    overlayPositioning: 'available-space-v1', monitors: ['1 · Primary'] }));
  assert.equal(await page.locator('#overlayTop').inputValue(), '100');
  assert.deepEqual(errors, []);
  const normal = await browser.newPage();
  await normal.goto(base);
  assert.equal(await normal.locator('#desktopControls').isVisible(), false);
  console.log('PASS: desktop bridge, mic/listener, private chat, paired overlay, bottom 100%, font changes, persistence/legacy fallback, process switch and cleanup.');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
  await fs.rm(directory, { recursive: true, force: true });
}
