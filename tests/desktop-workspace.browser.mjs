// Packaged UI rendered in a real browser with a deterministic native bridge, not shipped demo behavior.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { once } from 'node:events';
import { DEFAULT_ROOM_SETTINGS, TTS_VOICE_CATALOG } from '../src/room-settings.ts';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve('desktop/DotaCoach.Desktop/Ui');
const server = http.createServer(async (req, res) => {
  try {
    const route = new URL(req.url, 'http://local').pathname;
    const filename = route.startsWith('/heroes/') ? path.resolve('public' + route) : path.resolve(root, '.' + (route === '/' ? '/index.html' : route));
    assert.ok(filename.startsWith(root + '/') || filename.startsWith(path.resolve('public/heroes') + '/'));
    const bytes = await fs.readFile(filename);
    res.setHeader('Content-Type', filename.endsWith('.js') ? 'application/javascript' : filename.endsWith('.css') ? 'text/css' :
      filename.endsWith('.json') ? 'application/json' : filename.endsWith('.jpg') ? 'image/jpeg' : filename.endsWith('.png') ? 'image/png' : 'text/html');
    res.end(bytes);
  } catch { res.writeHead(404); res.end(); }
});
server.listen(0, '127.0.0.1'); await once(server, 'listening');
let browser;
try {
  browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH });
  const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(({ defaults, voices }) => {
    window.nativeCalls = [];
    const state = {
      version: '0.2.0', server: 'https://dota2.largeniu.com/', user: null, room: null,
      role: 'student', selfMonitor: false,
      config: { defaults, ttsVoiceCatalog: voices }, history: [], roster: null,
      microphone: { ready: false, active: false, status: '未连接房间', frames: 0 },
      listener: { ready: false, active: false, status: '未开始监听', frames: 0 },
    };
    let handler, overlay = { top: 100 }, nativeTimer;
    const room = { roomId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', name: '今晚的对线训练',
      ownerUsername: 'largeniu', isOwner: true, allowedUsernames: ['coach'],
      settings: defaults, inviteToken: 'test-only-invite' };
    const emit = message => handler?.({ data: JSON.parse(JSON.stringify(message)) });
    const update = () => emit({ type: 'state', state });
    window.nativeState = state;
    window.emitNative = emit;
    window.chrome = window.chrome || {};
    window.chrome.webview = {
      addEventListener(_type, cb) { handler = cb; },
      postMessage(request) {
        window.nativeCalls.push({ method: request.method, data: request.method === 'login' ? { username: request.data.username } : request.data });
        queueMicrotask(() => {
          try {
            let result = null;
            const data = request.data;
            switch (request.method) {
              case 'bootstrap': result = { state, overlay, monitors: ['Primary', 'Secondary'], shortcuts: { overlay: true, listener: true, microphone: true } }; break;
              case 'login': state.user = { username: data.username, canCreateRooms: true }; result = state; update(); break;
              case 'logout': state.user = state.room = null; state.history = []; state.microphone.active = state.microphone.ready = state.listener.active = state.listener.ready = false; result = state; update(); break;
              case 'rooms.list': result = { rooms: [room] }; break;
              case 'users.list': result = { users: [{ username: 'largeniu' }, { username: 'coach' }] }; break;
              case 'room.create': room.name = data.name; result = { room }; break;
              case 'room.join': state.room = room; state.roster = { roomId: room.roomId, ...defaults, matchHeroes: ['Axe', 'Crystal Maiden', 'Pudge'] }; state.microphone.ready = true; state.microphone.status = '语音已就绪 · 麦克风关闭'; result = state; update(); break;
              case 'room.leave': state.room = null; state.history = []; state.microphone.ready = state.microphone.active = state.listener.ready = state.listener.active = false; result = state; update(); break;
              case 'room.reconnect': state.microphone.ready = true; result = state; update(); break;
              case 'room.preferences': state.role = data.role; state.selfMonitor = data.selfMonitor; result = state; update(); break;
              case 'room.access': room.allowedUsernames = data.allowedUsernames; result = state; update(); break;
              case 'room.save': room.settings = data.settings; result = state; update(); break;
              case 'room.heroes': state.roster.matchHeroes = data.heroes; update(); break;
              case 'mic.devices': result = [{ id: 'headset', name: '耳机麦克风 · HyperX Cloud' }]; break;
              case 'process.list': result = [{ pid: 123, name: 'dota2.exe', startedAt: '100' }, { pid: 456, name: 'YY.exe', startedAt: '200' }]; break;
              case 'mic.start': state.microphone.active = true; state.microphone.status = '麦克风采集中'; result = state; update(); break;
              case 'mic.stop': state.microphone.active = false; state.microphone.status = '麦克风已关闭'; result = state; update(); break;
              case 'listener.start':
                state.listener = { ...state.listener, active: true, ready: true, sourceName: 'dota2.exe', status: '正在监听 · 仅自己可见' }; result = state; update();
                clearInterval(nativeTimer);
                nativeTimer = setInterval(() => {
                  if (!state.listener.active) return;
                  state.listener.frames++;
                  emit({ type: 'level', source: 'listener', level: .34 });
                  if (state.microphone.active) emit({ type: 'level', source: 'microphone', level: .22 });
                  emit({ type: 'receipt', source: 'listener' });
                }, 60);
                break;
              case 'listener.stop': state.listener.active = state.listener.ready = state.listener.connecting = false; result = state; update(); break;
              case 'overlay.settings': overlay = data; result = overlay; break;
              case 'overlay.preview': case 'window.tray': case 'room.copyInvite': case 'gsi.download': case 'diagnostics.export': case 'web.open': break;
              case 'text.send': break;
              default: throw new Error('Unexpected command ' + request.method);
            }
            emit({ type: 'response', id: request.id, ok: true, result });
          } catch (error) { emit({ type: 'response', id: request.id, ok: false, error: error.message }); }
        });
      },
    };
  }, { defaults: DEFAULT_ROOM_SETTINGS, voices: TTS_VOICE_CATALOG });
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await page.waitForFunction(() => document.getElementById('processSelect').options.length > 1);
  await fs.mkdir('.tmp/desktop-v020', { recursive: true });
  await page.screenshot({ path: '.tmp/desktop-v020/workspace-offline.png' });
  assert.equal(await page.locator('#micToggle').isDisabled(), true);
  await page.locator('[data-page="subtitles"]').first().click();
  await page.locator('#largePreviewButton').click();
  await page.waitForFunction(() => window.nativeCalls.some(c => c.method === 'overlay.preview'));
  assert.equal(await page.locator('#overlayTop').inputValue(), '100');
  await page.locator('[data-page="workspace"]').first().click();
  await page.locator('#welcomeLogin').click();
  await page.locator('#username').fill('largeniu');
  await page.locator('#password').fill('test-password');
  await page.locator('#loginSubmit').click();
  await page.locator('.room-list-item button').click();
  await page.waitForFunction(() => !document.getElementById('micToggle').disabled);
  await page.locator('#micToggle').click();
  await page.waitForFunction(() => window.nativeState.microphone.active);
  await page.waitForFunction(() => !document.getElementById('micToggle').disabled &&
    getComputedStyle(document.getElementById('micToggle')).backgroundColor === 'rgb(60, 41, 45)');
  await page.locator('#listenToggle').click();
  await page.waitForFunction(() => window.nativeState.listener.active);
  await page.waitForFunction(() => document.getElementById('listenLevelText').textContent !== '0%');
  await page.evaluate(() => {
    const texts = [
      ['Coach · Andrew', 'Hold the wave here. We can punish him when he goes for the ranged creep.', '把兵线控在这里。他补远程兵的时候，我们就上去打他。', 'microphone'],
      ['dota2.exe', 'We should smoke and take Roshan. Their wards are on the top side.', '我们开雾去打肉山。他们的眼位都在上半区。', 'process'],
      ['Coach · Andrew', 'Nice, save your TP. We are stronger with the next item timing.', '打得不错，留好 TP。等下一件装备出来，我们会更强。', 'microphone'],
    ];
    for (const [index, [speaker, source, target, sourceKind]] of texts.entries()) {
      const turn = { id: String(index), speaker, source, target, sourceKind, role: 'coach', timestamp: Date.now() - (2 - index) * 18000 };
      window.nativeState.history.push(turn); window.emitNative({ type: 'conversation', turn });
    }
  });
  await page.screenshot({ path: '.tmp/desktop-v020/workspace-live.png' });
  assert.equal(await page.locator('#conversationFeed .chat-turn').count(), 3);
  assert.equal(await page.locator('#conversationFeed .private-badge').count(), 1);
  const before = await page.evaluate(() => ({ calls: window.nativeCalls.length, frames: window.nativeState.listener.frames }));
  await page.locator('[data-page="subtitles"]').first().click();
  await page.locator('#overlayTop').focus(); await page.keyboard.press('Home');
  await page.locator('#overlayFontSize').fill('36');
  await page.locator('#overlayTop').focus(); await page.keyboard.press('End');
  await page.locator('#largePreviewButton').click();
  await page.screenshot({ path: '.tmp/desktop-v020/subtitles.png' });
  await page.locator('[data-page="settings"]').first().click();
  await page.screenshot({ path: '.tmp/desktop-v020/settings.png' });
  await page.waitForTimeout(200);
  const after = await page.evaluate(start => ({
    frames: window.nativeState.listener.frames, calls: window.nativeCalls.slice(start),
    mic: window.nativeState.microphone.active, listener: window.nativeState.listener.active,
  }), before.calls);
  assert.ok(after.frames > before.frames, 'Native listener must continue while navigating settings');
  assert.ok(after.mic && after.listener);
  assert.ok(!after.calls.some(c => ['mic.stop', 'listener.stop', 'room.leave'].includes(c.method)));
  assert.equal(after.calls.filter(c => c.method === 'overlay.settings').at(-1).data.top, 100);
  await page.locator('[data-page="workspace"]').first().click();
  await page.locator('#editHeroes').click();
  await page.locator('#heroSearch').fill('猴子');
  assert.equal(await page.locator('.hero-tile').count(), 1);
  await page.locator('.hero-tile').click(); await page.locator('#saveHeroes').click();
  await page.waitForFunction(() => window.nativeState.roster.matchHeroes.includes('Phantom Lancer'));
  await page.locator('#manageRoom').click();
  assert.equal(await page.locator('#roomName').inputValue(), '今晚的对线训练');
  await page.locator('#copyInvite').click();
  await page.locator('#roomDialog [data-close]').click();
  await page.locator('#micToggle').click();
  await page.waitForFunction(() => !window.nativeState.microphone.active);
  assert.equal(await page.evaluate(() => window.nativeState.listener.active), true);
  // Real pointer capture, including fast release during native startup.
  const ptt = await page.locator('#pttButton').boundingBox();
  await page.mouse.move(ptt.x + 10, ptt.y + 10); await page.mouse.down(); await page.mouse.up();
  await page.waitForTimeout(100);
  assert.equal(await page.evaluate(() => window.nativeState.microphone.active), false);
  await page.locator('#listenToggle').click();
  await page.waitForFunction(() => !window.nativeState.listener.active);
  // A slow native connection exposes cancellation, not a second queued start.
  await page.evaluate(() => {
    window.nativeState.listener.connecting = true;
    window.emitNative({ type: 'state', state: window.nativeState });
  });
  assert.equal(await page.locator('#listenToggle span').textContent(), '取消连接');
  assert.equal(await page.locator('#processSelect').isDisabled(), true);
  await page.locator('#listenToggle').click();
  await page.waitForFunction(() => !window.nativeState.listener.connecting);
  assert.equal(await page.evaluate(() => window.nativeCalls.at(-1).method), 'listener.stop');
  await page.setViewportSize({ width: 1000, height: 700 });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.screenshot({ path: '.tmp/desktop-v020/workspace-compact.png' });
  await page.evaluate(() => window.emitNative({ type: 'conversation', turn: { id: 'unsafe', speaker: '<script>alert(1)</script>',
    source: '<img src=x onerror=alert(1)>', target: '纯文本显示', role: 'coach', sourceKind: 'process', timestamp: Date.now() } }));
  assert.equal(await page.locator('#conversationFeed img').count(), 0);
  await page.locator('[data-page="settings"]').first().click();
  await page.locator('#logoutButton').click();
  await page.waitForFunction(() => !window.nativeState.user);
  assert.deepEqual(errors, []);
  console.log('PASS: packaged desktop UI, offline preview, login/rooms, dual capture, persistent audio across navigation, subtitle settings, heroes, invitation, PTT, privacy, compact layout and logout.');
} finally {
  await browser?.close(); await new Promise(resolve => server.close(resolve));
}
