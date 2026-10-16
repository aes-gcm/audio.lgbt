const { chromium } = require('playwright');
const DIR = '/Users/localhost/Desktop/call/screenshots';
const URL = 'http://127.0.0.1:8099/index.html';

(async () => {
  const browser = await chromium.launch({ headless: true });
  const log = [];

  // ---- Desktop dark ----
  let ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
  let page = await ctx.newPage();
  page.on('pageerror', e => log.push('PAGEERROR: ' + e.message));
  page.on('console', m => { if (m.type() === 'error') log.push('CONSOLE.err: ' + m.text()); });
  await page.goto(URL, { waitUntil: 'load' });
  await page.waitForTimeout(1300);
  await page.screenshot({ path: `${DIR}/8-audiochat-landing-dark.png` });
  await page.screenshot({ path: `${DIR}/8-audiochat-landing-dark-full.png`, fullPage: true });
  log.push('theme attr before toggle: ' + await page.getAttribute('html', 'data-theme'));

  // ---- Desktop light ----
  await page.click('#themeToggle').catch(e => log.push('themeToggle click failed: ' + e.message));
  await page.waitForTimeout(700);
  log.push('theme attr after toggle: ' + await page.getAttribute('html', 'data-theme'));
  await page.screenshot({ path: `${DIR}/9-audiochat-landing-light.png` });
  await ctx.close();

  // ---- Mobile dark (full page) ----
  ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true });
  page = await ctx.newPage();
  await page.goto(URL, { waitUntil: 'load' });
  await page.waitForTimeout(1100);
  await page.screenshot({ path: `${DIR}/10-audiochat-landing-mobile.png`, fullPage: true });
  await ctx.close();

  // ---- Invited state (arriving via link) desktop dark ----
  ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
  page = await ctx.newPage();
  await page.goto(URL + '#Zm9vYmFyZm9vYmFyMTIzNDU2', { waitUntil: 'load' });
  await page.waitForTimeout(1100);
  await page.screenshot({ path: `${DIR}/11-audiochat-landing-invited.png` });
  await ctx.close();

  await browser.close();
  console.log(log.join('\n') || 'no errors');
  console.log('SCREENSHOTS DONE');
})();
