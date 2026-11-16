import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import { createSandboxServer } from '../src/sandbox/server.ts';

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { server, token } = createSandboxServer();
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH });
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 940 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
  await page.goto(`${origin}/sandbox#token=${token}`);
  await page.waitForFunction(() => document.querySelector('#status').textContent.includes('已连接'));
  assert.equal(new URL(page.url()).hash, '', 'local token should be removed from the address after bootstrap');
  await Promise.all([
    page.waitForResponse(r => r.url().endsWith('/api/action') && r.status() === 200),
    page.getByRole('button', { name: '前往并清理', exact: true }).click(),
  ]);
  await page.getByRole('button', { name: '疯狂面具', exact: true }).click();
  await page.waitForFunction(() => Number(document.querySelector('#resources').textContent.match(/MP (\d+)/)[1]) < 556);
  await page.getByRole('button', { name: '推进 10 秒', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#clock').textContent === '10:10.0');
  assert.ok(Number(await page.locator('#neutral-gold').textContent()) > 0);
  const visits = ['neutralcamp_good_5', 'neutralcamp_good_9', 'neutralcamp_good_4'].map(campId => ({
    campId, targetPriority: [], casts: [{ name: 'item_mask_of_madness', seconds: 0 }],
  }));
  const response = await fetch(origin + '/api/plan', { method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ visits, model: 'browser-test-no-cloud', summary: '仅验证 API 与地图播放，不是大模型收益测试。' }) });
  assert.equal(response.status, 409);
  assert.equal((await response.json()).code, 'OWN_AREA_UNVERIFIED');
  assert.equal(await page.locator('#plan li').count(), 0);
  assert.ok(await page.getByRole('button', { name: '前往收线', exact: true }).isVisible());
  assert.ok(await page.getByRole('button', { name: '攻击一次并引向出口', exact: true }).isVisible());
  assert.ok((await page.locator('.metrics').textContent()).includes('线野合计 GPM'));
  assert.ok((await page.locator('#scope-warning').textContent()).includes('夜魇营地'));
  assert.ok((await page.locator('#model-summary').textContent()).includes('路线建议已撤下'));
  assert.equal(await page.locator('#experiment').isDisabled(), true);
  assert.equal(await page.getByRole('button', { name: '回放所选对照', exact: true }).isDisabled(), true);
  assert.equal(await page.locator('#experiment option').count(), 1);
  // Regression: an explicit manual action queued during a reset must not vanish.
  await page.route('**/api/reset', async route => {
    await new Promise(resolve => setTimeout(resolve, 250)); await route.continue();
  });
  await page.getByRole('button', { name: '重置同场景', exact: true }).click();
  await page.getByRole('button', { name: '推进 10 秒', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#clock').textContent === '10:10.0');
  await page.waitForFunction(() => document.querySelector('#neutral-gold').textContent === '0.0');
  assert.equal(await page.locator('#plan li').count(), 0);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await fs.mkdir('.tmp', { recursive: true });
  await page.screenshot({ path: '.tmp/sandbox-ui-test.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(100);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  assert.deepEqual(errors, []);
  console.log('PASS sandbox browser: manual mechanics, queued actions, withdrawn route advice, blocked activation, canvas, mobile layout.');
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
