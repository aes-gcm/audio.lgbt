// npm install --no-save playwright; BASE_URL=http://127.0.0.1:8081 node test/e2e/regression.cjs
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const base = process.env.BASE_URL || 'http://127.0.0.1:8081';
const shots = 'output/playwright';
fs.mkdirSync(shots, { recursive: true });
const capture = async (page, options) => { if (process.env.CAPTURE === '1') await page.screenshot(options); };
const waitAsync = async (page, predicate, arg) => {
 const deadline = Date.now() + 25000;
 while (Date.now() < deadline) {
  const result = await page.evaluate(predicate, arg);
  if (result) return result;
  await new Promise(resolve => setTimeout(resolve, 150));
 }
 throw new Error('Timed out waiting for media statistics');
};
(async () => {
 const browser = await chromium.launch({ headless: true, channel: 'chrome', args: ['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required'] });
 const errors = [];
 try {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 }, permissions: ['camera','microphone'] });
  await ctx.addInitScript(() => { window.testTracks = []; const md = navigator.mediaDevices; const gum = md.getUserMedia.bind(md); md.getUserMedia = async c => { const s = await gum(c); window.testTracks.push(...s.getTracks()); return s; }; });
  ctx.setDefaultTimeout(20000);
  const a = await ctx.newPage(); a.on('pageerror', e => errors.push(e.message));
  await a.goto(base + '/?debug=1');
  await capture(a, { path: shots+'/home-desktop.png', fullPage: true });
  await a.click('#createRoom');
  await a.waitForSelector('#join:not([disabled])');
  await a.fill('#name', 'Александра');
  await capture(a, { path: shots+'/preparation-desktop.png', fullPage: true });
  const room = a.url();
  await a.click('#join'); await a.waitForSelector('#call:not([hidden])');
  await a.waitForSelector('#waitingHint:not([hidden])');
  const ctxB = await browser.newContext({ viewport: { width: 390, height: 844 }, permissions: ['camera','microphone'] });
  ctxB.setDefaultTimeout(20000); const b = await ctxB.newPage(); b.on('pageerror', e => errors.push(e.message));
  await b.goto(room); await b.waitForSelector('#join:not([disabled])');
  await b.fill('#name', 'Михаил'); await capture(b, { path: shots+'/preparation-mobile.png', fullPage: true });
  await b.click('#join'); await b.waitForSelector('#call:not([hidden])');
  await a.waitForFunction(() => document.querySelector('#count').textContent === '2');
  await waitAsync(a, async () => (await window.__z.stats()).video?.frames > 3 && (await window.__z.stats()).audio?.pkts > 0);
  await waitAsync(b, async () => (await window.__z.stats()).video?.frames > 3 && (await window.__z.stats()).audio?.pkts > 0);
  assert.equal(await a.locator('#waitingHint').isVisible(), false);
  await a.click('#chatToggle'); await a.fill('#chatInput', 'Привет! Проверяем защищённый чат.'); await a.click('#chatSend');
  await b.click('#chatToggle'); await b.waitForSelector('#chatLog .bubble');
  assert.equal(await b.locator('#chatLog .bubble').textContent(), 'Привет! Проверяем защищённый чат.');
  await capture(a, { path: shots+'/call-desktop.png', fullPage: true });
  await b.click('#panelClose'); await capture(b, { path: shots+'/call-mobile.png', fullPage: true });
  assert.equal(await b.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await b.click('#leave'); await b.waitForSelector('#ended:not([hidden])');
  await a.waitForFunction(() => document.querySelector('#count').textContent === '1');
  await a.click('#peopleBtn'); await a.click('#endAll'); await a.click('#confirmEnd');
  await a.waitForSelector('#ended:not([hidden])');
  assert.equal(await a.locator('#returnCall').isVisible(), false);
  assert.equal(await a.evaluate(() => window.testTracks.every(t => t.readyState === 'ended')), true);
  await capture(a, { path: shots+'/ended-desktop.png', fullPage: true });
  await capture(b, { path: shots+'/ended-mobile.png', fullPage: true });
  // Blocked browser storage must not break boot or joining without hardware.
  const blocked = await browser.newContext();
  await blocked.addInitScript(() => { window.testSockets = []; const Original = WebSocket; window.WebSocket = class extends Original { constructor(...args) { super(...args); window.testSockets.push(this); } }; navigator.permissions.query = async () => ({ state: 'prompt' }); Object.defineProperty(window, 'localStorage', { get() { throw new DOMException('blocked', 'SecurityError'); } }); });
  const c = await blocked.newPage(); c.on('pageerror', e => errors.push(e.message));
  await c.goto(base); await c.click('#createRoom'); await c.click('#skipDevices'); await c.click('#join'); await c.waitForSelector('#call:not([hidden])'); await c.click('#leave');
  // Oversized keys are invalid, never silently truncated into a different room.
  await c.goto(base+'/#'+Buffer.alloc(17, 1).toString('base64url')); await c.waitForSelector('#inviteError:not([hidden])');
  // Light theme and narrow viewport retain all entry controls.
  await b.goto(base); await b.emulateMedia({ reducedMotion: 'reduce' }); await b.click('#themeToggle'); await capture(b, { path: shots+'/home-mobile-light.png', fullPage: true });
  assert.equal(await b.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  // Offline sends preserve the draft; failed E2EE support never joins silently.
  await c.goto(base+'/?debug=1'); await c.click('#createRoom'); await c.click('#skipDevices'); await c.click('#join'); await c.waitForSelector('#call:not([hidden])');
  await c.routeWebSocket('**/ws', ws => ws.close());
  await blocked.setOffline(true);
  await c.evaluate(() => window.testSockets.forEach(socket => socket.close()));
  await c.waitForFunction(() => document.querySelector('#banner').textContent.includes('потеряно'));
  await c.click('#chatToggle'); await c.fill('#chatInput', 'Сохранённый черновик'); await c.click('#chatSend'); await c.waitForSelector('#chatError:not([hidden])');
  assert.equal(await c.inputValue('#chatInput'), 'Сохранённый черновик'); assert.equal(await c.locator('#chatLog .mine').count(), 0);
  await c.click('#panelClose'); await c.click('#leave'); await blocked.setOffline(false);
  const unsupported = await browser.newContext();
  await unsupported.addInitScript(() => { navigator.permissions.query = async () => ({ state: 'prompt' }); delete window.RTCRtpScriptTransform; delete RTCRtpSender.prototype.createEncodedStreams; });
  const d = await unsupported.newPage(); await d.goto(base); await d.click('#createRoom'); await d.click('#skipDevices'); await d.click('#join');
  await d.waitForSelector('#pjError:not([hidden])'); assert.equal(await d.locator('#call').isVisible(), false);
  assert.deepEqual(errors, []);
  console.log('PASS: encrypted two-client audio/video, chat, host end, media cleanup, blocked storage, invalid invitation, desktop/mobile/light layout');
 } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
