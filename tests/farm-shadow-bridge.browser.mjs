import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { setTimeout as sleep } from 'node:timers/promises';
import { startFarmShadowBridge } from '../scripts/farm-shadow-bridge.ts';
import { playerPacket } from './farm-shadow-bridge-fixtures.ts';

// Fresh browser profile + ephemeral bridge only. No sandbox or existing desktop session.
const out = new URL('../.tmp/farm742-bridge/', import.meta.url), tmp = new URL('browser-temp/', out);
await mkdir(tmp, { recursive: true });
process.env.TMP = process.env.TEMP = process.env.TMPDIR = fileURLToPath(tmp);
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const s = await startFarmShadowBridge({ corpus: 'fixed' });
let browser, pump, sending = true, active = true, id = '742000001', side = 'radiant', hero = 'luna';
try {
  browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH,
    args: ['--disable-background-networking'] });
  const ctx = await browser.newContext({ viewport: { width: 1050, height: 1024 }, serviceWorkers: 'block' });
  const errors = [], requests = [];
  await ctx.route('**/*', route => {
    const url = new URL(route.request().url());
    requests.push(url); return url.origin === s.origin ? route.continue() : route.abort();
  });
  const misplaced = await ctx.newPage(), misplacedApi = [];
  misplaced.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/')) misplacedApi.push(r.url()); });
  const inert = await readFile(new URL('../public/farm-shadow-bridge.html', import.meta.url), 'utf8');
  await misplaced.route(s.origin + '/', route => route.fulfill({ contentType: 'text/html; charset=utf-8', body: inert }));
  await misplaced.goto(s.origin + '/'); await misplaced.getByText(/不是独立桥页面/).waitFor();
  assert.ok(await misplaced.locator('#token').isDisabled()); assert.ok(await misplaced.locator('#connect').isDisabled());
  await misplaced.close(); assert.deepEqual(misplacedApi, []);
  const page = await ctx.newPage(); page.on('pageerror', e => errors.push(e.message));
  page.setDefaultTimeout(8_000);
  await page.goto(s.origin + '/');
  assert.ok(await page.locator('#target').isDisabled());
  const started = Date.now();
  let pumpError;
  pump = (async () => {
    while (active) {
      if (sending) {
        const at = Date.now(), p = playerPacket(at, id, 660 + Math.floor((at - started) / 1000), side, hero);
        const res = await fetch(s.origin + '/gsi', { method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ...p, auth: { token: s.gsiToken } }) });
        if (!res.ok) { pumpError = `GSI: ${res.status}`; return; }
        await res.arrayBuffer();
      }
      await sleep(230);
    }
  })();
  await page.locator('#token').fill(s.operatorToken); await page.locator('#login button').first().click();
  const status = code => page.waitForFunction(x => document.querySelector('#status').textContent.includes(x), code);
  await status('CONFIRM_CONTEXT'); assert.equal(await page.locator('#token').inputValue(), '');
  await page.locator('#build').fill('6986'); await page.locator('#start').fill(String(Math.floor(started / 1000) - 3600));
  await page.locator('#ownView').check(); await page.locator('#confirm button').click(); await status('NEED_SIGHTING');
  await page.locator('#target').fill('lane:bot'); await page.locator('#presence').selectOption('present');
  await page.locator('#area').selectOption('own'); await page.locator('#danger').selectOption('not-seen');
  await page.locator('#visibleNow').check(); await page.getByRole('button', { name: '提交当前目击' }).click();
  await status('SHADOW_OVERLAP');
  assert.match(await page.locator('#shadow').innerText(), /非路线.*lane:bot/);
  await page.screenshot({ path: fileURLToPath(new URL('rehearsal-wide.png', out)), fullPage: true });
  await page.locator('#threat').click(); await status('CANCELLED'); assert.doesNotMatch(await page.locator('#shadow').innerText(), /lane:bot/);

  id = '742000002'; side = 'dire'; hero = 'troll_warlord'; await status('CONFIRM_CONTEXT');
  assert.match(await page.locator('#own').innerText(), /742000002 · 夜魇 · npc_dota_hero_troll_warlord/);
  assert.equal(await page.locator('#visible').innerText(), '—');
  assert.ok(await page.locator('#target').isDisabled()); assert.equal(await page.locator('#build').inputValue(), '');
  await page.setViewportSize({ width: 380, height: 850 });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.screenshot({ path: fileURLToPath(new URL('rehearsal-mobile.png', out)), fullPage: true });
  await page.route('**/api/state', route => route.abort());
  await status('无法确认'); assert.equal(await page.locator('#own').innerText(), '—'); assert.equal(await page.locator('#visible').innerText(), '—');
  await page.unroute('**/api/state'); await status('CONFIRM_CONTEXT');
  sending = false; await status('STALE_GSI'); assert.equal(await page.locator('#own').innerText(), '—');
  await page.locator('#stop').click(); await status('STOPPED');
  assert.ok(await page.locator('#stop').isDisabled());
  assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length, location.hash, location.search]), [0, 0, '', '']);
  assert.deepEqual(await ctx.cookies(), []); assert.deepEqual(errors, []); assert.equal(pumpError, undefined);
  assert.ok(requests.every(url => url.origin === s.origin && !url.search && !url.hash));
  console.log('PASS: real page + synthetic player GSI -> manual sighting -> read-only overlap -> danger/reset/offline/expiry/stop; wide/mobile; no external page requests.');
} finally {
  active = false; if (pump) await pump;
  try { if (browser) await browser.close(); } finally { await s.close(); }
}
